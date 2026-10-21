using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml;

namespace NoIntrovert.Core.Esde;

/// <summary>What a check did: whether a new list was downloaded, why it stopped early (offline, refused), and the mapped folders upstream no longer has.</summary>
public sealed record EsdeUpdateResult(bool Downloaded, string Ref, string? Problem, IReadOnlyList<string> Missing);

/// <summary>
/// Keeps the ES-DE system list in step with ES-DE's latest release, in the
/// style of <see cref="Dats.DatUpdater"/>: the GitLab releases API is asked
/// with its last ETag, so an unchanged release costs one 304. Only a new
/// release leads to the commits API (has es_systems.xml changed since the
/// list we have?), and only a new commit to a download.
/// </summary>
public sealed class EsdeUpdater(EsdeStore store, HttpMessageHandler? handler = null)
{
    public const string Project = "es-de/emulationstation-de";
    public const string SystemsPath = "resources/systems/unix/es_systems.xml";
    private const string Api = "https://gitlab.com/api/v4/projects/es-de%2Femulationstation-de";

    /// <summary>The app checks at start at most this often.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Whether a check at start is due (none yet, or the last one over a day ago).</summary>
    public bool IsDue(DateTime utcNow) => store.LoadState().LastCheckedUtc is not { } last || utcNow - last >= Interval;

    public async Task<EsdeUpdateResult> UpdateAsync(CancellationToken token = default)
    {
        await OneAtATime.WaitAsync(token);
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NoIntrovert", "1.0"));
            var state = store.LoadState();
            var list = EsdeSystems.Load(store);
            var downloaded = false;
            string? problem = null;
            try
            {
                var tag = await LatestReleaseAsync(http, state, token);
                if (tag != list.Ref)
                {
                    var commit = await LastCommitAsync(http, tag, state, token);
                    if (commit != list.Commit)
                    {
                        list = EsdeSystemList.Parse(await DownloadAsync(http, tag, token), tag, commit);
                        downloaded = true;
                    }
                    else list = new EsdeSystemList { Ref = tag, Commit = commit, Systems = list.Systems }; // a new release, the same file
                    store.SaveList(list);
                    EsdeSystems.Reload();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or XmlException or FormatException or KeyNotFoundException
                                           or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                problem = ex is TaskCanceledException ? "GitLab didn't answer in time" : ex.Message;
            }
            if (problem is null) state.LastCheckedUtc = DateTime.UtcNow;
            store.SaveState(state);
            return new EsdeUpdateResult(downloaded, list.Ref, problem, EsdeSystems.Problems(list));
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>The latest release's tag: asked for with the last ETag, kept from last time on a 304.</summary>
    private static async Task<string> LatestReleaseAsync(HttpClient http, EsdeState state, CancellationToken token)
    {
        using var response = await GetAsync(http, $"{Api}/releases?per_page=1", state.LatestRef is null ? null : state.ReleasesETag, token);
        if (response.StatusCode == HttpStatusCode.NotModified && state.LatestRef is { } known) return known;
        using var json = JsonDocument.Parse(await ReadAsync(response, token));
        var tag = json.RootElement.EnumerateArray().Select(r => r.GetProperty("tag_name").GetString()).FirstOrDefault()
                  ?? throw new HttpRequestException("GitLab listed no ES-DE releases");
        state.LatestRef = tag;
        state.ReleasesETag = response.Headers.ETag?.ToString();
        return tag;
    }

    /// <summary>The last commit that changed es_systems.xml at <paramref name="tag"/>.</summary>
    private static async Task<string> LastCommitAsync(HttpClient http, string tag, EsdeState state, CancellationToken token)
    {
        var etag = state.CommitsRef == tag && state.LatestCommit is not null ? state.CommitsETag : null;
        using var response = await GetAsync(http, $"{Api}/repository/commits?path={Uri.EscapeDataString(SystemsPath)}&ref_name={Uri.EscapeDataString(tag)}&per_page=1", etag, token);
        if (response.StatusCode == HttpStatusCode.NotModified && etag is not null) return state.LatestCommit!;
        using var json = JsonDocument.Parse(await ReadAsync(response, token));
        var commit = json.RootElement.EnumerateArray().Select(c => c.GetProperty("id").GetString()).FirstOrDefault()
                     ?? throw new HttpRequestException($"GitLab has no history for es_systems.xml at {tag}");
        state.CommitsRef = tag;
        state.LatestCommit = commit;
        state.CommitsETag = response.Headers.ETag?.ToString();
        return commit;
    }

    private static async Task<string> DownloadAsync(HttpClient http, string tag, CancellationToken token)
    {
        using var response = await GetAsync(http, $"https://gitlab.com/{Project}/-/raw/{Uri.EscapeDataString(tag)}/{SystemsPath}", null, token);
        return await ReadAsync(response, token);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, string url, string? etag, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return await http.SendAsync(request, token);
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode == HttpStatusCode.TooManyRequests
                ? "GitLab refused the request (too many requests; it'll be tried again later)"
                : $"GitLab answered {(int)response.StatusCode} {response.ReasonPhrase}");
        return await response.Content.ReadAsStringAsync(token);
    }
}

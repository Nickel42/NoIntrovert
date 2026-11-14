using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NoIntrovert.Core.Dats;

/// <summary>What an update did: DATs downloaded, and why it stopped early (offline, refused), if it did.</summary>
public sealed record DatUpdateResult(IReadOnlyList<string> Downloaded, string? Problem)
{
    public bool Changed => Downloaded.Count > 0;
}

/// <summary>
/// Keeps the DAT cache fresh from libretro-database (FR-2). Each folder's
/// listing comes from the GitHub contents API with its last ETag, so an
/// unchanged folder costs one 304; a DAT is downloaded only when it's wanted
/// (a system in the Library) and its git blob SHA differs from the cached copy.
/// </summary>
public sealed class DatUpdater(DatStore store, HttpMessageHandler? handler = null)
{
    public const string Repository = "libretro/libretro-database";
    public static readonly string[] Folders = ["no-intro", "redump"];

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Brings the DATs for <paramref name="keys"/> ("no-intro/Nintendo - Game Boy"…) up to date.</summary>
    public async Task<DatUpdateResult> UpdateAsync(IEnumerable<string> keys, CancellationToken token = default)
    {
        var wanted = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await OneAtATime.WaitAsync(token);
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(30);
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NoIntrovert", "1.0"));
            var state = store.LoadState();
            var downloaded = new List<string>();
            string? problem = null;
            foreach (var folder in Folders)
            {
                var keysHere = wanted.Where(k => k.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)).ToList();
                if (keysHere.Count == 0) continue;
                try
                {
                    var listing = await ListAsync(http, folder, state, token);
                    foreach (var key in keysHere)
                    {
                        var name = key[(folder.Length + 1)..];
                        if (!listing.TryGetValue(name, out var sha)) continue; // not (or no longer) in libretro-database
                        if (state.Cached.GetValueOrDefault(key) == sha && File.Exists(store.PathOf(key))) continue;
                        await DownloadAsync(http, folder, name, store.PathOf(key), token);
                        state.Cached[key] = sha;
                        downloaded.Add(key);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException
                                               or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
                {
                    problem = ex is TaskCanceledException ? "GitHub didn't answer in time" : ex.Message;
                    break;
                }
            }
            if (problem is null) state.LastCheckedUtc = DateTime.UtcNow;
            store.SaveState(state);
            return new DatUpdateResult(downloaded, problem);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>A folder's files (name → blob SHA): asked for with the last ETag, kept from last time on a 304.</summary>
    private static async Task<Dictionary<string, string>> ListAsync(HttpClient http, string folder, DatState state, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/contents/metadat/{folder}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var known = state.Listings.GetValueOrDefault(folder);
        if (known is not null && state.ETags.TryGetValue(folder, out var etag)) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotModified && known is not null) return known;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(response.StatusCode == HttpStatusCode.Forbidden
                ? "GitHub refused the request (its hourly limit for anonymous requests may be used up)"
                : $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var listing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && item.GetProperty("sha").GetString() is { } sha)
                listing[name[..^4]] = sha;
        }
        state.Listings[folder] = listing;
        if (response.Headers.ETag is { } tag) state.ETags[folder] = tag.ToString();
        return listing;
    }

    /// <summary>Downloads one DAT to a temporary file beside its place, then moves it in.</summary>
    private static async Task DownloadAsync(HttpClient http, string folder, string name, string target, CancellationToken token)
    {
        var url = $"https://raw.githubusercontent.com/{Repository}/master/metadat/{folder}/{Uri.EscapeDataString(name)}.dat";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{name}.dat: GitHub answered {(int)response.StatusCode}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".download";
        await using (var file = File.Create(temp))
            await response.Content.CopyToAsync(file, token);
        File.Move(temp, target, overwrite: true);
    }
}

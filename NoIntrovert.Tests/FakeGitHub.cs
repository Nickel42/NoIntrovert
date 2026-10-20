using System.Net;
using System.Text;
using System.Text.Json;

namespace NoIntrovert.Tests;

/// <summary>
/// Stands in for GitHub in the DAT update tests: the contents API listing of
/// metadat/no-intro and metadat/redump (with ETags and 304s) and the raw
/// downloads, counting what was asked for. <see cref="Offline"/> makes every
/// request fail as if there were no network.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    /// <summary>"no-intro/Nintendo - Game Boy" → the DAT's text.</summary>
    public Dictionary<string, string> Files { get; } = [];

    public bool Offline { get; set; }
    public int Listings, NotModified;
    public List<string> Downloads { get; } = [];

    public static string Sha(string text) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline) throw new HttpRequestException("No such host is known.");
        var url = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        const string contents = "/repos/libretro/libretro-database/contents/metadat/";
        const string raw = "/libretro/libretro-database/master/metadat/";
        if (url.StartsWith(contents))
        {
            Listings++;
            var folder = url[contents.Length..];
            var items = Files.Where(f => f.Key.StartsWith(folder + "/"))
                .Select(f => new { name = f.Key[(folder.Length + 1)..] + ".dat", sha = Sha(f.Value) }).ToList();
            var json = JsonSerializer.Serialize(items);
            var etag = $"\"{Sha(json)}\"";
            if (request.Headers.TryGetValues("If-None-Match", out var tags) && tags.Contains(etag))
            {
                NotModified++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }
            var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            ok.Headers.TryAddWithoutValidation("ETag", etag);
            return Task.FromResult(ok);
        }
        if (url.StartsWith(raw) && url.EndsWith(".dat"))
        {
            var key = url[raw.Length..^4];
            if (Files.TryGetValue(key, out var text))
            {
                Downloads.Add(key);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
            }
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

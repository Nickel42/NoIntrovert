using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace NoIntrovert.Tests;

/// <summary>
/// Stands in for ES-DE's GitLab project in the ES-DE system tests: the
/// releases and commits APIs (with ETags and 304s) and the raw
/// es_systems.xml at <see cref="Tag"/>, counting what was asked for.
/// <see cref="Offline"/> makes every request fail as if there were no network.
/// </summary>
internal sealed class FakeGitLab : HttpMessageHandler
{
    public string Tag { get; set; } = "";
    public string Commit { get; set; } = "";
    public string Xml { get; set; } = "";

    public bool Offline { get; set; }
    public int Releases, Commits, NotModified, Downloads;

    /// <summary>An es_systems.xml with these systems, as ES-DE writes it.</summary>
    public static string XmlOf(IEnumerable<NoIntrovert.Core.Esde.EsdeSystem> systems) =>
        new XDocument(new XElement("systemList", systems.Select(s => new XElement("system",
            new XElement("name", s.Name), new XElement("fullname", s.FullName), new XElement("path", "%ROMPATH%/" + s.Name),
            new XElement("extension", string.Join(" ", s.Extensions.SelectMany(e => new[] { e, e.ToUpperInvariant() }))),
            new XElement("command", new XAttribute("label", "Test"), "%EMULATOR_RETROARCH% %ROM%"))))).ToString();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline) throw new HttpRequestException("No such host is known.");
        var url = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        var query = Uri.UnescapeDataString(request.RequestUri.Query);
        const string api = "/api/v4/projects/es-de/emulationstation-de/";
        if (url == api + "releases")
        {
            Releases++;
            return Json(request, JsonSerializer.Serialize(new[] { new { tag_name = Tag, name = "Version " + Tag } }));
        }
        if (url == api + "repository/commits" && query.Contains("path=resources/systems/unix/es_systems.xml") && query.Contains("ref_name=" + Tag))
        {
            Commits++;
            return Json(request, JsonSerializer.Serialize(new[] { new { id = Commit, title = "Changed es_systems.xml" } }));
        }
        if (url == $"/es-de/emulationstation-de/-/raw/{Tag}/resources/systems/unix/es_systems.xml")
        {
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Xml) });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private Task<HttpResponseMessage> Json(HttpRequestMessage request, string json)
    {
        var etag = $"W/\"{FakeGitHub.Sha(json)}\"";
        if (request.Headers.TryGetValues("If-None-Match", out var tags) && tags.Contains(etag))
        {
            NotModified++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }
        var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        ok.Headers.TryAddWithoutValidation("ETag", etag);
        return Task.FromResult(ok);
    }
}

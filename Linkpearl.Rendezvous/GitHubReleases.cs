using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Linkpearl.Rendezvous;

/// <summary>Les releases publiées sur GitHub.</summary>
/// <remarks>
/// « latest » ignore les pré-versions : une release en rc n'est jamais
/// installée automatiquement. Sans jeton, l'API accepte soixante requêtes par
/// heure et par adresse ; une ronde par heure en fait une.
/// </remarks>
public sealed partial class GitHubReleases(HttpClient http, string repository = "LinkPearl-Sync/linkpearl-sync-rendezvous")
    : IReleaseSource
{
    [GeneratedRegex("^v[0-9]+\\.[0-9]+\\.[0-9]+$")]
    private static partial Regex ReleaseTag();

    public async Task<string> LatestTagAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("lprdv", ReleaseVersion.Current?.ToString(3) ?? "0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var tag = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["tag_name"]?.GetValue<string>();

        // Le tag finit dans une URL : on n'y admet que la forme d'une version.
        return tag is not null && ReleaseTag().IsMatch(tag)
            ? tag
            : throw new HttpRequestException($"tag de release inattendu : {tag}");
    }

    public async Task<byte[]> DownloadAsync(string tag, string file, CancellationToken ct)
    {
        using var response = await http.GetAsync($"https://github.com/{repository}/releases/download/{tag}/{file}", ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}

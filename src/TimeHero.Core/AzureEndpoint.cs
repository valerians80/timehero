using System.Text.RegularExpressions;

namespace TimeHero.Core;

public static class AzureEndpoint
{
    /// <summary>
    /// Accetta sia l'endpoint della risorsa (https://nome.openai.azure.com) sia il "Target URI" copiato dalla pagina del
    /// deployment (…/openai/deployments/NOME/audio/transcriptions?api-version=…) e ne ricava endpoint, deployment e versione API.
    /// Il deployment e la versione presenti nell'URL hanno la precedenza su quelli scritti a parte.
    /// </summary>
    public static (string Endpoint, string Deployment, string ApiVersion) Normalize(
        string? endpoint, string? deployment, string? apiVersion)
    {
        var dep = (deployment ?? "").Trim();
        var ver = (apiVersion ?? "").Trim();
        var ep = (endpoint ?? "").Trim();

        if (Uri.TryCreate(ep, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            var m = Regex.Match(uri.AbsolutePath, @"/openai/deployments/([^/]+)", RegexOptions.IgnoreCase);
            if (m.Success) dep = Uri.UnescapeDataString(m.Groups[1].Value);

            var q = Regex.Match(uri.Query, @"[?&]api-version=([^&]+)", RegexOptions.IgnoreCase);
            if (q.Success) ver = Uri.UnescapeDataString(q.Groups[1].Value);

            ep = $"{uri.Scheme}://{uri.Authority}";
        }
        else
        {
            ep = ep.TrimEnd('/');
        }
        return (ep, dep, string.IsNullOrEmpty(ver) ? "2025-03-01-preview" : ver);
    }
}

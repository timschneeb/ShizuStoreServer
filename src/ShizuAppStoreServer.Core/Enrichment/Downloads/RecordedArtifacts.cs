using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.Enrichment.Downloads;

/// <summary>
/// The <c>sha256 url</c> entries AppEnricher remembers on a download row for
/// artifacts it analyzed but does not serve (flavor twins, older or
/// cross-source candidates). Shared with the release poll so change detection
/// counts the same URLs as known and stops force-enriching apps whose picked
/// asset already lives in that history.
/// </summary>
public static class RecordedArtifacts
{
    public static (string Sha256, string Url)? Parse(string entry)
    {
        var split = entry.IndexOf(' ');
        return split > 0 && split < entry.Length - 1 ? (entry[..split], entry[(split + 1)..]) : null;
    }

    public static IEnumerable<string> Urls(AppDownload row)
    {
        foreach (var entry in row.AnalyzedArtifacts)
        {
            if (Parse(entry) is { } parsed)
            {
                yield return parsed.Url;
            }
        }
    }
}

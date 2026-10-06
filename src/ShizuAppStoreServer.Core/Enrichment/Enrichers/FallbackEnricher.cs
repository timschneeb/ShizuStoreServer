using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment.Icons;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Enrichment.Enrichers;

internal sealed class FallbackEnricher(
    IEnrichmentPipeline pipeline,
    IPlayStoreClient? play,
    IconPipeline icons,
    ILogger<AppEnricher>? log = null)
{
    internal async Task<EnrichResult> EnrichFallbackAsync(App app, DateTimeOffset now, CancellationToken ct)
    {
        var kind = SourceClassifier.Classify(app.Url);
        app.SourceKind = kind;

        // Play listings carry the only metadata external-only apps have.
        var playDetails = kind == SourceKind.Play
            ? await FetchPlayDetailsAsync(app, ct)
            : null;
        ApplyPlayDetails(app, playDetails);

        // The real Play listing icon beats a generated avatar when linked.
        var hasIcon = playDetails?.IconUrl is { Length: > 0 } remoteIcon
            ? await icons.TryAdoptPlayIconAsync(app, remoteIcon, ct)
            : await icons.TryPlayIconAsync(app, now, ct);
        if (!hasIcon)
        {
            var avatar = LetterAvatarGenerator.Generate(app.DisplayName ?? app.Name);
            await icons.WriteIconFileAsync(avatar, ct);
            app.IconHash = avatar.Sha256;
            app.IconAdaptive = false;
        }

        if (kind == SourceKind.Play)
        {
            app.Availability = Availability.PlayRedirect;
            app.StoreUrl = app.Url;
            app.ExcludedReason = null;
        }
        else
        {
            app.Availability = Availability.LinkOnly;
            app.ExcludedReason = null;
        }

        app.LastCheckedAt = now;
        app.LastError = null;
        return new EnrichResult(EnrichOutcome.AvatarFallback, null);
    }

    /// <summary>
    /// Resolves the linked Play listing and returns its scraped details.
    /// Best effort: a missing page or an unparseable package id leaves the
    /// app untouched, so a Play redirect can still fall back to the avatar.
    /// </summary>
    private async Task<PlayAppDetails?> FetchPlayDetailsAsync(App app, CancellationToken ct)
    {
        if (play is null)
        {
            return null;
        }

        var packageId = string.Empty;
        if (!SourceClassifier.TryParsePlayPackage(app.Url, out packageId)
            && !SourceClassifier.TryParsePlayPackage(app.SourceUrl, out packageId)
            && !SourceClassifier.TryParsePlayPackage(app.StoreUrl, out packageId))
        {
            return null;
        }

        // The listing URL is the authoritative package identity for Play apps.
        app.PackageName = packageId;

        try
        {
            return await play.GetAppDetailsAsync(packageId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogDebug(ex, "Play details fetch failed for {Slug}.", app.Slug);
            return null;
        }
    }

    private void ApplyPlayDetails(App app, PlayAppDetails? details)
    {
        if (details is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(details.DeveloperName))
        {
            app.AuthorName = details.DeveloperName;
            app.AuthorUrl = details.DeveloperUrl;
            app.AuthorKey = string.IsNullOrWhiteSpace(details.DeveloperId)
                ? $"play:{details.DeveloperName.ToLowerInvariant()}"
                : $"play:{details.DeveloperId}";
        }

        if (!string.IsNullOrWhiteSpace(details.VersionName))
        {
            app.VersionName = details.VersionName;
        }

        // Play publishes a real last-update date, unlike a detection time.
        if (details.UpdatedAt is { } updated
            && (app.VersionUpdatedAt is null || updated > app.VersionUpdatedAt))
        {
            app.VersionUpdatedAt = updated;
        }

        if (!string.IsNullOrWhiteSpace(details.FullDescription))
        {
            app.FullDescription = details.FullDescription.Length > pipeline.MaxFullDescriptionChars
                ? details.FullDescription[..pipeline.MaxFullDescriptionChars]
                : details.FullDescription;
            // Play text is not markdown and has no raw route; drop a stale
            // README URL so clients never refetch the wrong document.
            app.ReadmeUrl = null;
        }
    }

    /// <summary>
    /// A source repo without an APK is not a dead end when the list entry
    /// points at a Play listing: run the normal source fallback so the app
    /// becomes a Play redirect instead of a bare link.
    /// </summary>
    internal async Task<EnrichResult?> TryPlayRedirectFallbackAsync(
        App app, DateTimeOffset now, CancellationToken ct)
    {
        if (SourceClassifier.Classify(app.Url) != SourceKind.Play)
        {
            return null;
        }

        return await EnrichFallbackAsync(app, now, ct);
    }
}

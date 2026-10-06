using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Overrides;

namespace ShizuAppStoreServer.Core.Sources;

/// <summary>
/// Database-backed <see cref="IAppSourceOverrides"/>: reads the active
/// <c>source_*</c> rows of <c>app_overrides</c> once per scope and answers by
/// slug. The applier clears the recheck window when a row changes, so a new or
/// edited row takes effect on the next enrich of that app. Rows with a
/// malformed value are logged and ignored, never fatal.
/// </summary>
public sealed class DbAppSourceOverrides(
    ShizuDbContext db,
    ILogger<DbAppSourceOverrides>? log = null) : IAppSourceOverrides
{
    // One read per scope: a pass can dispatch many apps, and the table only
    // changes through operator SQL between passes.
    private readonly Lazy<Snapshot> snapshot = new(() => Load(db, log));

    public (string Owner, string Repo)? RemapReleaseHome(string appSlug) =>
        snapshot.Value.ReleaseHomes.TryGetValue(appSlug, out var home) ? home : null;

    public GitCodeMirror? GitCodeMirrorFor(string appSlug) =>
        snapshot.Value.Mirrors.TryGetValue(appSlug, out var mirror) ? mirror : null;

    public bool ScansAllReleases(string appSlug) => snapshot.Value.ScanAll.Contains(appSlug);

    public bool PrefersPrerelease(string appSlug) => snapshot.Value.PreferPrerelease.Contains(appSlug);

    public bool SkipReleasePoll(string appSlug) =>
        GitCodeMirrorFor(appSlug) is not null || ScansAllReleases(appSlug);

    private static Snapshot Load(ShizuDbContext db, ILogger? log)
    {
        var snapshot = new Snapshot();
        // app_overrides is operator-edited and tiny; route every known source
        // kind and leave the rest to the field registry.
        foreach (var row in db.AppOverrides.AsNoTracking().Where(o => o.DeletedAt == null).ToList())
        {
            if (!SourceOverrideKinds.IsSource(row.Field))
            {
                continue;
            }
            if (SourceOverrideKinds.Validate(row.Field, row.Value) is { } error)
            {
                Warn(log, row, error);
                continue;
            }

            switch (row.Field)
            {
                case SourceOverrideKinds.ReleaseHome:
                    SourceOverrideKinds.TryParseReleaseHome(row.Value, out var home);
                    snapshot.ReleaseHomes[row.AppSlug] = home;
                    break;
                case SourceOverrideKinds.GitCodeMirror:
                    SourceOverrideKinds.TryParseGitCodeMirror(row.Value, out var mirror);
                    snapshot.Mirrors[row.AppSlug] = mirror;
                    break;
                case SourceOverrideKinds.ScanAllReleases:
                    SourceOverrideKinds.TryParseFlag(row.Value, out var scanAll);
                    if (scanAll)
                    {
                        snapshot.ScanAll.Add(row.AppSlug);
                    }
                    break;
                case SourceOverrideKinds.PreferPrerelease:
                    SourceOverrideKinds.TryParseFlag(row.Value, out var preferPrerelease);
                    if (preferPrerelease)
                    {
                        snapshot.PreferPrerelease.Add(row.AppSlug);
                    }
                    break;
            }
        }

        return snapshot;
    }

    private static void Warn(ILogger? log, AppOverride row, string reason) =>
        log?.LogWarning("Source override {Slug}/{Field} ignored: {Reason}", row.AppSlug, row.Field, reason);

    private sealed class Snapshot
    {
        public Dictionary<string, (string Owner, string Repo)> ReleaseHomes { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, GitCodeMirror> Mirrors { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ScanAll { get; } = new(StringComparer.Ordinal);

        public HashSet<string> PreferPrerelease { get; } = new(StringComparer.Ordinal);
    }
}

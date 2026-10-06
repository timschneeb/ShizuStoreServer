using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Core.Overrides;

/// <summary>
/// Materializes operator state onto the <c>apps</c> rows: per-field overrides
/// from <c>app_overrides</c> and the global screenshot blocklist. Enrichment
/// keeps rewriting most columns on every pass, so the sync runs this applier
/// after enrichment and before the Shizuku gate; it re-asserts each active
/// override and restores the captured baseline when an override row is
/// soft-deleted. Rows for unknown fields or failed parses are logged and
/// skipped, never fatal.
/// </summary>
public sealed class AppOverrideApplier(
    ShizuDbContext db,
    ILogger<AppOverrideApplier>? log = null)
{
    /// <summary>Exclusion reason for entries hidden by an override.</summary>
    public const string ExcludedReason = "Excluded by an override.";

    /// <summary>
    /// Applies all active overrides, finishes restores and purges blocked
    /// screenshot URLs. Returns the number of apps that needed a write; rows
    /// whose <c>apps</c> row is gone stay inert.
    /// </summary>
    public async Task<int> ApplyAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        // This applier shares the pass scope, whose earlier steps tracked app
        // rows before enrichment ran in other scopes. Detach the clean ones so
        // the reads below see what enrichment wrote, not the stale snapshot.
        foreach (var entry in db.ChangeTracker.Entries<App>().ToList())
        {
            if (entry.State == EntityState.Unchanged)
            {
                entry.State = EntityState.Detached;
            }
        }

        var overrides = await db.AppOverrides.ToListAsync(ct);
        var changed = 0;
        if (overrides.Count > 0)
        {
            var slugs = overrides.Select(o => o.AppSlug).Distinct().ToList();
            var apps = await db.Apps.Where(a => slugs.Contains(a.Slug)).ToListAsync(ct);
            var bySlug = apps.ToDictionary(a => a.Slug, StringComparer.Ordinal);

            foreach (var group in overrides.GroupBy(o => o.AppSlug, StringComparer.Ordinal))
            {
                if (!bySlug.TryGetValue(group.Key, out var app))
                {
                    continue;
                }
                if (ApplyApp(app, group.ToList(), now))
                {
                    changed++;
                }
            }
        }

        var purged = await PurgeBlockedScreenshotsAsync(now, ct);

        if (changed > 0 || purged > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return changed + purged;
    }

    private bool ApplyApp(App app, List<AppOverride> rows, DateTimeOffset now)
    {
        var dirty = false;

        // Restores run first: a row re-added for the same field then captures
        // the restored value as its new baseline.
        foreach (var row in rows.Where(r => r.DeletedAt is not null
            && !AppOverrideFields.IsVisibility(r.Field)
            && !SourceOverrideKinds.IsSource(r.Field)).ToList())
        {
            if (!AppOverrideFields.TryGet(row.Field, out var field) || row.BaselineValue is null)
            {
                Remove(row, ref dirty);
                continue;
            }
            if (field.Write(app, row.BaselineValue) is { } error)
            {
                Warn(row, error);
                continue;
            }
            if (field.Ownership == AppOverrideOwnership.Enrichment)
            {
                ForceRecheck(app, ref dirty);
            }
            Remove(row, ref dirty);
        }

        ApplyFields(app, rows, now, ref dirty);
        ApplyVisibility(app, rows, now, ref dirty);
        ApplySourceFields(app, rows, ref dirty);

        return dirty;
    }

    /// <summary>
    /// Source-kind rows steer the enrichment/poll seam instead of writing a
    /// column, so they are consume-once: a new or edited value clears the
    /// recheck window once and is marked applied, and a soft-deleted row is
    /// removed with one recheck when it had been consumed. The seam itself
    /// reads the row while it is active, so nothing here touches the app's
    /// value or the delta clock.
    /// </summary>
    private void ApplySourceFields(App app, List<AppOverride> rows, ref bool dirty)
    {
        foreach (var row in rows.Where(r => SourceOverrideKinds.IsSource(r.Field)))
        {
            if (SourceOverrideKinds.Validate(row.Field, row.Value) is { } error)
            {
                Warn(row, error);
                continue;
            }

            if (row.DeletedAt is not null)
            {
                if (row.AppliedValue is not null)
                {
                    ForceRecheck(app, ref dirty);
                }
                Remove(row, ref dirty);
                continue;
            }

            if (!string.Equals(row.AppliedValue, row.Value, StringComparison.Ordinal))
            {
                row.AppliedValue = row.Value;
                ForceRecheck(app, ref dirty);
            }
        }
    }

    private void ApplyFields(App app, List<AppOverride> rows, DateTimeOffset now, ref bool dirty)
    {
        foreach (var row in rows.Where(r => r.DeletedAt is null
            && !AppOverrideFields.IsVisibility(r.Field)
            && !SourceOverrideKinds.IsSource(r.Field)))
        {
            if (!AppOverrideFields.TryGet(row.Field, out var field))
            {
                Warn(row, "unknown field");
                continue;
            }

            var before = field.Read(app);
            if (field.Write(app, row.Value) is { } error)
            {
                Warn(row, error);
                continue;
            }

            if (row.BaselineValue is null)
            {
                row.BaselineValue = before;
                dirty = true;
            }

            if (row.AppliedValue is null)
            {
                row.AppliedValue = row.Value;
                dirty = true;
                if (!string.Equals(before, row.Value, StringComparison.Ordinal))
                {
                    app.UpdatedAt = now;
                }
            }
            else if (!string.Equals(row.AppliedValue, row.Value, StringComparison.Ordinal))
            {
                row.AppliedValue = row.Value;
                dirty = true;
                if (!string.Equals(before, row.Value, StringComparison.Ordinal))
                {
                    app.UpdatedAt = now;
                }
            }
            else if (!string.Equals(before, row.Value, StringComparison.Ordinal))
            {
                // Steady state after an enrichment drift: write the override
                // back without moving the clock. An untouched value stays a
                // no-op so the applier does not report phantom writes.
                KeepDeltaClock(app);
                dirty = true;
            }
        }
    }

    private void ApplyVisibility(App app, List<AppOverride> rows, DateTimeOffset now, ref bool dirty)
    {
        var activeAvailability = rows.FirstOrDefault(r => r.DeletedAt is null && r.Field == AppOverrideFields.AvailabilityName);
        var activeReason = rows.FirstOrDefault(r => r.DeletedAt is null && r.Field == AppOverrideFields.ExcludedReasonName);
        var restoreAvailability = rows.FirstOrDefault(r => r.DeletedAt is not null && r.Field == AppOverrideFields.AvailabilityName);
        var restoreReason = rows.FirstOrDefault(r => r.DeletedAt is not null && r.Field == AppOverrideFields.ExcludedReasonName);

        if (activeAvailability is null && activeReason is null
            && restoreAvailability is null && restoreReason is null)
        {
            return;
        }

        if (app.ExcludedReason is SyncService.UnlistedReason or SyncService.ArchivedReason)
        {
            // Unlist and archive outrank overrides; the rows stay pending.
            return;
        }

        if (activeAvailability is not null && activeAvailability.BaselineValue is null)
        {
            activeAvailability.BaselineValue = app.Availability.ToString();
            dirty = true;
        }
        if (activeReason is not null && activeReason.BaselineValue is null)
        {
            activeReason.BaselineValue = app.ExcludedReason ?? string.Empty;
            dirty = true;
        }

        // A restore materializes the captured baseline; an active row
        // materializes its value. An already-applied row writes back silently.
        var availabilityValue = activeAvailability?.Value ?? restoreAvailability?.BaselineValue;
        var reasonValue = activeReason?.Value ?? restoreReason?.BaselineValue;
        var steady = activeAvailability is not null
            && string.Equals(activeAvailability.AppliedValue, activeAvailability.Value, StringComparison.Ordinal)
            && (activeReason is null
                || string.Equals(activeReason.AppliedValue, activeReason.Value, StringComparison.Ordinal));

        Availability? desired = null;
        if (availabilityValue is not null)
        {
            if (AppOverrideFields.TryParseAvailability(availabilityValue, out var parsed))
            {
                desired = parsed;
            }
            else
            {
                Warn(activeAvailability ?? restoreAvailability!, "expected direct_apk, play_redirect, link_only or excluded");
            }
        }
        var desiredReason = string.IsNullOrEmpty(reasonValue) ? null : reasonValue;

        var touched = false;
        if (desired == Availability.Excluded)
        {
            if (app.Availability != Availability.Excluded)
            {
                app.Availability = Availability.Excluded;
                touched = true;
            }
            var reason = desiredReason ?? ExcludedReason;
            if (app.ExcludedReason != reason)
            {
                app.ExcludedReason = reason;
                touched = true;
            }
            if (touched)
            {
                dirty = true;
                if (!steady)
                {
                    app.UpdatedAt = now;
                }
            }
            EnsureTombstone(app, now, ref dirty);
        }
        else if (desired is { } visible)
        {
            var wasExcluded = app.Availability == Availability.Excluded;
            if (app.Availability != visible)
            {
                app.Availability = visible;
                touched = true;
            }
            if (app.ExcludedReason == ExcludedReason)
            {
                app.ExcludedReason = null;
                touched = true;
            }
            if (touched)
            {
                dirty = true;
                if (!steady)
                {
                    app.UpdatedAt = now;
                }
            }
            if (wasExcluded)
            {
                RemoveTombstone(app.Slug, ref dirty);
                ForceRecheck(app, ref dirty);
            }
        }
        else if (desiredReason is not null
            && app.Availability == Availability.Excluded
            && app.ExcludedReason != desiredReason)
        {
            // Reason-only override for a row excluded by another path.
            app.ExcludedReason = desiredReason;
            if (!steady)
            {
                app.UpdatedAt = now;
            }
            dirty = true;
        }

        if (activeAvailability is not null)
        {
            MarkApplied(activeAvailability, ref dirty);
        }
        if (activeReason is not null)
        {
            MarkApplied(activeReason, ref dirty);
        }
        if (restoreAvailability is not null)
        {
            Remove(restoreAvailability, ref dirty);
        }
        if (restoreReason is not null)
        {
            Remove(restoreReason, ref dirty);
        }
    }

    /// <summary>
    /// Removes active blocked URLs from stored screenshot lists so the column
    /// converges without waiting for a forced refresh. A purge is a served
    /// detail change, so it moves the delta clock explicitly.
    /// </summary>
    private async Task<int> PurgeBlockedScreenshotsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var blocked = await ScreenshotBlocklist.LoadAsync(db, ct);
        if (blocked.Count == 0)
        {
            return 0;
        }

        var purged = 0;
        foreach (var app in await db.Apps.ToListAsync(ct))
        {
            if (app.Screenshots.Count == 0 || !app.Screenshots.Any(url => ScreenshotBlocklist.IsBlocked(url, blocked)))
            {
                continue;
            }
            app.Screenshots = ScreenshotBlocklist.Filter(app.Screenshots, blocked);
            // The served detail changed; the change feed keys off updated_at.
            app.UpdatedAt = now;
            purged++;
        }
        return purged;
    }

    private void EnsureTombstone(App app, DateTimeOffset now, ref bool dirty)
    {
        if (FindTombstone(app.Slug) is not null)
        {
            return;
        }
        db.RemovedApps.Add(new RemovedApp
        {
            Slug = app.Slug,
            Name = app.Name,
            Listing = app.Listing,
            RemovedAt = now,
        });
        dirty = true;
    }

    private void RemoveTombstone(string slug, ref bool dirty)
    {
        if (FindTombstone(slug) is { } tombstone)
        {
            db.RemovedApps.Remove(tombstone);
            dirty = true;
        }
    }

    private RemovedApp? FindTombstone(string slug) =>
        db.RemovedApps.Local.FirstOrDefault(t => t.Slug == slug)
        ?? db.RemovedApps.FirstOrDefault(t => t.Slug == slug);

    private static void ForceRecheck(App app, ref bool dirty)
    {
        app.LastCheckedAt = null;
        app.LastError = null;
        dirty = true;
    }

    private static void MarkApplied(AppOverride row, ref bool dirty)
    {
        if (!string.Equals(row.AppliedValue, row.Value, StringComparison.Ordinal))
        {
            row.AppliedValue = row.Value;
            dirty = true;
        }
    }

    private void Remove(AppOverride row, ref bool dirty)
    {
        db.AppOverrides.Remove(row);
        dirty = true;
    }

    private void KeepDeltaClock(App app)
    {
        var entry = db.Entry(app);
        db.ChangeTracker.DetectChanges();
        if (entry.State == EntityState.Modified)
        {
            // Block BumpUpdatedAtForSummaryChanges: the served value is unchanged.
            entry.Property(nameof(App.UpdatedAt)).IsModified = true;
        }
    }

    private void Warn(AppOverride row, string message) =>
        log?.LogWarning("App override {Slug}/{Field} ignored: {Reason}", row.AppSlug, row.Field, message);
}

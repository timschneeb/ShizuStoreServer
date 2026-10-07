using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.History;
using ShizuAppStoreServer.Core.Overrides;
using ShizuAppStoreServer.Core.Parsing;
using ShizuAppStoreServer.Core.Sources;

namespace ShizuAppStoreServer.Core.Sync;

/// <summary>An identity conflict from one import pass that needs operator attention.</summary>
public sealed record CatalogWarning(string Rule, long? AppId, string Slug, string Message);

/// <summary>Row counts from one import pass.</summary>
public sealed record UpsertCounts(
    int Added, int Updated, int Removed, List<string> RemovedSlugs, List<CatalogWarning> Warnings);

/// <summary>
/// Imports parsed list documents into the catalog. Used by the initial
/// backfill and later by the scheduled fast loop.
///
/// Identity rules:
/// <list type="bullet">
/// <item>Categories are matched by <c>(section, name, parent)</c>; slugs are reused.</item>
/// <item>Entries are matched by <c>(listing, url, category)</c>. A matching URL
/// with a new name is a rename: the row keeps its id and slug.</item>
/// <item>The same URL may legitimately appear in several categories
/// (e.g. <c>fluffy</c>, <c>krude</c>); those stay separate rows.</item>
/// <item>An entry whose old <c>(url, category)</c> location vanished is treated
/// as a move: the row is reused, not deleted + recreated.</item>
/// <item>An entry whose URL changed outright is adopted onto the vanished row
/// when exactly one row in the listing matches by name, or by author plus
/// description; the row keeps its id, slug, stats and overrides.</item>
/// <item>When no adoption applies and the parsed slug is taken by a row whose
/// URL vanished, the existing row is kept and the new entry is skipped with a
/// <see cref="IssueKind.Catalog"/> warning. Slugs are only suffixed for
/// collisions inside one pass, category slugs, variant slugs and the parser's
/// per-document dedupe.</item>
/// <item>Rows whose <c>(url, category)</c> pair vanished from the parse are
/// hard-deleted, and a <see cref="RemovedApp"/> tombstone is written (or
/// refreshed) per deleted slug so <c>GET /v1/changes</c> can report
/// <c>removed[]</c>. Re-adding a deleted slug clears its tombstone. Deleting a
/// row with recorded installs raises a warning, because its stats cascade.</item>
/// <item>Only the <c>## Apps</c> section is ingested: Development libraries
/// and Miscellaneous content stay out of the catalog entirely, so rows
/// from an earlier import of those sections sweep out as stale.</item>
/// </list>
/// </summary>
public sealed class CatalogUpserter(ShizuDbContext db)
{
    /// <summary>Logical category location, for move-vs-duplicate detection.</summary>
    private readonly record struct Location(Listing Listing, string Url, CategorySection Section, string Name, string? Sub);

    public static CategorySection MapSection(string section) => section switch
    {
        "Development libraries" => CategorySection.Libraries,
        "Apps" or "Closed-source apps" => CategorySection.Apps,
        _ => CategorySection.Misc,
    };

    public static Listing MapListing(string listingName) =>
        listingName.Contains("closed", StringComparison.OrdinalIgnoreCase)
            ? Listing.ClosedSource
            : Listing.Main;

    public static AppType MapType(CategorySection section, string categoryName, string? subcategory) =>
        section == CategorySection.Libraries ? AppType.Library
        : categoryName.Contains("Flows for", StringComparison.Ordinal)
            || subcategory?.Contains("Flows for", StringComparison.Ordinal) == true ? AppType.Flow
        : AppType.App;

    public async Task<UpsertCounts> UpsertAsync(
        IEnumerable<ParsedDocument> documents,
        IReadOnlyDictionary<string, EntryHistory> history,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var docs = documents.ToList();
        var syncedListings = docs.Select(d => MapListing(d.ListingName)).ToHashSet();

        // The pass clock (`now`) is captured before fetch and history parsing,
        // so a client that syncs while this pass runs gets a cursor ahead of
        // it. New rows and tombstones carry the write clock instead, or they
        // would fall behind that cursor and never reach /v1/changes.
        var commitNow = DateTimeOffset.UtcNow;

        var categories = await db.Categories.ToListAsync(ct);
        var allApps = await db.Apps.ToListAsync(ct);
        // Extra packages of a multi-app repo are enrich-created children; they
        // share their root's URL and must never take part in list matching or
        // staling. Only the root rows are the list's rows.
        var apps = allApps.Where(a => a.RootAppId is null).ToList();
        var tombstones = await db.RemovedApps.ToListAsync(ct);
        // An active date override owns its column. Skipping the history refresh
        // for it keeps the operator value from flickering mid-pass and from
        // bumping updated_at through the summary interceptor every pass; the
        // applier re-asserts and restores it.
        var dateOverrides = await db.AppOverrides.AsNoTracking()
            .Where(o => o.Field == AppOverrideFields.AddedAtName || o.Field == AppOverrideFields.ListUpdatedAtName)
            .Select(o => new { o.AppSlug, o.Field, o.DeletedAt })
            .ToListAsync(ct);
        var addedAtProtected = dateOverrides
            .Where(o => o.DeletedAt == null && o.Field == AppOverrideFields.AddedAtName)
            .Select(o => o.AppSlug)
            .ToHashSet(StringComparer.Ordinal);
        var listUpdatedAtProtected = dateOverrides
            .Where(o => o.DeletedAt == null && o.Field == AppOverrideFields.ListUpdatedAtName)
            .Select(o => o.AppSlug)
            .ToHashSet(StringComparer.Ordinal);
        // A soft-deleted override hands the column back to history exactly, so
        // slugs that ever owned one must not go through the min/max guard
        // below or the removed operator value would stick forever.
        var addedAtRestorable = dateOverrides
            .Where(o => o.Field == AppOverrideFields.AddedAtName)
            .Select(o => o.AppSlug)
            .ToHashSet(StringComparer.Ordinal);
        var listUpdatedAtRestorable = dateOverrides
            .Where(o => o.Field == AppOverrideFields.ListUpdatedAtName)
            .Select(o => o.AppSlug)
            .ToHashSet(StringComparer.Ordinal);
        // Variants own slugs too, so root slug generation must avoid them.
        var usedSlugs = new HashSet<string>(
            categories.Select(c => c.Slug).Concat(allApps.Select(a => a.Slug)), StringComparer.Ordinal);

        // Id → category for rows loaded from the DB (navigations aren't included).
        var categoriesById = categories.Where(c => c.Id != 0).ToDictionary(c => c.Id);

        var added = 0;
        var updated = 0;
        var warnings = new List<CatalogWarning>();
        // (listing, url, categoryId) pairs present in the parse, anything else
        // stored under a synced listing is stale.
        var parsedKeys = new HashSet<(Listing, string, long)>();
        var parsedLocations = CollectLocations(docs);
        // URLs present in the parse (order independent), used to tell a row
        // that is leaving the list from one that is merely being adopted.
        var parsedUrls = CollectUrls(docs);
        // The source lists some apps under two categories. Keep the first
        // occurrence so the catalog holds one row per (listing, url); later
        // occurrences are left out and any existing duplicate rows go stale.
        var seenEntries = new HashSet<(Listing, string)>();

        foreach (var doc in docs)
        {
            var listing = MapListing(doc.ListingName);
            foreach (var parsed in doc.Categories)
            {
                // Only the Apps section is ingested; Development libraries
                // and Miscellaneous content (flows, CLI tools) are left out.
                if (MapSection(parsed.Section) != CategorySection.Apps)
                {
                    continue;
                }

                var category = FindOrCreateCategory(categories, usedSlugs, parsed);
                var type = MapType(category.Section, parsed.Name, parsed.Subcategory);
                foreach (var entry in parsed.Entries)
                {
                    UpsertEntry(apps, tombstones, categories, categoriesById, usedSlugs, parsedKeys, parsedLocations,
                        parsedUrls, seenEntries, warnings,
                        addedAtProtected, listUpdatedAtProtected, addedAtRestorable, listUpdatedAtRestorable,
                        listing, category, type, entry, parent: null, history, now, commitNow, ref added, ref updated);
                }
            }
        }

        var stale = apps
            .Where(a => syncedListings.Contains(a.Listing)
                && !parsedKeys.Contains((a.Listing, a.Url, EffectiveCategoryId(a, categories))))
            .ToList();
        var removedSlugs = stale.Select(a => a.Slug).ToList();
        foreach (var app in stale.Where(a => a.Id != 0 && a.InstallCount > 0))
        {
            // The row and its daily/version stats cascade away with the delete,
            // so an entry leaving the list silently loses its install history.
            // The id is gone with the row, so the issue carries only the slug
            // (an app_id FK to a deleted row would reject the snapshot insert).
            warnings.Add(new CatalogWarning(
                "stale_with_installs", null, app.Slug,
                $"Deleted '{app.Name}' ({app.Url}) with {app.InstallCount} recorded installs; the entry left the list."));
        }

        WriteTombstones(tombstones, stale, commitNow);

        // Variants cascade with their root at the DB level, but each owns a
        // client row that only a tombstone can delete; without one the stale
        // child lingers forever and /v1/changes never reports it.
        var staleRootIds = stale.Where(a => a.Id != 0).Select(a => a.Id).ToHashSet();
        WriteTombstones(
            tombstones,
            allApps.Where(a => a.RootAppId is { } rootId && staleRootIds.Contains(rootId)).ToList(),
            commitNow);

        db.Apps.RemoveRange(stale);

        await db.SaveChangesAsync(ct);
        return new UpsertCounts(added, updated, stale.Count, removedSlugs, warnings);
    }

    /// <summary>
    /// Records one tombstone per deleted row (refreshing any existing row
    /// for the slug) so <c>GET /v1/changes removed[]</c> stays correct.
    /// </summary>
    private void WriteTombstones(List<RemovedApp> tombstones, List<App> stale, DateTimeOffset removedAt)
    {
        foreach (var app in stale)
        {
            var existing = tombstones.FirstOrDefault(t => t.Slug == app.Slug);
            if (existing is null)
            {
                existing = new RemovedApp { Slug = app.Slug };
                tombstones.Add(existing);
                db.RemovedApps.Add(existing);
            }

            existing.Name = app.Name;
            existing.Listing = app.Listing;
            existing.RemovedAt = removedAt;
        }
    }

    private static HashSet<Location> CollectLocations(List<ParsedDocument> docs)
    {
        var set = new HashSet<Location>();
        foreach (var doc in docs)
        {
            var listing = MapListing(doc.ListingName);
            foreach (var cat in doc.Categories)
            {
                var section = MapSection(cat.Section);
                if (section != CategorySection.Apps)
                {
                    continue;
                }

                CollectEntries(set, listing, section, cat.Name, cat.Subcategory, cat.Entries);
            }
        }

        return set;
    }

    private static HashSet<(Listing, string)> CollectUrls(List<ParsedDocument> docs)
    {
        var set = new HashSet<(Listing, string)>();
        foreach (var doc in docs)
        {
            var listing = MapListing(doc.ListingName);
            foreach (var cat in doc.Categories)
            {
                if (MapSection(cat.Section) != CategorySection.Apps)
                {
                    continue;
                }

                CollectUrls(set, listing, cat.Entries);
            }
        }

        return set;
    }

    private static void CollectUrls(HashSet<(Listing, string)> set, Listing listing, List<ParsedEntry> entries)
    {
        foreach (var e in entries)
        {
            set.Add((listing, e.Url));
            CollectUrls(set, listing, e.Children);
        }
    }

    private static void CollectEntries(
        HashSet<Location> set, Listing listing, CategorySection section,
        string name, string? sub, List<ParsedEntry> entries)
    {
        foreach (var e in entries)
        {
            set.Add(new Location(listing, e.Url, section, name, sub));
            CollectEntries(set, listing, section, name, sub, e.Children);
        }
    }

    /// <summary>
    /// Category id usable before <c>SaveChanges</c>: tracked <c>Added</c> rows
    /// still carry temporary keys, so resolve those via the tracked instance.
    /// Must stay consistent with <see cref="ParsedCategoryKey"/>.
    /// </summary>
    private static long EffectiveCategoryId(App app, List<Category> categories)
    {
        if (app.Category is not null)
        {
            var idx = categories.IndexOf(app.Category);
            if (idx >= 0 && categories[idx].Id != 0)
            {
                return categories[idx].Id;
            }

            return app.CategoryId != 0 ? app.CategoryId : ParsedCategoryKey(app.Category);
        }

        return app.CategoryId;
    }

    private static long ParsedCategoryKey(Category category) =>
        category.Id != 0 ? category.Id : category.GetHashCode();

    private Category FindOrCreateCategory(
        List<Category> categories, HashSet<string> usedSlugs, ParsedCategory parsed)
    {
        var section = MapSection(parsed.Section);

        Category? parent = null;
        if (parsed.Subcategory is not null)
        {
            parent = categories.FirstOrDefault(c =>
                    c.Section == section && c.Name == parsed.Name && IsSameCategoryParent(c, null))
                ?? CreateCategory(categories, usedSlugs, section, parsed.Name, parsed.Name, parent: null);
        }

        var name = parsed.Subcategory ?? parsed.Name;
        return categories.FirstOrDefault(c =>
                c.Section == section && c.Name == name && IsSameCategoryParent(c, parent))
            ?? CreateCategory(categories, usedSlugs, section, name, parsed.Slug, parent);
    }

    /// <summary>
    /// Parent comparison that works for persisted rows (real ids) and for
    /// rows created earlier in this same pass (reference equality, since
    /// temporary keys are all zero).
    /// </summary>
    private static bool IsSameCategoryParent(Category candidate, Category? parent) =>
        ReferenceEquals(candidate.Parent, parent)
        || (parent is null ? candidate.ParentId is null
            : parent.Id != 0 && candidate.ParentId == parent.Id);

    private Category CreateCategory(
        List<Category> categories, HashSet<string> usedSlugs,
        CategorySection section, string name, string slugBase, Category? parent)
    {
        var category = new Category
        {
            Name = name,
            Slug = UniqueSlug(usedSlugs, Slug.Slugify(slugBase)),
            Section = section,
            Parent = parent,
        };
        categories.Add(category);
        db.Categories.Add(category);
        return category;
    }

    private void UpsertEntry(
        List<App> apps, List<RemovedApp> tombstones, List<Category> categories, Dictionary<long, Category> categoriesById,
        HashSet<string> usedSlugs,
        HashSet<(Listing, string, long)> parsedKeys, HashSet<Location> parsedLocations,
        HashSet<(Listing, string)> parsedUrls, HashSet<(Listing, string)> seenEntries,
        List<CatalogWarning> warnings,
        HashSet<string> addedAtProtected, HashSet<string> listUpdatedAtProtected,
        HashSet<string> addedAtRestorable, HashSet<string> listUpdatedAtRestorable,
        Listing listing, Category category, AppType type,
        ParsedEntry entry, App? parent,
        IReadOnlyDictionary<string, EntryHistory> history,
        DateTimeOffset now, DateTimeOffset commitNow, ref int added, ref int updated)
    {
        if (!seenEntries.Add((listing, entry.Url)))
        {
            return;
        }
        var inCategory = apps
            .Where(a => a.Listing == listing && a.Url == entry.Url && IsSameCategory(a, category))
            .ToList();
        App? match = inCategory.Count switch
        {
            // Same URL listed twice under one category shouldn't happen, but
            // prefer the same-name row instead of crashing if it does.
            > 1 => inCategory.FirstOrDefault(a => a.Name == entry.Name) ?? inCategory[0],
            1 => inCategory[0],
            _ => FindMoveTarget(apps, categoriesById, parsedLocations, listing, category, entry),
        };

        history.TryGetValue(entry.Url, out var h);

        // URL-change adoption runs before the clash guard: a rename that also
        // moved the repo keeps the row, its slug, its stats and its overrides.
        var adoptionMatches = 0;
        if (match is null)
        {
            var (adopted, matched) = FindAdoptionTarget(apps, parsedUrls, listing, entry);
            match = adopted;
            adoptionMatches = matched;
        }

        if (match is null)
        {
            var clash = FindSlugClash(apps, parsedUrls, entry.Slug);
            if (clash is not null)
            {
                // Keep the row that owns the slug instead of forking the app
                // into a suffixed duplicate nobody would find. Sparing it from
                // the stale sweep keeps its stats and overrides; the operator
                // decides the outcome.
                parsedKeys.Add((clash.Listing, clash.Url, EffectiveCategoryId(clash, categories)));
                warnings.Add(new CatalogWarning(
                    "slug_clash", clash.Id, clash.Slug,
                    $"New entry '{entry.Name}' ({entry.Url}) wants slug '{entry.Slug}' " +
                    $"held by '{clash.Name}' ({clash.Url}); kept the existing entry."));
                return;
            }

            if (adoptionMatches > 1)
            {
                warnings.Add(new CatalogWarning(
                    "ambiguous_identity", null, entry.Slug,
                    $"Entry '{entry.Name}' ({entry.Url}) matches {adoptionMatches} rows that left the list; imported it as a new entry."));
            }

            match = new App
            {
                Name = entry.Name,
                Slug = UniqueSlug(usedSlugs, entry.Slug),
                Url = entry.Url,
                Description = entry.Description,
                License = entry.License,
                Listing = listing,
                Type = type,
                IsRecommended = entry.IsRecommended,
                HasPaid = entry.HasPaid,
                HasIap = entry.HasIap,
                HasAds = entry.HasAds,
                TrialDays = entry.TrialDays,
                RequiresRoot = entry.RequiresRoot,
                SourceUrl = entry.SourceUrl,
                Category = category,
                Parent = parent,
                AddedAt = h?.AddedAt ?? now,
                ListUpdatedAt = h?.UpdatedAt,
                // Change clock, so the write time: a first-seen row must be
                // newer than any cursor already handed to a client.
                UpdatedAt = commitNow,
            };
            apps.Add(match);
            db.Apps.Add(match);
            added++;

            // Resurrection: the slug is live again, so a tombstone from an
            // earlier deletion no longer applies (otherwise /v1/changes would
            // report the slug as both removed and added).
            var tombstone = tombstones.FirstOrDefault(t => t.Slug == match.Slug);
            if (tombstone is not null)
            {
                tombstones.Remove(tombstone);
                db.RemovedApps.Remove(tombstone);
            }
        }
        else
        {
            var changed = match.Name != entry.Name
                || match.Url != entry.Url
                || match.Description != entry.Description
                || match.License != entry.License
                || match.SourceUrl != entry.SourceUrl
                || match.Type != type
                || match.IsRecommended != entry.IsRecommended
                || match.HasPaid != entry.HasPaid
                || match.HasIap != entry.HasIap
                || match.HasAds != entry.HasAds
                || match.TrialDays != entry.TrialDays
                || match.RequiresRoot != entry.RequiresRoot
                || !IsSameCategory(match, category)
                || !IsSameAppParent(match, parent);

            match.Name = entry.Name;
            match.Url = entry.Url;
            match.Description = entry.Description;
            match.License = entry.License;
            match.SourceUrl = entry.SourceUrl;
            match.Type = type;
            match.IsRecommended = entry.IsRecommended;
            match.HasPaid = entry.HasPaid;
            match.HasIap = entry.HasIap;
            match.HasAds = entry.HasAds;
            match.TrialDays = entry.TrialDays;
            match.RequiresRoot = entry.RequiresRoot;
            match.Category = category;
            match.Parent = parent;

            if (changed)
            {
                var ts = h?.UpdatedAt ?? commitNow;
                if (ts > match.UpdatedAt)
                {
                    match.UpdatedAt = ts;
                }

                updated++;
            }

            // History is authoritative for both the introduction and the
            // list-change dates (silent commits already filtered out), and
            // refreshes them even when the parsed entry itself is unchanged.
            // An active override wins instead: the applier owns that column.
            // A removed override restores the exact history date; otherwise
            // min/max keep an adopted row's original dates when the new URL's
            // history starts later (added) or keeps an older last edit.
            if (h is not null)
            {
                if (!addedAtProtected.Contains(match.Slug))
                {
                    match.AddedAt = addedAtRestorable.Contains(match.Slug) || h.AddedAt < match.AddedAt
                        ? h.AddedAt
                        : match.AddedAt;
                }

                if (!listUpdatedAtProtected.Contains(match.Slug))
                {
                    match.ListUpdatedAt = listUpdatedAtRestorable.Contains(match.Slug)
                        ? h.UpdatedAt
                        : match.ListUpdatedAt is { } existing && existing > h.UpdatedAt
                            ? existing
                            : h.UpdatedAt;
                }
            }
        }

        parsedKeys.Add((listing, match.Url, ParsedCategoryKey(category)));

        foreach (var child in entry.Children)
        {
            UpsertEntry(apps, tombstones, categories, categoriesById, usedSlugs, parsedKeys, parsedLocations,
                parsedUrls, seenEntries, warnings,
                addedAtProtected, listUpdatedAtProtected, addedAtRestorable, listUpdatedAtRestorable,
                listing, category, type, child, parent: match, history, now, commitNow, ref added, ref updated);
        }
    }

    /// <summary>
    /// URL-change adoption: exactly one root row in this listing lost its URL
    /// from the parse and matches the entry by name, or by author plus
    /// description (a rename that also moved the repo). Zero matches means a
    /// genuinely new entry; several matches stay untouched and are reported.
    /// </summary>
    private static (App? Target, int Matches) FindAdoptionTarget(
        List<App> apps, HashSet<(Listing, string)> parsedUrls, Listing listing, ParsedEntry entry)
    {
        var candidates = apps
            .Where(a => a.Id != 0 && a.Listing == listing && !parsedUrls.Contains((a.Listing, a.Url)))
            .Where(a => MatchesEntry(a, entry))
            .ToList();
        return candidates.Count == 1 ? (candidates[0], 1) : (null, candidates.Count);
    }

    private static bool MatchesEntry(App app, ParsedEntry entry)
    {
        if (string.Equals(app.Name.Trim(), entry.Name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The author key is set by enrichment, so only enriched rows can match
        // this way; an identical description keeps the false-positive rate low
        // (cebian -> XGesture kept both name-independent columns).
        return app.AuthorKey is not null
            && app.AuthorKey == DeriveAuthorKey(entry.Url)
            && string.Equals(app.Description, entry.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// Author identity derived from an entry URL, mirroring the enrichment
    /// services so a pre-change row compares equal to its new URL.
    /// </summary>
    private static string? DeriveAuthorKey(string? url)
    {
        if (SourceClassifier.TryParseGitHubRepo(url, out var owner, out _))
        {
            return $"github:{owner.ToLowerInvariant()}";
        }

        if (SourceClassifier.TryParseGitLabRepo(url, out var projectPath))
        {
            var group = projectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return group is null ? null : $"gitlab:{group.ToLowerInvariant()}";
        }

        return null;
    }

    /// <summary>
    /// A live root row that owns the parsed slug while its own URL left the
    /// list: inserting the entry under a suffixed slug would bury the row that
    /// carries the install history, so it is kept and the entry is reported.
    /// </summary>
    private static App? FindSlugClash(List<App> apps, HashSet<(Listing, string)> parsedUrls, string slug) =>
        apps.FirstOrDefault(a => a.Id != 0 && a.Slug == slug && !parsedUrls.Contains((a.Listing, a.Url)));

    /// <summary>
    /// Move reuse: exactly one row carries this URL elsewhere in the listing
    /// <i>and</i> its old location is gone from the parse. When the old
    /// location is still listed the entry is a deliberate duplicate, return
    /// null so a new row is created instead of stealing the existing one.
    /// </summary>
    private static App? FindMoveTarget(
        List<App> apps, Dictionary<long, Category> categoriesById,
        HashSet<Location> parsedLocations,
        Listing listing, Category category, ParsedEntry entry)
    {
        var sameUrl = apps.Where(a => a.Listing == listing && a.Url == entry.Url).ToList();
        if (sameUrl.Count != 1)
        {
            return null;
        }

        var row = sameUrl[0];
        var rowCategory = row.Category ?? (categoriesById.TryGetValue(row.CategoryId, out var c) ? c : null);
        if (rowCategory is null)
        {
            return row; // Orphaned row; adopt it rather than duplicating.
        }

        if (!TryLocationKey(rowCategory, categoriesById, out var key))
        {
            return null; // Uncertain, never steal, create a new row.
        }

        var section = rowCategory.Section;
        return parsedLocations.Contains(new Location(listing, entry.Url, section, key.Name, key.Sub))
            ? null
            : row;
    }

    private static bool TryLocationKey(
        Category category, Dictionary<long, Category> byId, out (string Name, string? Sub) key)
    {
        if (category.Parent is not null)
        {
            key = (category.Parent.Name, category.Name);
            return true;
        }

        if (category.ParentId is null)
        {
            key = (category.Name, null);
            return true;
        }

        if (byId.TryGetValue(category.ParentId.Value, out var parent))
        {
            key = (parent.Name, category.Name);
            return true;
        }

        key = default;
        return false;
    }

    private static bool IsSameCategory(App app, Category category) =>
        ReferenceEquals(app.Category, category)
        || (category.Id != 0 && app.CategoryId == category.Id);

    private static bool IsSameAppParent(App app, App? parent) =>
        ReferenceEquals(app.Parent, parent)
        || (parent is null ? app.ParentId is null
            : parent.Id != 0 && app.ParentId == parent.Id);

    private static string UniqueSlug(HashSet<string> usedSlugs, string candidate)
    {
        if (usedSlugs.Add(candidate))
        {
            return candidate;
        }

        var i = 2;
        while (!usedSlugs.Add($"{candidate}-{i}"))
        {
            i++;
        }

        return $"{candidate}-{i}";
    }
}

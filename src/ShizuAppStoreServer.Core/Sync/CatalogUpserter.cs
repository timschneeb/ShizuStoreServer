using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.History;
using ShizuAppStoreServer.Core.Parsing;

namespace ShizuAppStoreServer.Core.Sync;

/// <summary>Row counts from one import pass.</summary>
public sealed record UpsertCounts(int Added, int Updated, int Removed, List<string> RemovedSlugs);

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
/// <item>Rows whose <c>(url, category)</c> pair vanished from the parse are
/// hard-deleted, and a <see cref="RemovedApp"/> tombstone is written (or
/// refreshed) per deleted slug so <c>GET /v1/changes</c> can report
/// <c>removed[]</c>. Re-adding a deleted slug clears its tombstone.</item>
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

        var categories = await db.Categories.ToListAsync(ct);
        var allApps = await db.Apps.ToListAsync(ct);
        // Extra packages of a multi-app repo are enrich-created children; they
        // share their root's URL and must never take part in list matching or
        // staling. Only the root rows are the list's rows.
        var apps = allApps.Where(a => a.RootAppId is null).ToList();
        var tombstones = await db.RemovedApps.ToListAsync(ct);
        // Variants own slugs too, so root slug generation must avoid them.
        var usedSlugs = new HashSet<string>(
            categories.Select(c => c.Slug).Concat(allApps.Select(a => a.Slug)), StringComparer.Ordinal);

        // Id → category for rows loaded from the DB (navigations aren't included).
        var categoriesById = categories.Where(c => c.Id != 0).ToDictionary(c => c.Id);

        var added = 0;
        var updated = 0;
        // (listing, url, categoryId) pairs present in the parse, anything else
        // stored under a synced listing is stale.
        var parsedKeys = new HashSet<(Listing, string, long)>();
        var parsedLocations = CollectLocations(docs);
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
                    UpsertEntry(apps, tombstones, categoriesById, usedSlugs, parsedKeys, parsedLocations, seenEntries,
                        listing, category, type, entry, parent: null, history, now, ref added, ref updated);
                }
            }
        }

        var stale = apps
            .Where(a => syncedListings.Contains(a.Listing)
                && !parsedKeys.Contains((a.Listing, a.Url, EffectiveCategoryId(a, categories))))
            .ToList();
        var removedSlugs = stale.Select(a => a.Slug).ToList();
        WriteTombstones(tombstones, stale, now);
        db.Apps.RemoveRange(stale);

        await db.SaveChangesAsync(ct);
        return new UpsertCounts(added, updated, stale.Count, removedSlugs);
    }

    /// <summary>
    /// Records one tombstone per deleted row (refreshing any existing row
    /// for the slug) so <c>GET /v1/changes removed[]</c> stays correct.
    /// </summary>
    private void WriteTombstones(List<RemovedApp> tombstones, List<App> stale, DateTimeOffset now)
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
            existing.RemovedAt = now;
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
        List<App> apps, List<RemovedApp> tombstones, Dictionary<long, Category> categoriesById, HashSet<string> usedSlugs,
        HashSet<(Listing, string, long)> parsedKeys, HashSet<Location> parsedLocations,
        HashSet<(Listing, string)> seenEntries,
        Listing listing, Category category, AppType type,
        ParsedEntry entry, App? parent,
        IReadOnlyDictionary<string, EntryHistory> history,
        DateTimeOffset now, ref int added, ref int updated)
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

        if (match is null)
        {
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
                UpdatedAt = h?.UpdatedAt ?? now,
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
                var ts = h?.UpdatedAt ?? now;
                if (ts > match.UpdatedAt)
                {
                    match.UpdatedAt = ts;
                }

                updated++;
            }

            // History is authoritative for both the introduction and the
            // list-change dates (silent commits already filtered out), and
            // refreshes them even when the parsed entry itself is unchanged.
            if (h is not null)
            {
                match.AddedAt = h.AddedAt;
                match.ListUpdatedAt = h.UpdatedAt;
            }
        }

        parsedKeys.Add((listing, match.Url, ParsedCategoryKey(category)));

        foreach (var child in entry.Children)
        {
            UpsertEntry(apps, tombstones, categoriesById, usedSlugs, parsedKeys, parsedLocations, seenEntries,
                listing, category, type, child, parent: match, history, now, ref added, ref updated);
        }
    }

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

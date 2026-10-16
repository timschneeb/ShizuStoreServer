using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.History;
using ShizuAppStoreServer.Core.Parsing;
using ShizuAppStoreServer.Core.Sync;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Upserter tests run against SQLite in-memory. No local Postgres server is
/// available on this machine (postgresql-libs only, docker daemon down), so
/// live-DB verification is deferred to the server; the migration SQL itself
/// is validated via <c>dotnet ef migrations script</c>.
/// </summary>
public sealed class CatalogUpserterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ShizuDbContext _db;

    private static readonly DateTimeOffset T0 = new(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public CatalogUpserterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new ShizuDbContext(new DbContextOptionsBuilder<ShizuDbContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private const string MainList = """
        ## Apps

        ### Audio

        * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time mic `MIT`
        * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
          * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

        ### Vendor-specific

        #### MIUI

        * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
        """;

    private static ParsedDocument ParseMain(string md = MainList) =>
        new AwesomeListParser().Parse(md, "main");

    private CatalogUpserter Upserter() => new(_db);

    [Fact]
    public async Task ImportsCategoriesAppsAndChildrenWithHistory()
    {
        var history = new Dictionary<string, EntryHistory>
        {
            ["https://github.com/papergray/MicUp"] = new(T0, T1),
        };

        var before = DateTimeOffset.UtcNow;
        var counts = await Upserter().UpsertAsync([ParseMain()], history, T1);

        Assert.Equal(4, counts.Added); // MicUp, Tuner, Tuner Beta, Aura
        Assert.Equal(0, counts.Updated);
        Assert.Equal(0, counts.Removed);

        var micUp = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal("MicUp", micUp.Name);
        Assert.Equal(Listing.Main, micUp.Listing);
        Assert.Equal(AppType.App, micUp.Type);
        Assert.True(micUp.IsRecommended);
        Assert.Equal(T0, micUp.AddedAt);
        // Change clock is the write time, so a client that synced mid-pass
        // still sees the row; the git date stays on ListUpdatedAt.
        Assert.True(micUp.UpdatedAt >= before);
        Assert.Equal(T1, micUp.ListUpdatedAt);
        Assert.Equal(Availability.LinkOnly, micUp.Availability);
        Assert.Equal(SourceKind.Other, micUp.SourceKind);

        // Entry without history falls back to now.
        var tuner = await _db.Apps.SingleAsync(a => a.Slug == "tuner");
        Assert.Equal(T1, tuner.AddedAt);
        Assert.Null(tuner.ListUpdatedAt);

        // Nested bullet → child row.
        var beta = await _db.Apps.SingleAsync(a => a.Slug == "tuner-beta");
        Assert.Equal(tuner.Id, beta.ParentId);

        // Subcategory → child category with a parent.
        var miui = await _db.Categories.SingleAsync(c => c.Name == "MIUI");
        Assert.NotNull(miui.ParentId);
        var vendor = await _db.Categories.SingleAsync(c => c.Id == miui.ParentId);
        Assert.Equal("Vendor-specific", vendor.Name);
        var aura = await _db.Apps.SingleAsync(a => a.Slug == "aura");
        Assert.Equal(miui.Id, aura.CategoryId);
    }

    [Fact]
    public async Task FirstSeenRowsCarryTheWriteClockNotThePassClock()
    {
        // The pass clock predates the upsert, so a client that synced while
        // the pass ran holds a cursor past it. These rows must still be newer
        // than that cursor or /v1/changes would never report them.
        var passClock = DateTimeOffset.UtcNow.AddMinutes(-10);
        var before = DateTimeOffset.UtcNow;

        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), passClock);

        foreach (var app in await _db.Apps.ToListAsync())
        {
            Assert.True(app.UpdatedAt >= before, $"'{app.Slug}' predates the write clock.");
            Assert.True(app.UpdatedAt > passClock);
        }
    }

    [Fact]
    public async Task ReimportIsIdempotent()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var counts = await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal(0, counts.Added);
        Assert.Equal(0, counts.Updated);
        Assert.Equal(0, counts.Removed);
        Assert.Equal(4, await _db.Apps.CountAsync());
    }

    [Fact]
    public async Task ActiveDateOverridesSurviveTheHistoryRefresh()
    {
        var history = new Dictionary<string, EntryHistory>
        {
            ["https://github.com/papergray/MicUp"] = new(T0, T1),
        };
        await Upserter().UpsertAsync([ParseMain()], history, T1);

        var overridden = T0.AddDays(-5);
        var micUp = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        micUp.AddedAt = overridden;
        micUp.ListUpdatedAt = overridden;
        foreach (var field in new[] { "added_at", "list_updated_at" })
        {
            _db.AppOverrides.Add(new AppOverride
            {
                AppSlug = "micup",
                Field = field,
                Value = overridden.ToString("O"),
                CreatedAt = T1,
                UpdatedAt = T1,
            });
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var clock = (await _db.Apps.SingleAsync(a => a.Slug == "micup")).UpdatedAt;

        // The history refresh must leave an overridden date alone: rewriting
        // it would flicker the value mid-pass and bump the delta clock.
        await Upserter().UpsertAsync([ParseMain()], history, T1);

        _db.ChangeTracker.Clear();
        micUp = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(overridden, micUp.AddedAt);
        Assert.Equal(overridden, micUp.ListUpdatedAt);
        Assert.Equal(clock, micUp.UpdatedAt);

        // Soft-deleting the rows hands the columns back to history.
        foreach (var row in await _db.AppOverrides.ToListAsync())
        {
            row.DeletedAt = T1;
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        await Upserter().UpsertAsync([ParseMain()], history, T1);

        _db.ChangeTracker.Clear();
        micUp = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(T0, micUp.AddedAt);
        Assert.Equal(T1, micUp.ListUpdatedAt);
    }

    [Fact]
    public async Task RenameKeepsIdAndSlug()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var before = await _db.Apps.SingleAsync(a => a.Slug == "micup");

        var renamed = ParseMain(MainList.Replace("[MicUp]", "[MicUp Pro]"));
        var counts = await Upserter().UpsertAsync([renamed], new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal(0, counts.Added);
        Assert.Equal(1, counts.Updated);
        var after = await _db.Apps.SingleAsync(a => a.Url == "https://github.com/papergray/MicUp");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("micup", after.Slug);
        Assert.Equal("MicUp Pro", after.Name);
    }

    [Fact]
    public async Task UrlChangeAdoptsTheVanishingRow()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var before = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        before.InstallCount = 42;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        const string moved = """
            ## Apps

            ### Audio

            * [MicUp](https://github.com/papergray/MicUpNext) ✨ - Real-time mic `MIT`
            * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
              * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

            ### Vendor-specific

            #### MIUI

            * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
            """;
        var counts = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(moved, "main")], new Dictionary<string, EntryHistory>(), T1);

        // The new URL adopts the old row instead of forking it, so the id,
        // slug and install count survive the move.
        Assert.Equal(0, counts.Added);
        Assert.Equal(1, counts.Updated);
        Assert.Equal(0, counts.Removed);
        Assert.Empty(counts.Warnings);
        var after = await _db.Apps.SingleAsync(a => a.Url == "https://github.com/papergray/MicUpNext");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("micup", after.Slug);
        Assert.Equal(42, after.InstallCount);
        Assert.Empty(await _db.RemovedApps.ToListAsync());

        // A later non-silent edit of the new URL must not re-date the row:
        // the adopted row keeps its original added date.
        var later = new Dictionary<string, EntryHistory>
        {
            ["https://github.com/papergray/MicUpNext"] = new(T1, T1),
        };
        await Upserter().UpsertAsync([new AwesomeListParser().Parse(moved, "main")], later, T1);

        _db.ChangeTracker.Clear();
        after = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(before.AddedAt, after.AddedAt);
    }

    [Fact]
    public async Task RenamedEntryWithNewUrlAdoptsByAuthorAndDescription()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var before = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        before.AuthorKey = "github:papergray";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Name and URL both change; the author key and description still
        // identify the same app, so the row is adopted in place.
        const string renamed = """
            ## Apps

            ### Audio

            * [MicUp Next](https://github.com/papergray/micup-next) - Real-time mic `MIT`

            ### Vendor-specific

            #### MIUI

            * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
            """;
        var counts = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(renamed, "main")], new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal(0, counts.Added);
        Assert.Equal(1, counts.Updated);
        Assert.Empty(counts.Warnings);
        var after = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("MicUp Next", after.Name);
        Assert.Equal("https://github.com/papergray/micup-next", after.Url);
    }

    [Fact]
    public async Task AmbiguousAdoptionDeclinesAndWarns()
    {
        const string seed = """
            ## Apps

            ### Audio

            * [MicUp One](https://github.com/papergray/one) - Shared text `MIT`
            * [MicUp Two](https://github.com/papergray/one-alt) - Shared text `MIT`
            """;
        await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(seed, "main")], new Dictionary<string, EntryHistory>(), T0);
        foreach (var row in await _db.Apps.ToListAsync())
        {
            row.AuthorKey = "github:papergray";
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Two vanishing rows match the new entry (author plus description),
        // so the upserter must not guess: it imports a new row and warns.
        const string entry = """
            ## Apps

            ### Audio

            * [Tuner anew](https://github.com/papergray/tuner-anew) - Shared text `MIT`
            """;
        var counts = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(entry, "main")], new Dictionary<string, EntryHistory>(), T1);

        var warning = Assert.Single(counts.Warnings);
        Assert.Equal("ambiguous_identity", warning.Rule);
        Assert.Equal(1, counts.Added);
        Assert.Equal(2, counts.Removed);
        Assert.NotNull(await _db.Apps.SingleOrDefaultAsync(a => a.Slug == "tuner-anew"));
    }

    [Fact]
    public async Task SlugClashSkipsTheNewEntryAndKeepsTheOwner()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var owner = await _db.Apps.SingleAsync(a => a.Slug == "micup");

        // A different entry that slugifies to an existing slug while the
        // owner's URL vanishes from the list. Importing it would steal the
        // owner's identity, so the upserter keeps the owner and warns.
        const string clash = """
            ## Apps

            ### Audio

            * [Micup!](https://example.com/micup-fork) - Fork of MicUp `MIT`
            """;
        var counts = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(clash, "main")], new Dictionary<string, EntryHistory>(), T1);

        var warning = Assert.Single(counts.Warnings);
        Assert.Equal("slug_clash", warning.Rule);
        Assert.Equal(0, counts.Added);
        // The owner stays; the other rows leave the list as usual.
        Assert.Equal(3, counts.Removed);
        Assert.DoesNotContain("micup", counts.RemovedSlugs);
        Assert.Equal(owner.Id, (await _db.Apps.SingleAsync(a => a.Slug == "micup")).Id);
        Assert.DoesNotContain(await _db.RemovedApps.ToListAsync(), t => t.Slug == "micup");
    }

    [Fact]
    public async Task StaleWithInstallsRetiresAndKeepsHistory()
    {
        var before = await SeedMicUpWithStats();

        const string shrunk = """
            ## Apps

            ### Audio

            * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
              * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

            ### Vendor-specific

            #### MIUI

            * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
            """;
        var counts = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(shrunk, "main")], new Dictionary<string, EntryHistory>(), T1);

        var warning = Assert.Single(counts.Warnings);
        Assert.Equal("stale_with_installs", warning.Rule);
        Assert.Equal(before.Id, warning.AppId);
        Assert.Equal("micup", warning.Slug);
        Assert.Contains("micup", counts.RemovedSlugs);

        // Retired, not deleted: same id, freed slug, hidden, history intact.
        Assert.Null(await _db.Apps.SingleOrDefaultAsync(a => a.Slug == "micup"));
        var retired = await _db.Apps.SingleAsync(a => a.Id == before.Id);
        Assert.Equal("micup-retired", retired.Slug);
        Assert.True(retired.Retired);
        Assert.Equal(Availability.Excluded, retired.Availability);
        Assert.Equal(CatalogUpserter.RetiredReason, retired.ExcludedReason);
        Assert.Equal(42, retired.InstallCount);
        Assert.Equal(1, await _db.AppInstallDays.CountAsync(d => d.AppId == before.Id));

        // Tombstoned under the original slug so clients drop their copy.
        var tombstone = Assert.Single(await _db.RemovedApps.ToListAsync(), t => t.Slug == "micup");
        Assert.Equal("MicUp", tombstone.Name);
    }

    [Fact]
    public async Task StaleWithOnlyDayHistoryRetires()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var micUp = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        _db.AppInstallDays.Add(new AppInstallDay
        {
            AppId = micUp.Id,
            Day = new DateOnly(2024, 6, 1),
            InstallCount = 3,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        const string shrunk = """
            ## Apps

            ### Audio

            * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
            """;
        await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(shrunk, "main")], new Dictionary<string, EntryHistory>(), T1);

        var retired = await _db.Apps.SingleAsync(a => a.Id == micUp.Id);
        Assert.True(retired.Retired);
        Assert.Equal("micup-retired", retired.Slug);
        Assert.Equal(1, await _db.AppInstallDays.CountAsync(d => d.AppId == micUp.Id));
    }

    [Fact]
    public async Task StaleWithoutHistoryHardDeletes()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);

        const string shrunk = """
            ## Apps

            ### Audio

            * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
              * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

            ### Vendor-specific

            #### MIUI

            * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
            """;
        var counts = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(shrunk, "main")], new Dictionary<string, EntryHistory>(), T1);

        Assert.Empty(counts.Warnings);
        Assert.DoesNotContain(await _db.Apps.ToListAsync(), a => a.Retired);
        Assert.Null(await _db.Apps.SingleOrDefaultAsync(a => a.Slug.StartsWith("micup")));
        Assert.Contains("micup", counts.RemovedSlugs);
        Assert.Single(await _db.RemovedApps.ToListAsync(), t => t.Slug == "micup");
    }

    [Fact]
    public async Task RetiredEntryReturningRestoresAndReclaimsSlug()
    {
        var before = await SeedMicUpWithStats();

        const string shrunk = """
            ## Apps

            ### Audio

            * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
            """;
        await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(shrunk, "main")], new Dictionary<string, EntryHistory>(), T1);
        Assert.True((await _db.Apps.SingleAsync(a => a.Id == before.Id)).Retired);
        _db.ChangeTracker.Clear();

        var counts = await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T1);

        var restored = await _db.Apps.SingleAsync(a => a.Id == before.Id);
        Assert.False(restored.Retired);
        Assert.Equal("micup", restored.Slug);
        Assert.Equal(42, restored.InstallCount);
        Assert.Null(restored.ExcludedReason);
        Assert.Null(restored.LastCheckedAt); // forced re-enrichment
        Assert.Equal(1, await _db.AppInstallDays.CountAsync(d => d.AppId == before.Id));
        Assert.DoesNotContain(await _db.RemovedApps.ToListAsync(), t => t.Slug == "micup");
        Assert.DoesNotContain(counts.Warnings, w => w.Rule == "slug_clash");
    }

    [Fact]
    public async Task RestoreKeepsFreedNameWhenSlugTaken()
    {
        var before = await SeedMicUpWithStats();

        const string shrunk = """
            ## Apps

            ### Audio

            * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
            """;
        await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(shrunk, "main")], new Dictionary<string, EntryHistory>(), T1);
        _db.ChangeTracker.Clear();

        // A different entry reuses the freed slug through the normal import;
        // like any same-slug re-add it also clears the retire tombstone.
        const string squatter = """
            ## Apps

            ### Audio

            * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
            * [MicUp!](https://github.com/other/micup) - Other mic app `MIT`
            """;
        await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(squatter, "main")], new Dictionary<string, EntryHistory>(), T1);
        Assert.Equal("micup", (await _db.Apps.SingleAsync(a => a.Url == "https://github.com/other/micup")).Slug);
        Assert.DoesNotContain(await _db.RemovedApps.ToListAsync(), t => t.Slug == "micup");
        _db.ChangeTracker.Clear();

        // The original entry returns and is adopted onto its retired row, but
        // the slug is taken and no tombstone names it: the freed name stays,
        // with nothing to report.
        var counts = await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T1);

        var restored = await _db.Apps.SingleAsync(a => a.Id == before.Id);
        Assert.False(restored.Retired);
        Assert.Equal("micup-retired", restored.Slug);
        Assert.Equal(42, restored.InstallCount);
        Assert.DoesNotContain(counts.Warnings, w => w.Rule == "slug_clash");
    }

    [Fact]
    public async Task RestoreReportsClashWhenTombstonedSlugTaken()
    {
        var before = await SeedMicUpWithStats();

        const string shrunk = """
            ## Apps

            ### Audio

            * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
            """;
        await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(shrunk, "main")], new Dictionary<string, EntryHistory>(), T1);
        Assert.Single(await _db.RemovedApps.ToListAsync(), t => t.Slug == "micup");

        // Operator-created state: the tombstoned slug is held by another live
        // row, so reclaiming it would steal a live slug.
        var audio = await _db.Categories.SingleAsync(c => c.Name == "Audio");
        _db.Apps.Add(new App
        {
            Slug = "micup",
            Name = "MicUp!",
            Url = "https://github.com/other/micup",
            Description = "Other mic app",
            CategoryId = audio.Id,
            AddedAt = T1,
            UpdatedAt = T1,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var counts = await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T1);

        var restored = await _db.Apps.SingleAsync(a => a.Id == before.Id);
        Assert.False(restored.Retired);
        Assert.Equal("micup-retired", restored.Slug);
        var clash = Assert.Single(counts.Warnings, w => w.Rule == "slug_clash");
        Assert.Equal(before.Id, clash.AppId);
    }

    [Fact]
    public async Task SameUrlInTwoCategoriesKeepsOneRow()
    {
        const string md = """
            ## Apps

            ### Audio

            * [fluffy](https://example.com/fluffy) - TV file manager `GPL-3.0`

            ### File management

            * [fluffy](https://example.com/fluffy) - File manager w/ TV support `GPL-3.0`
            """;

        var counts = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(md, "main")], new Dictionary<string, EntryHistory>(), T0);

        // The source lists the same url under two categories; only the first
        // occurrence is kept so the catalog has no duplicate apps.
        Assert.Equal(1, counts.Added);
        Assert.Equal(1, await _db.Apps.CountAsync(a => a.Url == "https://example.com/fluffy"));

        var again = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(md, "main")], new Dictionary<string, EntryHistory>(), T1);
        Assert.Equal((0, 0, 0), (again.Added, again.Updated, again.Removed));
    }

    private const string MovedToNewCategoryList = """
        ## Apps

        ### Audio

        * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
          * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

        ### Productivity

        * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time mic `MIT`

        ### Vendor-specific

        #### MIUI

        * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
        """;

    private const string MovedToExistingCategoryList = """
        ## Apps

        ### Audio

        * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
          * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

        ### Vendor-specific

        * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time mic `MIT`

        #### MIUI

        * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
        """;

    private const string RenamedCategoryList = """
        ## Apps

        ### Sound

        * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time mic `MIT`
        * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
          * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

        ### Vendor-specific

        #### MIUI

        * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
        """;

    private const string RenamedSubcategoryList = """
        ## Apps

        ### Audio

        * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time mic `MIT`
        * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
          * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

        ### Vendor-specific

        #### HyperOS

        * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
        """;

    private const string RenamedAndMovedList = """
        ## Apps

        ### Sound

        * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
          * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

        ### Vendor-specific

        * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time mic `MIT`

        #### MIUI

        * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
        """;

    private async Task<App> SeedMicUpWithStats()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var micUp = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        micUp.InstallCount = 42;
        _db.AppInstallDays.Add(new AppInstallDay
        {
            App = micUp,
            Day = new DateOnly(2024, 6, 1),
            InstallCount = 7,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return await _db.Apps.SingleAsync(a => a.Slug == "micup");
    }

    [Fact]
    public async Task MovingAnEntryToANewCategoryKeepsIdSlugAndStats()
    {
        var before = await SeedMicUpWithStats();

        var counts = await Upserter().UpsertAsync(
            [ParseMain(MovedToNewCategoryList)], new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal((0, 1, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Empty(counts.Warnings);
        Assert.Empty(await _db.RemovedApps.ToListAsync());

        var after = await _db.Apps.SingleAsync(a => a.Url == "https://github.com/papergray/MicUp");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("micup", after.Slug);
        Assert.Equal(42, after.InstallCount);
        var productivity = await _db.Categories.SingleAsync(c => c.Name == "Productivity");
        Assert.Equal(productivity.Id, after.CategoryId);
        Assert.Equal(1, await _db.AppInstallDays.CountAsync(d => d.AppId == before.Id));
    }

    [Fact]
    public async Task MovingAnEntryToAnExistingCategoryKeepsIdSlugAndStats()
    {
        var before = await SeedMicUpWithStats();

        var counts = await Upserter().UpsertAsync(
            [ParseMain(MovedToExistingCategoryList)], new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal((0, 1, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Empty(counts.Warnings);
        Assert.Empty(await _db.RemovedApps.ToListAsync());

        var after = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(42, after.InstallCount);
        var vendor = await _db.Categories.SingleAsync(c => c.Name == "Vendor-specific");
        Assert.Equal(vendor.Id, after.CategoryId);
        Assert.Equal(1, await _db.AppInstallDays.CountAsync(d => d.AppId == before.Id));
    }

    private const string MainWithoutMicUpList = """
        ## Apps

        ### Audio

        * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
          * [Tuner Beta](https://github.com/thetwom/tuner-beta) - Beta builds `GPL-3.0`

        ### Vendor-specific

        #### MIUI

        * [Aura](https://github.com/tgvdufuture/Aura) - LED app `MIT`
        """;

    private const string ClosedMicUpList = """
        ## Closed-source apps

        ### Audio

        * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time mic `MIT`
        """;

    private static ParsedDocument ParseClosed(string md) =>
        new AwesomeListParser().Parse(md, "closed-source");

    [Fact]
    public async Task MovingAnEntryToTheOtherListingKeepsIdSlugAndStats()
    {
        var before = await SeedMicUpWithStats();

        var counts = await Upserter().UpsertAsync(
            [ParseMain(MainWithoutMicUpList), ParseClosed(ClosedMicUpList)],
            new Dictionary<string, EntryHistory>(), T1);

        // The same URL in the other list is the same entry: the row flips
        // listings instead of sweeping out of main and being skipped there as
        // a slug clash in the closed list.
        Assert.Equal((0, 1, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Empty(counts.Warnings);

        var after = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(Listing.ClosedSource, after.Listing);
        Assert.Equal(42, after.InstallCount);
        Assert.Equal(1, await _db.AppInstallDays.CountAsync(d => d.AppId == before.Id));
        var audio = await _db.Categories.SingleAsync(c => c.Name == "Audio");
        Assert.Equal(audio.Id, after.CategoryId);

        // Clients that only follow main must drop the row, so a tombstone is
        // recorded for the listing it left; the closed listing's clients get
        // the row itself.
        var tombstone = Assert.Single(await _db.RemovedApps.ToListAsync(), t => t.Slug == "micup");
        Assert.Equal(Listing.Main, tombstone.Listing);
        Assert.Equal("MicUp", tombstone.Name);
    }

    [Fact]
    public async Task MovingAnEntryBackToTheOriginalListingClearsTheTombstone()
    {
        await SeedMicUpWithStats();
        await Upserter().UpsertAsync(
            [ParseMain(MainWithoutMicUpList), ParseClosed(ClosedMicUpList)],
            new Dictionary<string, EntryHistory>(), T1);

        var counts = await Upserter().UpsertAsync(
            [ParseMain(), new ParsedDocument { ListingName = "closed-source" }],
            new Dictionary<string, EntryHistory>(), T1);

        // Coming back to main clears the departure tombstone, otherwise
        // /v1/changes would report the slug as removed while it is live again.
        Assert.Equal((0, 1, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Empty(counts.Warnings);
        Assert.Empty(await _db.RemovedApps.ToListAsync());

        var after = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(Listing.Main, after.Listing);
        Assert.Equal(42, after.InstallCount);
    }

    [Fact]
    public async Task VariantFollowsItsRootAcrossListings()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var root = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        var child = NewVariant(root, "com.example.plugin", T0);
        child.UsageShort = "The plugin bridges to the root app over Shizuku.";
        child.UsageAnalyzedAt = T0;
        _db.Apps.Add(child);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var counts = await Upserter().UpsertAsync(
            [ParseMain(MainWithoutMicUpList), ParseClosed(ClosedMicUpList)],
            new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal((0, 1, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Empty(counts.Warnings);

        // A variant never takes part in list matching, so the pass carries its
        // listing over with the root instead of leaving it served under the
        // listing the root just left.
        var variant = await _db.Apps.SingleAsync(a => a.RootAppId == root.Id);
        Assert.Equal(Listing.ClosedSource, variant.Listing);
        Assert.True(variant.UpdatedAt > T1, $"variant change clock stuck at {variant.UpdatedAt:O}");
        Assert.Null(variant.UsageShort);
        Assert.Null(variant.UsageAnalyzedAt);
        var tombstone = Assert.Single(
            await _db.RemovedApps.ToListAsync(), t => t.Slug == variant.Slug);
        Assert.Equal(Listing.Main, tombstone.Listing);
        // Only the root flipped listings during the entry loop; the variant is
        // reconciled after it, so exactly one row counts as updated.
        Assert.Equal(Listing.ClosedSource, (await _db.Apps.SingleAsync(a => a.Slug == "micup")).Listing);
    }

    [Fact]
    public async Task MovingAnEntryToTheClosedListingDropsItsUsageReport()
    {
        var before = await SeedMicUpWithStats();
        var seeded = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        seeded.UsageShort = "MicUp reads the microphone through Shizuku.";
        seeded.UsageMarkdown = "## Usage\n\nMicUp uses Shizuku for the mic.";
        seeded.UsageMarkdownUsage = "- Microphone access";
        seeded.UsageMarkdownApiUsage = "android.media.action.*";
        seeded.UsageMarkdownNotableDetails = "Falls back to the system mic.";
        seeded.UsageAnalyzedAt = T1;
        seeded.UsageModel = "mimo-v2.6-flash";
        seeded.UsageCommit = "7106dd3502962b0c529549a5ba507d82474b6261";
        seeded.UsageReleaseRef = "v1.2.3";
        seeded.UsagePromptVersion = 17;
        seeded.UsageAnalysisVersion = 12;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // A category move inside the main list leaves the report alone.
        await Upserter().UpsertAsync(
            [ParseMain(MovedToNewCategoryList)], new Dictionary<string, EntryHistory>(), T1);
        _db.ChangeTracker.Clear();
        var kept = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.NotNull(kept.UsageShort);
        Assert.Equal(17, kept.UsagePromptVersion);

        var counts = await Upserter().UpsertAsync(
            [ParseMain(MainWithoutMicUpList), ParseClosed(ClosedMicUpList)],
            new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal((0, 1, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Empty(counts.Warnings);

        // The closed entry publishes no source, so the report generated while
        // the source was still public is dropped instead of being served for
        // code that is gone. The move also moved the change clock, so the
        // detail ETag rotates and clients drop their cached text.
        var after = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(Listing.ClosedSource, after.Listing);
        Assert.Null(after.UsageShort);
        Assert.Null(after.UsageMarkdown);
        Assert.Null(after.UsageMarkdownUsage);
        Assert.Null(after.UsageMarkdownApiUsage);
        Assert.Null(after.UsageMarkdownNotableDetails);
        Assert.Null(after.UsageAnalyzedAt);
        Assert.Null(after.UsageModel);
        Assert.Null(after.UsageCommit);
        Assert.Null(after.UsageReleaseRef);
        Assert.Equal(0, after.UsagePromptVersion);
        Assert.Equal(0, after.UsageAnalysisVersion);
        Assert.True(after.UpdatedAt > before.UpdatedAt);
    }

    [Fact]
    public async Task StaleHistoryCategoryMoveStillAdvancesChangeClock()
    {
        var before = await SeedMicUpWithStats();

        // Enrichment stamped the row after the last history entry, which is
        // what a [silent] list commit leaves behind (silent commits never
        // reach the history dictionary). ListUpdatedAt already carries that
        // same history date, so re-writing it must not mask the test via the
        // DbContext summary bump.
        before.UpdatedAt = T1;
        before.ListUpdatedAt = T0;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var history = new Dictionary<string, EntryHistory>
        {
            ["https://github.com/papergray/MicUp"] = new EntryHistory(T0, T0),
        };
        var counts = await Upserter().UpsertAsync(
            [ParseMain(MovedToNewCategoryList)], history, T1);

        Assert.Equal((0, 1, 0), (counts.Added, counts.Updated, counts.Removed));

        var after = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(before.Id, after.Id);
        // The move must cross a client's /v1/changes cursor even though the
        // history date predates the row's own enrichment stamp.
        Assert.True(after.UpdatedAt > T1, $"change clock stuck at {after.UpdatedAt:O}");
    }

    [Fact]
    public async Task RenamingACategoryKeepsTheRowIdentityAndItsApps()
    {
        var before = await SeedMicUpWithStats();
        var audio = await _db.Categories.SingleAsync(c => c.Name == "Audio");
        var categoryCount = await _db.Categories.CountAsync();

        var counts = await Upserter().UpsertAsync(
            [ParseMain(RenamedCategoryList)], new Dictionary<string, EntryHistory>(), T1);

        // A heading rename with a stable slug is invisible to API clients:
        // the row keeps its id, so every app keeps its category slug.
        Assert.Equal((0, 0, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Empty(counts.Warnings);
        Assert.Empty(await _db.RemovedApps.ToListAsync());

        _db.ChangeTracker.Clear();
        var renamed = await _db.Categories.SingleAsync(c => c.Id == audio.Id);
        Assert.Equal("Sound", renamed.Name);
        Assert.Equal("audio", renamed.Slug);
        Assert.Equal(categoryCount, await _db.Categories.CountAsync());
        Assert.Equal(0, await _db.Categories.CountAsync(c => c.Slug == "sound"));

        var micUp = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(audio.Id, micUp.CategoryId);
        Assert.Equal(42, micUp.InstallCount);
        Assert.Equal(1, await _db.AppInstallDays.CountAsync(d => d.AppId == before.Id));
    }

    [Fact]
    public async Task RenamingASubcategoryKeepsTheRowIdentityAndItsApps()
    {
        await SeedMicUpWithStats();
        var miui = await _db.Categories.SingleAsync(c => c.Name == "MIUI");
        var categoryCount = await _db.Categories.CountAsync();

        var counts = await Upserter().UpsertAsync(
            [ParseMain(RenamedSubcategoryList)], new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal((0, 0, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Empty(counts.Warnings);
        Assert.Empty(await _db.RemovedApps.ToListAsync());

        _db.ChangeTracker.Clear();
        var renamed = await _db.Categories.SingleAsync(c => c.Id == miui.Id);
        Assert.Equal("HyperOS", renamed.Name);
        Assert.Equal(miui.Slug, renamed.Slug);
        Assert.Equal(miui.ParentId, renamed.ParentId);
        Assert.Equal(categoryCount, await _db.Categories.CountAsync());

        var aura = await _db.Apps.SingleAsync(a => a.Slug == "aura");
        Assert.Equal(miui.Id, aura.CategoryId);
    }

    [Fact]
    public async Task RenamingACategoryWhileMovingAnEntryKeepsBothIdentities()
    {
        var before = await SeedMicUpWithStats();
        var audio = await _db.Categories.SingleAsync(c => c.Name == "Audio");
        var vendor = await _db.Categories.SingleAsync(c => c.Name == "Vendor-specific");

        var counts = await Upserter().UpsertAsync(
            [ParseMain(RenamedAndMovedList)], new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal((0, 1, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Empty(counts.Warnings);
        Assert.Empty(await _db.RemovedApps.ToListAsync());

        _db.ChangeTracker.Clear();
        var renamed = await _db.Categories.SingleAsync(c => c.Id == audio.Id);
        Assert.Equal("Sound", renamed.Name);
        Assert.Equal("audio", renamed.Slug);

        var micUp = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        Assert.Equal(before.Id, micUp.Id);
        Assert.Equal(42, micUp.InstallCount);
        Assert.Equal(vendor.Id, micUp.CategoryId);
        Assert.Equal(1, await _db.AppInstallDays.CountAsync(d => d.AppId == before.Id));
    }

    [Fact]
    public async Task MissingEntriesAreRemovedWithSlugsReported()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);

        const string shrunk = """
            ## Apps

            ### Audio

            * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time mic `MIT`
            """;
        var counts = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(shrunk, "main")], new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal(3, counts.Removed);
        Assert.Contains("tuner", counts.RemovedSlugs);
        Assert.Single(await _db.Apps.ToListAsync());
    }

    [Fact]
    public async Task IngestsOnlyAppsSection()
    {
        const string closed = """
            ## Closed-source apps

            ### Audio

            * [Call Recorder](https://example.com/callrec) `Paid` - Recorder `Proprietary`
            """;
        const string libs = """
            ## Development libraries

            ### Core

            * [Shizuku-API](https://github.com/RikkaApps/Shizuku-API) - Docs `Apache-2.0`
            """;
        const string flows = """
            ## Miscellaneous content

            ### Flows for [Automate](https://llamalab.com/automate/)

            * [Keeper](https://llamalab.com/automate/community/flows/1) - Keep alive `MIT`
            """;

        var parser = new AwesomeListParser();
        // Upserter-level proof that a Closed-source apps section maps to Apps
        // and keeps the closed listing; SyncService feeds the parsed real file.
        var counts = await Upserter().UpsertAsync([
            parser.Parse(closed, "closed-source"),
            parser.Parse(libs, "main"),
            parser.Parse(flows, "main"),
        ], new Dictionary<string, EntryHistory>(), T0);

        // Development libraries and Miscellaneous content are dropped
        // entirely (user call: only the Apps section is ingested), so
        // their rows would sweep out as stale on the next pass.
        Assert.Equal(1, counts.Added);
        var recorder = await _db.Apps.SingleAsync();
        Assert.Equal(Listing.ClosedSource, recorder.Listing);
        Assert.Equal(AppType.App, recorder.Type);
        Assert.Empty(await _db.Apps.Where(a => a.Slug == "shizuku-api").ToListAsync());
        Assert.Empty(await _db.Apps.Where(a => a.Slug == "keeper").ToListAsync());
        Assert.Empty(await _db.Categories.Where(c => c.Name == "Core").ToListAsync());
    }

    [Fact]
    public async Task FullBackfillOfRealList()
    {
        var readme = FindFile("awesome-shizuku", "README.md");
        if (readme is null)
        {
            // Dev-machine only.
            return;
        }

        var parser = new AwesomeListParser();
        var main = parser.Parse(await File.ReadAllTextAsync(readme), "main");
        // The empty closed doc still marks the listing as synced so closed
        // rows sweep out; SyncService parses the real file instead.
        var closedDoc = new ParsedDocument { ListingName = "closed-source" };

        // Real git history for the README (empty when git is unavailable).
        var history = new Dictionary<string, EntryHistory>(StringComparer.Ordinal);
        try
        {
            var git = new GitHistoryService();
            var repo = Path.GetDirectoryName(readme)!;
            foreach (var kv in await git.GetHistoryAsync(repo, "README.md"))
            {
                history[kv.Key] = kv.Value;
            }
        }
        catch (InvalidOperationException)
        {
        }

        var counts = await Upserter().UpsertAsync([main, closedDoc], history, DateTimeOffset.UtcNow);

        Assert.True(counts.Added > 300, $"Expected >300 apps, got {counts.Added}.");
        Assert.True(await _db.Categories.CountAsync() > 20);
        Assert.Equal(0, counts.Updated);
        Assert.Equal(0, counts.Removed);

        // Spot checks: hierarchy, nesting, duplicates, history.
        var miui = await _db.Categories.SingleAsync(c => c.Name == "MIUI");
        Assert.NotNull(miui.ParentId);
        var child = await _db.Apps.SingleAsync(a => a.Name == "aShell You");
        Assert.NotNull(child.ParentId);
        // krude is listed twice in the source; dedupe keeps a single row.
        Assert.Equal(1, await _db.Apps.CountAsync(a => a.Url == "https://github.com/KusStar/krude"));
        var hail = await _db.Apps.SingleAsync(a => a.Name == "Hail");
        Assert.True(hail.AddedAt <= hail.UpdatedAt);
        Assert.True(hail.AddedAt.Year >= 2022);
    }

    [Fact]
    public async Task VariantsSurviveListSyncAndAreNotStaled()
    {
        // Extra packages share their root's URL, so a sync that only sees the
        // list row must neither match nor stale the enrich-created children.
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var root = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        _db.Apps.Add(NewVariant(root, "com.example.plugin", T0));
        await _db.SaveChangesAsync();

        var counts = await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T1);

        Assert.Equal((0, 0, 0), (counts.Added, counts.Updated, counts.Removed));
        Assert.Equal(5, await _db.Apps.CountAsync());
        Assert.Equal(1, await _db.Apps.CountAsync(a => a.RootAppId == root.Id));
        Assert.Empty(await _db.RemovedApps.ToListAsync());
    }

    [Fact]
    public async Task VariantIsRemovedWhenRootLeavesList()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var root = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        _db.Apps.Add(NewVariant(root, "com.example.plugin", T0));
        await _db.SaveChangesAsync();

        const string shrunk = """
            ## Apps

            ### Audio

            * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
            """;
        var counts = await Upserter().UpsertAsync(
            [new AwesomeListParser().Parse(shrunk, "main")], new Dictionary<string, EntryHistory>(), T1);

        Assert.Contains("micup", counts.RemovedSlugs);
        Assert.Null(await _db.Apps.FirstOrDefaultAsync(a => a.Slug == "com-example-plugin"));

        // The variant cascades with its root, but clients only delete rows
        // listed in removed[], so a tombstone of its own is required.
        var variantTombstone = await _db.RemovedApps.SingleOrDefaultAsync(t => t.Slug == "com-example-plugin");
        Assert.NotNull(variantTombstone);
        Assert.Equal("MicUp", variantTombstone.Name);
        Assert.Equal(Listing.Main, variantTombstone.Listing);
    }

    private static App NewVariant(App root, string package, DateTimeOffset now) => new()
    {
        Slug = package.Replace('.', '-'),
        Name = root.Name,
        Url = root.Url,
        Listing = root.Listing,
        Type = root.Type,
        CategoryId = root.CategoryId,
        AddedAt = now,
        UpdatedAt = now,
        RootAppId = root.Id,
        PackageName = package,
    };

    private static string? FindFile(string repoDir, string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, repoDir, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

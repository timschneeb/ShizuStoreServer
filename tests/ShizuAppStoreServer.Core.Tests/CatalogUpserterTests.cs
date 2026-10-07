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
    public async Task StaleDeleteWithInstallsWarns()
    {
        await Upserter().UpsertAsync([ParseMain()], new Dictionary<string, EntryHistory>(), T0);
        var micUp = await _db.Apps.SingleAsync(a => a.Slug == "micup");
        micUp.InstallCount = 7;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

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
        Assert.Equal("micup", warning.Slug);
        Assert.Contains("micup", counts.RemovedSlugs);
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

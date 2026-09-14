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
        Assert.Equal(T1, micUp.UpdatedAt);
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
        // The closed doc is passed directly to prove a Closed-source apps
        // section still maps to Apps when one reaches the upserter; sync
        // itself never reads CLOSED_SOURCE.md anymore.
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
        // Sync ignores CLOSED_SOURCE.md; the empty doc still marks the
        // listing as synced so pre-decision rows sweep out as stale.
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

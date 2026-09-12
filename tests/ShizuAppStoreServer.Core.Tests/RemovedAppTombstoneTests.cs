using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.History;
using ShizuAppStoreServer.Core.Parsing;
using ShizuAppStoreServer.Core.Sync;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Tombstone lifecycle for <c>GET /v1/changes removed[]</c> (M5).</summary>
public sealed class RemovedAppTombstoneTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ShizuDbContext _db;

    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2024, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Both = """
        ## Apps

        ### Audio

        * [MicUp](https://github.com/papergray/MicUp) - Real-time mic `MIT`
        * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
        """;

    private const string TunerOnly = """
        ## Apps

        ### Audio

        * [Tuner](https://github.com/thetwom/tuner) - Tuner app `GPL-3.0`
        """;

    public RemovedAppTombstoneTests()
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

    private static ParsedDocument Parse(string md) => new AwesomeListParser().Parse(md, "main");

    private static Dictionary<string, EntryHistory> NoHistory() => new();

    [Fact]
    public async Task DeletingStaleRowWritesTombstone()
    {
        await new CatalogUpserter(_db).UpsertAsync([Parse(Both)], NoHistory(), T0);
        var counts = await new CatalogUpserter(_db).UpsertAsync([Parse(TunerOnly)], NoHistory(), T1);

        Assert.Equal(1, counts.Removed);
        Assert.Equal(["micup"], counts.RemovedSlugs);
        var tombstone = await _db.RemovedApps.SingleAsync();
        Assert.Equal("micup", tombstone.Slug);
        Assert.Equal("MicUp", tombstone.Name);
        Assert.Equal(Data.Listing.Main, tombstone.Listing);
        Assert.Equal(T1, tombstone.RemovedAt);
    }

    [Fact]
    public async Task ReaddingClearsTombstone()
    {
        await new CatalogUpserter(_db).UpsertAsync([Parse(Both)], NoHistory(), T0);
        await new CatalogUpserter(_db).UpsertAsync([Parse(TunerOnly)], NoHistory(), T1);
        Assert.Equal(1, await _db.RemovedApps.CountAsync());

        var counts = await new CatalogUpserter(_db).UpsertAsync([Parse(Both)], NoHistory(), T2);

        Assert.Equal(1, counts.Added);
        Assert.Equal(0, await _db.RemovedApps.CountAsync());
    }

    [Fact]
    public async Task RedeleteRefreshesExistingTombstone()
    {
        await new CatalogUpserter(_db).UpsertAsync([Parse(Both)], NoHistory(), T0);
        await new CatalogUpserter(_db).UpsertAsync([Parse(TunerOnly)], NoHistory(), T0);
        await new CatalogUpserter(_db).UpsertAsync([Parse(Both)], NoHistory(), T1);
        await new CatalogUpserter(_db).UpsertAsync([Parse(TunerOnly)], NoHistory(), T2);

        // Still exactly one row (slug is unique) with the latest timestamp.
        var tombstone = await _db.RemovedApps.SingleAsync();
        Assert.Equal("micup", tombstone.Slug);
        Assert.Equal(T2, tombstone.RemovedAt);
    }
}

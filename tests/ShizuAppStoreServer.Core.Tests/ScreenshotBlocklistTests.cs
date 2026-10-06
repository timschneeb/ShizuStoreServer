using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Overrides;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Repo-fallback screenshots are pinned to a commit, so blocks must ignore the
/// commit segment or a repository re-pin would resurrect a blocked image.
/// </summary>
public sealed class ScreenshotBlocklistTests : IDisposable
{
    private const string Sha1 = "722a6de918853fa4e1c0b9256527a1d0b035d253";
    private const string Sha2 = "b042081aeed7f235db4cf27a9b35b3156f3610e3";
    private const string Sha256 = "722a6de9722a6de9722a6de9722a6de9722a6de9722a6de9722a6de9722a6de9";
    private const string Sha256Other = "b042081ab042081ab042081ab042081ab042081ab042081ab042081ab042081a";
    private const string Pinned = "https://raw.githubusercontent.com/l930203811/ZenFile/" + Sha1 + "/assets/screenshots/wx.png";
    private const string Commitless = "https://raw.githubusercontent.com/l930203811/ZenFile/assets/screenshots/wx.png";

    private readonly SqliteConnection _connection;
    private readonly ShizuDbContext _db;

    public ScreenshotBlocklistTests()
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

    private static HashSet<string> Set(params string[] normalized) =>
        new(normalized, StringComparer.Ordinal);

    [Fact]
    public void PinnedGitHubUrlsNormalizeToTheCommitlessForm()
    {
        Assert.Equal(Commitless, ScreenshotBlocklist.Normalize(Pinned));
    }

    [Fact]
    public void BlocksMatchAcrossCommitPins()
    {
        var blocked = Set(ScreenshotBlocklist.Normalize(Pinned));
        var repinned = Pinned.Replace(Sha1, Sha2, StringComparison.Ordinal);

        // The stored candidate moved to a new head; the block still matches.
        Assert.True(ScreenshotBlocklist.IsBlocked(repinned, blocked));
        Assert.True(ScreenshotBlocklist.IsBlocked(Pinned, blocked));
        Assert.False(ScreenshotBlocklist.IsBlocked(
            "https://raw.githubusercontent.com/l930203811/ZenFile/" + Sha2 + "/assets/screenshots/other.png",
            blocked));
    }

    [Fact]
    public void Sha256CommitSegmentsAreAlsoDropped()
    {
        var pinned = $"https://raw.githubusercontent.com/o/r/{Sha256}/shots/a.png";
        var other = $"https://raw.githubusercontent.com/o/r/{Sha256Other}/shots/a.png";
        var blocked = Set(ScreenshotBlocklist.Normalize(pinned));

        Assert.Equal("https://raw.githubusercontent.com/o/r/shots/a.png", ScreenshotBlocklist.Normalize(pinned));
        Assert.True(ScreenshotBlocklist.IsBlocked(other, blocked));
    }

    [Fact]
    public void GitLabSubgroupRawUrlsNormalizeAcrossCommits()
    {
        var pinned = $"https://gitlab.com/group/sub/proj/-/raw/{Sha1}/docs/img/a.png";
        var other = $"https://gitlab.com/group/sub/proj/-/raw/{Sha2}/docs/img/a.png";
        var blocked = Set(ScreenshotBlocklist.Normalize(pinned));

        Assert.Equal("https://gitlab.com/group/sub/proj/-/raw/docs/img/a.png", ScreenshotBlocklist.Normalize(pinned));
        Assert.True(ScreenshotBlocklist.IsBlocked(other, blocked));
    }

    [Fact]
    public void NonPinnedAndNonCommitSegmentsStayExact()
    {
        // A 40-hex-looking segment on another host, a branch name and a short
        // ref must not be stripped.
        Assert.Equal(
            $"https://example.com/o/r/{Sha1}/a.png",
            ScreenshotBlocklist.Normalize($"https://example.com/o/r/{Sha1}/a.png"));
        Assert.Equal(
            "https://raw.githubusercontent.com/o/r/main/a.png",
            ScreenshotBlocklist.Normalize("https://raw.githubusercontent.com/o/r/main/a.png"));
        Assert.Equal(
            "not a url",
            ScreenshotBlocklist.Normalize("not a url"));
    }

    [Fact]
    public void QueryIsKeptAlongsideTheCommitDrop()
    {
        Assert.Equal(
            Commitless + "?token=1",
            ScreenshotBlocklist.Normalize(Pinned + "?token=1"));
    }

    [Fact]
    public async Task LoadNormalizesRowsAndSkipsSoftDeleted()
    {
        var now = DateTimeOffset.UtcNow;
        _db.BlockedScreenshotUrls.Add(new BlockedScreenshotUrl { Url = Pinned, CreatedAt = now, UpdatedAt = now });
        _db.BlockedScreenshotUrls.Add(new BlockedScreenshotUrl { Url = "https://example.com/plain.png", CreatedAt = now, UpdatedAt = now });
        _db.BlockedScreenshotUrls.Add(new BlockedScreenshotUrl { Url = Commitless + ".gone", CreatedAt = now, UpdatedAt = now, DeletedAt = now });
        await _db.SaveChangesAsync();

        var blocked = await ScreenshotBlocklist.LoadAsync(_db);

        Assert.Equal(2, blocked.Count);
        Assert.Contains(Commitless, blocked);
        Assert.Contains("https://example.com/plain.png", blocked);
    }

    [Fact]
    public void FilterPreservesOrderAndDropsMatches()
    {
        var blocked = Set(ScreenshotBlocklist.Normalize(Pinned));
        var urls = new List<string>
        {
            "https://example.com/a.png",
            Pinned.Replace(Sha1, Sha2, StringComparison.Ordinal),
            "https://example.com/b.png",
        };

        Assert.Equal(
            new[] { "https://example.com/a.png", "https://example.com/b.png" },
            ScreenshotBlocklist.Filter(urls, blocked));
    }
}

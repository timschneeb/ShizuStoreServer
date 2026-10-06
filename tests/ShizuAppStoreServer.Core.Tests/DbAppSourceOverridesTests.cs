using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Sources;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// The database-backed source-override seam: app_overrides rows are parsed per
/// kind and answered by slug, malformed or soft-deleted rows are ignored.
/// </summary>
public sealed class DbAppSourceOverridesTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ShizuDbContext _db;

    public DbAppSourceOverridesTests()
    {
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

    private async Task AddRowAsync(string slug, string field, string value, DateTimeOffset? deletedAt = null)
    {
        _db.AppOverrides.Add(new AppOverride
        {
            AppSlug = slug,
            Field = field,
            Value = value,
            CreatedAt = T0,
            UpdatedAt = T0,
            DeletedAt = deletedAt,
        });
        await _db.SaveChangesAsync();
    }

    private DbAppSourceOverrides Seam() => new(_db);

    [Fact]
    public async Task RemapReleaseHomeAnswersBySlug()
    {
        await AddRowAsync("instafel", "source_release_home", "instafel/u-rel");
        await AddRowAsync("linksheet", "source_release_home", " LinkSheet/nightly ");

        var seam = Seam();

        Assert.Equal(("instafel", "u-rel"), seam.RemapReleaseHome("instafel"));
        Assert.Equal(("LinkSheet", "nightly"), seam.RemapReleaseHome("linksheet"));
        Assert.Null(seam.RemapReleaseHome("other"));
    }

    [Fact]
    public async Task ParsesTheGitCodeMirror()
    {
        await AddRowAsync(
            "hlbmerge",
            "source_gitcode_mirror",
            "bigmolihuan/hlbmerge_flutter|molihuan/hlbmerge_flutter");

        var mirror = Seam().GitCodeMirrorFor("hlbmerge");

        Assert.NotNull(mirror);
        Assert.Equal("bigmolihuan", mirror.Owner);
        Assert.Equal("hlbmerge_flutter", mirror.Repo);
        Assert.Equal("molihuan", mirror.ReadmeOwner);
        Assert.Equal("hlbmerge_flutter", mirror.ReadmeRepo);
        Assert.Null(Seam().GitCodeMirrorFor("other"));
    }

    [Fact]
    public async Task ScansAllReleasesOnlyForTrueRows()
    {
        await AddRowAsync("smartspacer", "source_scan_all_releases", "true");
        await AddRowAsync("other", "source_scan_all_releases", "false");

        var seam = Seam();

        Assert.True(seam.ScansAllReleases("smartspacer"));
        Assert.False(seam.ScansAllReleases("other"));
        Assert.False(seam.ScansAllReleases("missing"));
    }

    [Fact]
    public async Task PrefersPrereleaseOnlyForTrueRows()
    {
        await AddRowAsync("revanced", "source_prefer_prerelease", "True");

        var seam = Seam();

        Assert.True(seam.PrefersPrerelease("revanced"));
        Assert.False(seam.PrefersPrerelease("missing"));
    }

    [Fact]
    public async Task SkipReleasePollFoldsMirrorAndScanAll()
    {
        await AddRowAsync("hlbmerge", "source_gitcode_mirror", "bigmolihuan/hlbmerge_flutter|molihuan/hlbmerge_flutter");
        await AddRowAsync("smartspacer", "source_scan_all_releases", "true");
        await AddRowAsync("instafel", "source_release_home", "instafel/u-rel");

        var seam = Seam();

        Assert.True(seam.SkipReleasePoll("hlbmerge"));
        Assert.True(seam.SkipReleasePoll("smartspacer"));
        Assert.False(seam.SkipReleasePoll("instafel"));
        Assert.False(seam.SkipReleasePoll("other"));
    }

    [Fact]
    public async Task MalformedAndNonSourceRowsAreIgnored()
    {
        await AddRowAsync("broken-home", "source_release_home", "no-slash");
        await AddRowAsync("broken-mirror", "source_gitcode_mirror", "only|two|parts");
        await AddRowAsync("broken-flag", "source_scan_all_releases", "sometimes");
        await AddRowAsync("plain", "display_name", "Not a source row");

        var seam = Seam();

        Assert.Null(seam.RemapReleaseHome("broken-home"));
        Assert.Null(seam.GitCodeMirrorFor("broken-mirror"));
        Assert.False(seam.ScansAllReleases("broken-flag"));
        Assert.Null(seam.RemapReleaseHome("plain"));
        Assert.False(seam.SkipReleasePoll("plain"));
    }

    [Fact]
    public async Task SoftDeletedRowsAreIgnored()
    {
        await AddRowAsync("instafel", "source_release_home", "instafel/u-rel", deletedAt: T0.AddDays(1));

        Assert.Null(Seam().RemapReleaseHome("instafel"));
    }
}

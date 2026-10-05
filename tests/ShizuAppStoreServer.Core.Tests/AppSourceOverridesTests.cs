using ShizuAppStoreServer.Core.Sources;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// The in-code special cases behind the override seam. Keeping them pinned
/// here protects the enrichment and poll call sites that share this policy.
/// </summary>
public sealed class AppSourceOverridesTests
{
    private static readonly IAppSourceOverrides Overrides = StaticAppSourceOverrides.Instance;

    [Theory]
    [InlineData("mamiiblt", "instafel", "instafel", "u-rel")]
    [InlineData("LinkSheet", "LinkSheet", "LinkSheet", "nightly")]
    public void RemapsKnownReleaseHomes(
        string owner, string repo, string expectedOwner, string expectedRepo)
    {
        Assert.Equal((expectedOwner, expectedRepo), Overrides.RemapReleaseHome(owner, repo));
    }

    [Theory]
    [InlineData("owner", "repo")]
    [InlineData("mamiiblt", "other")]
    [InlineData("instafel", "u-rel")]
    [InlineData("LinkSheet", "nightly")]
    public void LeavesOtherReleaseHomesUntouched(string owner, string repo)
    {
        Assert.Equal((owner, repo), Overrides.RemapReleaseHome(owner, repo));
    }

    [Fact]
    public void MirrorsHlbmergeGitCodeReleasesWithGitHubReadme()
    {
        var mirror = Overrides.GitCodeMirrorFor("molihuan", "hlbmerge_flutter");

        Assert.NotNull(mirror);
        Assert.Equal("bigmolihuan", mirror.Owner);
        Assert.Equal("hlbmerge_flutter", mirror.Repo);
        Assert.Equal("molihuan", mirror.ReadmeOwner);
        Assert.Equal("hlbmerge_flutter", mirror.ReadmeRepo);
    }

    [Theory]
    [InlineData("owner", "repo")]
    [InlineData("molihuan", "other")]
    [InlineData("KieronQuinn", "SmartspacerPlugins")]
    public void HasNoMirrorForOtherRepos(string owner, string repo)
    {
        Assert.Null(Overrides.GitCodeMirrorFor(owner, repo));
    }

    [Fact]
    public void ScansAllReleasesForSmartspacerPlugins()
    {
        Assert.True(Overrides.ScansAllReleases("KieronQuinn", "SmartspacerPlugins"));
        Assert.False(Overrides.ScansAllReleases("KieronQuinn", "Smartspacer"));
        Assert.False(Overrides.ScansAllReleases("owner", "repo"));
    }

    [Theory]
    [InlineData("molihuan", "hlbmerge_flutter")]
    [InlineData("KieronQuinn", "SmartspacerPlugins")]
    public void SkipsPollingFeedsWithoutACheapSignal(string owner, string repo)
    {
        Assert.True(Overrides.SkipReleasePoll(owner, repo));
    }

    [Theory]
    [InlineData("instafel", "u-rel")]
    [InlineData("LinkSheet", "nightly")]
    [InlineData("owner", "repo")]
    public void PollsOtherForgeFeeds(string owner, string repo)
    {
        Assert.False(Overrides.SkipReleasePoll(owner, repo));
    }
}

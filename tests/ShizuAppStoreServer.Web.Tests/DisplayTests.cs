using ShizuAppStoreServer.Web.Mapping;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class DisplayTests
{
    [Theory]
    [InlineData(null, "-")]
    [InlineData(999L, "999")]
    [InlineData(1000L, "1k")]
    [InlineData(1200L, "1.2k")]
    [InlineData(1_000_000L, "1M")]
    [InlineData(1_500_000L, "1.5M")]
    public void CompactCountMatchesClient(long? value, string expected) =>
        Assert.Equal(expected, Display.CompactCount(value));

    [Theory]
    [InlineData(null, "-")]
    [InlineData(1L, "-")]
    [InlineData(999L, "999")]
    [InlineData(1000L, "1 KB")]
    [InlineData(12_500_000L, "12 MB")]
    [InlineData(1_500_000_000L, "1 GB")]
    public void SizeLabelTruncatesLikeClient(long? value, string expected) =>
        Assert.Equal(expected, Display.SizeLabel(value));

    [Fact]
    public void RelativeAgeUsesClientBuckets()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Equal("just now", Display.RelativeAge(now.AddSeconds(-30)));
        Assert.Equal("5 minutes ago", Display.RelativeAge(now.AddMinutes(-5.5)));
        Assert.Equal("1 minute ago", Display.RelativeAge(now.AddSeconds(-90)));
        Assert.Equal("2 hours ago", Display.RelativeAge(now.AddHours(-2.5)));
        Assert.Equal("1 day ago", Display.RelativeAge(now.AddDays(-1.5)));
        Assert.Equal("1 week ago", Display.RelativeAge(now.AddDays(-9)));
        Assert.Equal("1 month ago", Display.RelativeAge(now.AddDays(-40)));
        Assert.Equal("1 year ago", Display.RelativeAge(now.AddDays(-400)));
        Assert.Equal("just now", Display.RelativeAge(now.AddMinutes(5)));
        Assert.Equal("-", Display.RelativeAge(null));
    }
}

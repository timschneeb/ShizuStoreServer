using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using Xunit;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>
/// Summary labels are filtered to locales whose label differs from the APK's
/// default label so list payloads stay small; detail keeps every localized
/// label.
/// </summary>
public sealed class AppMapperTests
{
    [Fact]
    public void SummaryKeepsOnlyLabelsThatDifferFromTheDisplayName()
    {
        var app = NewApp(["en=Reader", "de=Leser", "fr=Reader", "zh=阅读器"], displayName: "Reader");

        var summary = AppMapper.ToSummary(app);

        Assert.Equal(["de", "zh"], summary.LocalizedLabels!.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("Leser", summary.LocalizedLabels["de"]);
    }

    [Fact]
    public void SummaryOmitsLabelsWithoutDifferences()
    {
        var app = NewApp(["en=Catalog Name", "de=Catalog Name"]);

        var summary = AppMapper.ToSummary(app);

        Assert.Empty(summary.LocalizedLabels!);
    }

    [Fact]
    public void SummaryComparesAgainstTheDefaultApkLabel()
    {
        var app = NewApp(
            ["en=Shelter", "de=Shelter", "zh=庇护所"],
            displayName: "Shelter (AntiForensic-Tools)",
            apkLabel: "Shelter");

        var summary = AppMapper.ToSummary(app);

        Assert.Equal(["zh"], summary.LocalizedLabels!.Keys);
    }

    [Fact]
    public void DetailKeepsEveryLocalizedLabel()
    {
        var app = NewApp(["en=Reader", "de=Leser", "fr=Reader", "zh=阅读器"], displayName: "Reader");

        var detail = AppMapper.ToDetail(app, []);

        Assert.Equal(4, detail.Downloads[0].LocalizedLabels!.Count);
    }

    private static App NewApp(IReadOnlyList<string> labels, string? displayName = null, string? apkLabel = null) => new()
    {
        Slug = "app",
        Name = "Catalog Name",
        DisplayName = displayName,
        ApkLabel = apkLabel,
        Url = "https://github.com/example/app",
        Downloads =
        {
            new AppDownload
            {
                ApkUrl = "https://cdn.example/app.apk",
                SigKey = "sig",
                IsPrimary = true,
                LocalizedLabels = [.. labels],
            },
        },
    };
}

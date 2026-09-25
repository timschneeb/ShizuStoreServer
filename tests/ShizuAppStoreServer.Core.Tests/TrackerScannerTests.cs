using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Sources;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Hermetic tests for the Exodus code-signature DEX matcher.</summary>
public sealed class TrackerScannerTests
{
    private static TrackerSignature Signature(int id, string name, string code, params string[] categories) =>
        new(id, name, code, categories);

    [Fact]
    public void MatchesCodeSignatureTokensInDexText()
    {
        TrackerSignature[] catalog =
        [
            Signature(1, "Google Analytics", "com.google.android.apps.analytics.", "Analytics"),
            Signature(2, "AppLovin", "com.applovin.", "Advertisement"),
        ];

        var hits = TrackerScanner.ScanText(["noise Lcom/applovin/sdk/AppLovinSdk; noise"], catalog);

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.Id);
        Assert.Equal("AppLovin", hit.Name);
        Assert.Equal("com.applovin.", hit.Signature);
        Assert.Equal(["Advertisement"], hit.Tags);
    }

    [Fact]
    public void MatchesAnyAlternativeInPipeSeparatedSignatures()
    {
        TrackerSignature[] catalog = [Signature(4, "Flurry", "com.flurry.|com.flurry.android.", "Analytics")];

        var hits = TrackerScanner.ScanText(["Lcom/flurry/android/FlurryAgent;"], catalog);

        Assert.Equal("com.flurry.android.", Assert.Single(hits).Signature);
    }

    [Fact]
    public void MatchesLeadingDotSignaturesAsRelativePackages()
    {
        // `.sizmek.` names a class relative to the host app package, so the
        // descriptor path component is what can be matched statically.
        TrackerSignature[] catalog = [Signature(9, "Sizmek", ".sizmek.", "Advertisement")];

        var hits = TrackerScanner.ScanText(["Lcom/host/app/sizmek/Tag;"], catalog);

        Assert.Equal(["Advertisement"], Assert.Single(hits).Tags);
    }

    [Fact]
    public void CarriesAllTrackerCategoriesAsTags()
    {
        TrackerSignature[] catalog =
        [
            Signature(5, "Multi", "com.multi.", "Analytics", "Advertisement", "Location"),
        ];

        var hits = TrackerScanner.ScanText(["Lcom/multi/Tracker;"], catalog);

        Assert.Equal(["Analytics", "Advertisement", "Location"], Assert.Single(hits).Tags);
    }

    [Fact]
    public void DeduplicatesMultipleSignaturesOfOneTracker()
    {
        TrackerSignature[] catalog = [Signature(3, "Flurry", "com.flurry.|com.flurry.android.", "Analytics")];

        var hits = TrackerScanner.ScanText(["Lcom/flurry/One;Lcom/flurry/android/Two;"], catalog);

        // One hit per tracker; the longest matching alternative is the evidence.
        Assert.Equal("com.flurry.android.", Assert.Single(hits).Signature);
    }

    [Fact]
    public void ReturnsEmptyWhenNothingMatches()
    {
        TrackerSignature[] catalog = [Signature(2, "AppLovin", "com.applovin.", "Advertisement")];

        Assert.Empty(TrackerScanner.ScanText(["Lcom/example/app/Main;"], catalog));
    }

    [Fact]
    public void ReturnsEmptyWithoutCatalog()
    {
        Assert.Empty(TrackerScanner.ScanText(["Lcom/applovin/Foo;"], []));
    }

    [Fact]
    public void ScanApkReadsAllClassesDexEntries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shizu-dex-{Guid.NewGuid():N}.apk");
        try
        {
            File.WriteAllBytes(path, TestAssets.BuildApk(
                ("classes.dex", "Lcom/applovin/Foo;"u8.ToArray()),
                ("classes2.dex", "Lcom/other/Bar;"u8.ToArray()),
                ("resources.arsc", "Lcom/applovin/NotDex;"u8.ToArray())));
            TrackerSignature[] catalog = [Signature(2, "AppLovin", "com.applovin.", "Advertisement")];

            var hits = TrackerScanner.ScanApk(path, catalog);

            var hit = Assert.Single(hits);
            Assert.Equal("AppLovin", hit.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScanApkReturnsEmptyForUnreadableFile()
    {
        TrackerSignature[] catalog = [Signature(2, "AppLovin", "com.applovin.", "Advertisement")];

        Assert.Empty(TrackerScanner.ScanApk(
            Path.Combine(Path.GetTempPath(), $"shizu-missing-{Guid.NewGuid():N}.apk"), catalog));
    }
}

/// <summary>Hermetic tests for the declared manager signals.</summary>
public sealed class ShizukuSignalScannerTests
{
    [Fact]
    public void DetectsTheDhizukuPermission()
    {
        var signals = ShizukuSignalScanner.Scan(
            ["android.permission.INTERNET", "com.rosan.dhizuku.permission.API"]);

        Assert.True(signals.DhizukuDeclared);
    }

    [Fact]
    public void IgnoresTheShizukuPermission()
    {
        // Almost every Shizuku app declares this, so it carries no signal.
        var signals = ShizukuSignalScanner.Scan(["moe.shizuku.manager.permission.API_V23"]);

        Assert.Equal(ApkSignals.None, signals);
    }

    [Fact]
    public void NoPermissionsMeansNoSignals() =>
        Assert.Equal(ApkSignals.None, ShizukuSignalScanner.Scan([]));
}

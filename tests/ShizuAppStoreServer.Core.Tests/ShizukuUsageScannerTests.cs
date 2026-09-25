using ShizuAppStoreServer.Core.Enrichment;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class ShizukuUsageScannerTests
{
    [Fact]
    public void DetectsShizukuPermissionAndComponentMarkers()
    {
        var usage = ShizukuUsageScanner.ScanPermissions(
            ["moe.shizuku.manager.permission.API_V23", "android.permission.INTERNET"], "apk");

        Assert.Equal(["shizuku"], usage.Managers);
        Assert.Empty(usage.Capabilities);
        Assert.False(usage.Optional);
        Assert.Equal(["permission"], usage.Evidence.Select(e => e.Kind));
        Assert.All(usage.Evidence, e => Assert.Equal("strong", e.Confidence));
    }

    [Fact]
    public void DetectsDhizukuSuiAndRootPermissions()
    {
        var usage = ShizukuUsageScanner.ScanPermissions(
            [
                "com.rosan.dhizuku.permission.API",
                "rikka.sui.permission.API_V23",
                "android.permission.ACCESS_SUPERUSER",
            ],
            "apk");

        Assert.Equal(["dhizuku", "sui", "root"], usage.Managers);
        Assert.Equal(["strong", "strong", "weak"], usage.Evidence.Select(e => e.Confidence));
    }

    [Fact]
    public void ClassifiesCommandStringsAsCapabilities()
    {
        var usage = ShizukuUsageScanner.ScanDexText(
            ["pm uninstall --user 0 com.example", "cmd package compile -m speed -f com.example"]);

        Assert.Equal(["uninstall", "compile"], usage.Capabilities);
        Assert.Equal(["command", "command"], usage.Evidence.Select(e => e.Kind));
    }

    [Fact]
    public void PrefersUserServiceApiFormOverNewProcessAndPermission()
    {
        var usage = ShizukuUsageScanner.Merge(
            ShizukuUsageScanner.ScanDexText(["newProcess"]),
            ShizukuUsageScanner.ScanDexText(["bindUserService"]),
            ShizukuUsageScanner.ScanPermissions(["moe.shizuku.manager.permission.API_V23"], "apk"));

        Assert.Equal("user_service", usage.ApiForm);
    }

    [Fact]
    public void MarksUsageOptionalOnlyWhenManagerAndFallbackArePresent()
    {
        var fallbackOnly = ShizukuUsageScanner.ScanDexText(["android.content.pm.PackageInstaller"]);
        var withManager = ShizukuUsageScanner.Merge(
            fallbackOnly,
            ShizukuUsageScanner.ScanPermissions(["moe.shizuku.manager.permission.API_V23"], "apk"));

        Assert.False(fallbackOnly.Optional);
        Assert.True(withManager.Optional);
    }

    [Fact]
    public void KeepsCanonicalManagerAndCapabilityOrder()
    {
        var usage = ShizukuUsageScanner.Merge(
            ShizukuUsageScanner.ScanDexText(["pm disable-user --user 0 com.example"]),
            ShizukuUsageScanner.ScanPermissions(
                ["android.permission.ACCESS_SUPERUSER", "com.rosan.dhizuku.permission.API"], "apk"),
            ShizukuUsageScanner.ScanPermissions(["moe.shizuku.manager.permission.API_V23"], "apk"));

        Assert.Equal(["shizuku", "dhizuku", "root"], usage.Managers);
        Assert.Equal(["freeze"], usage.Capabilities);
    }

    [Fact]
    public void DeduplicatesEvidenceAcrossMerges()
    {
        var first = ShizukuUsageScanner.ScanDexText(["pm install /data/local/tmp/app.apk"]);
        var second = ShizukuUsageScanner.ScanDexText(["pm install /data/local/tmp/app.apk"]);

        var merged = ShizukuUsageScanner.Merge(first, second);

        Assert.Single(merged.Evidence);
        Assert.Equal(["install"], merged.Capabilities);
    }

    [Fact]
    public void EncodeDecodeRoundTripsEvidence()
    {
        var usage = ShizukuUsageScanner.Merge(
            ShizukuUsageScanner.ScanPermissions(["moe.shizuku.manager.permission.API_V23"], "apk"),
            ShizukuUsageScanner.ScanFiles([("src/Main.kt", "Shizuku.bindUserService(...)")]));

        var rebuilt = ShizukuUsageScanner.FromEvidence(ShizukuUsageScanner.Encode(usage.Evidence));

        Assert.Equal(usage.Managers, rebuilt.Managers);
        Assert.Equal(usage.ApiForm, rebuilt.ApiForm);
        Assert.Equal(usage.Capabilities, rebuilt.Capabilities);
        Assert.Equal(usage.Optional, rebuilt.Optional);
        Assert.Equal(usage.Evidence.Count, rebuilt.Evidence.Count);
    }

    [Fact]
    public void SanitizesPipeCharactersInEvidenceValues()
    {
        var encoded = ShizukuUsageScanner.Encode(
            [UsageEvidence.Strong("command", "pm install|pm uninstall", "apk")]);

        Assert.Equal(["command|pm install/pm uninstall|apk|strong"], encoded);

        var rebuilt = ShizukuUsageScanner.FromEvidence(encoded);

        Assert.Equal("pm install/pm uninstall", Assert.Single(rebuilt.Evidence).Value);
    }

    [Fact]
    public void IgnoresBlankTextsAndUnknownContent()
    {
        var usage = ShizukuUsageScanner.Merge(
            ShizukuUsageScanner.ScanDexText([""]),
            ShizukuUsageScanner.ScanFiles([("src/Main.kt", "val x = 1")]));

        Assert.True(usage.IsEmpty);
    }

    [Fact]
    public void ScanApkReadsPermissionsAndDexMarkers()
    {
        var apk = Path.Combine(Path.GetTempPath(), $"usage-{Guid.NewGuid():N}.apk");
        var dex = "Lrikka/shizuku/Shizuku; bindUserService newProcess"u8.ToArray();
        try
        {
            File.WriteAllBytes(apk, TestAssets.BuildApk(("classes.dex", dex)));

            var usage = ShizukuUsageScanner.ScanApk(apk, ["moe.shizuku.manager.permission.API_V23"]);

            Assert.Equal(["shizuku"], usage.Managers);
            Assert.Equal("user_service", usage.ApiForm);
            Assert.Contains(usage.Evidence, e => e.Source == "apk" && e.Kind == "permission");
        }
        finally
        {
            File.Delete(apk);
        }
    }
}

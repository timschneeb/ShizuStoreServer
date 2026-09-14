using System.IO.Compression;
using ShizuAppStoreServer.Core.Enrichment;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Shared builders for enrichment tests (solid PNGs, fake APK zips, canned badging).</summary>
internal static class TestAssets
{
    public static byte[] SolidPng(int width, int height, Color color)
    {
        using var image = new Image<Rgba32>(width, height, color.ToPixel<Rgba32>());
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    public static byte[] SolidWebp(int width, int height, Color color)
    {
        using var image = new Image<Rgba32>(width, height, color.ToPixel<Rgba32>());
        using var ms = new MemoryStream();
        image.Save(ms, new WebpEncoder());
        return ms.ToArray();
    }

    public static byte[] BuildApk(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(content);
            }
        }

        return ms.ToArray();
    }

    public const string MdpiIcon = "res/mipmap-mdpi-v4/ic_launcher.png";
    public const string XxxhdpiIcon = "res/mipmap-xxxhdpi-v4/ic_launcher.png";

    public static string CannedBadging(string package = "com.example.app", string versionCode = "42",
        string? versionName = "1.2.3", string? minSdk = "24", string? iconPath = null) => $"""
        package: name='{package}' versionCode='{versionCode}' versionName='{versionName}' platformBuildVersionName='14' platformBuildVersionCode='34' compileSdkVersion='34' compileSdkVersionCodename='14'
        minSdkVersion:'{minSdk}'
        targetSdkVersion:'34'
        uses-permission: name='android.permission.INTERNET'
        application-label:'Example'
        application-icon-160:'{iconPath ?? MdpiIcon}'
        application-icon-640:'{iconPath ?? XxxhdpiIcon}'
        application: label='Example' icon='{MdpiIcon}'
        launchable-activity: name='{package}.MainActivity'
        """;
}

public sealed class BadgingParserTests
{
    [Fact]
    public void ParsesFullOutput()
    {
        var info = BadgingParser.Parse(TestAssets.CannedBadging());

        Assert.Equal("com.example.app", info.PackageName);
        Assert.Equal(42, info.VersionCode);
        Assert.Equal("1.2.3", info.VersionName);
        Assert.Equal(24, info.MinSdk);
        Assert.Equal(2, info.Icons.Count);
        Assert.Equal(["android.permission.INTERNET"], info.Permissions);
    }

    [Fact]
    public void ParsesUsesPermissionsIncludingSdk23AndDeduplicates()
    {
        const string output = """
            package: name='com.x' versionCode='1'
            uses-permission: name='android.permission.INTERNET'
            uses-permission-sdk-23: name='android.permission.POST_NOTIFICATIONS'
            uses-permission: name='android.permission.INTERNET'
            """;

        var info = BadgingParser.Parse(output);

        Assert.Equal(
            ["android.permission.INTERNET", "android.permission.POST_NOTIFICATIONS"],
            info.Permissions);
    }

    [Fact]
    public void ToleratesMissingOptionalFields()
    {
        var info = BadgingParser.Parse("package: name='com.x' versionCode='7'\n");
        Assert.Equal("com.x", info.PackageName);
        Assert.Equal(7, info.VersionCode);
        Assert.Null(info.VersionName);
        Assert.Null(info.MinSdk);
        Assert.Empty(info.Icons);
    }

    [Fact]
    public void ParsesHugeVersionCodesAsLong()
    {
        var info = BadgingParser.Parse("package: name='com.x' versionCode='5000000000' versionName='9'\n");
        Assert.Equal(5_000_000_000L, info.VersionCode);
    }

    [Fact]
    public void MapsUnqualifiedIconToDensityZero()
    {
        var info = BadgingParser.Parse("package: name='com.x' versionCode='1'\napplication-icon:'res/drawable/icon.png'\n");
        var icon = Assert.Single(info.Icons);
        Assert.Equal(0, icon.Density);
        Assert.Equal("res/drawable/icon.png", icon.Path);
    }

    [Fact]
    public void ThrowsWithoutPackageLine() =>
        Assert.Throws<BadgingParseException>(() => BadgingParser.Parse("sdkVersion:'24'\n"));

    [Fact]
    public void ParsesVerbatimAapt2Output()
    {
        // Byte-for-byte shape of `aapt2 dump badging` from build-tools 35.0.0
        // (minimal APK assembled locally; see HANDOFF M3 notes).
        const string real = """
            package: name='com.example.apktest' versionCode='42' versionName='1.2.3' platformBuildVersionName='14' platformBuildVersionCode='34' compileSdkVersion='34' compileSdkVersionCodename='14'
            minSdkVersion:'24'
            targetSdkVersion:'34'
            application-label:'ApkTest'
            application-icon-160:'res/mipmap-mdpi-v4/ic_launcher.png'
            application-icon-640:'res/mipmap-xxxhdpi-v4/ic_launcher.png'
            application: label='ApkTest' icon='res/mipmap-mdpi-v4/ic_launcher.png'
            feature-group: label=''
              uses-feature: name='android.hardware.faketouch'
              uses-implied-feature: name='android.hardware.faketouch' reason='default feature for all apps'
            supports-screens: 'small' 'normal' 'large' 'xlarge'
            supports-any-density: 'true'
            locales: '--_--'
            densities: '160' '640'
            """;
        var info = BadgingParser.Parse(real);

        Assert.Equal("com.example.apktest", info.PackageName);
        Assert.Equal(42, info.VersionCode);
        Assert.Equal("1.2.3", info.VersionName);
        Assert.Equal(24, info.MinSdk);
        Assert.Equal(
            ["res/mipmap-mdpi-v4/ic_launcher.png", "res/mipmap-xxxhdpi-v4/ic_launcher.png"],
            info.Icons.OrderBy(i => i.Density).Select(i => i.Path));
    }
}

public sealed class IconProcessorTests
{
    private static string WriteTempApk(byte[] apk)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shizu-test-{Guid.NewGuid():N}.apk");
        File.WriteAllBytes(path, apk);
        return path;
    }

    [Fact]
    public void PicksHighestDensityAndNormalizesTo192()
    {
        var apk = TestAssets.BuildApk(
            (TestAssets.MdpiIcon, TestAssets.SolidPng(48, 48, Color.Red)),
            (TestAssets.XxxhdpiIcon, TestAssets.SolidPng(512, 512, Color.Blue)));
        var path = WriteTempApk(apk);
        try
        {
            var icon = IconProcessor.ExtractBestIcon(path, BadgingParser.Parse(TestAssets.CannedBadging()));
            Assert.NotNull(icon);
            using var image = Image.Load<Rgba32>(icon.Png);
            Assert.Equal(192, image.Width);
            Assert.Equal(192, image.Height);
            // Blue (xxxhdpi) won over red (mdpi).
            Assert.Equal(Color.Blue.ToPixel<Rgba32>(), image[100, 100]);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(icon.Png)), icon.Sha256);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeepsSmallIconsAtNativeSize()
    {
        var apk = TestAssets.BuildApk((TestAssets.MdpiIcon, TestAssets.SolidPng(48, 48, Color.Red)));
        var badging = BadgingParser.Parse(
            $"package: name='com.x' versionCode='1'\napplication-icon-160:'{TestAssets.MdpiIcon}'\n");
        var path = WriteTempApk(apk);
        try
        {
            var icon = IconProcessor.ExtractBestIcon(path, badging);
            Assert.NotNull(icon);
            using var image = Image.Load(icon.Png);
            Assert.Equal(48, image.Width);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReturnsNullForXmlOnlyIcon()
    {
        var apk = TestAssets.BuildApk(("res/mipmap-anydpi-v26/ic_launcher.xml", "<adaptive-icon/>"u8.ToArray()));
        var badging = BadgingParser.Parse(
            "package: name='com.x' versionCode='1'\napplication-icon-640:'res/mipmap-anydpi-v26/ic_launcher.xml'\n");
        var path = WriteTempApk(apk);
        try
        {
            Assert.Null(IconProcessor.ExtractBestIcon(path, badging));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReturnsNullForCorruptPng()
    {
        var apk = TestAssets.BuildApk((TestAssets.MdpiIcon, [1, 2, 3, 4]));
        var badging = BadgingParser.Parse(
            $"package: name='com.x' versionCode='1'\napplication-icon-160:'{TestAssets.MdpiIcon}'\n");
        var path = WriteTempApk(apk);
        try
        {
            Assert.Null(IconProcessor.ExtractBestIcon(path, badging));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// End-to-end check with a real <c>aapt2</c> binary + real APK. Opt-in via
/// env (<c>SHIZU_REAL_AAPT2</c> + <c>SHIZU_REAL_APK</c>); skipped otherwise,
/// like M2's real-clone history test.
/// </summary>
public sealed class RealAapt2SmokeTests
{
    [Fact]
    public async Task RealAapt2BadgingAndIconExtraction()
    {
        var aapt2 = Environment.GetEnvironmentVariable("SHIZU_REAL_AAPT2");
        var apk = Environment.GetEnvironmentVariable("SHIZU_REAL_APK");
        if (aapt2 is null || apk is null)
        {
            return;
        }

        var info = BadgingParser.Parse(await new Aapt2Runner(aapt2).DumpBadgingAsync(apk));
        Assert.False(string.IsNullOrEmpty(info.PackageName));
        Assert.NotNull(info.VersionCode);
        Assert.NotNull(info.MinSdk);

        var icon = IconProcessor.ExtractBestIcon(apk, info);
        Assert.NotNull(icon);
        using var image = Image.Load(icon.Png);
        Assert.True(image.Width <= 192 && image.Height <= 192);
    }
}

public sealed class LetterAvatarTests
{
    [Fact]
    public void Renders192pxPngDeterministically()
    {
        var a = LetterAvatarGenerator.Generate("MicUp");
        var b = LetterAvatarGenerator.Generate("MicUp");
        Assert.Equal(a.Sha256, b.Sha256);
        using var image = Image.Load(a.Png);
        Assert.Equal(192, image.Width);
        Assert.Equal(192, image.Height);
    }

    [Fact]
    public void DiffersPerName()
    {
        var a = LetterAvatarGenerator.Generate("Alpha");
        var b = LetterAvatarGenerator.Generate("Beta");
        Assert.NotEqual(a.Sha256, b.Sha256);
    }

    [Theory]
    [InlineData("micUp", 'M')]
    [InlineData("7-Zip", '7')]
    [InlineData("", '?')]
    [InlineData("---", '?')]
    [InlineData("éclair", '?')] // Non-ASCII letter has no glyph → fallback.
    public void PicksDisplayableLetter(string name, char expected) =>
        Assert.Equal(expected, LetterAvatarGenerator.PickLetter(name));
}

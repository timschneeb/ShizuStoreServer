using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UsageReportValidatorTests
{
    private const string ValidJson = """
        {"short":"Can install apps using PackageManager.","markdown_usage":"Installs packages via `PackageManager`."}
        """;

    [Fact]
    public void AcceptsReportAndNormalizesShort()
    {
        var report = UsageReportValidator.Validate(ValidJson, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.Equal("Can install apps using PackageManager.", report.Short);
        Assert.Contains("PackageManager", report.MarkdownUsage);
        Assert.Null(report.MarkdownApiUsage);
        Assert.Null(report.MarkdownNotableDetails);
        Assert.Contains("PackageManager", report.ComposedMarkdown);
    }

    [Fact]
    public void StripsHtmlImagesAndOffAllowlistLinks()
    {
        var json = """
            {"short":"Can install apps.","markdown_usage":"<script>alert(1)</script>Installs apps <b>silently</b>.\n\n![shot](https://evil.example/x.png)\n\nSee [docs](https://evil.example/page) and [code](app/src/main/java/Installer.kt#L3)."}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.DoesNotContain("script", report.MarkdownUsage);
        Assert.DoesNotContain("<b>", report.MarkdownUsage);
        Assert.DoesNotContain("evil.example", report.MarkdownUsage);
        Assert.DoesNotContain("github.com", report.MarkdownUsage);
        Assert.Contains("docs", report.MarkdownUsage);
        Assert.Contains("code", report.MarkdownUsage);
    }

    [Fact]
    public void RejectsFilePathsInVisibleText()
    {
        var json = """
            {"short":"Can install apps.","markdown_usage":"Installs via `app/src/main/java/Installer.kt:3`."}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(report);
        Assert.Contains("file paths", error);
    }

    [Fact]
    public void RejectsOnboardingAndPermissionGrantNarration()
    {
        var json = """
            {"short":"Can record calls.","markdown_usage":"**Grant Shizuku access**: from the onboarding screen the app asks the user to approve the Shizuku permission request, and only proceeds once that grant is confirmed."}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(report);
        Assert.Contains("setup", error);
    }

    [Fact]
    public void AcceptsOtherAppsPermissionDialogsAsACapability()
    {
        var json = """
            {"short":"Can dismiss system permission dialogs.","markdown_usage":"Automatically taps Allow on other apps' permission dialogs."}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
    }

    [Fact]
    public void AcceptsASeparateApiListAndComposesTheHeadings()
    {
        var json = """
            {"short":"Can record calls.","markdown_usage":"Records calls through a user service.\n\n- **Record calls**: both directions are captured.","markdown_api_usage":"- `IActivityManager.startActivityAsUser`\n- `AudioRecord` with `MediaRecorder.AudioSource.VOICE_CALL`\n- `pm install`"}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.Contains("IActivityManager.startActivityAsUser", report.MarkdownApiUsage);
        Assert.Contains("## Android APIs or commands used", report.ComposedMarkdown);
        Assert.DoesNotContain("## Android APIs or commands used", report.MarkdownUsage);
    }

    [Fact]
    public void AcceptsNotableDetailsAsItsOwnMember()
    {
        var json = """
            {"short":"Can record calls.","markdown_usage":"Records calls through a user service.\n\n- **Record calls**: both sides are captured.","markdown_api_usage":"- `AudioRecord`","markdown_notable_details":"A fallback records only the microphone when Shizuku is unavailable."}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.Contains("fallback", report.MarkdownNotableDetails);
        Assert.Contains("## Notable details", report.ComposedMarkdown);
    }

    [Fact]
    public void RejectsAUsageReportWhoseCapabilitySectionIsNotABulletList()
    {
        var json = """
            {"short":"Can change settings.","markdown_usage":"Changes settings.\n\n**Change display size**: a value is applied with `wm density`.","markdown_api_usage":"- `wm density`"}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(report);
        Assert.Contains("bullet list", error);
    }

    [Fact]
    public void RejectsCapabilityBulletsWithoutBoldLabels()
    {
        var json = """
            {"short":"Can change settings.","markdown_usage":"Changes settings.\n\n* Change display size with `wm density`.","markdown_api_usage":"- `wm density`"}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(report);
        Assert.Contains("bold label", error);
    }

    [Fact]
    public void AcceptsStarBulletsWithBoldLabels()
    {
        var json = """
            {"short":"Can change settings.","markdown_usage":"Changes settings.\n\n* **Change display size**: a value is applied with `wm density`.","markdown_api_usage":"- `wm density`"}
            """;

        var report = UsageReportValidator.Validate(json, out _);

        Assert.NotNull(report);
    }

    [Fact]
    public void RejectsShizukuSdkHelpersInTheApiList()
    {
        var json = """
            {"short":"Can install apps.","markdown_usage":"Installs apps through a privileged path.\n\n- **Install apps**: packages are installed silently.","markdown_api_usage":"- `Shizuku.newProcess`\n- `IPackageManager.installPackage`"}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(report);
        Assert.NotNull(error);
        Assert.Contains("Shizuku SDK helpers", error);
    }

    [Fact]
    public void RejectsApiProseLines()
    {
        var json = """
            {"short":"Can record calls.","markdown_usage":"Records calls through a user service.\n\n- **Record calls**: both sides are captured.","markdown_api_usage":"The privileged path calls:\n- `AudioRecord`"}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(report);
        Assert.NotNull(error);
        Assert.Contains("no prose lines", error);
    }

    [Fact]
    public void RejectsApiBulletsWithoutBackticks()
    {
        var json = """
            {"short":"Can change settings.","markdown_usage":"Changes settings.\n\n- **Change display size**: a value is applied with the density command.","markdown_api_usage":"- wm density"}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(report);
        Assert.NotNull(error);
        Assert.Contains("backticks", error);
    }

    [Fact]
    public void RepairsAnApiListWhoseFirstLineMissesTheBullet()
    {
        var json = """
            {"short":"Can record calls.","markdown_usage":"Records calls through a user service.\n\n- **Record calls**: both sides are captured.","markdown_api_usage":"`AudioRecord`\n* `pm install`"}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.StartsWith("- `AudioRecord`", report.MarkdownApiUsage);
    }

    [Fact]
    public void JoinsAnApiArrayIntoTheList()
    {
        var json = """
            {"short":"Can install apps.","markdown_usage":"Installs packages through a user service.\n\n- **Install apps**: packages are installed silently.","markdown_api_usage":["`cmd package install -r -S`","`pm install`"]}
            """;

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.StartsWith("- `cmd package install -r -S`", report.MarkdownApiUsage);
        Assert.Contains("- `pm install`", report.MarkdownApiUsage);
    }

    [Fact]
    public void NormalizesEmDashesAndFencedJson()
    {
        var json = "```json\n{\"short\":\"Can install apps \u2014 safely.\",\"markdown_usage\":\"Installs via `PackageManager` \u2014 silently.\"}\n```";

        var report = UsageReportValidator.Validate(json, out var error);

        Assert.Null(error);
        Assert.NotNull(report);
        Assert.DoesNotContain('\u2014', report.Short);
        Assert.DoesNotContain('\u2014', report.MarkdownUsage);
        Assert.Contains("Can install apps - safely.", report.Short);
    }

    [Fact]
    public void RejectsMalformedJson() =>
        Assert.Null(UsageReportValidator.Validate("not json at all", out _));
}

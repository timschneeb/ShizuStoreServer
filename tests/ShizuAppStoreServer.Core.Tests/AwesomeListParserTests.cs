using ShizuAppStoreServer.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class AwesomeListParserTests(ITestOutputHelper output)
{
    private static readonly AwesomeListParser Parser = new();

    private const string AudioSection = """
        ## Apps

        ### Audio

        * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time microphone audio processing for Android `MIT`
        * [RootlessJamesDSP](https://play.google.com/store/apps/details?id=me.timschneeberger.rootlessjamesdsp) - An implementation of the system-wide JamesDSP audio processing engine for non-rooted Android devices `GPL-3.0` [(Source code)](https://github.com/timschneeb/RootlessJamesDSP)
        """;

    [Fact]
    public void ParsesBasicEntries()
    {
        var doc = Parser.Parse(AudioSection, "main");

        var audio = Assert.Single(doc.Categories);
        Assert.Equal("Apps", audio.Section);
        Assert.Equal("Audio", audio.Name);
        Assert.Null(audio.Subcategory);
        Assert.Equal(2, audio.Entries.Count);

        var micUp = audio.Entries[0];
        Assert.Equal("MicUp", micUp.Name);
        Assert.Equal("https://github.com/papergray/MicUp", micUp.Url);
        Assert.Equal("Real-time microphone audio processing for Android", micUp.Description);
        Assert.Equal("MIT", micUp.License);
        Assert.Null(micUp.SourceUrl);
        Assert.True(micUp.IsRecommended);

        var dsp = audio.Entries[1];
        Assert.Equal("RootlessJamesDSP", dsp.Name);
        Assert.Equal("GPL-3.0", dsp.License);
        Assert.Equal("https://github.com/timschneeb/RootlessJamesDSP", dsp.SourceUrl);
        Assert.False(dsp.IsRecommended);

        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void ParsesMonetizationAndTrialTags()
    {
        const string markdown = """
            ## Apps

            ### Software management

            * [Inure App Manager](https://play.google.com/store/apps/details?id=app.simple.inure.play) `15-day trial` `IAP` 💰 - Android app manager for both rooted and non-rooted devices `GPL-3.0` [(Source code)](https://github.com/Hamza417/Inure)
            * [MMRL](https://github.com/MMRLApp/MMRL) `Root` - Manage your Magisk module repository `GPL-3.0`
            """;

        var doc = Parser.Parse(markdown, "main");
        var entries = Assert.Single(doc.Categories).Entries;

        Assert.Equal(15, entries[0].TrialDays);
        Assert.True(entries[0].HasIap);
        Assert.False(entries[0].HasPaid);

        Assert.True(entries[1].RequiresRoot);
        Assert.Null(entries[1].TrialDays);

        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void NormalizesPropietaryTypo()
    {
        const string markdown = """
            ## Apps

            ### Android TV

            * [RecentAppsTV](https://github.com/Qutaiba-Khader/RecentAppsTV) - Recent Apps overlay for Android TV `Propietary`
            """;

        var doc = Parser.Parse(markdown, "main");
        Assert.Equal("Proprietary", Assert.Single(Assert.Single(doc.Categories).Entries).License);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void ParsesNestedEntriesAsChildren()
    {
        const string markdown = """
            ## Apps

            ### Terminals

            * [aShell](https://gitlab.com/sunilpaulmathew/ashell) - A local ADB shell for Shizuku-powered Android devices `GPL-3.0`
              * [aShell You](https://github.com/DP-Hridayan/aShellYou) - Material You Redesign of aShell app. `GPL-3.0`
            * [Haven](https://f-droid.org/packages/sh.haven.app/) - Terminal, SSH, VNC, RDP, SFTP & cloud storage client for Android `AGPL-3.0` [(Source code)](https://github.com/GlassHaven/Haven)
            """;

        var doc = Parser.Parse(markdown, "main");
        var entries = Assert.Single(doc.Categories).Entries;

        Assert.Equal(2, entries.Count);
        var child = Assert.Single(entries[0].Children);
        Assert.Equal("aShell You", child.Name);
        Assert.Equal("https://github.com/DP-Hridayan/aShellYou", child.Url);
        Assert.Empty(entries[1].Children);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void AttachesSubcategories()
    {
        const string markdown = """
            ## Apps

            ### Vendor-specific

            #### Samsung OneUI

            * [ShutterMute](https://github.com/ajebulon/ShutterMute) - Disable the forced camera shutter sounds `Proprietary`

            #### MIUI

            * [Aura](https://github.com/tgvdufuture/Aura) - Custom RGB notification LED app `MIT`
            """;

        var doc = Parser.Parse(markdown, "main");

        Assert.Equal(2, doc.Categories.Count);
        Assert.Equal("Vendor-specific", doc.Categories[0].Name);
        Assert.Equal("Samsung OneUI", doc.Categories[0].Subcategory);
        Assert.Equal("ShutterMute", Assert.Single(doc.Categories[0].Entries).Name);
        Assert.Equal("MIUI", doc.Categories[1].Subcategory);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void SkipsIgnoredSections()
    {
        const string markdown = """
            ## Table of contents

            - [Apps](#apps)

            ## Apps

            ### Audio

            * [MicUp](https://github.com/papergray/MicUp) ✨ - Real-time microphone audio processing for Android `MIT`

            ## Annotations
            - ✨ - My personal recommendation

            ## License

            This list is licensed under CC-BY-SA.
            """;

        var doc = Parser.Parse(markdown, "main");

        var category = Assert.Single(doc.Categories);
        Assert.Equal("MicUp", Assert.Single(category.Entries).Name);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void KeepsMalformedEntriesWithWarningsInsteadOfCrashing()
    {
        const string markdown = """
            ## Apps

            ### Audio

            * [Broken](https://example.com) - No license tag here
            * [MicUp](https://github.com/papergray/MicUp) - Real-time microphone audio processing for Android `MIT`
            """;

        var doc = Parser.Parse(markdown, "main");
        var entries = Assert.Single(doc.Categories).Entries;

        Assert.Equal(2, entries.Count);
        Assert.Null(entries[0].License);
        Assert.Equal("No license tag here", entries[0].Description);
        Assert.Equal("MIT", entries[1].License);
        Assert.Single(doc.Warnings);
    }

    [Fact]
    public void ParsesRealReadme()
    {
        var readme = FindAwesomeShizukuReadme();
        if (readme is null)
        {
            // Dev-machine only: the awesome-shizuku checkout is a sibling directory.
            return;
        }

        var doc = Parser.Parse(File.ReadAllText(readme), "main");

        foreach (var warning in doc.Warnings)
        {
            output.WriteLine($"WARN [{warning.Location}] {warning.Message}");
        }

        var allEntries = doc.Categories.SelectMany(c => c.Entries).ToList();
        Assert.True(allEntries.Count > 300, $"Expected >300 entries, got {allEntries.Count}.");
        Assert.True(doc.Categories.Count > 20, $"Expected >20 categories, got {doc.Categories.Count}.");

        var hail = allEntries.FirstOrDefault(e => e.Name == "Hail");
        Assert.NotNull(hail);
        Assert.True(hail.IsRecommended);

        var inure = allEntries.FirstOrDefault(e => e.Name == "Inure App Manager");
        Assert.NotNull(inure);
        Assert.Equal(15, inure.TrialDays);
        Assert.True(inure.HasIap);

        var pixel = doc.Categories.FirstOrDefault(c => c.Subcategory == "Google Pixel");
        Assert.NotNull(pixel);
        Assert.NotEmpty(pixel.Entries);
    }

    private static string? FindAwesomeShizukuReadme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "awesome-shizuku", "README.md");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

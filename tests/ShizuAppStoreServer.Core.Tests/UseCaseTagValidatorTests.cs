using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UseCaseTagValidatorTests
{
    private static readonly string[] Allowed = ["install-apps", "file-access", "settings-writes"];

    [Fact]
    public void AcceptsKnownVocabularySlugs()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": ["install-apps", "file-access"]}""", Allowed, 6, 3, out var error);

        Assert.NotNull(report);
        Assert.Null(error);
        Assert.Equal(["install-apps", "file-access"], report!.UseCases);
    }

    [Fact]
    public void AcceptsAnEmptyUseCaseArray()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": []}""", Allowed, 6, 3, out var error);

        Assert.NotNull(report);
        Assert.Null(error);
        Assert.Empty(report!.UseCases);
    }

    [Fact]
    public void RejectsAnUnknownSlugAndFailsClosed()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": ["install-apps", "teleport-presence"]}""", Allowed, 6, 3, out var error);

        Assert.Null(report);
        Assert.Contains("not an active use case", error!);
    }

    [Fact]
    public void RejectsAMissingUseCaseArray()
    {
        var report = UseCaseTagValidator.Validate("""{"proposed_use_cases": []}""", Allowed, 6, 3, out var error);

        Assert.Null(report);
        Assert.Contains("'use_cases'", error!);
    }

    [Fact]
    public void RejectsNonStringUseCaseEntries()
    {
        var report = UseCaseTagValidator.Validate("""{"use_cases": [1]}""", Allowed, 6, 3, out var error);

        Assert.Null(report);
        Assert.Contains("strings", error!);
    }

    [Fact]
    public void DedupesRepeatedSlugs()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": ["install-apps", "install-apps", "file-access"]}""", Allowed, 6, 3, out _);

        Assert.Equal(["install-apps", "file-access"], report!.UseCases);
    }

    [Fact]
    public void CapsTheTagCount()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": ["install-apps", "file-access", "settings-writes"]}""", Allowed, 1, 3, out _);

        Assert.Equal(["install-apps"], report!.UseCases);
    }

    [Fact]
    public void ParsesAndNormalizesProposals()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": [], "proposed_use_cases": [{"name": "Wireless ADB", "reason": "Toggles wireless debugging."}]}""",
            Allowed, 6, 3, out _);

        var proposal = Assert.Single(report!.Proposals);
        Assert.Equal("wireless-adb", proposal.Slug);
        Assert.Equal("Wireless ADB", proposal.Name);
        Assert.Equal("Toggles wireless debugging.", proposal.Reason);
    }

    [Fact]
    public void DropsAProposalReasonThatWasEmpty()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": [], "proposed_use_cases": [{"name": "Wireless ADB", "reason": "   "}]}""",
            Allowed, 6, 3, out _);

        Assert.Null(Assert.Single(report!.Proposals).Reason);
    }

    [Fact]
    public void SkipsProposalsThatCollideWithTheVocabulary()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": [], "proposed_use_cases": [{"name": "Install Apps"}]}""", Allowed, 6, 3, out _);

        Assert.Empty(report!.Proposals);
    }

    [Fact]
    public void SkipsDuplicateProposals()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": [], "proposed_use_cases": [{"name": "Wireless ADB"}, {"name": "wireless-adb"}]}""",
            Allowed, 6, 3, out _);

        Assert.Single(report!.Proposals);
    }

    [Fact]
    public void CapsTheProposalCount()
    {
        var report = UseCaseTagValidator.Validate(
            """{"use_cases": [], "proposed_use_cases": [{"name": "One"}, {"name": "Two"}, {"name": "Three"}]}""",
            Allowed, 6, 1, out _);

        Assert.Equal("one", Assert.Single(report!.Proposals).Slug);
    }

    [Fact]
    public void CapsTheProposalNameLength()
    {
        var name = new string('a', 80);
        var report = UseCaseTagValidator.Validate(
            $$"""{"use_cases": [], "proposed_use_cases": [{"name": "{{name}}"}]}""", Allowed, 6, 3, out _);

        Assert.True(Assert.Single(report!.Proposals).Name.Length <= 60);
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.Null(UseCaseTagValidator.Validate("not json at all", Allowed, 6, 3, out _));
    }

    [Theory]
    [InlineData("Wireless ADB", "wireless-adb")]
    [InlineData("  Mixed CASE  ", "mixed-case")]
    [InlineData("a--b", "a-b")]
    [InlineData("Trailing-", "trailing")]
    public void SlugifyNormalizesNames(string input, string expected)
    {
        Assert.Equal(expected, UseCaseTagValidator.Slugify(input));
    }
}

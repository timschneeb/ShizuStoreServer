using ShizuAppStoreServer.Core.Overrides;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Pins the public catalog/validation surface operator tooling (the stats
/// dashboard) builds against: the full allowlist, group order, normalization
/// and the exact error strings the applier also reports.
/// </summary>
public sealed class AppOverrideFieldsContractTests
{
    [Fact]
    public void CatalogListsEveryColumnFieldAndTheSourceKinds()
    {
        Assert.Equal(37, AppOverrideFields.Catalog.Count);
        Assert.Equal(12, AppOverrideFields.Catalog.Count(f => f.Ownership == AppOverrideOwnership.List));
        Assert.Equal(19, AppOverrideFields.Catalog.Count(f => f.Ownership == AppOverrideOwnership.Enrichment));
        Assert.Equal(2, AppOverrideFields.Catalog.Count(f => f.Ownership == AppOverrideOwnership.Visibility));
        Assert.Equal(4, AppOverrideFields.Catalog.Count(f => f.Ownership == AppOverrideOwnership.Source));
        Assert.Equal(
            AppOverrideFields.Catalog.Count,
            AppOverrideFields.Catalog.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CatalogGroupsAreOrderedAndAlphabetical()
    {
        var groups = AppOverrideFields.Catalog.Select(f => f.Ownership).ToList();
        var firstSource = groups.IndexOf(AppOverrideOwnership.Source);
        Assert.Equal(
            AppOverrideOwnership.Source,
            groups[^1]);
        Assert.DoesNotContain(AppOverrideOwnership.Source, groups.Take(firstSource));
        Assert.Equal(37, firstSource + 4);

        foreach (var ownership in new[]
                 {
                     AppOverrideOwnership.List,
                     AppOverrideOwnership.Enrichment,
                     AppOverrideOwnership.Visibility,
                 })
        {
            var names = AppOverrideFields.Catalog
                .Where(f => f.Ownership == ownership)
                .Select(f => f.Name)
                .ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        }
    }

    [Fact]
    public void CatalogContainsTheKnownListAndEnrichmentFields()
    {
        var names = AppOverrideFields.Catalog.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
                 {
                     "name", "description", "added_at", "list_updated_at", "license", "source_url",
                     "display_name", "package_name", "permissions", "screenshots", "icon_hash",
                     "availability", "excluded_reason",
                     "source_release_home", "source_gitcode_mirror", "source_scan_all_releases",
                     "source_prefer_prerelease",
                 })
        {
            Assert.Contains(expected, names);
        }
    }

    [Theory]
    [InlineData("name", "MicUp", "MicUp")]
    [InlineData("display_name", "My Tuner", "My Tuner")]
    [InlineData("is_recommended", "TRUE", "true")]
    [InlineData("trial_days", "30", "30")]
    [InlineData("download_total", "123456789", "123456789")]
    [InlineData("added_at", "2024-01-02T03:04:05+02:00", "2024-01-02T01:04:05.0000000+00:00")]
    [InlineData("list_updated_at", "", "")]
    [InlineData("availability", "DirectApk", "direct_apk")]
    [InlineData("availability", "link_only", "link_only")]
    [InlineData("permissions", "A\n\nB\r\nC", "A\nB\nC")]
    [InlineData("icon_hash", "", "")]
    [InlineData("source_release_home", " instafel/u-rel ", "instafel/u-rel")]
    [InlineData("source_scan_all_releases", "true", "true")]
    public void ValidValuesNormalize(string field, string value, string expected)
    {
        Assert.Null(AppOverrideFields.Validate(field, value, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void UppercaseIconHashNormalizesToLowercase()
    {
        var value = new string('A', 64);
        Assert.Null(AppOverrideFields.Validate("icon_hash", value, out var normalized));
        Assert.Equal(new string('a', 64), normalized);
    }

    [Theory]
    [InlineData("name", "", "value is required")]
    [InlineData("description", "", "value is required")]
    [InlineData("added_at", "", "value is required")]
    [InlineData("added_at", "not-a-date", "expected an ISO-8601 timestamp")]
    [InlineData("list_updated_at", "not-a-date", "expected an ISO-8601 timestamp")]
    [InlineData("is_recommended", "yes", "expected true or false")]
    [InlineData("trial_days", "x", "expected an integer")]
    [InlineData("trial_days", "-1", "must not be negative")]
    [InlineData("stars", "-5", "must not be negative")]
    [InlineData("download_total", "-1", "must not be negative")]
    [InlineData("icon_hash", "abc", "expected 64 hex characters")]
    [InlineData("availability", "bogus", "expected direct_apk, play_redirect, link_only or excluded")]
    [InlineData("source_release_home", "owner", "expected owner/repo")]
    [InlineData("source_gitcode_mirror", "owner/repo", "expected targetOwner/targetRepo|readmeOwner/readmeRepo")]
    [InlineData("source_prefer_prerelease", "maybe", "expected true or false")]
    [InlineData("id", "1", "unknown field")]
    public void InvalidValuesReturnTheApplierErrors(string field, string value, string expected)
    {
        Assert.Equal(expected, AppOverrideFields.Validate(field, value, out var normalized));
        Assert.Equal(value, normalized);
    }

    [Theory]
    [InlineData("name", "text, required")]
    [InlineData("availability", "direct_apk, play_redirect, link_only or excluded")]
    [InlineData("icon_hash", "64 hex characters, empty clears")]
    [InlineData("screenshots", "newline-separated URLs, empty clears")]
    [InlineData("source_release_home", "owner/repo")]
    [InlineData("package_name", "text, empty clears")]
    public void FormatHintsCoverTheInputShapes(string field, string expected)
    {
        Assert.Equal(expected, AppOverrideFields.FormatHint(field));
    }
}

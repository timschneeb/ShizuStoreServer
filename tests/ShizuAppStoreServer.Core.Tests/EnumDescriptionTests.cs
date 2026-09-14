using ShizuAppStoreServer.Core.Data;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Friendly names behind <c>sourceName</c> live on the enum members.</summary>
public sealed class EnumDescriptionTests
{
    [Theory]
    [InlineData(SourceKind.GitHub, "GitHub")]
    [InlineData(SourceKind.GitLab, "GitLab")]
    [InlineData(SourceKind.Codeberg, "Codeberg")]
    [InlineData(SourceKind.FDroid, "F-Droid")]
    [InlineData(SourceKind.Izzy, "IzzyOnDroid")]
    [InlineData(SourceKind.Play, "Play Store")]
    [InlineData(SourceKind.Other, "Website")]
    public void SourceKindResolvesItsFriendlyName(SourceKind kind, string expected) =>
        Assert.Equal(expected, kind.GetDescription());

    [Fact]
    public void MembersWithoutAttributeFallBackToTheirName() =>
        Assert.Equal(nameof(AppType.App), AppType.App.GetDescription());
}

using ShizuAppStoreServer.Core.Enrichment;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>Startup-gate probe tests: hermetic cases + env-gated real toolchain.</summary>
public sealed class ToolProbeTests
{
    [Fact]
    public async Task ReportsAvailableTool()
    {
        var result = await ExternalToolProbe.CheckAsync(
            "dotnet", "dotnet", ["--version"], TimeSpan.FromSeconds(30));

        Assert.True(result.Available);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Fact]
    public async Task ReportsMissingBinary()
    {
        var result = await ExternalToolProbe.CheckAsync(
            "nope", "/nonexistent/shizu-tool-xyz", [], TimeSpan.FromSeconds(10));

        Assert.False(result.Available);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
    }

    [Fact]
    public async Task ReportsNonZeroExit()
    {
        var result = await ExternalToolProbe.CheckAsync(
            "sh", "sh", ["-c", "exit 3"], TimeSpan.FromSeconds(10));

        Assert.False(result.Available);
    }

    [Fact]
    public async Task ProbesRealToolchainWhenProvided()
    {
        // Same env-gate pattern as the aapt2/apksigner tests, e.g.:
        //   SHIZU_REAL_AAPT2=/home/tim/Android/Sdk/build-tools/35.0.0/aapt2 \
        //   SHIZU_REAL_APKSIGNER=/home/tim/Android/Sdk/build-tools/35.0.0/apksigner
        var aapt2 = Environment.GetEnvironmentVariable("SHIZU_REAL_AAPT2");
        var apksigner = Environment.GetEnvironmentVariable("SHIZU_REAL_APKSIGNER");
        if (string.IsNullOrEmpty(aapt2) || string.IsNullOrEmpty(apksigner))
        {
            return; // Not a failure: env-gated by design.
        }

        var results = await Task.WhenAll(
            ExternalToolProbe.CheckAsync("aapt2", aapt2, ["version"], TimeSpan.FromSeconds(30)),
            ExternalToolProbe.CheckAsync("apksigner", apksigner, ["--version"], TimeSpan.FromSeconds(60)));

        Assert.All(results, r => Assert.True(r.Available));
    }
}

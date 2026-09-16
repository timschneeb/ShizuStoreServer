using ShizuAppStoreServer.Core.Enrichment;
using Xunit;

namespace ShizuAppStoreServer.Core.Tests;

/// <summary>
/// Salvage behavior: a killed Gradle build must not throw away an icon it
/// already wrote (production renders were killed at the timeout right after
/// the PNG hit disk). Fake gradle shell scripts, no real toolchain.
/// </summary>
public sealed class PaparazziRendererTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shizu-render-test-" + Guid.NewGuid().ToString("N"));

    public PaparazziRendererTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, the temp dir disappears with the host.
        }
    }

    private PaparazziRenderer Renderer(string script)
    {
        var path = Path.Combine(_dir, "fake-gradle");
        File.WriteAllText(path, "#!/bin/sh\n" + script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return new PaparazziRenderer(path, _dir, Limit);
    }

    [Fact]
    public async Task SingleRenderSalvagesTheOutputOfAFailedBuild()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var renderer = Renderer(
            "out=\"\"\n" +
            "for arg in \"$@\"; do case \"$arg\" in -Pout=*) out=\"${arg#-Pout=}\";; esac; done\n" +
            "printf '\\211PNG\\r\\n\\032\\nX' > \"$out\"\n" +
            "exit 1\n");

        var png = await renderer.RenderAsync(_dir, "shizu_0", 432);

        Assert.Equal(9, png.Length);
        Assert.Equal(0x89, png[0]);
    }

    [Fact]
    public async Task SingleRenderStillFailsWhenNothingUsableWasWritten()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var renderer = Renderer("exit 1\n");

        await Assert.ThrowsAsync<PaparazziException>(() => renderer.RenderAsync(_dir, "shizu_0", 432));
    }

    [Fact]
    public async Task SingleRenderRejectsANonPngLeftover()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var renderer = Renderer(
            "out=\"\"\n" +
            "for arg in \"$@\"; do case \"$arg\" in -Pout=*) out=\"${arg#-Pout=}\";; esac; done\n" +
            "printf 'partial junk' > \"$out\"\n" +
            "exit 1\n");

        await Assert.ThrowsAsync<PaparazziException>(() => renderer.RenderAsync(_dir, "shizu_0", 432));
    }

    [Fact]
    public async Task BatchRenderReadsTheIconsOfAFailedBuildAndBlanksTheRest()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var renderer = Renderer(
            "batch=\"\"\n" +
            "for arg in \"$@\"; do case \"$arg\" in -Pbatch=*) batch=\"${arg#-Pbatch=}\";; esac; done\n" +
            "first=$(head -n 1 \"$batch\" | cut -d'|' -f3)\n" +
            "printf '\\211PNG\\r\\n\\032\\nX' > \"$first\"\n" +
            "exit 1\n");

        var pngs = await renderer.RenderBatchAsync(_dir, [new("a", null), new("b", null)], 432);

        Assert.Equal(2, pngs.Length);
        Assert.NotNull(pngs[0]);
        Assert.Equal(9, pngs[0]!.Length);
        Assert.Null(pngs[1]);
    }

    [Fact]
    public async Task BatchRenderFailsWhenNoIconWasWritten()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var renderer = Renderer("exit 1\n");

        await Assert.ThrowsAsync<PaparazziException>(
            () => renderer.RenderBatchAsync(_dir, [new("a", null)], 432));
    }
}

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

    private string WriteExecutable(string name, string script)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private PaparazziRenderer Renderer(string script)
        => new(WriteExecutable("fake-gradle", script), _dir, Limit);

    // Stand-in for systemd-run: records its argv, then hands the command
    // after "--" to the fake gradle so rendering still happens.
    private string WriteRecordingSystemdRun(out string argsPath)
    {
        argsPath = Path.Combine(_dir, "scope-args");
        return WriteExecutable(
            "fake-systemd-run",
            $"printf '%s\\n' \"$@\" > '{argsPath}'\n" +
            "while [ \"$1\" != \"--\" ]; do shift; done\n" +
            "shift\n" +
            "exec \"$@\"\n");
    }

    private PaparazziRenderer ScopedRenderer(string gradleScript, string systemdRunPath)
        => new(WriteExecutable("fake-gradle", gradleScript), _dir, Limit,
            scope: new RenderScope(systemdRunPath, "1G", "1400M", "0", "150%"));

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

    [Fact]
    public async Task StopGradleDaemonsRunsTheStopTask()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var argsPath = Path.Combine(_dir, "stop-args");
        var renderer = Renderer($"printf '%s\\n' \"$@\" > '{argsPath}'\n");

        await renderer.StopGradleDaemonsAsync();

        var args = await File.ReadAllLinesAsync(argsPath);
        Assert.Contains("--stop", args);
    }

    [Fact]
    public async Task BatchRenderRunsGradleWithoutADaemon()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var argsPath = Path.Combine(_dir, "batch-args");
        var renderer = Renderer(
            $"printf '%s\\n' \"$@\" > '{argsPath}'\n" +
            "batch=\"\"\n" +
            "for arg in \"$@\"; do case \"$arg\" in -Pbatch=*) batch=\"${arg#-Pbatch=}\";; esac; done\n" +
            "first=$(head -n 1 \"$batch\" | cut -d'|' -f3)\n" +
            "printf '\\211PNG\\r\\n\\032\\nX' > \"$first\"\n");

        var pngs = await renderer.RenderBatchAsync(_dir, [new("a", null)], 432);

        Assert.NotNull(pngs[0]);
        var args = await File.ReadAllLinesAsync(argsPath);
        Assert.Contains("--no-daemon", args);
    }

    [Fact]
    public async Task SingleRenderKeepsTheWarmDaemon()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var argsPath = Path.Combine(_dir, "single-args");
        var renderer = Renderer(
            $"printf '%s\\n' \"$@\" > '{argsPath}'\n" +
            "out=\"\"\n" +
            "for arg in \"$@\"; do case \"$arg\" in -Pout=*) out=\"${arg#-Pout=}\";; esac; done\n" +
            "printf '\\211PNG\\r\\n\\032\\nX' > \"$out\"\n");

        await renderer.RenderAsync(_dir, "shizu_0", 432);

        var args = await File.ReadAllLinesAsync(argsPath);
        Assert.DoesNotContain("--no-daemon", args);
    }

    [Fact]
    public async Task ScopedRenderRunsGradleThroughASystemdUserScope()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var renderer = ScopedRenderer(
            "out=\"\"\n" +
            "for arg in \"$@\"; do case \"$arg\" in -Pout=*) out=\"${arg#-Pout=}\";; esac; done\n" +
            "printf '\\211PNG\\r\\n\\032\\nX' > \"$out\"\n",
            WriteRecordingSystemdRun(out var argsPath));

        var png = await renderer.RenderAsync(_dir, "shizu_0", 432);

        Assert.Equal(9, png.Length);
        var args = await File.ReadAllLinesAsync(argsPath);
        Assert.Equal("--user", args[0]);
        Assert.Contains("--scope", args);
        Assert.Contains("--quiet", args);
        Assert.Contains("--collect", args);
        Assert.Contains(args, a => a.StartsWith("--unit=shizu-render-", StringComparison.Ordinal));
        Assert.Contains("MemoryHigh=1G", args);
        Assert.Contains("MemoryMax=1400M", args);
        Assert.Contains("MemorySwapMax=0", args);
        Assert.Contains("CPUQuota=150%", args);
        var separator = Array.IndexOf(args, "--");
        Assert.True(separator > 0);
        Assert.Equal("nice", args[separator + 1]);
        Assert.Equal("5", args[separator + 3]);
        Assert.EndsWith("fake-gradle", args[separator + 4], StringComparison.Ordinal);
        Assert.Contains("-PiconName=shizu_0", args);
    }

    [Fact]
    public async Task ScopedRenderFailsWhenTheScopeCannotStart()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var renderer = ScopedRenderer(
            "exit 0\n",
            WriteExecutable("fake-systemd-run", "echo 'bus unavailable' >&2\nexit 1\n"));

        await Assert.ThrowsAsync<PaparazziException>(() => renderer.RenderAsync(_dir, "shizu_0", 432));
    }
}

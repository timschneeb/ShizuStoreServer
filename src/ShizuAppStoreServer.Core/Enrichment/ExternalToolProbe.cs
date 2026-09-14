using System.Diagnostics;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>One startup availability check (tool name + path + version line).</summary>
public sealed record ToolProbeResult(string Name, string Path, bool Available, string Detail);

/// <summary>
/// Startup gate for required external binaries (aapt2, apksigner): runs
/// <c>&lt;path&gt; &lt;args&gt;</c> with a timeout and requires exit 0.
/// Used once in Program.cs, per-APK enrichment failures stay best-effort.
/// </summary>
public static class ExternalToolProbe
{
    public static async Task<ToolProbeResult> CheckAsync(
        string name, string path, string[] args, TimeSpan timeout, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return new ToolProbeResult(name, path, false, ex.Message);
        }

        if (process is null)
        {
            return new ToolProbeResult(name, path, false, "process failed to start");
        }

        using (process)
        {
            string stdout, stderr;
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exiting */ }
                return new ToolProbeResult(name, path, false, $"timed out after {timeout.TotalSeconds:0}s");
            }

            if (process.ExitCode != 0)
            {
                return new ToolProbeResult(
                    name, path, false,
                    FirstLine(stdout) ?? FirstLine(stderr) ?? $"exit {process.ExitCode}");
            }

            return new ToolProbeResult(
                name, path, true,
                FirstLine(stdout) ?? FirstLine(stderr) ?? "ok");
        }
    }

    private static string? FirstLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length > 200 ? trimmed[..200] + "…" : trimmed;
            }
        }

        return null;
    }
}

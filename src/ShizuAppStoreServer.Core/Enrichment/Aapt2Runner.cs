using System.Diagnostics;
using System.Text;

namespace ShizuAppStoreServer.Core.Enrichment;

public sealed class Aapt2Exception(string message) : Exception(message);

public interface IAapt2Runner
{
    /// <returns>Stdout of <c>aapt2 dump badging &lt;apk&gt;</c>.</returns>
    /// <exception cref="Aapt2Exception">Binary missing, non-zero exit, timeout, …</exception>
    Task<string> DumpBadgingAsync(string apkPath, CancellationToken ct = default);
}

/// <summary>
/// Runs the <c>aapt2</c> binary from the Android SDK build-tools
/// (see <c>docs/server-setup.md</c>). One-shot process per APK; the APK is
/// deleted right after, so throughput is bounded by downloads, not aapt2.
/// </summary>
public sealed class Aapt2Runner(string aapt2Path) : IAapt2Runner
{
    public Task<string> DumpBadgingAsync(string apkPath, CancellationToken ct = default) =>
        RunAsync(["dump", "badging", apkPath], "dump badging", ct);

    private async Task<string> RunAsync(string[] args, string operation, CancellationToken ct)
    {
        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = aapt2Path,
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
            throw new Aapt2Exception($"Cannot start aapt2 '{aapt2Path}': {ex.Message}");
        }

        if (process is null)
        {
            throw new Aapt2Exception($"Cannot start aapt2 '{aapt2Path}'.");
        }

        using (process)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exiting */ }
                throw;
            }

            if (process.ExitCode != 0)
            {
                var err = stderr.ToString().Trim();
                if (err.Length > 500)
                {
                    err = err[..500] + "…";
                }

                throw new Aapt2Exception($"aapt2 {operation} failed (exit {process.ExitCode}): {err}");
            }

            return stdout.ToString();
        }
    }
}

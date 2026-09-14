using System.Diagnostics;
using System.Text;

namespace ShizuAppStoreServer.Core.Enrichment;

public sealed class ApkSignerException(string message) : Exception(message);

public interface IApkSignerRunner
{
    /// <returns>Stdout of <c>apksigner verify --print-certs &lt;apk&gt;</c>.</returns>
    /// <exception cref="ApkSignerException">Binary missing, non-zero exit, …</exception>
    Task<string> PrintCertsAsync(string apkPath, CancellationToken ct = default);
}

/// <summary>
/// Runs the <c>apksigner</c> binary from the Android SDK build-tools. Same one-shot pattern as
/// <see cref="Aapt2Runner"/>; callers treat failures as best-effort (sigs
/// stay null, enrichment still succeeds).
/// </summary>
public sealed class ApkSignerRunner(string apksignerPath) : IApkSignerRunner
{
    public async Task<string> PrintCertsAsync(string apkPath, CancellationToken ct = default)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = apksignerPath,
                ArgumentList = { "verify", "--print-certs", apkPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            throw new ApkSignerException($"Cannot start apksigner '{apksignerPath}': {ex.Message}");
        }

        if (process is null)
        {
            throw new ApkSignerException($"Cannot start apksigner '{apksignerPath}'.");
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
                var err = stdout.ToString().Trim();
                if (err.Length == 0)
                {
                    err = stderr.ToString().Trim();
                }

                if (err.Length > 500)
                {
                    err = err[..500] + "…";
                }

                throw new ApkSignerException($"apksigner verify failed (exit {process.ExitCode}): {err}");
            }

            return stdout.ToString();
        }
    }
}

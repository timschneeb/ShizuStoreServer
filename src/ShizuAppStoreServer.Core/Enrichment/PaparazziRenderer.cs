using System.Diagnostics;
using System.Text;

namespace ShizuAppStoreServer.Core.Enrichment;

public sealed class PaparazziException(string message) : Exception(message);

public interface IPaparazziRenderer
{
    /// <returns>Raw PNG bytes of the rendered drawable (full-bleed square).</returns>
    /// <exception cref="PaparazziException">Gradle missing, link failure, timeout, …</exception>
    Task<byte[]> RenderAsync(string stagedResDir, string drawableName, int sizePx, CancellationToken ct = default);

    /// <summary>
    /// Renders many staged drawables with one Gradle invocation (see the
    /// batch design in the class comment). The result aligns with
    /// <paramref name="batch"/>; a null entry means that icon produced no
    /// output (caller falls back per icon). Only a total tool failure
    /// throws.
    /// </summary>
    Task<byte[]?[]> RenderBatchAsync(
        string stagedResDir, IReadOnlyList<BatchRenderRequest> batch, int sizePx, CancellationToken ct = default);
}

/// <summary>One batch entry: merged-resource name plus the staged
/// adaptive-icon root file (null for plain drawables).</summary>
public sealed record BatchRenderRequest(string DrawableName, string? RootFile)
{
    // Mirrors PendingBatchIcon: only <adaptive-icon> roots stage beside
    // res/, so a set RootFile is the true-adaptive signal SyncService
    // forwards to the commit step.
    public bool IsAdaptive => RootFile is not null;
}

/// <summary>
/// Renders staged drawable resources through the Paparazzi Gradle tool
/// (LayoutLib, the same engine Android Studio previews use). The daemon
/// stays warm between renders. Renders are serialized: concurrent
/// invocations share one project dir and one snapshot file name, so
/// parallel runs would serve each other icons. Batch mode exists because
/// one Gradle invocation per icon costs a task graph + test JVM each
/// (~minutes for an icon backfill); one invocation renders the whole
/// batch at seconds per icon. Any failure throws and the caller falls
/// back to a letter avatar.
/// </summary>
public sealed class PaparazziRenderer(string gradlePath, string toolDir, TimeSpan timeout) : IPaparazziRenderer
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<byte[]> RenderAsync(
        string stagedResDir, string drawableName, int sizePx, CancellationToken ct = default)
    {
        // Serialize before touching the temp output: two renders must never
        // share the single Gradle project dir at once.
        await _gate.WaitAsync(ct);
        try
        {
            var outPng = Path.Combine(Path.GetTempPath(), $"shizu-icon-{Guid.NewGuid():N}.png");
            try
            {
                await RunGradleAsync(stagedResDir, drawableName, sizePx, outPng, ct);
                return await File.ReadAllBytesAsync(outPng, ct);
            }
            finally
            {
                try { File.Delete(outPng); } catch { /* best effort */ }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]?[]> RenderBatchAsync(
        string stagedResDir, IReadOnlyList<BatchRenderRequest> batch, int sizePx, CancellationToken ct = default)
    {
        // One Gradle invocation for the whole batch (see class comment);
        // the gate still serializes against single renders sharing the dir.
        await _gate.WaitAsync(ct);
        try
        {
            if (batch.Count == 0)
            {
                return [];
            }

            var outs = new string[batch.Count];
            var manifest = new StringBuilder();
            try
            {
                for (var i = 0; i < batch.Count; i++)
                {
                    outs[i] = Path.Combine(Path.GetTempPath(), $"shizu-icon-{Guid.NewGuid():N}.png");
                    manifest.Append(batch[i].DrawableName).Append('|')
                        .Append(batch[i].RootFile ?? "").Append('|')
                        .Append(outs[i]).Append('\n');
                }

                var manifestPath = Path.Combine(Path.GetTempPath(), $"shizu-batch-{Guid.NewGuid():N}.txt");
                try
                {
                    await File.WriteAllTextAsync(manifestPath, manifest.ToString(), ct);
                    // Headroom scales with batch size; the cap is generous
                    // because one slow icon must not kill 200 good ones.
                    var batchTimeout = timeout + TimeSpan.FromMinutes(batch.Count);
                    await RunGradleAsync(
                        [
                            "renderIconBatch",
                            $"-PstagedRes={stagedResDir}",
                            $"-Pbatch={manifestPath}",
                            $"-PiconPx={sizePx}",
                        ],
                        batchTimeout, ct);
                }
                finally
                {
                    try { File.Delete(manifestPath); } catch { /* best effort */ }
                }

                var pngs = new byte[]?[batch.Count];
                for (var i = 0; i < batch.Count; i++)
                {
                    try
                    {
                        pngs[i] = File.Exists(outs[i]) ? await File.ReadAllBytesAsync(outs[i], ct) : null;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        pngs[i] = null;
                    }
                }

                return pngs;
            }
            finally
            {
                foreach (var outPng in outs)
                {
                    try { File.Delete(outPng); } catch { /* best effort */ }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunGradleAsync(
        string stagedResDir, string drawableName, int sizePx, string outPng, CancellationToken ct)
    {
        await RunGradleAsync(
            [
                "renderIcon",
                $"-PstagedRes={stagedResDir}",
                $"-PiconName={drawableName}",
                $"-PiconPx={sizePx}",
                $"-Pout={outPng}",
            ],
            timeout, ct);

        if (!File.Exists(outPng))
        {
            throw new PaparazziException("Paparazzi render produced no output file.");
        }
    }

    private async Task RunGradleAsync(
        IReadOnlyList<string> taskArgs, TimeSpan limit, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked.Token;

        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = gradlePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(toolDir);
            foreach (var arg in taskArgs)
            {
                startInfo.ArgumentList.Add(arg);
            }

            startInfo.ArgumentList.Add("--console=plain");
            startInfo.ArgumentList.Add("-q");

            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new PaparazziException($"Cannot start gradle '{gradlePath}': {ex.Message}");
        }

        if (process is null)
        {
            throw new PaparazziException($"Cannot start gradle '{gradlePath}'.");
        }

        using (process)
        {
            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            process.BeginErrorReadLine();
            // Stdout is discarded (-q keeps it small); drain to avoid blocking.
            process.OutputDataReceived += (_, _) => { };
            process.BeginOutputReadLine();

            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exiting */ }
                throw new PaparazziException($"Paparazzi render timed out after {limit}.");
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

                throw new PaparazziException($"Paparazzi render failed (exit {process.ExitCode}): {err}");
            }
        }
    }
}

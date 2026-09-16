using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Sync;

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
/// back to a letter avatar; before throwing, a failed run is checked for
/// a complete PNG the test JVM already wrote (it writes the exact output
/// before anything optional), because a killed client or a slow daemon
/// must not discard an icon that is sitting on disk.
/// </summary>
public sealed class PaparazziRenderer(
    string gradlePath, string toolDir, TimeSpan timeout, string? cpuAffinity = null,
    ILogger<PaparazziRenderer>? log = null, IRunLog? runLog = null) : IPaparazziRenderer
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Render timings land in the enrichment run log so a slow icon pass is
    // visible without attaching a profiler.
    private readonly IRunLog _runLog = runLog ?? NullRunLog.Instance;

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    public async Task<byte[]> RenderAsync(
        string stagedResDir, string drawableName, int sizePx, CancellationToken ct = default)
    {
        // Serialize before touching the temp output: two renders must never
        // share the single Gradle project dir at once.
        await _gate.WaitAsync(ct);
        try
        {
            var outPng = Path.Combine(Path.GetTempPath(), $"shizu-icon-{Guid.NewGuid():N}.png");
            var started = Stopwatch.GetTimestamp();
            _runLog.Detail($"render {drawableName} start");
            try
            {
                try
                {
                    await RunGradleAsync(stagedResDir, drawableName, sizePx, outPng, ct);
                }
                catch (PaparazziException ex)
                {
                    if (await ReadSalvagedPngAsync(outPng, ct) is not { } salvaged)
                    {
                        _runLog.Detail($"render {drawableName} failed after {Elapsed(started)}ms: {ex.Message}");
                        throw;
                    }

                    log?.LogWarning(ex, "Paparazzi render failed; salvaged the output PNG it left behind.");
                    _runLog.Detail($"render {drawableName} salvaged after {Elapsed(started)}ms");
                    return salvaged;
                }

                var rendered = await File.ReadAllBytesAsync(outPng, ct);
                _runLog.Detail($"render {drawableName} done {rendered.Length}B in {Elapsed(started)}ms");
                return rendered;
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
                    var started = Stopwatch.GetTimestamp();
                    _runLog.Detail($"batch render {batch.Count} icons start");
                    // Headroom scales with batch size; the cap is generous
                    // because one slow icon must not kill 200 good ones.
                    var batchTimeout = timeout + TimeSpan.FromMinutes(batch.Count);
                    PaparazziException? failure = null;
                    try
                    {
                        await RunGradleAsync(
                            [
                                "renderIconBatch",
                                $"-PstagedRes={stagedResDir}",
                                $"-Pbatch={manifestPath}",
                                $"-PiconPx={sizePx}",
                            ],
                            batchTimeout, ct);
                    }
                    catch (PaparazziException ex)
                    {
                        // Icons written before the failure are still good;
                        // only a batch with no output at all is a failure.
                        failure = ex;
                        log?.LogWarning(ex, "Paparazzi batch render failed; reading any icons it left behind.");
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

                    if (failure is not null && pngs.All(p => p is null))
                    {
                        _runLog.Detail($"batch render failed after {Elapsed(started)}ms: {failure.Message}");
                        throw failure;
                    }

                    _runLog.Detail($"batch render done {pngs.Count(p => p is not null)}/{batch.Count} icons in {Elapsed(started)}ms");
                    return pngs;
                }
                finally
                {
                    try { File.Delete(manifestPath); } catch { /* best effort */ }
                }
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

    /// <summary>
    /// Reads the output file when a failed run left a complete PNG (magic
    /// bytes intact); a truncated file is not an icon.
    /// </summary>
    private static async Task<byte[]?> ReadSalvagedPngAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, ct);
            return bytes.Length > PngMagic.Length && bytes.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic)
                ? bytes
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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
            // With an affinity set, launch through taskset so the build and
            // the daemon it starts (workers inherit affinity) stay on the
            // allowed cores. The warm daemon is kept.
            var startInfo = new ProcessStartInfo
            {
                FileName = cpuAffinity is null ? gradlePath : "taskset",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (cpuAffinity is not null)
            {
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(cpuAffinity);
                startInfo.ArgumentList.Add(gradlePath);
            }

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

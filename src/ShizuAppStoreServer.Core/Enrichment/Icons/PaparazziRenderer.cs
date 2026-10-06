using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ShizuAppStoreServer.Core.Jobs;
using ShizuAppStoreServer.Core.Sync;

namespace ShizuAppStoreServer.Core.Enrichment.Icons;

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

    /// <summary>
    /// Stops the warm Gradle daemon(s) for this tool. Called after a batched
    /// refresh: chunks already ran with <c>--no-daemon</c> and exited, but
    /// the daemon left warm by earlier single renders would otherwise sit on
    /// a few hundred MB. Also called at service startup, because a daemon
    /// that survived a restart keeps the old instance's private temp mount
    /// and can no longer host Paparazzi's self-attaching agent. Best effort;
    /// a failure just means the daemon idles out on its own.
    /// </summary>
    Task StopGradleDaemonsAsync();
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
/// Transient systemd user scope for the Gradle process tree: the render
/// JVMs must not be charged to the API unit's cgroup, whose MemoryHigh they
/// can trip into a reclaim stall that takes the API down until a restart.
/// The scope is bounded on its own, so a runaway render throttles or
/// OOM-kills itself and the API keeps serving.
/// </summary>
public sealed record RenderScope(
    string SystemdRunPath, string MemoryHigh, string MemoryMax, string SwapMax, string CpuQuota);

/// <summary>
/// Renders staged drawable resources through the Paparazzi Gradle tool
/// (LayoutLib, the same engine Android Studio previews use). The daemon
/// stays warm between renders. Renders are serialized: concurrent
/// invocations share one project dir and one snapshot file name, so
/// parallel runs would serve each other icons. Batch mode exists because
/// one Gradle invocation per icon costs a task graph + test JVM each
/// (~minutes for an icon backfill); one invocation renders a chunk of the
/// batch at seconds per icon. The caller bounds the chunk and this class
/// runs every batch invocation with <c>--no-daemon</c>, so no render JVM
/// survives it; the caller stops the warm daemon once the refresh is done.
/// Any failure throws and the caller falls
/// back to a letter avatar; before throwing, a failed run is checked for
/// a complete PNG the test JVM already wrote (it writes the exact output
/// before anything optional), because a killed client or a slow daemon
/// must not discard an icon that is sitting on disk. With a
/// <see cref="RenderScope"/> the Gradle process tree runs in a transient
/// systemd user scope instead of the API unit's cgroup.
/// </summary>
public sealed class PaparazziRenderer(
    string gradlePath, string toolDir, TimeSpan timeout, string? cpuAffinity = null,
    ILogger<PaparazziRenderer>? log = null, RenderScope? scope = null)
    : IPaparazziRenderer
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly SemaphoreSlim _gate = new(1, 1);

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
            JobContext.Current?.Render($"render {drawableName} start");
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
                        JobContext.Current?.Render(
                            $"render {drawableName} failed after {Elapsed(started)}ms: {ex.Message}",
                            level: JobEventLevel.Warning);
                        throw;
                    }

                    log?.LogWarning(ex, "Paparazzi render failed; salvaged the output PNG it left behind.");
                    JobContext.Current?.Render(
                        $"render {drawableName} salvaged after {Elapsed(started)}ms",
                        level: JobEventLevel.Warning);
                    return salvaged;
                }

                var rendered = await File.ReadAllBytesAsync(outPng, ct);
                JobContext.Current?.Render($"render {drawableName} done {rendered.Length}B in {Elapsed(started)}ms");
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
                    JobContext.Current?.Render($"batch render {batch.Count} icons start");
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
                            batchTimeout, isolate: true, ct);
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
                        JobContext.Current?.Render(
                            $"batch render failed after {Elapsed(started)}ms: {failure.Message}",
                            level: JobEventLevel.Warning);
                        throw failure;
                    }

                    JobContext.Current?.Render($"batch render done {pngs.Count(p => p is not null)}/{batch.Count} icons in {Elapsed(started)}ms");
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
    /// Stops the warm daemon(s) for this tool through <c>gradle --stop</c>.
    /// Best effort with its own budget: a cancelled refresh still gets the
    /// daemon freed, and a failure only means it idles out on its own.
    /// </summary>
    public async Task StopGradleDaemonsAsync()
    {
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
            startInfo.ArgumentList.Add("--stop");
            startInfo.ArgumentList.Add("--console=plain");
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "gradle --stop could not start; any daemon idles out on its own.");
            return;
        }

        if (process is null)
        {
            return;
        }

        using (process)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                process.OutputDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.ErrorDataReceived += (_, _) => { };
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(cts.Token);
                JobContext.Current?.Detail($"gradle --stop done (exit {process.ExitCode})");
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already exiting */ }
                log?.LogDebug("gradle --stop did not finish within 60s; the daemon idles out on its own.");
            }
            catch (Exception ex)
            {
                log?.LogDebug(ex, "gradle --stop failed; the daemon idles out on its own.");
            }
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
            timeout, isolate: false, ct);

        if (!File.Exists(outPng))
        {
            throw new PaparazziException("Paparazzi render produced no output file.");
        }
    }

    private async Task RunGradleAsync(
        IReadOnlyList<string> taskArgs, TimeSpan limit, bool isolate, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linked.Token;

        var launcherPath = scope?.SystemdRunPath ?? (cpuAffinity is null ? gradlePath : "taskset");
        Process? process;
        try
        {
            // The build and the daemon it starts (workers inherit affinity)
            // go through taskset when an affinity is set. With a scope the
            // whole command line runs under systemd-run, which puts it in a
            // transient user scope with its own memory/CPU boundaries.
            var command = new List<string>();
            if (cpuAffinity is not null)
            {
                command.Add("taskset");
                command.Add("-c");
                command.Add(cpuAffinity);
            }

            command.Add(gradlePath);
            if (isolate)
            {
                // A batch chunk must not leave a daemon behind: the next
                // chunk starts its own JVM, and the heap it used is returned
                // to the OS between chunks.
                command.Add("--no-daemon");
            }

            command.Add("-p");
            command.Add(toolDir);
            command.AddRange(taskArgs);
            command.Add("--console=plain");
            command.Add("-q");

            if (scope is not null)
            {
                // The API unit's Nice=5 does not cover the render once it
                // sits outside the cgroup, and Nice is not a settable
                // transient property, so lower priority in-band; the daemon
                // the client starts inherits it.
                command.Insert(0, "nice");
                command.Insert(1, "-n");
                command.Insert(2, "5");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var firstArg = 0;
            if (scope is not null)
            {
                startInfo.ArgumentList.Add("--user");
                startInfo.ArgumentList.Add("--scope");
                startInfo.ArgumentList.Add("--quiet");
                startInfo.ArgumentList.Add("--collect");
                // Unique per render: renders are serialized, and a scope a
                // killed render left behind must not block a later one.
                startInfo.ArgumentList.Add($"--unit=shizu-render-{Guid.NewGuid():N}");
                AddScopeProperty(startInfo, "MemoryHigh", scope.MemoryHigh);
                AddScopeProperty(startInfo, "MemoryMax", scope.MemoryMax);
                AddScopeProperty(startInfo, "MemorySwapMax", scope.SwapMax);
                AddScopeProperty(startInfo, "CPUQuota", scope.CpuQuota);
                startInfo.ArgumentList.Add("--");
            }
            else
            {
                firstArg = 1;
            }

            for (var i = firstArg; i < command.Count; i++)
            {
                startInfo.ArgumentList.Add(command[i]);
            }

            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new PaparazziException($"Cannot start '{launcherPath}' for gradle '{gradlePath}': {ex.Message}");
        }

        if (process is null)
        {
            throw new PaparazziException($"Cannot start '{launcherPath}' for gradle '{gradlePath}'.");
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

    /// <summary>One <c>systemd-run -p Name=Value</c> property pair.</summary>
    private static void AddScopeProperty(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add($"{name}={value}");
    }
}

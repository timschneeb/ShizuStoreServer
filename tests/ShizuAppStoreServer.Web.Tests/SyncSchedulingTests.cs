using Microsoft.Extensions.Logging.Abstractions;
using ShizuAppStoreServer.Core.Sync;
using ShizuAppStoreServer.Sync;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>
/// Scheduling semantics of the webhook latch: a request runs a pass
/// immediately when the loop is idle, and turns into an immediate follow-up
/// pass when one is already running.
/// </summary>
public sealed class SyncSchedulingTests
{
    [Fact]
    public async Task WebhookWakesTheIdleWorkerImmediately()
    {
        var runner = new FakeRunner();
        var signal = new SyncSignal();
        var worker = new SyncWorker(
            runner, signal, new SyncOptions { FastLoopMinutes = 1440, RunOnStartup = false },
            NullLogger<SyncWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            signal.Request();

            await WaitForAsync(() => runner.Runs.Count == 1);
            Assert.Equal("scheduled", runner.Runs[0]);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RequestDuringAPassRunsAgainImmediatelyAfterIt()
    {
        var runner = new FakeRunner();
        var signal = new SyncSignal();
        var worker = new SyncWorker(
            runner, signal, new SyncOptions { FastLoopMinutes = 1440, RunOnStartup = false },
            NullLogger<SyncWorker>.Instance);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.BlockNextRun(gate);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            signal.Request();
            await WaitForAsync(() => runner.Runs.Count == 1);

            // Lands while the first pass is still held open by the gate.
            signal.Request();
            gate.SetResult();

            await WaitForAsync(() => runner.Runs.Count == 2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task IdleWorkerDoesNotRunWithoutATickOrRequest()
    {
        var runner = new FakeRunner();
        var worker = new SyncWorker(
            runner, new SyncSignal(), new SyncOptions { FastLoopMinutes = 1440, RunOnStartup = false },
            NullLogger<SyncWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            Assert.Empty(runner.Runs);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the expected sync run.");
            }

            await Task.Delay(25);
        }
    }

    private sealed class FakeRunner : ISyncPassRunner
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _block;

        public List<string> Runs { get; } = [];

        /// <summary>Holds the next run open until the returned source is set.</summary>
        public void BlockNextRun(TaskCompletionSource gate)
        {
            lock (_gate)
            {
                _block = gate;
            }
        }

        public async Task<bool> RunOnceAsync(string trigger, bool fullRecheck, CancellationToken ct)
        {
            TaskCompletionSource? block;
            lock (_gate)
            {
                Runs.Add(trigger);
                block = _block;
            }

            if (block is not null)
            {
                await block.Task.WaitAsync(ct);
            }

            return true;
        }
    }
}

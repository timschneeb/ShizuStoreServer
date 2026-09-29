using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShizuAppStoreServer.Core.UsageAnalysis;
using ShizuAppStoreServer.Sync;

namespace ShizuAppStoreServer.Api.Tests;

public sealed class UsageAnalysisWorkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shizu-worker-logs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task PrunesStaleTranscriptsAtStartupWhileDisabled()
    {
        var stale = await NewFileAsync("20260901-000000-old-r1.json", "{}", TimeSpan.FromHours(30));
        var fresh = await NewFileAsync("20260928-000000-new-r2.html", "<html></html>", TimeSpan.Zero);

        await RunAsync(new UsageAnalysisOptions
        {
            Enabled = false,
            LogPath = _dir,
            LogRetention = TimeSpan.FromHours(24),
        });

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task NullRetentionKeepsTranscripts()
    {
        var stale = await NewFileAsync("20260901-000000-old-r1.json", "{}", TimeSpan.FromDays(30));

        await RunAsync(new UsageAnalysisOptions
        {
            Enabled = false,
            LogPath = _dir,
            LogRetention = null,
        });

        Assert.True(File.Exists(stale));
    }

    private async Task<string> NewFileAsync(string name, string content, TimeSpan age)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, content);
        if (age > TimeSpan.Zero)
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        }

        return path;
    }

    private static async Task RunAsync(UsageAnalysisOptions options)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var worker = new UsageAnalysisWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<UsageAnalysisWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
    }
}

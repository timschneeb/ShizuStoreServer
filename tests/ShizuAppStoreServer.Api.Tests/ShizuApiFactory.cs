using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Exporter;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Jobs;
using ShizuAppStoreServer.Core.Sync;
using ShizuAppStoreServer.Sync;
using ShizuAppStoreServer.Tracking;

namespace ShizuAppStoreServer.Api.Tests;

/// <summary>
/// In-memory API host: SQLite (shared open connection) instead of Npgsql,
/// temp-dir icon store, configurable rate limit + admin secret. One instance
/// per test class (<c>IClassFixture</c>); each test starts with
/// <see cref="ResetAsync"/> for full isolation.
/// </summary>
/// <remarks>
/// Per-suite settings go through DI swaps here, NOT
/// <c>ConfigureAppConfiguration</c>: with minimal hosting,
/// <c>WebApplication.CreateBuilder</c> rebuilds
/// <c>builder.Configuration</c> from appsettings/env/cmdline only, so
/// host-builder-level in-memory values never reach Program.cs (they layer
/// underneath appsettings.json). This cost a debugging session; don't
/// reintroduce it.
/// </remarks>
public class ShizuApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly int _rateLimitPerMinute;
    private readonly string? _adminSecret;
    private readonly string _excludedIps;

    public string IconDir { get; } =
        Path.Combine(Path.GetTempPath(), "shizu-test-icons-" + Guid.NewGuid().ToString("N"));

    public ShizuApiFactory()
        : this(rateLimitPerMinute: 100_000, adminSecret: "test-admin-secret")
    {
    }

    internal ShizuApiFactory(
        int rateLimitPerMinute,
        string? adminSecret,
        string excludedIps = "")
    {
        _rateLimitPerMinute = rateLimitPerMinute;
        _adminSecret = adminSecret;
        _excludedIps = excludedIps;
        Directory.CreateDirectory(IconDir);
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Hermetic suites: no external binaries. This also skips the
        // Program.cs startup tool probe, which only runs outside Testing.
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // AddDbContext registers both the options and an options-action
            // holder (IDbContextOptionsConfiguration<>); the Npgsql holder
            // must go too, otherwise the rebuilt options carry both
            // providers and EF refuses to start.
            services.RemoveAll<DbContextOptions<ShizuDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<ShizuDbContext>>();
            services.AddDbContext<ShizuDbContext>(o => o.UseSqlite(_connection));

            // Per-suite option overrides (see remarks above).
            services.RemoveAll<ApiOptions>();
            services.AddSingleton(new ApiOptions { RateLimitPerMinute = _rateLimitPerMinute });
            services.RemoveAll<EnrichmentOptions>();
            services.AddSingleton(new EnrichmentOptions { IconStorePath = IconDir });
            services.RemoveAll<AdminOptions>();
            services.AddSingleton(new AdminOptions { Token = _adminSecret });

            // Never sync in tests: no startup pass, and the fast loop ticks
            // once a day (suites finish in seconds). The workers still start
            // with the host, which is exactly the boot path under test. The
            // webhook latch is inert here so an admin request cannot start a
            // real pass against the shared SQLite connection mid-test.
            services.RemoveAll<SyncOptions>();
            services.AddSingleton(new SyncOptions
            {
                ListPath = Path.Combine(Path.GetTempPath(), "shizu-test-no-list"),
                FastLoopMinutes = 1440,
                RunOnStartup = false,
            });
            services.RemoveAll<SyncSignal>();
            services.AddSingleton<SyncSignal>(new IdleSyncSignal());

            // Keep the user-agent flush timer out of tests: a timer flush
            // runs on a worker thread and races the shared SQLite connection
            // (CreateFunction fails while another statement is active).
            // Tracking tests call UserAgentTrackingWorker.FlushAsync directly.
            services.RemoveAll<UserAgentTrackingOptions>();
            services.AddSingleton(new UserAgentTrackingOptions
            {
                FlushInterval = TimeSpan.FromHours(1),
            });
            // Same for the request-log timer; RequestLoggingTests call
            // RequestLogWorker.FlushAsync directly.
            services.RemoveAll<RequestLogOptions>();
            services.AddSingleton(new RequestLogOptions
            {
                FlushInterval = TimeSpan.FromHours(1),
                ExcludedIps = _excludedIps,
            });

            // Disable output caching: replace every production policy with a
            // no-op (re-adding a policy name overwrites the earlier one).
            services.AddOutputCache(o =>
            {
                o.AddPolicy("apps-list", NoOutputCachePolicy.Instance);
                o.AddPolicy("app-detail", NoOutputCachePolicy.Instance);
                o.AddPolicy("categories", NoOutputCachePolicy.Instance);
                o.AddPolicy("use-cases", NoOutputCachePolicy.Instance);
                o.AddPolicy("changes", NoOutputCachePolicy.Instance);
                o.AddPolicy("issues", NoOutputCachePolicy.Instance);
                o.AddPolicy("meta", NoOutputCachePolicy.Instance);
                o.AddPolicy("trending", NoOutputCachePolicy.Instance);
                o.AddPolicy("app-history", NoOutputCachePolicy.Instance);
            });

            // Scrapes in a suite arrive back to back; the exporter's response
            // cache would serve a body collected before the test recorded its
            // own measurements.
            services.Configure<PrometheusAspNetCoreOptions>(
                o => o.ScrapeResponseCacheDurationMilliseconds = 0);

            // JobLogWorker runs a recovery query at startup; the wrapper makes
            // that query observable so tests can wait for it before their
            // first database operation (the shared SQLite connection is not
            // thread-safe). JobLogTests still exercise JobLog directly.
            services.RemoveAll<IJobLog>();
            services.AddSingleton<IJobLog>(sp => new RecoveryGatedJobLog(
                new JobLog([.. sp.GetServices<IJobSink>()])));
        });
    }

    /// <summary>
    /// Test HTTP client. Cross-test isolation comes from
    /// <see cref="NoOutputCachePolicy"/> (the production output cache is
    /// fully disabled in this host), not from request headers, the
    /// <c>OutputCacheMiddleware</c> has no request-driven bypass, so
    /// <c>Cache-Control: no-cache</c>/<c>no-store</c> would NOT help.
    /// </summary>
    public HttpClient NewClient() => CreateClient();

    /// <summary>
    /// Wipes the database and runs <paramref name="seed"/> in a fresh scope.
    /// </summary>
    public async Task ResetAsync(Action<ShizuDbContext> seed)
    {
        await AwaitStartupRecoveryAsync();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShizuDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        seed(db);
        await db.SaveChangesAsync();
    }

    /// <summary>Direct DB access for assertions (separate scope, fresh view).</summary>
    public async Task<T> QueryAsync<T>(Func<ShizuDbContext, Task<T>> query)
    {
        await AwaitStartupRecoveryAsync();
        using var scope = Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<ShizuDbContext>());
    }

    private Task? _startupRecovery;

    /// <summary>
    /// The job-log worker recovers interrupted runs on a background thread as
    /// soon as the host starts; the shared SQLite connection cannot serve it
    /// and a test query at the same time, so tests sequence behind it once.
    /// </summary>
    private async Task AwaitStartupRecoveryAsync()
    {
        _ = Services; // starts the host and its workers
        _startupRecovery ??= ((RecoveryGatedJobLog)Services.GetRequiredService<IJobLog>())
            .Recovered
            .WaitAsync(TimeSpan.FromSeconds(30));
        await _startupRecovery;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
            try
            {
                Directory.Delete(IconDir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Inert webhook latch for test hosts: requests queue rows but never wake the
/// worker, so admin tests keep full control of the connection and the clock.
/// </summary>
internal sealed class IdleSyncSignal : SyncSignal
{
    public override void Request()
    {
    }
}

/// <summary>
/// Wraps the real job log so tests can observe the worker's startup recovery
/// pass, which runs on a background thread against the shared connection.
/// </summary>
internal sealed class RecoveryGatedJobLog(IJobLog inner) : IJobLog
{
    private readonly TaskCompletionSource _recovered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Recovered => _recovered.Task;

    public JobSession? Current => inner.Current;

    public JobSession Begin(JobStart start) => inner.Begin(start);

    public void Skipped(JobStart start, string reason) => inner.Skipped(start, reason);

    public async Task RecoverInterruptedAsync(CancellationToken ct)
    {
        try
        {
            await inner.RecoverInterruptedAsync(ct);
        }
        finally
        {
            _recovered.TrySetResult();
        }
    }
}

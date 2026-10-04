using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Jobs;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Api.Tests;

public sealed class UseCasesApiTests : IClassFixture<UseCasesApiTests.EnabledFactory>
{
    private const string Token = "test-admin-secret";
    private readonly EnabledFactory _factory;

    public UseCasesApiTests(EnabledFactory factory) => _factory = factory;

    /// <summary>Analyzer enabled but the worker replaced with a no-op so tests stay hermetic.</summary>
    public sealed class EnabledFactory : ShizuApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UsageAnalysisOptions>();
                services.AddSingleton(new UsageAnalysisOptions
                {
                    Enabled = true,
                    MaxRunsPerDay = 500,
                    MonthlyBudgetUsd = 1000m,
                });
                services.RemoveAll<IUsageAnalysisRunner>();
                services.AddScoped<IUsageAnalysisRunner, NoopRunner>();
            });
        }

        private sealed class NoopRunner : IUsageAnalysisRunner
        {
            public Task<bool> RunNextAsync(CancellationToken ct = default) => Task.FromResult(false);

            public Task<int> RecoverInterruptedAsync(CancellationToken ct = default) => Task.FromResult(0);
        }
    }

    private static HttpClient Admin(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private static UseCase NewUseCase(string slug, string? name = null, bool active = true) => new()
    {
        Slug = slug,
        Name = name ?? slug,
        Definition = $"{slug} definition",
        IsActive = active,
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task ListReturnsActiveUseCasesOrderedByCount()
    {
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            var install = NewUseCase("install-apps", "Install apps");
            var files = NewUseCase("file-access", "File access");
            var retired = NewUseCase("retired", "Retired", active: false);
            var empty = NewUseCase("empty", "Empty");
            db.UseCases.AddRange(install, files, retired, empty);
            var alpha = Seeds.NewApp("alpha", cat);
            alpha.UseCases.Add(install);
            alpha.UseCases.Add(files);
            var beta = Seeds.NewApp("beta", cat);
            beta.UseCases.Add(install);
            var gamma = Seeds.NewApp("gamma", cat);
            gamma.UseCases.Add(retired);
            db.Apps.AddRange(alpha, beta, gamma);
        });

        var items = await _factory.NewClient()
            .GetFromJsonAsync<List<UseCaseCountDto>>("/v1/use-cases");

        Assert.NotNull(items);
        Assert.Collection(
            items,
            first =>
            {
                Assert.Equal("install-apps", first.Slug);
                Assert.Equal("Install apps", first.Name);
                Assert.Equal(2, first.AppCount);
            },
            second =>
            {
                Assert.Equal("file-access", second.Slug);
                Assert.Equal(1, second.AppCount);
            });
    }

    [Fact]
    public async Task ListHonorsListingAndEtag()
    {
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            var install = NewUseCase("install-apps", "Install apps");
            db.UseCases.Add(install);
            var main = Seeds.NewApp("main-app", cat);
            main.UseCases.Add(install);
            var closed = Seeds.NewApp("closed-app", cat, listing: Listing.ClosedSource);
            closed.UseCases.Add(install);
            db.Apps.AddRange(main, closed);
        });

        var client = _factory.NewClient();
        var mainOnly = await client.GetFromJsonAsync<List<UseCaseCountDto>>("/v1/use-cases");
        Assert.Equal(1, Assert.Single(mainOnly!).AppCount);

        var both = await client.GetFromJsonAsync<List<UseCaseCountDto>>(
            "/v1/use-cases?listing=main,closed_source");
        Assert.Equal(2, Assert.Single(both!).AppCount);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/use-cases?listing=bogus")).StatusCode);

        var first = await client.GetAsync("/v1/use-cases");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);
        var conditional = new HttpRequestMessage(HttpMethod.Get, "/v1/use-cases");
        conditional.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
    }

    [Fact]
    public async Task AppsFilterByUseCase()
    {
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            var install = NewUseCase("install-apps", "Install apps");
            var retired = NewUseCase("retired", "Retired", active: false);
            db.UseCases.AddRange(install, retired);
            var tagged = Seeds.NewApp("tagged", cat);
            tagged.UseCases.Add(install);
            db.Apps.AddRange(tagged, Seeds.NewApp("untagged", cat));
        });

        var client = _factory.NewClient();
        var page = await client.GetFromJsonAsync<PagedAppsDto>("/v1/apps?useCase=install-apps");
        Assert.Equal("tagged", Assert.Single(page!.Items).Slug);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/apps?useCase=no-such-tag")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/apps?useCase=retired")).StatusCode);
    }

    [Fact]
    public async Task SummaryDetailAndChangesCarryUseCases()
    {
        var now = DateTimeOffset.UtcNow;
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            var install = NewUseCase("install-apps", "Install apps");
            var files = NewUseCase("file-access", "File access");
            var retired = NewUseCase("retired", "Retired", active: false);
            db.UseCases.AddRange(install, files, retired);
            var app = Seeds.NewApp("carrier", cat, updatedAt: now);
            app.UseCases.Add(install);
            app.UseCases.Add(files);
            app.UseCases.Add(retired);
            db.Apps.Add(app);
        });

        var client = _factory.NewClient();
        var page = await client.GetFromJsonAsync<PagedAppsDto>("/v1/apps?q=carrier");
        var summary = Assert.Single(page!.Items);
        Assert.Equal(["file-access", "install-apps"], summary.UseCases!.Select(u => u.Slug));

        var detail = await client.GetFromJsonAsync<AppDetailDto>("/v1/apps/carrier");
        Assert.Equal(["file-access", "install-apps"], detail!.UseCases!.Select(u => u.Slug));

        var since = Uri.EscapeDataString(now.AddMinutes(-5).ToString("O"));
        var changes = await client.GetFromJsonAsync<ChangesDto>($"/v1/changes?since={since}");
        var updated = Assert.Single(changes!.Updated);
        Assert.Equal("carrier", updated.Slug);
        Assert.Equal(["file-access", "install-apps"], updated.UseCases!.Select(u => u.Slug));
    }

    [Fact]
    public async Task AdminVocabularyRequiresTokenAndListsCounts()
    {
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            var install = NewUseCase("install-apps", "Install apps");
            db.UseCases.Add(install);
            var app = Seeds.NewApp("tagged", cat);
            app.UseCases.Add(install);
            db.Apps.Add(app);
        });

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _factory.NewClient().GetAsync("/v1/admin/use-cases")).StatusCode);

        var items = await Admin(_factory.NewClient())
            .GetFromJsonAsync<List<UseCaseAdminDto>>("/v1/admin/use-cases");
        var item = Assert.Single(items!);
        Assert.Equal("install-apps", item.Slug);
        Assert.Equal("install-apps definition", item.Definition);
        Assert.True(item.IsActive);
        Assert.Equal(1, item.AppCount);
    }

    [Fact]
    public async Task AdminUpsertCreatesAndQueuesARetag()
    {
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            var reported = Seeds.NewApp("reported", cat);
            reported.UsageShort = "Can install apps.";
            db.Apps.Add(reported);
        });

        var client = Admin(_factory.NewClient());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/v1/admin/use-cases", new { })).StatusCode);

        var response = await client.PostAsJsonAsync(
            "/v1/admin/use-cases",
            new { slug = "install-apps", name = "Install apps", definition = "Installs third-party APKs." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<UseCaseAdminDto>())!;
        Assert.Equal("install-apps", created.Slug);
        Assert.Equal("Installs third-party APKs.", created.Definition);
        Assert.True(created.IsActive);

        var run = Assert.Single(await _factory.QueryAsync(db => db.UsageAnalysisRuns.AsNoTracking().ToListAsync()));
        Assert.Equal(UsageAnalysisKind.Tagging, run.Kind);
        Assert.Equal(JobTrigger.Backfill, run.Trigger);
        Assert.Equal(UsageAnalysisStatus.Pending, run.Status);
        Assert.Equal(
            _factory.Services.GetRequiredService<UsageAnalysisOptions>().TagPromptVersion,
            run.PromptVersion);
    }

    [Fact]
    public async Task AdminUpsertNormalizesSlugsAndRetagsReactivation()
    {
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            var reported = Seeds.NewApp("reported", cat);
            reported.UsageShort = "Can install apps.";
            db.Apps.Add(reported);
        });

        var client = Admin(_factory.NewClient());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/v1/admin/use-cases", new { slug = "!!!", name = "" })).StatusCode);

        var created = await (await client.PostAsJsonAsync(
                "/v1/admin/use-cases",
                new { slug = "Wireless Debugging!", name = "Wireless debugging", isActive = false }))
            .Content.ReadFromJsonAsync<UseCaseAdminDto>();
        Assert.Equal("wireless-debugging", created!.Slug);
        Assert.False(created.IsActive);
        Assert.Empty(await _factory.QueryAsync(db => db.UsageAnalysisRuns.AsNoTracking().ToListAsync()));

        var reactivated = await (await client.PostAsJsonAsync(
                "/v1/admin/use-cases",
                new { slug = "wireless-debugging", name = "Wireless debugging", isActive = true }))
            .Content.ReadFromJsonAsync<UseCaseAdminDto>();
        Assert.True(reactivated!.IsActive);

        // Updates may toggle a tag by slug alone; the name is not required.
        var toggled = await (await client.PostAsJsonAsync(
                "/v1/admin/use-cases",
                new { slug = "wireless-debugging", isActive = false }))
            .Content.ReadFromJsonAsync<UseCaseAdminDto>();
        Assert.False(toggled!.IsActive);
        Assert.Equal("Wireless debugging", toggled.Name);

        var run = Assert.Single(await _factory.QueryAsync(
            db => db.UsageAnalysisRuns.AsNoTracking().ToListAsync()));
        Assert.Equal(UsageAnalysisKind.Tagging, run.Kind);
        Assert.Equal(JobTrigger.Backfill, run.Trigger);
    }

    [Fact]
    public async Task AdminPromoteCreatesTheTagAndAssignsProposers()
    {
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            var candidate = new UseCaseCandidate
            {
                Slug = "wireless-tethering",
                Name = "Wireless tethering",
                Status = UseCaseCandidateStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.UseCaseCandidates.Add(candidate);
            var one = Seeds.NewApp("one", cat);
            var two = Seeds.NewApp("two", cat);
            db.Apps.AddRange(one, two);
            db.AppUseCaseProposals.AddRange(
                new AppUseCaseProposal
                {
                    App = one,
                    Candidate = candidate,
                    Reason = "onboarding mention",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                },
                new AppUseCaseProposal
                {
                    App = two,
                    Candidate = candidate,
                    Reason = "turns the hotspot on",
                    CreatedAt = DateTimeOffset.UtcNow,
                });
        });

        var client = Admin(_factory.NewClient());
        var candidates = await client.GetFromJsonAsync<List<UseCaseCandidateDto>>(
            "/v1/admin/use-cases/candidates");
        var listed = Assert.Single(candidates!);
        Assert.Equal("wireless-tethering", listed.Slug);
        Assert.Equal(2, listed.AppCount);
        Assert.Equal("turns the hotspot on", listed.LatestReason);

        var response = await client.PostAsJsonAsync(
            "/v1/admin/use-cases/candidates/wireless-tethering/promote", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var promoted = (await response.Content.ReadFromJsonAsync<UseCaseAdminDto>())!;
        Assert.Equal("wireless-tethering", promoted.Slug);
        Assert.True(promoted.IsActive);
        Assert.Equal(2, promoted.AppCount);
        Assert.Equal("turns the hotspot on", promoted.Definition);

        var again = await client.PostAsJsonAsync(
            "/v1/admin/use-cases/candidates/wireless-tethering/promote", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/v1/admin/use-cases/candidates/nope/promote", new { })).StatusCode);

        var stored = await _factory.QueryAsync(db => db.UseCaseCandidates.AsNoTracking()
            .SingleAsync(c => c.Slug == "wireless-tethering"));
        Assert.Equal(UseCaseCandidateStatus.Promoted, stored.Status);
    }

    [Fact]
    public async Task AdminDismissesAndMergesCandidates()
    {
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            db.UseCases.Add(NewUseCase("install-apps", "Install apps"));
            var dismiss = new UseCaseCandidate
            {
                Slug = "self-update",
                Name = "Self update",
                Status = UseCaseCandidateStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var merge = new UseCaseCandidate
            {
                Slug = "app-installer",
                Name = "App installer",
                Status = UseCaseCandidateStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.UseCaseCandidates.AddRange(dismiss, merge);
            var app = Seeds.NewApp("app", cat);
            db.Apps.Add(app);
            db.AppUseCaseProposals.Add(new AppUseCaseProposal
            {
                App = app,
                Candidate = merge,
                Reason = "installs APKs",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        });

        var client = Admin(_factory.NewClient());
        var dismissed = await client.PostAsync("/v1/admin/use-cases/candidates/self-update/dismiss", null);
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);
        var dismissDto = (await dismissed.Content.ReadFromJsonAsync<UseCaseCandidateDto>())!;
        Assert.Equal("Dismissed", dismissDto.Status);

        var merged = await client.PostAsJsonAsync(
            "/v1/admin/use-cases/candidates/app-installer/merge",
            new { targetSlug = "install-apps" });
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        var mergedDto = (await merged.Content.ReadFromJsonAsync<UseCaseAdminDto>())!;
        Assert.Equal("install-apps", mergedDto.Slug);
        Assert.Equal(1, mergedDto.AppCount);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/v1/admin/use-cases/candidates/nope/dismiss", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync(
                "/v1/admin/use-cases/candidates/app-installer/merge", new { targetSlug = "" })).StatusCode);
    }

    [Fact]
    public async Task AdminTagBackfillQueuesTaggingRuns()
    {
        await _factory.ResetAsync(db =>
        {
            var cat = Seeds.NewCategory("apps");
            db.Categories.Add(cat);
            var reported = Seeds.NewApp("reported", cat);
            reported.UsageShort = "Can install apps.";
            db.Apps.AddRange(
                reported,
                Seeds.NewApp("noreport", cat),
                Seeds.NewApp("norepo", cat, url: "https://example.com/app"));
        });

        var unauthorized = await _factory.NewClient().PostAsJsonAsync(
            "/v1/admin/usage-analysis/tag-backfill", new UsageTaggingQueueRequestDto());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var response = await Admin(_factory.NewClient()).PostAsJsonAsync(
            "/v1/admin/usage-analysis/tag-backfill", new UsageTaggingQueueRequestDto());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<UsageAnalysisQueueDto>())!;
        Assert.Equal(1, result.Queued);

        var run = Assert.Single(await _factory.QueryAsync(db => db.UsageAnalysisRuns.AsNoTracking().ToListAsync()));
        Assert.Equal(UsageAnalysisKind.Tagging, run.Kind);
        Assert.Equal(JobTrigger.Backfill, run.Trigger);
    }
}

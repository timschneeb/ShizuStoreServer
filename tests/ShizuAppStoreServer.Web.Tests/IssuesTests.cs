using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShizuAppStoreServer.Api;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Web.Tests;

public sealed class IssuesTests(ShizuApiFactory factory) : IClassFixture<ShizuApiFactory>
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static void SeedRun(ShizuDbContext db, string head, params (IssueKind Kind, string Rule, string? Slug)[] rows)
    {
        var run = new SyncRun
        {
            StartedAt = T0,
            FinishedAt = T0.AddMinutes(1),
            Trigger = "scheduled",
            HeadCommit = head,
        };
        db.SyncRuns.Add(run);
        foreach (var (kind, rule, slug) in rows)
        {
            db.SyncIssues.Add(new SyncIssue
            {
                SyncRun = run,
                Kind = kind,
                Rule = rule,
                Slug = slug,
                Message = $"{rule} for {slug ?? "list"}",
                Location = kind == IssueKind.Parse ? "Audio" : null,
                CreatedAt = T0.AddMinutes(1),
            });
        }
    }

    [Fact]
    public async Task EmptySnapshotReturnsZeroSummary()
    {
        await factory.ResetAsync(_ => { });

        var response = await factory.NewClient().GetAsync("/v1/issues");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<IssuesDto>(Json))!;

        Assert.Null(body.RunId);
        Assert.Equal(0, body.Summary.Total);
        Assert.Empty(body.Items);
    }

    [Fact]
    public async Task ListsSnapshotWithSummary()
    {
        await factory.ResetAsync(db => SeedRun(db, "abc123",
            (IssueKind.Parse, "parse_warning", null),
            (IssueKind.Enrich, "enrich_failed", "tuner"),
            (IssueKind.Quality, "missing_license", "tuner"),
            (IssueKind.Quality, "missing_icon", "micup")));

        var body = (await factory.NewClient().GetFromJsonAsync<IssuesDto>("/v1/issues", Json))!;

        Assert.NotNull(body.RunId);
        Assert.Equal("abc123", body.HeadCommit);
        Assert.Equal(1, body.Summary.Parse);
        Assert.Equal(1, body.Summary.Enrich);
        Assert.Equal(2, body.Summary.Quality);
        Assert.Equal(4, body.Summary.Total);
        Assert.Equal(4, body.Total);
        Assert.Equal(4, body.Items.Count);
        var enrich = body.Items.Single(i => i.Kind == "enrich");
        Assert.Equal("tuner", enrich.Slug);
        Assert.Equal("enrich_failed", enrich.Rule);
    }

    [Fact]
    public async Task KindAndRuleFiltersApply()
    {
        await factory.ResetAsync(db => SeedRun(db, "abc123",
            (IssueKind.Enrich, "enrich_failed", "tuner"),
            (IssueKind.Quality, "missing_license", "tuner"),
            (IssueKind.Quality, "missing_icon", "micup")));

        var client = factory.NewClient();
        var quality = (await client.GetFromJsonAsync<IssuesDto>("/v1/issues?kind=quality", Json))!;
        Assert.Equal(2, quality.Total);
        Assert.All(quality.Items, i => Assert.Equal("quality", i.Kind));
        // Summary still covers the whole snapshot.
        Assert.Equal(3, quality.Summary.Total);

        var rule = (await client.GetFromJsonAsync<IssuesDto>("/v1/issues?rule=missing_icon", Json))!;
        Assert.Equal(1, rule.Total);
        Assert.Equal("micup", rule.Items[0].Slug);
    }

    [Fact]
    public async Task InvalidKindAndPageAre400()
    {
        var client = factory.NewClient();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/issues?kind=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync("/v1/issues?page=0")).StatusCode);
    }

    [Fact]
    public async Task EtagReturns304()
    {
        await factory.ResetAsync(db => SeedRun(db, "abc123",
            (IssueKind.Quality, "missing_icon", "tuner")));

        var client = factory.NewClient();
        var first = await client.GetAsync("/v1/issues");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var conditional = new HttpRequestMessage(HttpMethod.Get, "/v1/issues");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        var second = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task PagingIsClamped()
    {
        await factory.ResetAsync(db => SeedRun(db, "abc123",
            (IssueKind.Quality, "missing_icon", "tuner"),
            (IssueKind.Quality, "missing_license", "micup")));

        var body = (await factory.NewClient()
            .GetFromJsonAsync<IssuesDto>("/v1/issues?page=2&pageSize=1", Json))!;

        Assert.Equal(2, body.Total);
        Assert.Single(body.Items);
        Assert.Equal(2, body.Page);
    }
}

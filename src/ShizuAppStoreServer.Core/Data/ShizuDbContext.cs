using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ShizuAppStoreServer.Core.Data;

public sealed class ShizuDbContext(DbContextOptions<ShizuDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<App> Apps => Set<App>();
    public DbSet<AppVersion> AppVersions => Set<AppVersion>();
    public DbSet<AppDownload> Downloads => Set<AppDownload>();
    public DbSet<UsageAnalysisRun> UsageAnalysisRuns => Set<UsageAnalysisRun>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<SyncIssue> SyncIssues => Set<SyncIssue>();
    public DbSet<SyncRequest> SyncRequests => Set<SyncRequest>();
    public DbSet<RemovedApp> RemovedApps => Set<RemovedApp>();
    public DbSet<PackageException> PackageExceptions => Set<PackageException>();
    public DbSet<AppDownloadExclusion> AppDownloadExclusions => Set<AppDownloadExclusion>();
    public DbSet<ConfigFlag> ConfigFlags => Set<ConfigFlag>();
    public DbSet<ClientUserAgent> ClientUserAgents => Set<ClientUserAgent>();
    public DbSet<ClientUserAgentDay> ClientUserAgentDays => Set<ClientUserAgentDay>();
    public DbSet<AppInstallDay> AppInstallDays => Set<AppInstallDay>();
    public DbSet<AppVersionInstallDay> AppVersionInstallDays => Set<AppVersionInstallDay>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    public override int SaveChanges()
    {
        BumpUpdatedAtForSummaryChanges();
        NormalizeDateTimeOffsets();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        BumpUpdatedAtForSummaryChanges();
        NormalizeDateTimeOffsets();
        return base.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Icon hashes, stars, download totals, the served APK version, the list-change
    /// date and the APK-derived display name are part of the summary clients cache
    /// and reach them through /v1/changes, which is keyed on UpdatedAt. Enrichment rewrites
    /// those columns without touching UpdatedAt, so bump it here for every
    /// producer at once.
    /// </summary>
    private void BumpUpdatedAtForSummaryChanges()
    {
        ChangeTracker.DetectChanges();
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries<App>())
        {
            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            var isRelevant = entry.Property(nameof(App.IconHash)).IsModified
                || entry.Property(nameof(App.Stars)).IsModified
                || entry.Property(nameof(App.DownloadTotal)).IsModified
                || entry.Property(nameof(App.VersionUpdatedAt)).IsModified
                || entry.Property(nameof(App.ListUpdatedAt)).IsModified
                || entry.Property(nameof(App.AuthorName)).IsModified
                || entry.Property(nameof(App.AuthorUrl)).IsModified
                || entry.Property(nameof(App.AuthorKey)).IsModified
                || entry.Property(nameof(App.VersionName)).IsModified
                || entry.Property(nameof(App.DisplayName)).IsModified;
            if (!isRelevant)
            {
                continue;
            }

            var updatedAt = entry.Property(nameof(App.UpdatedAt));
            if (updatedAt.IsModified)
            {
                continue;
            }

            updatedAt.CurrentValue = now;
            updatedAt.IsModified = true;
        }
    }
    
    private void NormalizeDateTimeOffsets()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is DateTimeOffset value && value.Offset != TimeSpan.Zero)
                {
                    property.CurrentValue = value.ToUniversalTime();
                }
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.ToTable("categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Section).HasColumnName("section").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.ParentId).HasColumnName("parent_id");
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.Section, x.ParentId });
        });

        b.Entity<App>(e =>
        {
            e.ToTable("apps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(200);
            e.Property(x => x.ApkLabel).HasColumnName("apk_label").HasMaxLength(200);
            e.Property(x => x.Description).HasColumnName("description").IsRequired();
            e.Property(x => x.License).HasColumnName("license").HasMaxLength(64);
            e.Property(x => x.Listing).HasColumnName("listing").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.IsRecommended).HasColumnName("is_recommended");
            e.Property(x => x.HasPaid).HasColumnName("has_paid");
            e.Property(x => x.HasIap).HasColumnName("has_iap");
            e.Property(x => x.HasAds).HasColumnName("has_ads");
            e.Property(x => x.TrialDays).HasColumnName("trial_days");
            e.Property(x => x.RequiresRoot).HasColumnName("requires_root");
            e.Property(x => x.ParentId).HasColumnName("parent_id");
            e.HasOne(x => x.Parent).WithMany(x => x.Children).HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            // Extra packages from the same repo cascade with the list row.
            e.Property(x => x.RootAppId).HasColumnName("root_app_id");
            e.HasOne(x => x.Root).WithMany(x => x.Variants).HasForeignKey(x => x.RootAppId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RootAppId);
            e.Property(x => x.Url).HasColumnName("url").HasMaxLength(2000).IsRequired();
            // NOTE: non-unique on purpose: the real list contains the same URL
            // in several categories (e.g. fluffy, krude, AlwaysOnDisplayToggle).
            e.HasIndex(x => x.Url);
            e.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(2000);
            e.Property(x => x.SourceKind).HasColumnName("source_kind").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.Availability).HasColumnName("availability").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.ExcludedReason).HasColumnName("excluded_reason");
            e.Property(x => x.ExcludeOverride).HasColumnName("exclude_override");
            e.Property(x => x.PackageName).HasColumnName("package_name").HasMaxLength(256);
            // Permissions are a plain string list; store newline-joined so the
            // column stays readable and the schema is identical on Postgres and
            // the SQLite test provider. The comparer makes mutations detectable.
            e.Property(x => x.Permissions)
                .HasColumnName("permissions")
                .HasConversion(
                    v => string.Join('\n', v),
                    v => v.Length == 0
                        ? new List<string>()
                        : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    new ValueComparer<List<string>>(
                        (a, b) => a!.SequenceEqual(b!),
                        v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                        v => v.ToList()))
                .IsRequired();
            e.Property(x => x.AuthorName).HasColumnName("author_name").HasMaxLength(200);
            e.Property(x => x.AuthorUrl).HasColumnName("author_url").HasMaxLength(2000);
            e.Property(x => x.AuthorKey).HasColumnName("author_key").HasMaxLength(200);
            e.Property(x => x.FullDescription).HasColumnName("full_description");
            e.Property(x => x.Changelog).HasColumnName("changelog");
            e.Property(x => x.ChangelogUrl).HasColumnName("changelog_url").HasMaxLength(2000);
            // Screenshot URLs share the Permissions list encoding: newline
            // joined so the schema stays identical on Postgres and SQLite.
            e.Property(x => x.Screenshots)
                .HasColumnName("screenshots")
                .HasConversion(
                    v => string.Join('\n', v),
                    v => v.Length == 0
                        ? new List<string>()
                        : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    new ValueComparer<List<string>>(
                        (a, b) => a!.SequenceEqual(b!),
                        v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                        v => v.ToList()))
                .IsRequired();
            e.Property(x => x.ScreenshotsCheckedAt).HasColumnName("screenshots_checked_at");
            e.Property(x => x.StoreUrl).HasColumnName("store_url").HasMaxLength(2000);
            e.Property(x => x.VersionName).HasColumnName("version_name").HasMaxLength(200);
            e.Property(x => x.IconHash).HasColumnName("icon_hash").HasMaxLength(128);
            e.Property(x => x.IconAdaptive).HasColumnName("icon_adaptive");
            e.Property(x => x.Stars).HasColumnName("stars");
            e.Property(x => x.DownloadTotal).HasColumnName("download_total");
            e.Property(x => x.InstallCount).HasColumnName("install_count").HasDefaultValue(0L);
            e.Property(x => x.InstallCountUpdatedAt).HasColumnName("install_count_updated_at");
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.HasOne(x => x.Category).WithMany(x => x.Apps).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.AddedAt).HasColumnName("added_at").IsRequired();
            e.Property(x => x.ListUpdatedAt).HasColumnName("list_updated_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.VersionUpdatedAt).HasColumnName("version_updated_at");
            e.Property(x => x.LastCheckedAt).HasColumnName("last_checked_at");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.EnrichEtag).HasColumnName("enrich_etag").HasMaxLength(256);
            // AI source analysis. Detail-only fields plus the generation
            // markers the backfill compares against.
            e.Property(x => x.UsageShort).HasColumnName("usage_short").HasMaxLength(320);
            e.Property(x => x.UsageMarkdown).HasColumnName("usage_markdown");
            e.Property(x => x.UsageMarkdownUsage).HasColumnName("usage_markdown_usage");
            e.Property(x => x.UsageMarkdownApiUsage).HasColumnName("usage_markdown_api_usage");
            e.Property(x => x.UsageMarkdownNotableDetails).HasColumnName("usage_markdown_notable_details");
            e.Property(x => x.UsageAnalyzedAt).HasColumnName("usage_analyzed_at");
            e.Property(x => x.UsageModel).HasColumnName("usage_model").HasMaxLength(64);
            e.Property(x => x.UsageCommit).HasColumnName("usage_commit").HasMaxLength(64);
            e.Property(x => x.UsageReleaseRef).HasColumnName("usage_release_ref").HasMaxLength(128);
            e.Property(x => x.UsagePromptVersion).HasColumnName("usage_prompt_version");
            e.Property(x => x.UsageAnalysisVersion).HasColumnName("usage_analysis_version");
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.UpdatedAt);
            e.HasIndex(x => x.Availability);
        });

        b.Entity<AppDownload>(e =>
        {
            e.ToTable("app_downloads");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.AppId).HasColumnName("app_id");
            e.HasOne(x => x.App).WithMany(x => x.Downloads).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.PackageName).HasColumnName("package_name").HasMaxLength(255);
            e.Property(x => x.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.SourceRef).HasColumnName("source_ref").HasMaxLength(256);
            e.Property(x => x.ReleaseTag).HasColumnName("release_tag").HasMaxLength(128);
            e.Property(x => x.ApkUrl).HasColumnName("apk_url").HasMaxLength(2000).IsRequired();
            e.Property(x => x.ArchiveEntry).HasColumnName("archive_entry").HasMaxLength(256);
            e.Property(x => x.VersionCode).HasColumnName("version_code");
            e.Property(x => x.VersionName).HasColumnName("version_name").HasMaxLength(64);
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(128);
            e.Property(x => x.SigSha256).HasColumnName("sig_sha256").HasMaxLength(512);
            e.Property(x => x.SigMd5).HasColumnName("sig_md5").HasMaxLength(512);
            e.Property(x => x.MinSdk).HasColumnName("min_sdk");
            e.Property(x => x.TargetSdk).HasColumnName("target_sdk");
            e.Property(x => x.CompileSdk).HasColumnName("compile_sdk");
            // Locales share the Permissions encoding: newline-joined so the
            // schema stays identical on Postgres and the SQLite test provider.
            e.Property(x => x.Locales)
                .HasColumnName("locales")
                .HasConversion(
                    v => string.Join('\n', v),
                    v => v.Length == 0
                        ? new List<string>()
                        : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    new ValueComparer<List<string>>(
                        (a, b) => a!.SequenceEqual(b!),
                        v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                        v => v.ToList()))
                .IsRequired();
            // F-Droid publishes <nativecode> as a comma-joined ABI list, so
            // this must fit several architectures, not just one.
            e.Property(x => x.Abi).HasColumnName("abi").HasMaxLength(128);
            e.Property(x => x.DhizukuDeclared).HasColumnName("dhizuku_declared");
            // Signal lists share the Locales encoding: newline-joined.
            MapNewlineList(e, x => x.Trackers, "trackers");
            MapNewlineList(e, x => x.TrackerSignatures, "tracker_signatures");
            MapNewlineList(e, x => x.TrackerTags, "tracker_tags");
            MapNewlineList(e, x => x.Abis, "abis");
            // Localized labels are "locale=label" entries: the first '='
            // separates, so a label containing '=' survives intact.
            MapNewlineList(e, x => x.LocalizedLabels, "localized_labels");
            e.Property(x => x.SignerDn).HasColumnName("signer_dn");
            e.Property(x => x.SignerScheme).HasColumnName("signer_scheme").HasMaxLength(64);
            e.Property(x => x.SignerKeyAlgorithm).HasColumnName("signer_key_algorithm").HasMaxLength(64);
            e.Property(x => x.AnalysisVersion).HasColumnName("analysis_version");
            e.Property(x => x.SigKey).HasColumnName("sig_key").HasMaxLength(128).IsRequired();
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.Analyzed).HasColumnName("analyzed");
            e.Property(x => x.Inspected).HasColumnName("inspected");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at").IsRequired();
            // NULLS NOT DISTINCT so universal builds (abi null) still dedupe.
            // Package is part of the key: flavor builds of one app share a row
            // but are distinct installable packages.
            e.HasIndex(x => new { x.AppId, x.PackageName, x.SigKey, x.Abi }).IsUnique().AreNullsDistinct(false);
            // At most one primary candidate per app; recomputed on every upsert.
            e.HasIndex(x => x.AppId).IsUnique().HasFilter("is_primary");
        });

        b.Entity<UsageAnalysisRun>(e =>
        {
            e.ToTable("usage_analysis_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.AppId).HasColumnName("app_id");
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.RepoForge).HasColumnName("repo_forge").HasMaxLength(16);
            e.Property(x => x.RepoCommit).HasColumnName("repo_commit").HasMaxLength(64);
            e.Property(x => x.RepoRef).HasColumnName("repo_ref").HasMaxLength(128);
            e.Property(x => x.Model).HasColumnName("model").HasMaxLength(64);
            e.Property(x => x.PromptVersion).HasColumnName("prompt_version");
            e.Property(x => x.Error).HasColumnName("error").HasMaxLength(1024);
            e.Property(x => x.LogFile).HasColumnName("log_file").HasMaxLength(260);
            e.Property(x => x.InputTokens).HasColumnName("input_tokens");
            e.Property(x => x.CachedInputTokens).HasColumnName("cached_input_tokens");
            e.Property(x => x.OutputTokens).HasColumnName("output_tokens");
            e.Property(x => x.CostUsd).HasColumnName("cost_usd").HasPrecision(12, 6);
            e.Property(x => x.ToolCalls).HasColumnName("tool_calls");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").IsRequired();
            // At most one active run per app; enforce in the database because
            // claim and enqueue race across worker slots and enrichment scopes.
            e.HasIndex(x => x.AppId).IsUnique().HasFilter("status in ('Pending', 'Running')");
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.AppId, x.CreatedAt });
        });

        b.Entity<AppVersion>(e =>
        {
            e.ToTable("app_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.AppId).HasColumnName("app_id");
            e.HasOne(x => x.App).WithMany(x => x.Versions).HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.VersionCode).HasColumnName("version_code");
            e.Property(x => x.VersionName).HasColumnName("version_name").HasMaxLength(64);
            e.Property(x => x.ApkUrl).HasColumnName("apk_url").HasMaxLength(2000);
            e.Property(x => x.DetectedAt).HasColumnName("detected_at").IsRequired();
            e.HasIndex(x => new { x.AppId, x.VersionCode }).IsUnique();
        });

        b.Entity<SyncRun>(e =>
        {
            e.ToTable("sync_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.StartedAt).HasColumnName("started_at").IsRequired();
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.Property(x => x.Trigger).HasColumnName("trigger").HasMaxLength(32).IsRequired();
            e.Property(x => x.HeadCommit).HasColumnName("head_commit").HasMaxLength(64);
            e.Property(x => x.Added).HasColumnName("added");
            e.Property(x => x.Updated).HasColumnName("updated");
            e.Property(x => x.Removed).HasColumnName("removed");
            e.Property(x => x.Failed).HasColumnName("failed");
            e.Property(x => x.IssueCount).HasColumnName("issue_count");
            e.Property(x => x.Error).HasColumnName("error");
        });

        b.Entity<SyncIssue>(e =>
        {
            e.ToTable("sync_issues");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.SyncRunId).HasColumnName("sync_run_id");
            e.HasOne(x => x.SyncRun).WithMany().HasForeignKey(x => x.SyncRunId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.Rule).HasColumnName("rule").HasMaxLength(64).IsRequired();
            e.Property(x => x.AppId).HasColumnName("app_id");
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(200);
            e.Property(x => x.Message).HasColumnName("message").IsRequired();
            e.Property(x => x.Location).HasColumnName("location");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.HasIndex(x => x.SyncRunId);
            e.HasIndex(x => x.Kind);
            e.HasIndex(x => x.Rule);
            e.HasIndex(x => x.Slug);
        });

        b.Entity<SyncRequest>(e =>
        {
            e.ToTable("sync_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.RequestedAt).HasColumnName("requested_at").IsRequired();
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(256);
            e.Property(x => x.Full).HasColumnName("full");
            e.Property(x => x.Processed).HasColumnName("processed");
            e.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            e.HasIndex(x => x.Processed);
        });

        b.Entity<RemovedApp>(e =>
        {
            e.ToTable("removed_apps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
            e.Property(x => x.Listing).HasColumnName("listing").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.RemovedAt).HasColumnName("removed_at").IsRequired();
            e.HasIndex(x => x.RemovedAt);
        });

        b.Entity<AppDownloadExclusion>(e =>
        {
            e.ToTable("app_download_exclusions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.AppSlug).HasColumnName("app_slug").HasMaxLength(200).IsRequired();
            e.Property(x => x.PackageName).HasColumnName("package_name").HasMaxLength(256).IsRequired();
            e.HasIndex(x => new { x.AppSlug, x.PackageName }).IsUnique();
            e.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });

        b.Entity<ConfigFlag>(e =>
        {
            e.ToTable("config_flags");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(200).IsRequired();
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });

        b.Entity<ClientUserAgent>(e =>
        {
            e.ToTable("client_user_agents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.UserAgent).HasColumnName("user_agent")
                .HasMaxLength(ClientUserAgent.MaxUserAgentLength).IsRequired();
            e.HasIndex(x => x.UserAgent).IsUnique();
            e.Property(x => x.RequestCount).HasColumnName("request_count");
            e.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at").IsRequired();
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at").IsRequired();
            e.Property(x => x.LastPath).HasColumnName("last_path").HasMaxLength(512);
        });

        b.Entity<ClientUserAgentDay>(e =>
        {
            e.ToTable("client_user_agent_days");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.UserAgent).HasColumnName("user_agent")
                .HasMaxLength(ClientUserAgent.MaxUserAgentLength).IsRequired();
            e.Property(x => x.Day).HasColumnName("day").IsRequired();
            e.Property(x => x.RequestCount).HasColumnName("request_count");
            e.HasIndex(x => new { x.UserAgent, x.Day }).IsUnique();
        });

        b.Entity<AppInstallDay>(e =>
        {
            e.ToTable("app_install_days");
            e.HasKey(x => new { x.AppId, x.Day });
            e.Property(x => x.AppId).HasColumnName("app_id");
            e.Property(x => x.Day).HasColumnName("day").IsRequired();
            e.Property(x => x.InstallCount).HasColumnName("install_count");
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AppVersionInstallDay>(e =>
        {
            e.ToTable("app_version_install_days");
            e.HasKey(x => new { x.AppId, x.VersionCode, x.InstallType, x.Day });
            e.Property(x => x.AppId).HasColumnName("app_id");
            e.Property(x => x.VersionCode).HasColumnName("version_code");
            e.Property(x => x.InstallType).HasColumnName("install_type")
                .HasMaxLength(AppVersionInstallDay.MaxInstallTypeLength).IsRequired();
            e.Property(x => x.Day).HasColumnName("day").IsRequired();
            e.Property(x => x.InstallCount).HasColumnName("install_count");
            e.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RequestLog>(e =>
        {
            e.ToTable("request_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.SeenAt).HasColumnName("seen_at").IsRequired();
            e.HasIndex(x => x.SeenAt);
            e.Property(x => x.Method).HasColumnName("method").IsRequired();
            e.Property(x => x.Path).HasColumnName("path").IsRequired();
            e.Property(x => x.QueryString).HasColumnName("query_string");
            e.Property(x => x.RawTarget).HasColumnName("raw_target").IsRequired();
            e.Property(x => x.Protocol).HasColumnName("protocol").IsRequired();
            e.Property(x => x.Scheme).HasColumnName("scheme").IsRequired();
            e.Property(x => x.Host).HasColumnName("host");
            e.Property(x => x.RawRequest).HasColumnName("raw_request").IsRequired();
            // jsonb so an operator can query individual headers with SQL.
            e.Property(x => x.Headers).HasColumnName("headers").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.StatusCode).HasColumnName("status_code");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.UserAgent).HasColumnName("user_agent");
            e.Property(x => x.Origin).HasColumnName("origin");
            e.Property(x => x.RemoteIp).HasColumnName("remote_ip");
            e.Property(x => x.ClientIp).HasColumnName("client_ip");
            e.Property(x => x.ForwardedFor).HasColumnName("forwarded_for");
            e.Property(x => x.CfRay).HasColumnName("cf_ray");
            e.Property(x => x.Country).HasColumnName("country");
            e.Property(x => x.TraceId).HasColumnName("trace_id");
        });

        b.Entity<PackageException>(e =>
        {
            e.ToTable("package_exceptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.PackageName).HasColumnName("package_name").HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.PackageName).IsUnique();
            e.Property(x => x.Action).HasColumnName("action").HasConversion<string>().HasMaxLength(16).IsRequired();
            e.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();

            // rish-mcp talks to Shizuku through the rish shell gateway, so it
            // declares no Shizuku permission but must stay available.
            e.HasData(new PackageException
            {
                Id = 1,
                PackageName = "kr.scin.rishmcp",
                Action = PackageExceptionAction.Allow,
                Note = "Uses the rish shell gateway instead of the Shizuku permission.",
                CreatedAt = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero),
            });
        });
    }

    /// <summary>
    /// Newline-joined string list column, the encoding used for every list on
    /// the catalog tables: readable in SQL and identical on Postgres and the
    /// SQLite test provider. The comparer makes in-place mutations visible to
    /// change tracking.
    /// </summary>
    private static void MapNewlineList(
        EntityTypeBuilder<AppDownload> builder,
        Expression<Func<AppDownload, List<string>>> property,
        string column)
    {
        builder.Property(property)
            .HasColumnName(column)
            .HasConversion(
                v => string.Join('\n', v),
                v => v.Length == 0
                    ? new List<string>()
                    : v.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                new ValueComparer<List<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                    v => v.ToList()))
            .IsRequired();
    }
}

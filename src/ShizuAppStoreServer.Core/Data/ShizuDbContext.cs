using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ShizuAppStoreServer.Core.Data;

public sealed class ShizuDbContext(DbContextOptions<ShizuDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<App> Apps => Set<App>();
    public DbSet<AppVersion> AppVersions => Set<AppVersion>();
    public DbSet<AppDownload> Downloads => Set<AppDownload>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<SyncIssue> SyncIssues => Set<SyncIssue>();
    public DbSet<SyncRequest> SyncRequests => Set<SyncRequest>();
    public DbSet<RemovedApp> RemovedApps => Set<RemovedApp>();
    public DbSet<ConfigFlag> ConfigFlags => Set<ConfigFlag>();

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
    /// Icon hashes, stars, download totals, the served APK version, and the
    /// list-change date are part of the summary clients cache and reach them
    /// through /v1/changes, which is keyed on UpdatedAt. Enrichment rewrites
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
                || entry.Property(nameof(App.VersionName)).IsModified;
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
            e.Property(x => x.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.SourceRef).HasColumnName("source_ref").HasMaxLength(256);
            e.Property(x => x.ApkUrl).HasColumnName("apk_url").HasMaxLength(2000).IsRequired();
            e.Property(x => x.ArchiveEntry).HasColumnName("archive_entry").HasMaxLength(256);
            e.Property(x => x.VersionCode).HasColumnName("version_code");
            e.Property(x => x.VersionName).HasColumnName("version_name").HasMaxLength(64);
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(128);
            e.Property(x => x.SigSha256).HasColumnName("sig_sha256").HasMaxLength(512);
            e.Property(x => x.SigMd5).HasColumnName("sig_md5").HasMaxLength(512);
            e.Property(x => x.MinSdk).HasColumnName("min_sdk");
            e.Property(x => x.SigKey).HasColumnName("sig_key").HasMaxLength(128).IsRequired();
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at").IsRequired();
            e.HasIndex(x => new { x.AppId, x.SigKey }).IsUnique();
            // At most one primary candidate per app; recomputed on every upsert.
            e.HasIndex(x => x.AppId).IsUnique().HasFilter("is_primary");
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

        b.Entity<ConfigFlag>(e =>
        {
            e.ToTable("config_flags");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(200).IsRequired();
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        });
    }
}

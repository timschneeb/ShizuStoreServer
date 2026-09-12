using Microsoft.EntityFrameworkCore;

namespace ShizuAppStoreServer.Core.Data;

public sealed class ShizuDbContext(DbContextOptions<ShizuDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<App> Apps => Set<App>();
    public DbSet<AppVersion> AppVersions => Set<AppVersion>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();
    public DbSet<SyncRequest> SyncRequests => Set<SyncRequest>();
    public DbSet<RemovedApp> RemovedApps => Set<RemovedApp>();

    public override int SaveChanges()
    {
        NormalizeDateTimeOffsets();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        NormalizeDateTimeOffsets();
        return base.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Npgsql writes DateTimeOffset to timestamptz only with Offset=0
    /// (Postgres stores UTC instants, no offsets), while SQLite accepts
    /// anything — so a stray local offset passes every test and then
    /// crashes the production save (seen live 2026-09-12 via git-history
    /// dates). Normalizing here loses nothing and closes the divergence
    /// for all current and future producers at one choke point.
    /// </summary>
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
            // NOTE: non-unique on purpose — the real list contains the same URL
            // in several categories (e.g. fluffy, krude, AlwaysOnDisplayToggle).
            e.HasIndex(x => x.Url);
            e.Property(x => x.SourceUrl).HasColumnName("source_url").HasMaxLength(2000);
            e.Property(x => x.SourceKind).HasColumnName("source_kind").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.Availability).HasColumnName("availability").HasConversion<string>().HasMaxLength(32).IsRequired();
            e.Property(x => x.ExcludedReason).HasColumnName("excluded_reason");
            e.Property(x => x.ExcludeOverride).HasColumnName("exclude_override");
            e.Property(x => x.PackageName).HasColumnName("package_name").HasMaxLength(256);
            e.Property(x => x.VersionCode).HasColumnName("version_code");
            e.Property(x => x.VersionName).HasColumnName("version_name").HasMaxLength(64);
            e.Property(x => x.ApkUrl).HasColumnName("apk_url").HasMaxLength(2000);
            e.Property(x => x.ApkSource).HasColumnName("apk_source").HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ApkSourceRef).HasColumnName("apk_source_ref").HasMaxLength(256);
            e.Property(x => x.ApkSize).HasColumnName("apk_size");
            e.Property(x => x.ApkSha256).HasColumnName("apk_sha256").HasMaxLength(128);
            e.Property(x => x.ApkArchiveEntry).HasColumnName("apk_archive_entry").HasMaxLength(256);
            e.Property(x => x.MinSdk).HasColumnName("min_sdk");
            e.Property(x => x.StoreUrl).HasColumnName("store_url").HasMaxLength(2000);
            e.Property(x => x.IconHash).HasColumnName("icon_hash").HasMaxLength(128);
            e.Property(x => x.IconAdaptive).HasColumnName("icon_adaptive");
            e.Property(x => x.SigSha256).HasColumnName("sig_sha256").HasMaxLength(512);
            e.Property(x => x.SigMd5).HasColumnName("sig_md5").HasMaxLength(512);
            e.Property(x => x.FdroidApkUrl).HasColumnName("fdroid_apk_url").HasMaxLength(2000);
            e.Property(x => x.FdroidVersionCode).HasColumnName("fdroid_version_code");
            e.Property(x => x.FdroidVersionName).HasColumnName("fdroid_version_name").HasMaxLength(64);
            e.Property(x => x.FdroidApkSize).HasColumnName("fdroid_apk_size");
            e.Property(x => x.FdroidApkSha256).HasColumnName("fdroid_apk_sha256").HasMaxLength(128);
            e.Property(x => x.FdroidSigSha256).HasColumnName("fdroid_sig_sha256").HasMaxLength(512);
            e.Property(x => x.FdroidSigMd5).HasColumnName("fdroid_sig_md5").HasMaxLength(512);
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.HasOne(x => x.Category).WithMany(x => x.Apps).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.AddedAt).HasColumnName("added_at").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
            e.Property(x => x.LastCheckedAt).HasColumnName("last_checked_at");
            e.Property(x => x.LastError).HasColumnName("last_error");
            e.Property(x => x.EnrichEtag).HasColumnName("enrich_etag").HasMaxLength(256);
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.UpdatedAt);
            e.HasIndex(x => x.Availability);
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
            e.Property(x => x.Error).HasColumnName("error");
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
    }
}

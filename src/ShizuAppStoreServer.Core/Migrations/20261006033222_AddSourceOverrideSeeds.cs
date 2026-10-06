using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <summary>
    /// Seeds the source-behavior rows that were previously hard-coded: instafel
    /// and LinkSheet release homes, the hlbmerge GitCode mirror, Smartspacer's
    /// all-releases scan and the Universal-ReVanced-Manager prerelease channel.
    /// Keyed by list URL so a catalog that predates the deploy is matched.
    /// </summary>
    public partial class AddSourceOverrideSeeds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                INSERT INTO app_overrides (app_slug, field, value, note, created_at, updated_at)
                SELECT a.slug, v.field, v.value, v.note, now(), now()
                FROM apps a
                JOIN (VALUES
                    ('https://github.com/mamiiblt/instafel', 'source_release_home', 'instafel/u-rel', 'seed: instafel updater release repo'),
                    ('https://github.com/linksheet/linksheet', 'source_release_home', 'LinkSheet/nightly', 'seed: LinkSheet nightly release repo'),
                    ('https://github.com/molihuan/hlbmerge_flutter', 'source_gitcode_mirror', 'bigmolihuan/hlbmerge_flutter|molihuan/hlbmerge_flutter', 'seed: hlbmerge GitCode mirror'),
                    ('https://github.com/kieronquinn/smartspacerplugins', 'source_scan_all_releases', 'true', 'seed: SmartspacerPlugins ships one app per release'),
                    ('https://github.com/jman-github/universal-revanced-manager', 'source_prefer_prerelease', 'true', 'seed: Universal-ReVanced-Manager prerelease channel')
                ) AS v(url, field, value, note)
                  ON lower(rtrim(a.url, '/')) = v.url
                 AND a.root_app_id IS NULL
                ON CONFLICT (app_slug, field) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data seed only; operators may have edited these rows after the
            // fact, so a rollback deliberately leaves them in place.
        }
    }
}

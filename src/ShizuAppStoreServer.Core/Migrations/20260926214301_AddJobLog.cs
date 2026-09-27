using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJobLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Order matters: build job_runs and copy the existing sync run
            // history into it before sync_runs is dropped, so run ids (and the
            // sync_issues references to them) survive. Ids are copied verbatim
            // and the identity sequence is advanced afterwards.
            migrationBuilder.DropForeignKey(
                name: "FK_sync_issues_sync_runs_sync_run_id",
                table: "sync_issues");

            migrationBuilder.RenameColumn(
                name: "sync_run_id",
                table: "sync_issues",
                newName: "job_run_id");

            migrationBuilder.RenameIndex(
                name: "IX_sync_issues_sync_run_id",
                table: "sync_issues",
                newName: "IX_sync_issues_job_run_id");

            migrationBuilder.AddColumn<string>(
                name: "trigger",
                table: "usage_analysis_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "job_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_day = table.Column<DateOnly>(type: "date", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    items_total = table.Column<int>(type: "integer", nullable: false),
                    items_ok = table.Column<int>(type: "integer", nullable: false),
                    items_skipped = table.Column<int>(type: "integer", nullable: false),
                    items_failed = table.Column<int>(type: "integer", nullable: false),
                    events_count = table.Column<int>(type: "integer", nullable: false),
                    events_dropped = table.Column<int>(type: "integer", nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    usage_analysis_run_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_runs", x => x.id);
                    table.ForeignKey(
                        name: "FK_job_runs_usage_analysis_runs_usage_analysis_run_id",
                        column: x => x.usage_analysis_run_id,
                        principalTable: "usage_analysis_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "job_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_run_id = table.Column<long>(type: "bigint", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    phase = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    app_id = table.Column<long>(type: "bigint", nullable: true),
                    slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_job_events_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_job_events_job_runs_job_run_id",
                        column: x => x.job_run_id,
                        principalTable: "job_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                INSERT INTO job_runs (
                    id, kind, trigger, status, started_at, started_day, finished_at, duration_ms,
                    reference, items_total, items_ok, items_skipped, items_failed,
                    events_count, events_dropped, summary, error, metadata)
                SELECT
                    id,
                    'Sync',
                    CASE trigger
                        WHEN 'startup' THEN 'Startup'
                        WHEN 'scheduled' THEN 'Scheduled'
                        WHEN 'nightly' THEN 'Nightly'
                        WHEN 'webhook' THEN 'Webhook'
                        WHEN 'backfill' THEN 'Backfill'
                        ELSE 'Manual'
                    END,
                    CASE WHEN error IS NULL THEN 'Succeeded' ELSE 'Failed' END,
                    started_at,
                    (started_at AT TIME ZONE 'UTC')::date,
                    finished_at,
                    CASE WHEN finished_at IS NULL THEN NULL
                         ELSE (EXTRACT(EPOCH FROM (finished_at - started_at)) * 1000)::integer END,
                    head_commit,
                    added + updated + removed,
                    added + updated,
                    removed,
                    failed,
                    0,
                    0,
                    NULL,
                    error,
                    jsonb_build_object(
                        'added', added, 'updated', updated, 'removed', removed,
                        'issueCount', issue_count)
                FROM sync_runs;

                SELECT setval(
                    pg_get_serial_sequence('job_runs', 'id'),
                    COALESCE(MAX(id), 1),
                    MAX(id) IS NOT NULL)
                FROM job_runs;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_job_events_app_id",
                table: "job_events",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_job_events_job_run_id_seq",
                table: "job_events",
                columns: new[] { "job_run_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_events_job_run_id_type",
                table: "job_events",
                columns: new[] { "job_run_id", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_kind_started_day",
                table: "job_runs",
                columns: new[] { "kind", "started_day" });

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_started_day",
                table: "job_runs",
                column: "started_day");

            migrationBuilder.CreateIndex(
                name: "IX_job_runs_usage_analysis_run_id",
                table: "job_runs",
                column: "usage_analysis_run_id");

            migrationBuilder.AddForeignKey(
                name: "FK_sync_issues_job_runs_job_run_id",
                table: "sync_issues",
                column: "job_run_id",
                principalTable: "job_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.DropTable(
                name: "sync_runs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_sync_issues_job_runs_job_run_id",
                table: "sync_issues");

            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    added = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    failed = table.Column<int>(type: "integer", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    head_commit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    issue_count = table.Column<int>(type: "integer", nullable: false),
                    removed = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_runs", x => x.id);
                });

            // Copy sync runs back so a rollback keeps the history. Skipped runs
            // have no representation in the old shape and are left behind.
            migrationBuilder.Sql(
                """
                INSERT INTO sync_runs (
                    id, added, error, failed, finished_at, head_commit, issue_count,
                    removed, started_at, trigger, updated)
                SELECT
                    id,
                    COALESCE((metadata ->> 'added')::integer, 0),
                    error,
                    items_failed,
                    finished_at,
                    reference,
                    COALESCE((metadata ->> 'issueCount')::integer, 0),
                    COALESCE((metadata ->> 'removed')::integer, 0),
                    started_at,
                    lower(trigger),
                    COALESCE((metadata ->> 'updated')::integer, 0)
                FROM job_runs
                WHERE kind = 'Sync' AND status IN ('Succeeded', 'Failed');

                SELECT setval(
                    pg_get_serial_sequence('sync_runs', 'id'),
                    COALESCE(MAX(id), 1),
                    MAX(id) IS NOT NULL)
                FROM sync_runs;
                """);

            migrationBuilder.RenameColumn(
                name: "job_run_id",
                table: "sync_issues",
                newName: "sync_run_id");

            migrationBuilder.RenameIndex(
                name: "IX_sync_issues_job_run_id",
                table: "sync_issues",
                newName: "IX_sync_issues_sync_run_id");

            migrationBuilder.DropColumn(
                name: "trigger",
                table: "usage_analysis_runs");

            migrationBuilder.DropTable(
                name: "job_events");

            migrationBuilder.DropTable(
                name: "job_runs");

            migrationBuilder.AddForeignKey(
                name: "FK_sync_issues_sync_runs_sync_run_id",
                table: "sync_issues",
                column: "sync_run_id",
                principalTable: "sync_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

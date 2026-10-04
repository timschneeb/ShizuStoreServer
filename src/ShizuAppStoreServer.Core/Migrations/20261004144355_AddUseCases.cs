using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShizuAppStoreServer.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUseCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "usage_analysis_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Analysis");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "use_case_tags_analyzed_at",
                table: "apps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "use_case_tags_model",
                table: "apps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "use_case_tags_prompt_version",
                table: "apps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "use_cases",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    definition = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_use_cases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_use_cases",
                columns: table => new
                {
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    use_case_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_use_cases", x => new { x.app_id, x.use_case_id });
                    table.ForeignKey(
                        name: "FK_app_use_cases_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_app_use_cases_use_cases_use_case_id",
                        column: x => x.use_case_id,
                        principalTable: "use_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "use_case_candidates",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    merged_into_use_case_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_use_case_candidates", x => x.id);
                    table.ForeignKey(
                        name: "FK_use_case_candidates_use_cases_merged_into_use_case_id",
                        column: x => x.merged_into_use_case_id,
                        principalTable: "use_cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "app_use_case_proposals",
                columns: table => new
                {
                    app_id = table.Column<long>(type: "bigint", nullable: false),
                    candidate_id = table.Column<long>(type: "bigint", nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_use_case_proposals", x => new { x.app_id, x.candidate_id });
                    table.ForeignKey(
                        name: "FK_app_use_case_proposals_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_app_use_case_proposals_use_case_candidates_candidate_id",
                        column: x => x.candidate_id,
                        principalTable: "use_case_candidates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_use_case_proposals_candidate_id",
                table: "app_use_case_proposals",
                column: "candidate_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_use_cases_use_case_id",
                table: "app_use_cases",
                column: "use_case_id");

            migrationBuilder.CreateIndex(
                name: "IX_use_case_candidates_merged_into_use_case_id",
                table: "use_case_candidates",
                column: "merged_into_use_case_id");

            migrationBuilder.CreateIndex(
                name: "IX_use_case_candidates_slug",
                table: "use_case_candidates",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_use_cases_slug",
                table: "use_cases",
                column: "slug",
                unique: true);

            // Seed vocabulary. Definitions are the scope contract handed to the
            // classifier; edits here are behavior changes. Self-scoped work is
            // explicitly excluded so an app that only updates itself earns no
            // install-apps tag.
            migrationBuilder.Sql(
                """
                INSERT INTO use_cases (slug, name, definition, is_active, created_at, updated_at) VALUES
                ('install-apps', 'Install and uninstall apps', 'Installs, updates or uninstalls arbitrary third-party apps or APK files that the user selects or the app downloads. Installing or updating only its own package does not qualify.', true, now(), now()),
                ('app-management', 'Manage other apps', 'Freezes, unfreezes, disables, force-stops, clears data of or changes components of other installed apps. Managing only its own package does not qualify.', true, now(), now()),
                ('file-access', 'Access protected files', 'Reads or writes files outside its own sandbox through Shizuku, such as Android/data, Android/obb, private directories of other apps or system paths. Access limited to its own sandbox does not qualify.', true, now(), now()),
                ('settings-writes', 'Change system settings', 'Reads or writes secure, system or global settings on behalf of the user, for example animation scale, screen timeout or hidden settings screens. Ordinary app preferences do not qualify.', true, now(), now()),
                ('wireless-debugging', 'Wireless debugging', 'Enables, disables or configures wireless debugging, or connects the device over ADB to pair and control a shell without a cable.', true, now(), now()),
                ('activity-launch', 'Launch hidden activities', 'Starts activities, services or shortcuts that normal apps cannot start, including unexported components of other apps or restricted system screens.', true, now(), now()),
                ('shell-command', 'Run shell commands', 'Exposes privileged shell or command execution to the user, scripts or other apps, beyond a fixed set of hidden operations. Running fixed commands internally does not qualify.', true, now(), now()),
                ('call-recording', 'Record phone calls', 'Records incoming or outgoing phone calls, usually by capturing audio as the shell user or reading call audio streams.', true, now(), now()),
                ('audio-control', 'Control audio routing', 'Captures, routes or processes audio beyond ordinary app playback, for example system-wide equalizers, advanced volume control or recording internal audio.', true, now(), now()),
                ('input-automation', 'Automate input', 'Synthesizes taps, swipes, keys or text input to control other apps or the system, for example key mapping or UI automation.', true, now(), now()),
                ('screen-capture', 'Capture the screen', 'Takes screenshots or records the screen, including captures that bypass the standard system consent dialog.', true, now(), now()),
                ('display-control', 'Control displays', 'Changes display configuration such as resolution, density, refresh rate, brightness or rotation, or creates and manages virtual displays, screen mirroring and desktop experiences.', true, now(), now()),
                ('device-owner', 'Device owner and admin', 'Acts as a device owner, profile owner or device admin to apply policies, provision the device or manage users and restrictions.', true, now(), now()),
                ('app-data-backup', 'Back up app data', 'Backs up, restores or migrates data or private files of other apps, for example debuggable app data, game saves or whole-app snapshots.', true, now(), now()),
                ('permission-management', 'Manage permissions', 'Grants, revokes, inspects or otherwise manages runtime permissions and app ops of other apps.', true, now(), now()),
                ('system-ui', 'Customize system UI', 'Changes the status bar, notification shade, quick settings, navigation bar or other system UI surfaces beyond what normal apps can do.', true, now(), now()),
                ('system-monitoring', 'Monitor system internals', 'Reads system or process internals that normal apps cannot access, such as process stats, thermal data, wakelocks or frame timing, for monitoring, diagnostics or performance tools.', true, now(), now()),
                ('power-control', 'Control power and reboot', 'Reboots, powers off, enters recovery or download mode, or otherwise controls the device power state.', true, now(), now()),
                ('network-control', 'Control networking', 'Changes network configuration such as DNS, hosts, firewall or tethering settings beyond ordinary app network access.', true, now(), now()),
                ('sensor-input-read', 'Read raw sensors and input', 'Reads raw sensor data or input events that normal apps cannot access, for example hardware key events or sensor streams outside the standard APIs.', true, now(), now()),
                ('esim-management', 'Manage eSIM profiles', 'Reads, downloads, enables or deletes eSIM profiles via privileged telephony access.', true, now(), now());
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_use_case_proposals");

            migrationBuilder.DropTable(
                name: "app_use_cases");

            migrationBuilder.DropTable(
                name: "use_case_candidates");

            migrationBuilder.DropTable(
                name: "use_cases");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "usage_analysis_runs");

            migrationBuilder.DropColumn(
                name: "use_case_tags_analyzed_at",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "use_case_tags_model",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "use_case_tags_prompt_version",
                table: "apps");
        }
    }
}

# AGENTS.md

C# / .NET 10 backend for a Shizuku-app store. Single binary + systemd, Postgres.
Behavior spec: `docs/SPEC.md`. App developer guide: `docs/listing-and-metadata.md`.
Ops: `docs/server-setup.md`. Overrides: `docs/overrides.md`.
Storefront: `docs/storefront.md`. Governance: `docs/constitution.md`.

## Commands

```bash
dotnet build ShizuAppStoreServer.slnx --nologo -v q
dotnet test tests/ShizuAppStoreServer.Core.Tests/ShizuAppStoreServer.Core.Tests.csproj --nologo -v q
dotnet test tests/ShizuAppStoreServer.Api.Tests/ShizuAppStoreServer.Api.Tests.csproj --nologo -v q
dotnet test tests/ShizuAppStoreServer.Web.Tests/ShizuAppStoreServer.Web.Tests.csproj --nologo -v q
```

## Rules

- Done means: build with 0 warnings 0 errors, full suite green (676 Core + 148 Api + 45 Web).
- The storefront (`src/ShizuAppStoreServer.Web/`, `docs/storefront.md`) reads the
  database read-only through Core's `ShizuDbContext`. Never add migrations or writes
  there, and never route it through the public API.
- Tests are hermetic (stubbed HTTP, fake runners, SQLite). No network, no binaries
  in the default run; external tools get verbatim-output tests plus `SHIZU_REAL_*`
  env-gated live tests.
- Verify against real code and real artifacts; never guess formats or behavior.
- Concise comments (WHY, not WHAT). No em-dashes in code, comments, or docs.
- Forge-first; variant data never alters the primary outcome; `apps.url` is not identity.
- Update the affected docs (`docs/SPEC.md`) in the same pass.
- Keep `docs/listing-and-metadata.md` current: any change to how apps are discovered, enriched, scheduled or displayed updates it in the same pass.
- Deploy after every completed change, without being asked: storefront
  `./deploy/deploy-web.sh srv1.timschneeberger.me`, API
  `./deploy/deploy.sh srv1.timschneeberger.me`.
- Never stop the API except during a deployment (seconds of downtime). Routine
  refreshes, heals and backfills run through the admin API on the live server
  (`POST /v1/admin/sync`, `/v1/admin/refresh-icons`, `/v1/admin/refresh-screenshots`);
  the one-shot units are for first boot and deployment windows only and must
  never run while `shizuappstore.service` is active.
- Never commit unless asked. Never copy AGPL-licensed code or files into the repo.

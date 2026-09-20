# AGENTS.md

C# / .NET 10 backend for a Shizuku-app store. Single binary + systemd, Postgres.
Behavior spec: `docs/SPEC.md`. App developer guide: `docs/listing-and-metadata.md`.
Ops: `docs/server-setup.md`. State: `HANDOFF.md`.
Governance: `docs/constitution.md`.

## Commands

```bash
dotnet build ShizuAppStoreServer.slnx --nologo -v q
dotnet test tests/ShizuAppStoreServer.Core.Tests/ShizuAppStoreServer.Core.Tests.csproj --nologo -v q
dotnet test tests/ShizuAppStoreServer.Web.Tests/ShizuAppStoreServer.Web.Tests.csproj --nologo -v q
```

## Rules

- Done means: build with 0 warnings 0 errors, full suite green (469 Core + 67 Web).
- Tests are hermetic (stubbed HTTP, fake runners, SQLite). No network, no binaries
  in the default run; external tools get verbatim-output tests plus `SHIZU_REAL_*`
  env-gated live tests.
- Verify against real code and real artifacts; never guess formats or behavior.
- Concise comments (WHY, not WHAT). No em-dashes in code, comments, or docs.
- Forge-first; variant data never alters the primary outcome; `apps.url` is not identity.
- Update the affected docs (`docs/SPEC.md`, `HANDOFF.md`, `PLAN.md`) in the same pass.
- Keep `docs/listing-and-metadata.md` current: any change to how apps are discovered, enriched, scheduled or displayed updates it in the same pass.
- Never commit unless asked. Never copy AGPL-licensed code or files into the repo.

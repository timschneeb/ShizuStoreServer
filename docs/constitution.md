<!-- Sync Impact Report (scratch; remove before commit):
  - Version change: 1.1.0 → 1.2.0 (principle text narrowed; MINOR per semver policy)
  - Modified principles: V. Simplicity and Explicit Boundaries (removed the
    "Prefer flat, explicit schema and code over abstraction" clause so shared
    source abstractions are permitted; degradation clause retained verbatim)
  - Added sections: none
  - Removed sections: none
  - Rationale: enrichment repeated the same release/asset/ABI pipeline per
    source. A shared source abstraction removes that duplication without
    changing the boundaries the rest of the principle protects
    (constructor-injected interfaces, no ASP.NET in `Core`, visible
    degradation).
  - Follow-up TODOs: none. Note: pre-existing em-dashes in code/docs outside this file
    are out of scope for this command and remain for a separate cleanup pass.
-->
# ShizuAppStoreServer Constitution

## Core Principles

### I. Evidence Before Synthesis (NON-NEGOTIABLE)
No upstream format, tool output, or API behavior is ever assumed:
everything is captured from the real artifact first. Parsers are proven
against live data (real `index.xml`, real `dump badging` /
`apksigner` output from local binaries); verbatim-output tests lock
formats so regressions surface as test failures, not production
surprises. Code is executed to confirm outputs, tests are run to
confirm fixes, and any finding that contradicts a prior claim is
stated explicitly with the evidence winning.

### II. Forge-First and Signature Honesty
A GitHub/GitLab link anywhere in an entry wins the primary APK;
F-Droid is never primary while a forge source exists. Both signers
are recorded because F-Droid builds are delayed and differently
signed: primary fingerprints plus the F-Droid alternate variant.
The variant sidecar may only add data; it must never change the
primary outcome. File bytes win over index values on disagreement;
a package-name mismatch between index row and file is the one hard
failure. Clients match installed certs against all recorded
fingerprints and are offered the matching download.

### III. Test Discipline (NON-NEGOTIABLE)
All tests are hermetic: stubbed HTTP handlers, fake runners, real
ZIP/PNG/byte handling, SQLite in-memory, with no network and no
external binaries in the default run. External-tool behavior is covered by
verbatim-output unit tests plus env-gated live tests (same
`SHIZU_REAL_*` pattern throughout). A change is done only when the
full suite is green with zero warnings; a failing test is
investigated as a possible code bug first and a test-expectation
slip second, with the distinction stated. The `Testing` host
environment keeps endpoint suites hermetic; production boot without
the toolchain must fail.

### IV. Data Integrity and Closure
`slug` is globally unique and stable across renames; entry identity
is `(listing, url, category)`; `apps.url` is never a key. Every
delete path writes a tombstone and every re-add path clears it, or
`/v1/changes removed[]` drifts. The sync worker makes no app
mutations after the upsert save (final save = requests + run row
only). A `Skipped` pass writes nothing. Emptiness is meaningful
(an emptied `ARCHIVED.md` still un-marks). Backfill windows are
client-side and documented where the provider forces them.

### V. Simplicity and Explicit Boundaries
`Core` holds all domain logic with no ASP.NET references; the host
boundary is crossed only through constructor-injected interfaces
(runners, release clients, providers). Degradation is best-effort and
visible (null fingerprints, index-only records, `last_error` +
backoff), never silent corruption. Hard requirements fail fast at startup
with a message naming the missing piece. No Play scraping,
whatsoever. No AGPL-licensed code or files enter the repo.

### VI. Comment and Language Economy
Comments state decisions, traps, and non-obvious constraints (the
WHY), never restate what the code plainly does (the WHAT). One line
is the default; longer explanations must earn their place. No
narrated thinking, no change diaries. Documentation follows the same
economy: state each rule once and link instead of repeating. No
em-dashes in code, comments, or documentation; use commas, colons,
or parentheses.

## Technology & Stack Constraints

- .NET 10, C# latest, single self-contained-capable binary +
  systemd; Postgres 17 via EF Core + Npgsql (SQLite exists only
  for tests: no `ORDER BY`/`Max`/`Where` over `DateTimeOffset`
  in shared LINQ; sort and filter those in memory).
- String-mapped enums, snake_case tables/columns, `bigint`
  identity keys; migrations ship as code, the `efbundle` is
  rebuilt per deploy and never committed.
- External tools: `aapt2` + `apksigner` (build-tools, JRE for the
  latter), verified at startup; ImageSharp for icons (license-safe
  version line only).
- Public API is unauthenticated with fixed-window rate limits and
  output caching; the only secret is the admin-webhook HMAC key,
  which fail-closes when absent.

## Development Workflow

- Work flows spec → implement → verify → document: `PLAN.md`
  tracks milestones, `HANDOFF.md` carries decisions and state,
  `docs/SPEC.md` specifies behavior, `docs/server-setup.md`
  covers operations. Code changes update the affected docs in the
  same pass.
- Quality gates for every change: solution builds with 0 warnings
  0 errors; full suite green; migration SQL script-reviewed;
  live-binary verification wherever an external format is
  touched; probe programs and scratch files live in `/tmp` and
  never enter the repo.
- Failing builds or suites are fixed at the root cause: pins,
  provider quirks, and test-host traps get code comments and a
  decision note, not workarounds without explanation.

## Governance
<!-- Constitution supersedes all conflicting practices; amendments require reasoning, version bump, and migration notes -->

Amendments require a stated rationale, a version bump per the
policy below, and a Sync Impact Report reviewed before the
amended file is committed. All changes are reviewed for
compliance with the principles above; justified exceptions are
recorded in HANDOFF decisions, not silently adopted. Runtime
guidance lives in `docs/SPEC.md`; this document governs, it does
not re-specify.

**Version**: 1.2.0 | **Ratified**: 2026-09-12 | **Last Amended**: 2026-09-15

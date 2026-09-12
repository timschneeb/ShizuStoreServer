# Feature Specification: Server Baseline Spec

**Feature Branch**: `001-server-baseline-spec`

**Created**: 2026-09-12

**Status**: Draft

**Input**: User description: "Read @docs/SPEC.md"

## User Scenarios & Testing *(mandatory)*

This spec captures the intended behavior of the already-built
ShizuAppStoreServer as a baseline for future changes. Each story is
a capability slice, independently testable against a running server.

### User Story 1 - Browse and search the catalog (Priority: P1)

A store client lists, searches, filters, and pages the app catalog,
and opens full detail for one entry (version, download links,
category path, flags).

**Why this priority**: Catalog browsing is the core value; every
other capability serves it.

**Independent Test**: Query the list endpoint with each filter,
sort, and page parameter; open detail for entries of every
availability kind; confirm excluded entries never appear (detail
reads them as not-found).

**Acceptance Scenarios**:

1. **Given** a populated catalog, **When** a client requests page 1
   with default parameters, **Then** it receives at most 50 items,
   a total count, and the newest-updated entries first.
2. **Given** a category with subcategories, **When** a client
   filters by the parent category, **Then** entries from all
   subcategories are included.
3. **Given** an entry available only on F-Droid, **When** a client
   opens its detail, **Then** the response carries a direct download
   link plus version and icon references.
4. **Given** an unknown category, page 0, or an unknown sort key,
   **When** a client requests the list, **Then** it receives a 400
   response explaining the problem.

---

### User Story 2 - Delta-sync against the catalog (Priority: P1)

A store client keeps a local copy current by asking what changed
since a timestamp: added entries, updated entries, and removed
entries (including renames, which keep their identity, and
deletions, which appear once as removals).

**Why this priority**: Delta sync is what makes the catalog usable
on devices; without reliable removals, clients accumulate ghosts.

**Independent Test**: Seed changes (add, rename, delete an entry),
then request the changes feed with several `since` timestamps and
confirm each change appears in exactly the right bucket, oldest
first.

**Acceptance Scenarios**:

1. **Given** an entry renamed in the list, **When** a client syncs,
   **Then** it sees an update under the same stable identifier, not
   an add plus a removal.
2. **Given** an entry deleted from the list, **When** a client syncs
   past the deletion, **Then** it sees the entry once in removals
   with its last known name.
3. **Given** a malformed timestamp, **When** a client requests
   changes, **Then** it receives a 400 response.

---

### User Story 3 - Install the build matching the device (Priority: P2)

A store client detects which build is installed on the device
(developer-signed or F-Droid-signed) by comparing the installed
signing certificate against the fingerprints the server publishes,
and offers the matching download link.

**Why this priority**: Installing a differently-signed build over an
existing install fails on Android; correct matching prevents broken
updates.

**Independent Test**: For entries shipped in both builds, confirm
the API exposes fingerprints for each build and distinct download
links; simulate an installed F-Droid certificate and confirm the
variant link is the correct offer.

**Acceptance Scenarios**:

1. **Given** an entry published on both GitHub and F-Droid,
   **When** a client reads its detail, **Then** it finds the
   primary download plus a separate F-Droid variant block with its
   own fingerprints.
2. **Given** a device with the F-Droid build installed, **When**
   the client compares certificates, **Then** the variant
   fingerprint matches and the primary does not (unless both builds
   share the signer's key).
3. **Given** an entry published only on F-Droid, **When** a client
   reads its detail, **Then** the primary record itself carries the
   F-Droid download and its fingerprint.

---

### User Story 4 - Keep the catalog fresh automatically (Priority: P2)

An operator runs the server and the catalog tracks the upstream
list with no manual steps: periodic checks pick up list changes,
a webhook triggers immediate checks, and a nightly pass re-checks
every entry for new upstream releases.

**Why this priority**: A stale catalog silently serves outdated
downloads; freshness is the operator's core promise.

**Independent Test**: Change the list, wait for or trigger a pass,
and confirm the catalog, version history, and run log reflect the
change; break the upstream remote and confirm serving continues
off the last-known state.

**Acceptance Scenarios**:

1. **Given** a changed upstream list, **When** the next check runs,
   **Then** new entries appear with first-seen dates preserved
   from list history and a run record is written.
2. **Given** an unchanged list and nothing due for re-check,
   **When** the loop ticks, **Then** no run record is written.
3. **Given** a signed webhook call, **When** it arrives, **Then** a
   check is queued (202 accepted); unsigned or wrongly-signed
   calls are rejected, and calls without a configured secret are
   refused outright.
4. **Given** a failed check, **When** it ends, **Then** the error
   is recorded in the run log and the queued trigger is kept for
   the next pass.

---

### User Story 5 - Operate safely with guardrails (Priority: P3)

An operator gets a server that refuses to boot without its
analysis toolchain, stays within upstream rate limits, shields
itself from traffic spikes, and serves immutable icons with
long-lived caching.

**Why this priority**: Guardrails prevent silent degradation
(a catalog that never enriches, a banned API token, an
overloaded host).

**Independent Test**: Boot without the toolchain (must refuse);
inspect response headers and caching behavior; exceed the request
rate and confirm throttling; confirm icons carry immutable cache
headers.

**Acceptance Scenarios**:

1. **Given** a missing analysis binary, **When** the server boots,
   **Then** it logs the missing tool and exits instead of serving.
2. **Given** repeated conditional upstream checks with no new
   release, **When** enrichment runs, **Then** no download happens
   and only the last-checked timestamp moves.
3. **Given** traffic above 100 requests/minute from one address,
   **When** the excess arrives, **Then** it is rejected with 429.

### Edge Cases

- Play-Store-only entries with no installable source are excluded
  from every endpoint unless an operator override keeps them as
  store links.
- Unsigned or unanalyzable APKs still enrich (download link,
  version, icon) with empty fingerprints rather than failing.
- An F-Droid package disappearing from the index clears the
  recorded variant instead of serving a dead link.
- An emptied archive file un-marks previously archived entries
  instead of being ignored.
- The same upstream URL listed in several categories yields one
  entry per category, each with a stable identity.
- Concurrent sync triggers never run two passes at once; the
  loser skips.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST keep a stable entry identifier across
  list renames (same entry before and after, never add-plus-remove).
- **FR-002**: System MUST treat (list, URL, category) as entry
  identity, allowing the same URL in several categories.
- **FR-003**: System MUST derive first-seen and last-updated dates
  per entry from list history and merge them across both lists
  (earliest first-seen, latest last-updated).
- **FR-004**: System MUST surface deleted entries once in the
  changes feed, then stop serving them; re-added entries MUST
  clear their removal record.
- **FR-005**: System MUST exclude archived entries from all
  endpoints with a reason, and restore entries removed from the
  archive.
- **FR-006**: System MUST prefer developer-forge releases for the
  primary download whenever a forge link exists in the entry, and
  MUST never serve an F-Droid build as primary in that case.
- **FR-007**: System MUST re-check healthy entries at least every
  24 hours and retry failed entries at least every 12 hours.
- **FR-008**: System MUST use conditional upstream requests and
  MUST skip downloads when the upstream reports nothing new.
- **FR-009**: System MUST record one version-history row per
  previously unseen (version code, version name) pair per entry.
- **FR-010**: System MUST publish signing-certificate fingerprints
  (SHA-256 and MD5) for analyzed builds and the index-provided
  fingerprint for index-only builds.
- **FR-011**: System MUST publish the F-Droid alternate build
  (link, version, size, file hash, fingerprints) for
  forge-primary entries also shipped on F-Droid, and MUST clear
  it when F-Droid drops the package.
- **FR-012**: System MUST serve icons under stable links with
  immutable caching, and MUST fall back to a generated avatar
  when no icon exists.
- **FR-013**: System MUST check the upstream list at least every
  15 minutes, skip the pass when nothing changed and nothing is
  due, and fully re-check all entries nightly.
- **FR-014**: System MUST accept authenticated webhook triggers
  for immediate checks, reject bad signatures, and refuse webhook
  operation when no secret is configured.
- **FR-015**: System MUST record every non-skipped pass (including
  failures) in the run log and MUST retain failed triggers for
  the next pass.
- **FR-016**: System MUST throttle API clients at 100
  requests/minute per address with 429 responses, exempting the
  health check.
- **FR-017**: System MUST refuse to boot without its APK analysis
  toolchain and MUST log the detected tool versions on boot.
- **FR-018**: System MUST answer repeated identical catalog reads
  from cache (list 60s, detail 60s, categories 5min, changes 30s,
  meta 60s) and MUST honor conditional detail/category requests
  with 304 responses.
- **FR-019**: System MUST validate list query parameters (unknown
  category, bad page, bad sort/order, bad filter values,
  malformed since-timestamp) with explanatory 400 responses.

### Key Entities

- **Catalog Entry**: one listed app with stable identity, flags,
  links, version, download, icon, fingerprints, and optional
  F-Droid variant.
- **Category**: named grouping with optional parent; entries are
  counted through the subtree.
- **Version Record**: one observed (version code, version name,
  download link) triple per entry with detection time.
- **Sync Run**: one check pass with trigger, upstream commit,
  per-outcome counts, and optional error; skipped passes leave
  no record.
- **Change Feed Item**: an added, updated, or removed entry
  snapshot for a given timestamp window.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Every entry of a populated upstream list is
  retrievable through list and detail reads with version,
  download link, and icon.
- **SC-002**: A renamed entry keeps its identifier across syncs
  (zero add-plus-remove pairs for renames in the changes feed).
- **SC-003**: A deleted entry appears exactly once in removals
  and is absent from all other endpoints afterwards.
- **SC-004**: For dual-published entries, both fingerprint sets
  resolve a simulated installed certificate to the correct
  download link in 100% of sampled cases.
- **SC-005**: A merged upstream change is queryable no later
  than one check interval plus enrichment after it is fetched.
- **SC-006**: Sustained traffic at the documented rate limit is
  served without errors while excess traffic is throttled.

## Assumptions

- The upstream awesome-list repository stays reachable over git
  and keeps its current file layout (main list, closed-source
  list, archive file).
- Forge release APIs and the F-Droid/Izzy index endpoints remain
  publicly reachable with current response shapes.
- The APK analysis toolchain is installed where the server runs.
- One operating party manages secrets, overrides, and deploys;
  the public API needs no end-user authentication.
- Upstream entries link at least one usable source (forge
  release, repo index, or store page) for enrichment beyond a
  plain link.

# Listing and metadata

How ShizuStore finds apps, collects metadata and shows them. Written for app
developers who want to know what the server does with their releases and what
they can do to get their app displayed correctly.

ShizuStore uses my [awesome-shizuku list](https://github.com/timschneeb/awesome-shizuku) as an index. 
Submit your app there to publish it in ShizuStore.

Closed-source apps listed from `pages/CLOSED_SOURCE.md` are opt-in.
The client has a setting to show these entries which is off by default.

## When a change becomes visible

| Change | Usually visible in the catalog |
|---|---|
| New release on GitHub/GitLab/F-Droid/IzzyOnDroid | within 30 minutes (+ analysis time) |
| New release on Play/Codeberg/other sites | within 24 hours |
| List entry added/edited | within 30 minutes (+ analysis time) |
| Metadata refresh (GitHub stars, etc.) | within 24 hours |
| New screenshots added or removed | F-Droid/Izzy shots refresh within 24 hours; the app repo is re-scanned every week and dead URLs are cleared on the next scan |

## How the server finds releases

Source selection: an entry can end up with
candidates from more than one source, but a forge link always wins the primary
slot. F-Droid is never primary while a usable forge source exists.

Order of resolution:

1. GitHub
2. GitLab
3. F-Droid and IzzyOnDroid, matched by explicit package URL or by the index
   application whose source URL matches the entry's forge repo.
4. Fallback: Link to the Play Store page or the website. APK downloads are not supported in these cases; Play-only entries are still listed with an open-in-Play action instead of being hidden.

The app store can handle app variants with different signatures automatically (e.g. F-Droid vs. GitHub releases) and will choose the correct source to install updates from, avoiding signature conflicts.

### Which release is used

- GitHub: the newest non-draft release of the first 100. Pre-releases count,
  which matters for projects that only ship pre-releases.
- GitLab: newest non-upcoming release.
- F-Droid and Izzy: the newest package in the index, matched by package name or
  source URL.

### Which file is used

- A `.apk` asset whose name contains `release` is preferred, otherwise the largest `.apk`.
- If the release has no `.apk` but a `.zip`, it will try to extract it and the best APK
  inside is analyzed.
- Every other `.apk` asset of the release is analyzed as a candidate, so a
  release with one APK per architecture exposes every ABI.
- If a release contains multiple `.apk` assets with different package names and different app names, the server will create separate app entries for them (see below).
- APKs that do not declare the Shizuku permission in their manifest are excluded.
- GitLab release descriptions can link APKs directly, instead of an attached release asset. Those links are collected as candidates too.

#### Multi-app repos and flavor builds

Some repositories ship several apps, or several builds of one app. The server
groups analyzed APKs by their application label:

- One label belongs to the list entry itself. That group picks a canonical
  package: the package named by the list URL (F-Droid, Izzy or Play), else the
  shortest package that all others share, else the package matching the label
  or repo name, else the primary asset's package.
- Other labels become variant rows with their own slug, downloads and icon.
  Their display name is `Label (root list name)` so the extra apps stay
  traceable to the list entry. The suffix is dropped when the label already
  equals the list name.
- Builds that share the root's label but use a different package (FOSS, Play,
  debug or spoofed flavors) stay on the root entry as extra candidates. Each
  candidate carries its own `packageName`, so one entry can offer every flavor.

TV and Wear builds are dropped when the same package also ships a phone build.
A package that only ships a TV or Wear build is kept, because then that form
factor is the app.

## Metadata collection

### What the server reads from an APK

- package name, version code, version name
- minimum SDK, target SDK, compile SDK and the declared locale list
- application label and every `application-label-<locale>` string
- requested permissions
- native ABI code (the primary ABI and the full native-code list)
- required features, used to detect TV and Wear builds
- signing certificate, its DN, key algorithm and size, and the verified
  signature schemes (v1..v4)
- the declared Dhizuku permission, if any
- Exodus tracker code signatures matched in the DEX, with each tracker's
  category tags

The application label becomes the display name for the store entry. 
The awesome-shizuku list name is only used when no APK label is known.
When an APK ships localized application labels, clients swap the
displayed name per device locale in lists and details; the default label
stays the fallback. List payloads carry only the labels that differ from
the display name, so unchanged strings do not inflate the catalog.

Signature details are display-only: selection still matches fingerprint
sets, and the DN, key algorithm and schemes let clients show what signed
the build (including rotations, where every distinct DN is kept).

Tracker detection matches only the Exodus code signatures (class-name
prefixes in the DEX string pool); the server performs no network analysis, so
an app without matches is not "tracker-free", only "not detected by code
signature". Each detected tracker keeps its Exodus category tags (Analytics,
Advertisement, ...), so clients can show which kind of tracking a tracker
does. The Dhizuku flag means the build declares a
`com.rosan.dhizuku.permission.*` permission; the Shizuku permission is not
tracked because nearly every app declares it.

### How the AI usage report is written

The "How this app uses Shizuku" text is generated from the app's **public
source repository**, not from the APK. The server shallow-clones the repo at
the release tag the served artifact came from: the tag recorded from the forge
release metadata, else the tag in the artifact URL, and only then a tag
matching the badged version (falling back to the default branch). That order
matters because a project can publish a new release without bumping its
`versionName`; the badged version alone would check out an older tree. The
server pre-scans the checkout into a privilege surface map (Shizuku imports,
entry points, user services, AIDL members and command helpers with file and
line, plus bridge call sites and dependency declarations for Flutter/Dart,
TypeScript/JavaScript and C#/.NET projects), and lets a model trace every
entry to its call sites through read-only tools (`repo_map`, `read_symbol`,
`find_callers`, `trace_symbol`, `read_file`, `search_code`). Symbol tracing
covers Kotlin, Java and AIDL; a bridge or package entry is followed by
searching the channel, plugin or method name across the repository and
reading the Dart, TypeScript or C# callers. When the implementation lives in
an external package (pub, npm or NuGet), the report describes what the app's
own code calls and names the dependency as the mechanism, without guessing
the package internals. Help screens, documentation and assets that only
mention Shizuku are not usage. When a report already exists for an older
release, it is carried into the next run as reference material: the model
re-verifies every claim against the current source, keeps what still holds
and drops what the new release no longer supports. The model may only say
what the source shows
(capability, never actual runtime behavior): it reads the callers before
stating a capability, and only claims a generic "run arbitrary commands"
capability when a traced call site forwards user input. A run does not
finalize while any surface entry is still uninspected: the model gets that
concrete list back for another pass, a bounded number of times, after which
the report is accepted. Tracing is advisory by default, but a run that used
no symbol tool at all is sent back once with its entry, user-service and
AIDL rows listed. The analysis is
Shizuku-only: root, su and Dhizuku backends are neither traced nor mentioned.
The report is a
one-line summary plus three markdown sections written for end users: the
capability text, the "Android APIs or commands used" list and an optional
"Notable details" note. The model returns them separately and the server adds
the section headings when it composes the text the app displays, so a
section can be rendered on its own later. The API list names the traced
Android platform APIs: framework classes and methods, hidden or internal
APIs, binder interfaces and shell commands, one backticked item per bullet
and without prose. It is Android platform APIs and commands only, never the
app's own classes, AIDL interfaces, user-service methods or Shizuku SDK
helpers, and it is omitted when no Shizuku usage was found. The capability
text is capability-first: each bullet opens with a bold short label and then
says what the user can do, not how the app implements it (no onboarding,
setup, permission-grant, lifecycle or health-check walkthroughs). Text that
is not a bullet list, whose bullets do not start with a bold label, or whose
API list carries prose lines or missing backticks is rejected so the model
rewrites it.

Consequences for app developers:

- A public GitHub or GitLab repository linked from the list entry (or the
  `source_url`) is required. Apps without one show no usage section; there is
  no marker-based fallback text anymore.
- Only direct-APK entries are analyzed, root and variant rows alike: each
  app row gets its own report, refreshed when its own artifact changes.
  Play-redirect and link-only entries show no usage report, and a report is
  kept if an entry stops being direct-APK after it was written.
- Keep release tags matching the version name (for example `v1.2.3`) so the
  analysis can be pinned to the released code instead of the branch head.
- The report is generated once per app and then only for new releases, and it
  is cached by repository commit, prompt generation and model. Plain English
  is required; raw HTML, images and external links are stripped, and only
  GitHub/GitLab links survive.
- If the source contains no Shizuku usage, the report
  says so plainly instead of guessing.

### Icons

The server resolves a launcher icon in this order:

1. The APK's manifest `android:icon`. 
  - Preferably: Vector or adaptive icons (rendered on the server using Google's `LayoutLib`)
  - Otherwise, legacy PNG/JPG/WEBP icons are used.
2. F-Droid
3. A generated letter avatar as the last resort.

Apps that are only available on the Play Store will pull the app icon from there instead.

### Screenshots

Resolution order:

1. Screenshots from F-Droid and Izzy. Shots are matched by every package name the
   app publishes, primary plus variants.
2. Otherwise the app's own repository is cloned without
   blobs. The server keeps image files whose name starts with `screenshot` or
   that live under a directory whose name contains `screenshot` (for example
   fastlane `phoneScreenshots` or `docs/screenshots`). Formats are png, jpg,
   jpeg, webp, gif and bmp. The resulting URLs are pinned to the fetched
   commit.


* Up to 12 screenshots are collected.

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
| New screenshots added | screenshots on F-Droid/Izzy within 24 hours, Git repos are scanned for screeenshots every week |

## How the server finds releases

Source selection: an entry can end up with
candidates from more than one source, but a forge link always wins the primary
slot. F-Droid is never primary while a usable forge source exists.

Order of resolution:

1. GitHub
2. GitLab
3. F-Droid and IzzyOnDroid, matched by explicit package URL or by the index
   application whose source URL matches the entry's forge repo.
4. Fallback: Link to Play Store page or website. APK downloads are not supported in these cases.

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
- minimum SDK
- application label
- requested permissions
- native ABI code
- required features, used to detect TV and Wear builds
- signing certificate

The application label becomes the display name for the store entry. 
The awesome-shizuku list name is only used when no APK label is known.

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
   
* Up to 12 screenshots are collected

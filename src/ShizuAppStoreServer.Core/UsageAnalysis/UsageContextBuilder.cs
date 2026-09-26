using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShizuAppStoreServer.Core.Data;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>One privilege-surface hit: an import, declared artifact, entry point, service, AIDL method, command helper or fallback.</summary>
public sealed record UsageSurfaceEntry(string Kind, string Symbol, string Value, string Path, int Line, string Text);

/// <summary>The report written for an earlier release of the same app, carried into the next run as reference context only.</summary>
public sealed record UsagePreviousAnalysis(string Short, string Markdown, string? Commit, string? ReleaseRef, DateTimeOffset? AnalyzedAt);

/// <summary>
/// Deterministic pre-scan of a checked-out repo plus the metadata the model
/// needs to interpret it. This is a map of where privileged access enters the
/// app, not a classification: the agent traces every entry to its call sites
/// and only then decides what the app can do.
/// </summary>
public sealed record UsageContext(
    string Slug,
    string Name,
    string? Package,
    string? Version,
    string Description,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<UsageSurfaceEntry> Surface,
    int FilesScanned,
    string ProjectKind = "unknown",
    UsagePreviousAnalysis? Previous = null)
{
    public string ToPromptJson()
    {
        var surface = new JsonArray();
        foreach (var entry in Surface)
        {
            surface.Add(new JsonObject
            {
                ["kind"] = entry.Kind,
                ["symbol"] = entry.Symbol.Length == 0 ? null : entry.Symbol,
                ["value"] = entry.Value,
                ["path"] = entry.Path,
                ["line"] = entry.Line,
                ["text"] = entry.Text,
            });
        }

        var root = new JsonObject
        {
            ["app"] = new JsonObject
            {
                ["slug"] = Slug,
                ["name"] = Name,
                ["package"] = Package,
                ["version"] = Version,
                ["listDescription"] = Description,
            },
            ["declaredPermissions"] = new JsonArray(Permissions.Select(p => (JsonNode?)p).ToArray()),
            ["projectKind"] = ProjectKind,
            ["privilegeSurface"] = surface,
            ["filesScanned"] = FilesScanned,
        };

        if (Previous is not null)
        {
            root["previousAnalysis"] = new JsonObject
            {
                ["short"] = Previous.Short,
                ["markdown"] = Previous.Markdown,
                ["commit"] = Previous.Commit,
                ["releaseRef"] = Previous.ReleaseRef,
                ["analyzedAt"] = Previous.AnalyzedAt?.ToString("O"),
            };
        }

        return root.ToJsonString();
    }
}

/// <summary>
/// Builds the context bundle: relevant declared permissions plus a privilege
/// surface map of the checked-out source. Imports of the Shizuku packages and
/// the call sites that enter the privileged layer are collected with file,
/// line and enclosing symbol; shell command keywords are deliberately not
/// collected because they are not evidence.
/// </summary>
public sealed class UsageContextBuilder(UsageAnalysisOptions options)
{
    private const int MaxSurfaceEntries = 200;
    private const int MaxSurfacePerFile = 20;
    private const int MaxImports = 60;
    private const int MaxLineChars = 200;

    /// <summary>Keeps one row per distinct needle; the agent traces call sites itself.</summary>
    private static readonly HashSet<string> DedupeByValueOnly =
        new(StringComparer.Ordinal) { "manifest", "dependency", "package", "service", "entry", "command", "fallback" };

    private static readonly Dictionary<string, int> KindCaps = new(StringComparer.Ordinal)
    {
        ["manifest"] = 12,
        ["dependency"] = 12,
        ["package"] = 12,
        ["service"] = 12,
        ["aidl"] = 24,
        ["entry"] = 40,
        ["command"] = 30,
        ["bridge"] = 30,
        ["fallback"] = 20,
    };

    private static readonly string[] ScanExtensions =
        [".kt", ".java", ".aidl", ".xml", ".gradle", ".kts", ".toml", ".csproj",
         ".dart", ".ts", ".tsx", ".js", ".jsx", ".cs"];

    private static readonly string[] DependencyFileNames = ["pubspec.yaml", "pubspec.yml", "package.json"];

    /// <summary>Markers of a Dart bridge into Shizuku; the line must also mention shizuku.</summary>
    private static readonly string[] DartBridgeNeedles =
        ["shizuku_api", "shizuku_apk_installer", "package:shizuku", "ShizukuApkInstaller",
         "MethodChannel", "EventChannel", "BasicMessageChannel", "HostApi"];

    /// <summary>Markers of a TypeScript or JavaScript bridge into Shizuku.</summary>
    private static readonly string[] ScriptBridgeNeedles =
        ["import ", "require(", "from '", "from \"", "registerPlugin", "NativeModules", "MethodChannel", "Capacitor"];

    /// <summary>Markers of a C# bridge into Shizuku. Case-sensitive so URLs and labels do not match.</summary>
    private static readonly string[] CSharpBridgeNeedles =
        ["ShizukuX", "Shizuku.", "new Shizuku", "using Shizuku", "typeof(Shizuku", "ShizukuProvider",
         "Rikka.Shizuku", "rikka.shizuku"];

    private static readonly HashSet<string> SkipDirectories =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "build", ".gradle", "node_modules", ".idea", "out" };

    private static readonly string[] PermissionMarkers =
        ["shizuku", "REQUEST_INSTALL_PACKAGES", "DELETE_PACKAGES",
         "INSTALL_PACKAGES", "WRITE_SECURE_SETTINGS", "PACKAGE_USAGE_STATS", "WRITE_SETTINGS"];

    private static readonly string[] PrivilegePackages =
        ["rikka.shizuku", "moe.shizuku"];

    private static readonly (string Kind, string Needle)[] SurfaceTable =
    [
        ("manifest", "ShizukuProvider"),
        ("service", "Shizuku.UserService"),
        ("service", "ShizukuUserService"),
        ("service", "ShizukuRemoteProcess"),
        ("service", "IUserService"),
        ("entry", "bindUserService"),
        ("entry", "UserServiceArgs"),
        ("entry", "newProcess"),
        ("entry", "ShizukuBinderWrapper"),
        ("entry", "ShizukuSystemProperties"),
        ("entry", "SystemServiceHelper"),
        ("entry", "Shizuku.getBinder"),
        ("entry", "Shizuku.requestPermission"),
        ("entry", "Shizuku.checkSelfPermission"),
        ("command", "Runtime.getRuntime().exec"),
        ("command", "ProcessBuilder"),
        ("fallback", "PackageInstaller"),
        ("fallback", "ACTION_INSTALL_PACKAGE"),
        ("fallback", "ACTION_MANAGE_UNKNOWN_APP_SOURCES"),
    ];

    private static readonly Dictionary<string, int> KindOrder = new(StringComparer.Ordinal)
    {
        ["import"] = 0,
        ["manifest"] = 1,
        ["dependency"] = 2,
        ["package"] = 3,
        ["service"] = 4,
        ["aidl"] = 5,
        ["entry"] = 6,
        ["command"] = 7,
        ["bridge"] = 8,
        ["fallback"] = 9,
    };

    public UsageContext Build(App app, RepoSnapshot snapshot, string? versionName)
    {
        var permissions = app.Permissions
            .Where(p => PermissionMarkers.Any(marker => p.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var entries = new List<UsageSurfaceEntry>();
        var scanned = 0;
        var flutter = false;
        var reactNative = false;
        var capacitor = false;
        var dotnet = false;
        var kmp = false;
        var jvm = false;
        foreach (var file in EnumerateFiles(snapshot.RootPath))
        {
            string text;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > options.MaxFileBytes)
                {
                    continue;
                }

                text = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }

            scanned++;
            var relative = Path.GetRelativePath(snapshot.RootPath, file).Replace('\\', '/');
            var extension = Path.GetExtension(file);
            var name = Path.GetFileName(file);
            if (name is "pubspec.yaml" or "pubspec.yml")
            {
                flutter = true;
            }
            else if (name == "package.json")
            {
                if (text.Contains("\"react-native\"", StringComparison.Ordinal))
                {
                    reactNative = true;
                }
                else if (text.Contains("capacitor", StringComparison.OrdinalIgnoreCase))
                {
                    capacitor = true;
                }
            }
            else if (name.StartsWith("capacitor.config.", StringComparison.OrdinalIgnoreCase))
            {
                capacitor = true;
            }

            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                dotnet = true;
            }

            if (extension.Equals(".kt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".java", StringComparison.OrdinalIgnoreCase))
            {
                jvm = true;
            }

            if ((extension.Equals(".gradle", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".kts", StringComparison.OrdinalIgnoreCase))
                && text.Contains("multiplatform", StringComparison.OrdinalIgnoreCase))
            {
                kmp = true;
            }

            CollectFile(relative, extension, text, entries);
        }

        var unity = Directory.Exists(Path.Combine(snapshot.RootPath, "Assets"))
            && Directory.Exists(Path.Combine(snapshot.RootPath, "ProjectSettings"));
        var projectKind = flutter ? "flutter"
            : reactNative ? "react-native"
            : capacitor ? "capacitor"
            : dotnet ? "dotnet"
            : unity ? "unity"
            : kmp ? "kmp"
            : jvm ? "android"
            : "unknown";

        var ordered = entries
            .OrderBy(e => KindOrder.TryGetValue(e.Kind, out var order) ? order : 99)
            .ThenBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Line)
            .ToList();

        var surface = new List<UsageSurfaceEntry>();
        var perFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var perKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var imports = 0;
        foreach (var entry in ordered)
        {
            if (surface.Count >= MaxSurfaceEntries)
            {
                break;
            }

            var key = entry.Kind switch
            {
                "bridge" => $"{entry.Kind}\u0000{entry.Path}\u0000{entry.Line}",
                _ when DedupeByValueOnly.Contains(entry.Kind) => $"{entry.Kind}\u0000{entry.Value}",
                _ => $"{entry.Kind}\u0000{entry.Symbol}\u0000{entry.Value}",
            };
            if (!seen.Add(key))
            {
                continue;
            }

            if (entry.Kind == "import" && imports >= MaxImports)
            {
                continue;
            }

            perKind.TryGetValue(entry.Kind, out var kindCount);
            if (KindCaps.TryGetValue(entry.Kind, out var cap) && kindCount >= cap)
            {
                continue;
            }

            perFile.TryGetValue(entry.Path, out var count);
            if (count >= MaxSurfacePerFile)
            {
                continue;
            }

            if (entry.Kind == "import")
            {
                imports++;
            }

            perFile[entry.Path] = count + 1;
            perKind[entry.Kind] = kindCount + 1;
            surface.Add(entry);
        }

        var previous = string.IsNullOrWhiteSpace(app.UsageShort) && string.IsNullOrWhiteSpace(app.UsageMarkdown)
            ? null
            : new UsagePreviousAnalysis(
                app.UsageShort ?? string.Empty,
                app.UsageMarkdown ?? string.Empty,
                app.UsageCommit,
                app.UsageReleaseRef,
                app.UsageAnalyzedAt);

        return new UsageContext(
            app.Slug,
            app.DisplayName ?? app.Name,
            app.PackageName,
            versionName ?? app.VersionName,
            app.Description,
            permissions,
            surface,
            scanned,
            projectKind,
            previous);
    }

    private static void CollectFile(string relative, string extension, string text, List<UsageSurfaceEntry> entries)
    {
        var lines = text.Split('\n');
        var ext = extension.ToLowerInvariant();
        var name = Path.GetFileName(relative);
        var aidl = ext == ".aidl";
        var buildFile = ext is ".gradle" or ".kts" or ".toml" or ".pro" or ".csproj";
        var manifest = ext == ".xml";
        var dependencyFile = DependencyFileNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        var dart = ext == ".dart";
        var script = ext is ".ts" or ".tsx" or ".js" or ".jsx";
        var csharp = ext == ".cs";
        var enclosing = SourceIndex.EnclosingMap(lines);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var snippet = trimmed.Length > MaxLineChars ? trimmed[..MaxLineChars] : trimmed;

            if (aidl)
            {
                var typeName = SourceIndex.AidlTypeName(trimmed);
                if (typeName is not null)
                {
                    entries.Add(new UsageSurfaceEntry("aidl", typeName, "type", relative, i + 1, snippet));
                    continue;
                }

                if (SourceIndex.TryDeclaration(trimmed, out var method) && trimmed.EndsWith(';'))
                {
                    entries.Add(new UsageSurfaceEntry("aidl", method, "method", relative, i + 1, snippet));
                }

                continue;
            }

            if (trimmed.StartsWith("import ", StringComparison.Ordinal)
                && PrivilegePackages.Any(pkg => trimmed.Contains(pkg, StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(new UsageSurfaceEntry("import", string.Empty, trimmed["import ".Length..].TrimEnd(';').Trim(), relative, i + 1, snippet));
                continue;
            }

            if (dependencyFile || buildFile || manifest)
            {
                if (dependencyFile || ext == ".csproj")
                {
                    if (trimmed.Contains("shizuku", StringComparison.OrdinalIgnoreCase))
                    {
                        var token = ExtractPackageToken(trimmed);
                        if (token.Length > 0)
                        {
                            entries.Add(new UsageSurfaceEntry("package", string.Empty, token, relative, i + 1, snippet));
                        }
                    }

                    continue;
                }

                var package = PrivilegePackages.FirstOrDefault(pkg => trimmed.Contains(pkg, StringComparison.OrdinalIgnoreCase));
                if (package is not null)
                {
                    entries.Add(new UsageSurfaceEntry(
                        buildFile ? "dependency" : "manifest", string.Empty, package, relative, i + 1, snippet));
                }

                continue;
            }

            if (!IsComment(trimmed))
            {
                var bridge = dart
                    ? MatchBridge(trimmed, DartBridgeNeedles, requireShizuku: true)
                    : script
                        ? MatchBridge(trimmed, ScriptBridgeNeedles, requireShizuku: true)
                        : csharp
                            ? MatchBridge(trimmed, CSharpBridgeNeedles, requireShizuku: false)
                            : null;
                if (bridge is not null)
                {
                    entries.Add(new UsageSurfaceEntry("bridge", enclosing[i] ?? string.Empty, bridge, relative, i + 1, snippet));
                    continue;
                }
            }

            foreach (var (kind, needle) in SurfaceTable)
            {
                if (!trimmed.Contains(needle, StringComparison.Ordinal))
                {
                    continue;
                }

                entries.Add(new UsageSurfaceEntry(kind, enclosing[i] ?? string.Empty, needle, relative, i + 1, snippet));
                break;
            }
        }
    }

    private static bool IsComment(string trimmed) =>
        trimmed.StartsWith("//", StringComparison.Ordinal)
        || trimmed.StartsWith("/*", StringComparison.Ordinal)
        || trimmed.StartsWith("*", StringComparison.Ordinal);

    private static string? MatchBridge(string trimmed, string[] needles, bool requireShizuku)
    {
        if (requireShizuku && !trimmed.Contains("shizuku", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var needle in needles)
        {
            if (trimmed.Contains(needle, StringComparison.Ordinal))
            {
                return needle;
            }
        }

        return null;
    }

    /// <summary>Package name from a pubspec, package.json or csproj line; quotes win, then the key separator.</summary>
    private static string ExtractPackageToken(string line)
    {
        var firstQuote = line.IndexOf('"');
        if (firstQuote >= 0)
        {
            var secondQuote = line.IndexOf('"', firstQuote + 1);
            if (secondQuote > firstQuote + 1)
            {
                return line[(firstQuote + 1)..secondQuote];
            }
        }

        var colon = line.IndexOf(':');
        var equals = line.IndexOf('=');
        var cut = colon >= 0 && (equals < 0 || colon < equals) ? colon : equals;
        var token = cut >= 0 ? line[..cut] : line;
        return token.Trim().TrimStart('-').Trim();
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(entries, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (!SkipDirectories.Contains(name))
                    {
                        stack.Push(entry);
                    }

                    continue;
                }

                var extension = Path.GetExtension(entry);
                if (name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (DependencyFileNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                    || ScanExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return entry;
                }
            }
        }
    }
}

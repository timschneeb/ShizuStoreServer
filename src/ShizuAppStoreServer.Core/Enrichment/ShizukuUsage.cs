namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// One piece of Shizuku usage evidence. <c>Kind</c> is permission, component,
/// marker, command, manager or fallback; <c>Source</c> is <c>apk</c> or
/// <c>source</c>; <c>Confidence</c> is <c>strong</c> or <c>weak</c>. Every
/// derived claim (managers, API form, capabilities) must be traceable to at
/// least one of these.
/// </summary>
public sealed record UsageEvidence(string Kind, string Value, string Source, string Confidence)
{
    public static UsageEvidence Strong(string kind, string value, string source) =>
        new(kind, value, source, "strong");

    public static UsageEvidence Weak(string kind, string value, string source) =>
        new(kind, value, source, "weak");

    public bool IsStrong => Confidence == "strong";
}

/// <summary>
/// Shizuku usage profile of one build: which managers it can drive, which API
/// form it uses, which capabilities its shell command strings suggest and
/// whether a fallback path hints that Shizuku is optional. The evidence list
/// is the audit trail; empty usage means no marker was found, never "no
/// Shizuku support" (obfuscated code can hide markers).
/// </summary>
public sealed record ShizukuUsage(
    IReadOnlyList<string> Managers,
    string? ApiForm,
    IReadOnlyList<string> Capabilities,
    bool Optional,
    IReadOnlyList<UsageEvidence> Evidence)
{
    public static ShizukuUsage Empty { get; } = new([], null, [], false, []);

    public bool IsEmpty => Managers.Count == 0 && Capabilities.Count == 0 && Evidence.Count == 0;
}

/// <summary>
/// Marker tables and pure matchers for Shizuku usage. Two independent evidence
/// streams feed the classifier: declared permissions plus manifest components
/// (cheap, survive obfuscation) and DEX/source text (finds API forms, managers
/// and shell commands). Copy built on this says the app "can" do something,
/// because a command string proves capability, not execution.
/// </summary>
public static class ShizukuUsageScanner
{
    /// <summary>Canonical manager order; keeps stored and rendered lists stable.</summary>
    public static readonly IReadOnlyList<string> ManagerIds = ["shizuku", "dhizuku", "sui", "root"];

    /// <summary>Canonical capability order.</summary>
    public static readonly IReadOnlyList<string> CapabilityIds =
        ["install", "uninstall", "freeze", "appops", "system_settings", "process", "diagnostics", "reboot", "wireless_adb", "compile"];

    private static readonly (string Needle, string Manager, bool Strong)[] ManagerMarkers =
    [
        ("moe.shizuku.manager", "shizuku", true),
        ("rikka.shizuku", "shizuku", true),
        ("Lrikka/shizuku/", "shizuku", true),
        ("com.rosan.dhizuku", "dhizuku", true),
        ("rikka.sui", "sui", true),
        ("Lrikka/sui/", "sui", true),
        ("com.topjohnwu.superuser", "root", true),
        ("Lcom/topjohnwu/superuser/", "root", true),
        ("eu.chainfire.libsuperuser", "root", true),
        ("libsu", "root", false),
    ];

    private static readonly (string Needle, string Manager)[] ComponentMarkers =
    [
        ("rikka.shizuku.ShizukuProvider", "shizuku"),
        ("moe.shizuku.client.V3_SUPPORT", "shizuku"),
    ];

    private static readonly (string Needle, string Form)[] ApiFormMarkers =
    [
        ("bindUserService", "user_service"),
        ("UserServiceArgs", "user_service"),
        ("Shizuku.UserService", "user_service"),
        ("newProcess", "new_process"),
        ("requestPermission", "permission"),
        ("checkSelfPermission", "permission"),
        ("shouldShowRequestPermissionRationale", "permission"),
    ];

    private static readonly (string Needle, string Capability)[] CommandMarkers =
    [
        ("install-create", "install"),
        ("install-write", "install"),
        ("install-commit", "install"),
        ("install-existing", "install"),
        ("pm install", "install"),
        ("pm uninstall", "uninstall"),
        ("uninstall-system-updates", "uninstall"),
        ("pm disable", "freeze"),
        ("pm hide", "freeze"),
        ("pm enable", "freeze"),
        ("pm unhide", "freeze"),
        ("disable-user", "freeze"),
        ("appops set", "appops"),
        ("appops get", "appops"),
        ("cmd appops", "appops"),
        ("settings put", "system_settings"),
        ("settings get", "system_settings"),
        ("svc data", "system_settings"),
        ("svc power reboot", "reboot"),
        ("svc power", "system_settings"),
        ("dumpsys battery set", "system_settings"),
        ("input keyevent", "system_settings"),
        ("am force-stop", "process"),
        ("am start", "process"),
        ("dumpsys activity", "diagnostics"),
        ("dumpsys display", "diagnostics"),
        ("dumpsys gfxinfo", "diagnostics"),
        ("dumpsys SurfaceFlinger", "diagnostics"),
        ("adb pair", "wireless_adb"),
        ("cmd package compile", "compile"),
    ];

    private static readonly string[] FallbackMarkers =
    [
        "android.content.pm.PackageInstaller",
        "ACTION_INSTALL_PACKAGE",
        "ACTION_MANAGE_UNKNOWN_APP_SOURCES",
    ];

    private static readonly Dictionary<string, int> ApiFormPriority = new()
    {
        ["user_service"] = 3,
        ["new_process"] = 2,
        ["permission"] = 1,
    };

    /// <summary>
    /// Scans the APK's declared permissions and manifest components plus its
    /// DEX string pools. Best-effort: an unreadable APK scans as empty.
    /// </summary>
    public static ShizukuUsage ScanApk(string apkPath, IReadOnlyList<string> declaredPermissions)
    {
        var permissions = ScanPermissions(declaredPermissions, "apk");
        var dex = ScanDexText(TrackerScanner.ReadDexText(apkPath));
        return Merge(permissions, dex);
    }

    /// <summary>Declared permissions and manifest components only, no DEX read.</summary>
    public static ShizukuUsage ScanPermissions(IReadOnlyList<string> declaredPermissions, string source)
    {
        var evidence = new List<UsageEvidence>();
        foreach (var permission in declaredPermissions)
        {
            if (permission.StartsWith("moe.shizuku.manager.permission.", StringComparison.Ordinal))
            {
                evidence.Add(UsageEvidence.Strong("permission", permission, source));
            }
            else if (permission.StartsWith("com.rosan.dhizuku.permission.", StringComparison.Ordinal))
            {
                evidence.Add(UsageEvidence.Strong("permission", permission, source));
            }
            else if (permission.StartsWith("rikka.sui.permission.", StringComparison.Ordinal))
            {
                evidence.Add(UsageEvidence.Strong("permission", permission, source));
            }
            else if (permission == "android.permission.ACCESS_SUPERUSER")
            {
                evidence.Add(UsageEvidence.Weak("permission", permission, source));
            }
        }

        return Build(evidence);
    }

    /// <summary>DEX string pools, evidence source <c>apk</c>.</summary>
    public static ShizukuUsage ScanDexText(IReadOnlyList<string> dexText) => ScanTexts(dexText, "apk");

    /// <summary>Source files, evidence source <c>source</c>; the path is not part of the evidence value.</summary>
    public static ShizukuUsage ScanFiles(IEnumerable<(string Path, string Text)> files) =>
        ScanTexts(files.Select(f => f.Text).ToList(), "source");

    private static ShizukuUsage ScanTexts(IReadOnlyList<string> texts, string source)
    {
        var evidence = new List<UsageEvidence>();
        foreach (var text in texts)
        {
            if (text.Length == 0)
            {
                continue;
            }

            foreach (var (needle, manager, strong) in ManagerMarkers)
            {
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    evidence.Add(strong
                        ? UsageEvidence.Strong("manager", needle, source)
                        : UsageEvidence.Weak("manager", needle, source));
                }
            }

            foreach (var (needle, manager) in ComponentMarkers)
            {
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    evidence.Add(UsageEvidence.Strong("component", needle, source));
                }
            }

            foreach (var (needle, form) in ApiFormMarkers)
            {
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    evidence.Add(UsageEvidence.Strong("marker", needle, source));
                }
            }

            foreach (var (needle, capability) in CommandMarkers)
            {
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    evidence.Add(UsageEvidence.Strong("command", needle, source));
                }
            }

            foreach (var needle in FallbackMarkers)
            {
                if (text.Contains(needle, StringComparison.Ordinal))
                {
                    evidence.Add(UsageEvidence.Weak("fallback", needle, source));
                }
            }
        }

        return Build(evidence);
    }

    /// <summary>Merges partial scans, keeping canonical manager/capability order and deduped evidence.</summary>
    public static ShizukuUsage Merge(params ShizukuUsage[] parts)
    {
        var managers = new HashSet<string>(StringComparer.Ordinal);
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        var evidence = new List<UsageEvidence>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? apiForm = null;
        var optional = false;

        foreach (var part in parts)
        {
            foreach (var manager in part.Managers)
            {
                managers.Add(manager);
            }

            foreach (var capability in part.Capabilities)
            {
                capabilities.Add(capability);
            }

            if (part.ApiForm is not null
                && (apiForm is null || ApiFormPriority.GetValueOrDefault(part.ApiForm) > ApiFormPriority.GetValueOrDefault(apiForm)))
            {
                apiForm = part.ApiForm;
            }

            foreach (var item in part.Evidence)
            {
                if (seen.Add($"{item.Kind}\u001f{item.Value}\u001f{item.Source}\u001f{item.Confidence}"))
                {
                    evidence.Add(item);
                }

                if (item.Kind == "fallback")
                {
                    optional = true;
                }
            }
        }

        return new ShizukuUsage(
            ManagerIds.Where(managers.Contains).ToList(),
            apiForm,
            CapabilityIds.Where(capabilities.Contains).ToList(),
            optional && managers.Count > 0,
            evidence);
    }

    /// <summary>Rebuilds a profile from stored evidence entries (kind|value|source|confidence).</summary>
    public static ShizukuUsage FromEvidence(IReadOnlyList<string> entries) => Build(Decode(entries));

    /// <summary>Encodes evidence for the <c>usage_evidence</c> column, one entry per line.</summary>
    public static List<string> Encode(IEnumerable<UsageEvidence> evidence) =>
        evidence.Select(e => $"{e.Kind}|{Sanitize(e.Value)}|{e.Source}|{e.Confidence}").ToList();

    /// <summary>Decodes stored evidence entries; malformed entries are skipped.</summary>
    public static List<UsageEvidence> Decode(IReadOnlyList<string> entries)
    {
        var evidence = new List<UsageEvidence>();
        foreach (var entry in entries)
        {
            var parts = entry.Split('|');
            if (parts.Length == 4 && parts[0].Length > 0 && parts[1].Length > 0)
            {
                evidence.Add(new UsageEvidence(parts[0], parts[1], parts[2], parts[3]));
            }
        }

        return evidence;
    }

    private static ShizukuUsage Build(IReadOnlyList<UsageEvidence> evidence)
    {
        var managers = new HashSet<string>(StringComparer.Ordinal);
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        string? apiForm = null;

        foreach (var item in evidence)
        {
            switch (item.Kind)
            {
                case "permission":
                case "component":
                case "manager":
                    if (ManagerOf(item.Value) is { } manager)
                    {
                        managers.Add(manager);
                    }

                    break;
                case "marker":
                    if (ApiFormOf(item.Value) is { } form
                        && (apiForm is null || ApiFormPriority.GetValueOrDefault(form) > ApiFormPriority.GetValueOrDefault(apiForm)))
                    {
                        apiForm = form;
                    }

                    break;
                case "command":
                    if (CapabilityOf(item.Value) is { } capability)
                    {
                        capabilities.Add(capability);
                    }

                    break;
            }
        }

        var optional = managers.Count > 0 && evidence.Any(e => e.Kind == "fallback");
        return new ShizukuUsage(
            ManagerIds.Where(managers.Contains).ToList(),
            apiForm,
            CapabilityIds.Where(capabilities.Contains).ToList(),
            optional,
            evidence);
    }

    private static string? ManagerOf(string value)
    {
        if (value.StartsWith("moe.shizuku.manager", StringComparison.Ordinal)
            || value.StartsWith("rikka.shizuku", StringComparison.Ordinal)
            || value == "Lrikka/shizuku/")
        {
            return "shizuku";
        }

        if (value.StartsWith("com.rosan.dhizuku", StringComparison.Ordinal))
        {
            return "dhizuku";
        }

        if (value.StartsWith("rikka.sui", StringComparison.Ordinal) || value == "Lrikka/sui/")
        {
            return "sui";
        }

        if (value.StartsWith("com.topjohnwu.superuser", StringComparison.Ordinal)
            || value == "Lcom/topjohnwu/superuser/"
            || value.StartsWith("eu.chainfire.libsuperuser", StringComparison.Ordinal)
            || value == "libsu"
            // The legacy superuser permission is the only root marker that is a
            // permission name rather than a package or class token.
            || value.Contains("ACCESS_SUPERUSER", StringComparison.Ordinal))
        {
            return "root";
        }

        return null;
    }

    private static string? ApiFormOf(string value) => value switch
    {
        "bindUserService" or "UserServiceArgs" or "Shizuku.UserService" => "user_service",
        "newProcess" => "new_process",
        "requestPermission" or "checkSelfPermission" or "shouldShowRequestPermissionRationale" => "permission",
        _ => null,
    };

    private static string? CapabilityOf(string value)
    {
        foreach (var (needle, capability) in CommandMarkers)
        {
            if (needle == value)
            {
                return capability;
            }
        }

        return null;
    }

    private static string Sanitize(string value) => value.Replace('|', '/');
}

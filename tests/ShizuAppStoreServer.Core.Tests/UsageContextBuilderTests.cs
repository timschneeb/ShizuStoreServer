using ShizuAppStoreServer.Core.Data;
using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.Enrichment.Repo;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class UsageContextBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly RepoSnapshot _snapshot;
    private readonly UsageContext _context;

    public UsageContextBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"shizu-context-{Guid.NewGuid():N}");
        var source = Path.Combine(_root, "app", "src", "main", "java", "com", "example");
        Directory.CreateDirectory(source);
        File.WriteAllLines(Path.Combine(source, "ShizukuHelper.kt"),
        [
            "package com.example",
            "import rikka.shizuku.Shizuku",
            "import com.rosan.dhizuku.api.Dhizuku",
            "object ShizukuHelper {",
            "    fun runShell(command: String) {",
            "        val binder = Shizuku.getBinder()",
            "        Shizuku.newProcess(arrayOf(\"sh\", \"-c\", command), null, null)",
            "        Runtime.getRuntime().exec(command)",
            "        Dhizuku.init(null)",
            "    }",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(source, "Commands.kt"),
        [
            "package com.example",
            "object Commands {",
            "    val size = \"wm size 1080x1920\"",
            "    val power = \"reboot\"",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(source, "Callbacks.kt"),
        [
            "package com.example",
            "object Callbacks {",
            "    suspend fun execute() {",
            "        val callback = object : CameraManager.TorchCallback() {",
            "            override fun onChanged() {",
            "            }",
            "        }",
            "        ShizukuBinderWrapper(binder)",
            "    }",
            "    private fun helper() {",
            "        Shizuku.requestPermission()",
            "    }",
            "    private fun flag(): Boolean = Shizuku.checkSelfPermission() == 0",
            "}",
        ]);
        var manifestDir = Path.Combine(_root, "app", "src", "main");
        File.WriteAllLines(Path.Combine(manifestDir, "AndroidManifest.xml"),
        [
            "<manifest>",
            "  <uses-permission android:name=\"moe.shizuku.manager.permission.API_V23\" />",
            "  <uses-permission android:name=\"com.rosan.dhizuku.permission.API_V23\" />",
            "  <provider android:name=\"rikka.shizuku.ShizukuProvider\" />",
            "</manifest>",
        ]);
        var aidlDir = Path.Combine(_root, "app", "src", "main", "aidl", "com", "example");
        Directory.CreateDirectory(aidlDir);
        File.WriteAllLines(Path.Combine(aidlDir, "IUserService.aidl"),
        [
            "package com.example;",
            "interface IUserService {",
            "    void exec(String command);",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(_root, "app", "build.gradle.kts"),
        [
            "dependencies {",
            "    implementation(\"dev.rikka.shizuku:api:13.1.5\")",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(_root, "pubspec.yaml"),
        [
            "name: helperapp",
            "dependencies:",
            "  shizuku_api: ^1.2.3",
        ]);
        var dartDir = Path.Combine(_root, "lib", "services");
        Directory.CreateDirectory(dartDir);
        File.WriteAllLines(Path.Combine(dartDir, "shizuku_service.dart"),
        [
            "import 'package:shizuku_api/shizuku_api.dart';",
            "",
            "class ShizukuService {",
            "  static const MethodChannel _channel = MethodChannel('helperapp/shizuku');",
            "}",
        ]);
        var helpDir = Path.Combine(_root, "lib", "components");
        Directory.CreateDirectory(helpDir);
        File.WriteAllLines(Path.Combine(helpDir, "how_to_install_shizuku.dart"),
        [
            "import 'package:flutter/material.dart';",
            "",
            "class HowToInstallShizuku extends StatelessWidget {",
            "  const Text('Install Shizuku from the app store');",
            "}",
        ]);
        var csDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(csDir);
        File.WriteAllLines(Path.Combine(csDir, "ShizukuClient.cs"),
        [
            "using ShizukuX;",
            "",
            "namespace Helper {",
            "    class Client {",
            "        void Run() {",
            "            var binder = Shizuku.GetBinder();",
            "        }",
            "    }",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(csDir, "Helper.csproj"),
        [
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "  <ItemGroup>",
            "    <PackageReference Include=\"Rikka.Shizuku\" Version=\"13.1.5\" />",
            "  </ItemGroup>",
            "</Project>",
        ]);
        File.WriteAllLines(Path.Combine(csDir, "shizuku.ts"),
        [
            "import { Shizuku } from 'capacitor-shizuku';",
            "const channel = new MethodChannel('helperapp/shizuku');",
        ]);

        _snapshot = new RepoSnapshot(RepoForge.GitHub, "example", "app", "https://github.com/example/app", "abc1234", null, _root);
        var app = new App
        {
            Slug = "helperapp",
            Name = "HelperApp",
            Url = "https://github.com/example/app",
            Description = "demo",
            Permissions =
            [
                "moe.shizuku.manager.permission.API_V23",
                "com.rosan.dhizuku.permission.API_V23",
                "android.permission.REBOOT",
                "android.permission.REQUEST_INSTALL_PACKAGES",
            ],
        };
        _context = new UsageContextBuilder(new UsageAnalysisOptions()).Build(app, _snapshot, "1.0");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void KeepsOnlyRelevantDeclaredPermissions()
    {
        Assert.Contains("moe.shizuku.manager.permission.API_V23", _context.Permissions);
        Assert.Contains("android.permission.REQUEST_INSTALL_PACKAGES", _context.Permissions);
        Assert.DoesNotContain("android.permission.REBOOT", _context.Permissions);
    }

    [Fact]
    public void CollectsPrivilegeImports()
    {
        var import = Assert.Single(_context.Surface, e => e.Kind == "import");
        Assert.Equal("rikka.shizuku.Shizuku", import.Value);
        Assert.Equal("app/src/main/java/com/example/ShizukuHelper.kt", import.Path);
        Assert.Equal(2, import.Line);
    }

    [Fact]
    public void CollectsManifestAndDependencyArtifacts()
    {
        Assert.Contains(_context.Surface, e => e.Kind == "manifest" && e.Value == "rikka.shizuku");
        Assert.Contains(_context.Surface, e => e.Kind == "dependency" && e.Value == "rikka.shizuku");
    }

    [Fact]
    public void IgnoresDhizukuArtifacts()
    {
        Assert.DoesNotContain(_context.Permissions, p => p.Contains("dhizuku", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_context.Surface, e => e.Value.Contains("dhizuku", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CollectsAidlTypesAndMethods()
    {
        Assert.Contains(_context.Surface, e => e.Kind == "aidl" && e.Value == "type" && e.Symbol == "IUserService");
        Assert.Contains(_context.Surface, e => e.Kind == "aidl" && e.Value == "method" && e.Symbol == "exec");
    }

    [Fact]
    public void CollectsEntryPointsWithTheEnclosingSymbol()
    {
        var entries = _context.Surface.Where(e => e.Kind == "entry").ToList();
        Assert.Contains(entries, e => e.Value == "Shizuku.getBinder" && e.Symbol == "runShell");
        Assert.Contains(entries, e => e.Value == "newProcess" && e.Symbol == "runShell");
    }

    [Fact]
    public void AttributesNeedlesToTheInnermostEnclosingDeclaration()
    {
        var callbacks = _context.Surface.Where(e => e.Path.EndsWith("Callbacks.kt", StringComparison.Ordinal)).ToList();

        var wrapper = Assert.Single(callbacks, e => e.Value == "ShizukuBinderWrapper");
        Assert.Equal("execute", wrapper.Symbol);
        Assert.Equal(8, wrapper.Line);

        var requestPermission = Assert.Single(callbacks, e => e.Value == "Shizuku.requestPermission");
        Assert.Equal("helper", requestPermission.Symbol);

        var checkSelfPermission = Assert.Single(callbacks, e => e.Value == "Shizuku.checkSelfPermission");
        Assert.Equal("flag", checkSelfPermission.Symbol);
    }

    [Fact]
    public void CollectsCommandHelpersButNeverShellKeywords()
    {
        Assert.Contains(_context.Surface, e => e.Kind == "command" && e.Value == "Runtime.getRuntime().exec");
        Assert.DoesNotContain(_context.Surface, e => e.Text.Contains("wm size", StringComparison.Ordinal));
        Assert.DoesNotContain(_context.Surface, e => e.Text.Contains("reboot", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptJsonCarriesTheSurfaceMap()
    {
        var json = _context.ToPromptJson();

        Assert.Contains("\"privilegeSurface\"", json);
        Assert.Contains("\"filesScanned\"", json);
        Assert.Contains("\"projectKind\":\"flutter\"", json);
        Assert.Contains("rikka.shizuku.Shizuku", json);
    }

    [Fact]
    public void CollectsDartAndCSharpBridgeCallSites()
    {
        var bridges = _context.Surface.Where(e => e.Kind == "bridge").ToList();
        Assert.Contains(bridges, e => e.Path == "lib/services/shizuku_service.dart" && e.Value == "shizuku_api");
        Assert.Contains(bridges, e => e.Path == "lib/services/shizuku_service.dart" && e.Value == "MethodChannel");
        Assert.Contains(bridges, e => e.Path == "src/ShizukuClient.cs" && e.Value == "ShizukuX");
        Assert.Contains(bridges, e => e.Path == "src/ShizukuClient.cs" && e.Value == "Shizuku.");
        Assert.Contains(bridges, e => e.Path == "src/shizuku.ts" && e.Value == "import ");
        Assert.Contains(bridges, e => e.Path == "src/shizuku.ts" && e.Value == "MethodChannel");
    }

    [Fact]
    public void CollectsNonJvmDependencyPackages()
    {
        Assert.Contains(_context.Surface, e => e.Kind == "package" && e.Value == "shizuku_api");
        Assert.Contains(_context.Surface, e => e.Kind == "package" && e.Value == "Rikka.Shizuku");
    }

    [Fact]
    public void DoesNotTreatHelpScreensAsBridges()
    {
        Assert.DoesNotContain(_context.Surface, e => e.Path.Contains("how_to_install_shizuku", StringComparison.Ordinal));
    }

    [Fact]
    public void CarriesThePreviousReportIntoThePrompt()
    {
        var app = new App
        {
            Slug = "helperapp",
            Name = "HelperApp",
            Url = "https://github.com/example/app",
            Description = "demo",
            Permissions = [],
            UsageShort = "Can install apps.",
            UsageMarkdown = "- **Install apps**: uses `pm install`.",
            UsageCommit = "abc1234",
            UsageReleaseRef = "v1.0",
            UsageAnalyzedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var context = new UsageContextBuilder(new UsageAnalysisOptions()).Build(app, _snapshot, "2.0");

        Assert.NotNull(context.Previous);
        var json = context.ToPromptJson();
        Assert.Contains("\"previousAnalysis\"", json);
        Assert.Contains("Can install apps.", json);
        Assert.Contains("v1.0", json);
    }

    [Fact]
    public void OmitsThePreviousReportWhenThereIsNone()
    {
        Assert.DoesNotContain("\"previousAnalysis\"", _context.ToPromptJson());
    }

    [Fact]
    public void DeduplicatesRepeatedSurfaceNeedles()
    {
        var source = Path.Combine(_root, "app", "src", "main", "java", "com", "example");
        var lines = new List<string> { "package com.example", "object Noisy {" };
        for (var i = 0; i < 30; i++)
        {
            lines.Add($"    fun f{i}() {{");
            lines.Add("        Shizuku.checkSelfPermission()");
            lines.Add("        PackageInstaller");
            lines.Add("    }");
        }

        lines.Add("}");
        File.WriteAllLines(Path.Combine(source, "Noisy.kt"), lines);

        var app = new App
        {
            Slug = "noisy",
            Name = "Noisy",
            Url = "https://github.com/example/app",
            Permissions = [],
        };
        var context = new UsageContextBuilder(new UsageAnalysisOptions()).Build(app, _snapshot, "1.0");

        Assert.Single(context.Surface, e => e.Kind == "entry" && e.Value == "Shizuku.checkSelfPermission");
        Assert.Single(context.Surface, e => e.Kind == "fallback" && e.Value == "PackageInstaller");
    }
}

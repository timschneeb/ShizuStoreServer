using ShizuAppStoreServer.Core.Enrichment;
using ShizuAppStoreServer.Core.UsageAnalysis;

namespace ShizuAppStoreServer.Core.Tests;

public sealed class RepoSearchTraceTests : IDisposable
{
    private readonly string _root;
    private readonly RepoSearch _search;

    public RepoSearchTraceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"shizu-search-{Guid.NewGuid():N}");
        var source = Path.Combine(_root, "app", "src", "main", "java", "com", "example");
        Directory.CreateDirectory(source);
        File.WriteAllLines(Path.Combine(source, "ShizukuHelper.kt"),
        [
            "package com.example",
            "object ShizukuHelper {",
            "    fun runShell(command: String) {",
            "        val process = newProcess(arrayOf(\"sh\", \"-c\", command))",
            "        log(command)",
            "    }",
            "    private fun log(message: String) {",
            "    }",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(source, "Feature.kt"),
        [
            "package com.example",
            "class Feature {",
            "    fun applySize() {",
            "        ShizukuHelper.runShell(\"wm size 1080x1920\")",
            "    }",
            "    fun grant() {",
            "        ShizukuHelper.runShell(\"pm grant com.example android.permission.WRITE_SECURE_SETTINGS\")",
            "    }",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(source, "Other.kt"),
        [
            "package com.example",
            "class Other {",
            "    fun call() {",
            "        Feature().applySize()",
            "    }",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(source, "ShizukuUtils.kt"),
        [
            "package com.example",
            "object ShizukuUtils {",
            "    fun runCommand(command: String) {",
            "        ShizukuHelper.runShell(command)",
            "    }",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(source, "ShellUtils.kt"),
        [
            "package com.example",
            "object ShellUtils {",
            "    fun runCommand(command: String) {",
            "        Runtime.getRuntime().exec(command)",
            "    }",
            "}",
        ]);
        File.WriteAllLines(Path.Combine(source, "Callbacks.kt"),
        [
            "package com.example",
            "class Callbacks {",
            "    fun outer() {",
            "        val callback = object : Listener() {",
            "            override fun onChanged() {",
            "                ShizukuHelper.runShell(\"dumpsys battery\")",
            "            }",
            "        }",
            "        ShizukuHelper.runShell(\"wm size 1080x1920\")",
            "    }",
            "}",
        ]);
        _search = new RepoSearch(
            new RepoSnapshot(RepoForge.GitHub, "example", "app", "https://github.com/example/app", "abc1234", null, _root),
            new UsageAnalysisOptions());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RepoMapListsDeclarationsWithPathAndLine()
    {
        var map = _search.RepoMap();

        Assert.Contains("ShizukuHelper.kt:2: object ShizukuHelper", map);
        Assert.Contains("ShizukuHelper.kt:3: fun runShell(command: String) {", map);
        Assert.Contains("Feature.kt:2: class Feature {", map);
    }

    [Fact]
    public void ReadSymbolReturnsTheBraceMatchedBody()
    {
        var body = _search.ReadSymbol("runShell");

        Assert.Contains("'runShell' lines 3-6", body);
        Assert.Contains("newProcess(", body);
        Assert.DoesNotContain("private fun log", body);
    }

    [Fact]
    public void ReadSymbolReportsUnknownSymbols()
    {
        Assert.Contains("(symbol not found", _search.ReadSymbol("ghost"));
    }

    [Fact]
    public void ClassQualifiedSymbolsResolveInsideTheNamedClass()
    {
        var qualified = _search.ReadSymbol("ShizukuUtils.runCommand");

        Assert.Contains("ShizukuUtils.kt", qualified);
        Assert.Contains("runShell(command)", qualified);

        var plain = _search.ReadSymbol("runCommand");
        Assert.Contains("ShellUtils.kt", plain);
        Assert.DoesNotContain("runShell(command)", plain);
    }

    [Fact]
    public void FindCallersReturnsEnclosingFunctionAndArguments()
    {
        var callers = _search.FindCallers("runShell");

        Assert.Contains("Feature.kt:4: applySize -> runShell(\"wm size 1080x1920\")", callers);
        Assert.Contains("Feature.kt:7: grant -> runShell(\"pm grant com.example", callers);
        Assert.DoesNotContain("ShizukuHelper.kt", callers);
    }

    [Fact]
    public void CallSitesAfterAClosedNestedDeclarationKeepTheOuterEnclosingName()
    {
        var callers = _search.FindCallers("runShell");

        Assert.Contains("Callbacks.kt:6: onChanged -> runShell", callers);
        Assert.Contains("Callbacks.kt:9: outer -> runShell", callers);
    }

    [Fact]
    public void TraceSymbolFollowsCallersUpwards()
    {
        var trace = _search.TraceSymbol("runShell", depth: 2);

        Assert.Contains("runShell (defined", trace);
        Assert.Contains("applySize at", trace);
        Assert.Contains("call at", trace);
        Assert.Contains("calls applySize()", trace);
    }

    [Fact]
    public void TraceSymbolReportsSymbolsWithoutCallers()
    {
        Assert.Contains("(no callers found", _search.TraceSymbol("call"));
    }

    [Fact]
    public void TraceSymbolSummarizesWhatItFollowed()
    {
        var trace = _search.TraceSymbol("runShell", depth: 2);

        Assert.Contains("(trace followed", trace);
        Assert.Contains("level(s))", trace);
    }

    [Fact]
    public void SearchCodeSuggestsDroppingThePathWhenScopedSearchFindsNothing()
    {
        Assert.Contains("retry without a path", _search.SearchCode("class RecorderService", "app/src/main"));
        Assert.Equal("(no matches)", _search.SearchCode("class RecorderService"));
    }

    [Fact]
    public void SearchCodeStillWorks()
    {
        Assert.Contains("ShizukuHelper.kt:3", _search.SearchCode("fun\\s+runShell"));
    }

    [Fact]
    public void DottedSymbolsAndOmittedPathsStillResolve()
    {
        var callers = _search.FindCallers("ShizukuHelper.runShell", max: 10);
        Assert.Contains("Feature.kt:4: applySize -> runShell(", callers);

        var body = _search.ReadSymbol("ShizukuHelper.runShell()");
        Assert.Contains("'runShell' lines 3-6", body);

        var trace = _search.TraceSymbol("ShizukuHelper.runShell");
        Assert.Contains("runShell (defined", trace);
    }

    [Fact]
    public void MissingOrBlankArgumentsDegradeInsteadOfThrowing()
    {
        Assert.Contains("(no symbol given)", _search.FindCallers());
        Assert.Contains("(no symbol given)", _search.ReadSymbol("   "));
        Assert.Contains("(no symbol given)", _search.TraceSymbol(null));
        Assert.Contains("(no pattern given)", _search.SearchCode(null));
        Assert.Contains("(file not found", _search.ReadFile());
        Assert.Contains("(no call sites of 'ghost')", _search.FindCallers("ghost"));
    }

    [Fact]
    public void TracesSurfaceCallSitesAndReadsMarkThemInspected()
    {
        _search.FindCallers("runShell");

        var site = _search.SurfacedCallSites
            .Single(s => s.Path.EndsWith("Feature.kt", StringComparison.Ordinal) && s.Line == 4);
        Assert.Equal("applySize", site.Enclosing);
        Assert.Contains("runShell", _search.InvestigatedSymbols);
        Assert.False(_search.WasRead(site.Path, 4));

        _search.ReadFile(site.Path, 3, 5);

        Assert.True(_search.WasRead(site.Path, 4));
    }

    [Fact]
    public void CountsOnlySymbolToolsForTheTracingTelemetry()
    {
        _search.ReadFile("app/src/main/java/com/example/Feature.kt");
        Assert.Equal(0, _search.SymbolToolCalls);

        _search.ReadSymbol("runShell");
        _search.FindCallers("runShell");
        _search.TraceSymbol("runShell");

        Assert.Equal(3, _search.SymbolToolCalls);
        Assert.Equal(4, _search.CallCount);
    }
}

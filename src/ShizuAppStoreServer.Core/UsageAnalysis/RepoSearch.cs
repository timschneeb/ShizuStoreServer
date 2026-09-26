using System.Text;
using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// Read-only browsing over one checked-out snapshot: file listing, bounded
/// reads with numbered lines, regex search and shallow call tracing over an
/// in-memory declaration index. Every path is confined to the snapshot root
/// and no tool executes anything, so untrusted repository content cannot
/// reach the host.
/// </summary>
public sealed class RepoSearch
{
    private readonly RepoSnapshot _snapshot;
    private readonly UsageAnalysisOptions _options;
    private readonly List<string> _files;
    private readonly List<ReadRegion> _regions = [];
    private readonly List<SurfacedCallSite> _sites = [];
    private readonly HashSet<string> _investigated = new(StringComparer.Ordinal);
    private List<Declaration>? _declarations;

    public RepoSearch(RepoSnapshot snapshot, UsageAnalysisOptions options)
    {
        _snapshot = snapshot;
        _options = options;
        _files = EnumerateFiles(snapshot.RootPath);
    }

    /// <summary>Tool invocations so far; the run record keeps this per analysis.</summary>
    public int CallCount { get; private set; }

    /// <summary>read_symbol, find_callers and trace_symbol invocations; these establish how a privileged call is reached.</summary>
    public int SymbolToolCalls { get; private set; }

    /// <summary>Files one read window actually returned.</summary>
    public IReadOnlyList<ReadRegion> ReadRegions => _regions;

    /// <summary>Call sites the traces have surfaced so far.</summary>
    public IReadOnlyList<SurfacedCallSite> SurfacedCallSites => _sites;

    /// <summary>Names the agent traced or read, normalized to the method segment.</summary>
    public IReadOnlyCollection<string> InvestigatedSymbols => _investigated;

    /// <summary>True when a read window returned the given line of a file.</summary>
    public bool WasRead(string path, int line)
    {
        var relative = Normalize(path);
        foreach (var region in _regions)
        {
            if (string.Equals(region.Path, relative, StringComparison.Ordinal)
                && line >= region.StartLine && line <= region.EndLine)
            {
                return true;
            }
        }

        return false;
    }

    public sealed record ReadRegion(string Path, int StartLine, int EndLine);

    public sealed record SurfacedCallSite(string Path, int Line, string? Enclosing, string Called);

    /// <summary>Repository-relative paths, bounded and sorted; directories end with '/'.</summary>
    public string ListFiles(string? path = null, int? max = null)
    {
        CallCount++;
        return Safe(() => ListFilesCore(path, max));
    }

    private string ListFilesCore(string? path, int? max)
    {
        var prefix = Normalize(path);
        var limit = Math.Clamp(max is > 0 ? max.Value : 400, 1, 800);
        var builder = new StringBuilder();
        var shown = 0;
        foreach (var file in _files)
        {
            if (prefix.Length > 0 && !file.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(file).Append('\n');
            shown++;
            if (shown >= limit)
            {
                builder.Append("... listing truncated; narrow the path.\n");
                break;
            }
        }

        return shown == 0 ? $"(no files under '{path}')" : builder.ToString();
    }

    /// <summary>Numbered lines of one text file, clamped to the read window.</summary>
    public string ReadFile(string? path = null, int? startLine = null, int? endLine = null)
    {
        CallCount++;
        return Safe(() => ReadFileCore(path, startLine, endLine));
    }

    private string ReadFileCore(string? path, int? startLine, int? endLine)
    {
        if (!TryResolve(path, out var fullPath, out var relative))
        {
            return $"(file not found: '{path}')";
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > _options.MaxFileBytes)
            {
                return $"(file too large to read: '{relative}')";
            }

            var lines = File.ReadAllLines(fullPath, Encoding.UTF8);
            var start = Math.Clamp(startLine is > 0 ? startLine.Value : 1, 1, Math.Max(1, lines.Length));
            var end = endLine is > 0 ? Math.Min(endLine.Value, lines.Length) : Math.Min(start + _options.MaxReadLines - 1, lines.Length);
            if (end < start)
            {
                end = Math.Min(start + _options.MaxReadLines - 1, lines.Length);
            }

            var builder = new StringBuilder();
            builder.Append(relative).Append(" (").Append(lines.Length).Append(" lines)\n");
            for (var i = start; i <= end; i++)
            {
                var line = lines[i - 1];
                if (line.Length > 400)
                {
                    line = line[..400];
                }

                builder.Append(i).Append('\t').Append(line).Append('\n');
            }

            _regions.Add(new ReadRegion(relative, start, end));
            return builder.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return $"(cannot read '{relative}': {ex.GetType().Name})";
        }
    }

    /// <summary>Regex search over text files, bounded by result count and line length.</summary>
    public string SearchCode(string? pattern = null, string? path = null, int? maxResults = null)
    {
        CallCount++;
        return Safe(() => SearchCodeCore(pattern, path, maxResults));
    }

    private string SearchCodeCore(string? pattern, string? path, int? maxResults)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "(no pattern given)";
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return $"(invalid regex: {ex.Message})";
        }

        var limit = Math.Clamp(maxResults is > 0 ? maxResults.Value : _options.MaxSearchResults, 1, 200);
        var prefix = Normalize(path);
        var builder = new StringBuilder();
        var shown = 0;
        foreach (var file in _files)
        {
            if (prefix.Length > 0 && !file.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines;
            try
            {
                var fullPath = Path.Combine(_snapshot.RootPath, file);
                if (new FileInfo(fullPath).Length > _options.MaxFileBytes)
                {
                    continue;
                }

                lines = File.ReadAllLines(fullPath, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                bool matched;
                try
                {
                    matched = regex.IsMatch(lines[i]);
                }
                catch (RegexMatchTimeoutException)
                {
                    return "(search timed out; simplify the pattern)";
                }

                if (!matched)
                {
                    continue;
                }

                var match = lines[i].Trim();
                if (match.Length > 220)
                {
                    match = match[..220];
                }

                builder.Append(file).Append(':').Append(i + 1).Append(": ").Append(match).Append('\n');
                shown++;
                if (shown >= limit)
                {
                    builder.Append("... results truncated; narrow the pattern or path.\n");
                    return builder.ToString();
                }
            }
        }

        if (shown > 0)
        {
            return builder.ToString();
        }

        return prefix.Length > 0
            ? $"(no matches under '{prefix}'; retry without a path to search the whole repository)"
            : "(no matches)";
    }

    /// <summary>Declaration index across the checkout, one line per symbol.</summary>
    public string RepoMap(string? path = null, int? max = null)
    {
        CallCount++;
        return Safe(() => RepoMapCore(path, max));
    }

    private string RepoMapCore(string? path, int? max)
    {
        var prefix = Normalize(path);
        var limit = Math.Clamp(max is > 0 ? max.Value : 300, 1, 800);
        var builder = new StringBuilder();
        var shown = 0;
        foreach (var declaration in Declarations())
        {
            if (prefix.Length > 0 && !declaration.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(declaration.Path).Append(':').Append(declaration.Line).Append(": ").Append(declaration.Text).Append('\n');
            shown++;
            if (shown >= limit)
            {
                builder.Append("... declaration list truncated; narrow the path.\n");
                break;
            }
        }

        return shown == 0 ? $"(no declarations under '{path}')" : builder.ToString();
    }

    /// <summary>Declaration plus brace-matched body, bounded by the read window.</summary>
    public string ReadSymbol(string? symbol = null, string? path = null, int? maxLines = null)
    {
        CallCount++;
        SymbolToolCalls++;
        return Safe(() => ReadSymbolCore(symbol, path, maxLines));
    }

    private string ReadSymbolCore(string? symbol, string? path, int? maxLines)
    {
        var (className, name) = SplitSymbol(symbol);
        if (name is null)
        {
            return "(no symbol given)";
        }

        var prefix = Normalize(path);
        var matches = Declarations()
            .Where(d => string.Equals(d.Symbol, name, StringComparison.Ordinal)
                && (prefix.Length == 0 || d.Path.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();
        if (matches.Count == 0)
        {
            return $"(symbol not found: '{name}')";
        }

        if (className is not null)
        {
            var qualified = matches.Where(d => MatchesClass(d, className)).ToList();
            if (qualified.Count > 0)
            {
                matches = qualified;
            }
        }

        var declaration = matches[0];
        var lines = ReadLines(declaration.Path);
        if (lines is null)
        {
            return $"(cannot read '{declaration.Path}')";
        }

        var limit = Math.Clamp(maxLines is > 0 ? maxLines.Value : _options.MaxReadLines, 1, _options.MaxReadLines);
        var start = declaration.Line - 1;
        var closing = SourceIndex.FindClosingBrace(lines, start);
        var end = closing >= 0 ? closing : Math.Min(start + 5, lines.Length - 1);
        if (end - start + 1 > limit)
        {
            end = start + limit - 1;
        }

        _regions.Add(new ReadRegion(declaration.Path, start + 1, end + 1));
        _investigated.Add(name);
        var builder = new StringBuilder();
        builder.Append(declaration.Path).Append(" (").Append(lines.Length).Append(" lines), '").Append(name)
            .Append("' lines ").Append(start + 1).Append('-').Append(end + 1).Append('\n');
        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            if (line.Length > 400)
            {
                line = line[..400];
            }

            builder.Append(i + 1).Append('\t').Append(line).Append('\n');
        }

        if (matches.Count > 1)
        {
            builder.Append("Other declarations: ")
                .Append(string.Join(", ", matches.Skip(1).Take(5).Select(d => $"{d.Path}:{d.Line}")))
                .Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Call sites of a symbol with the enclosing function and arguments.</summary>
    public string FindCallers(string? symbol = null, string? path = null, int? max = null)
    {
        CallCount++;
        SymbolToolCalls++;
        return Safe(() => FindCallersCore(symbol, path, max));
    }

    private string FindCallersCore(string? symbol, string? path, int? max)
    {
        var (_, name) = SplitSymbol(symbol);
        if (name is null)
        {
            return "(no symbol given)";
        }

        var limit = Math.Clamp(max is > 0 ? max.Value : 40, 1, 100);
        var callers = Callers(name, Normalize(path), limit, out var truncated);
        _investigated.Add(name);
        foreach (var caller in callers)
        {
            _sites.Add(new SurfacedCallSite(caller.Path, caller.Line, caller.Enclosing, name));
        }

        if (callers.Count == 0)
        {
            return $"(no call sites of '{name}')";
        }

        var builder = new StringBuilder();
        foreach (var caller in callers)
        {
            builder.Append(caller.Path).Append(':').Append(caller.Line).Append(": ")
                .Append(caller.Enclosing ?? "(top-level)").Append(" -> ").Append(name).Append(caller.Arguments).Append('\n');
        }

        if (truncated)
        {
            builder.Append("... call site list truncated; raise max or narrow the path.\n");
        }

        builder.Append("Tip: trace_symbol follows these callers further up in one call.\n");
        return builder.ToString();
    }

    /// <summary>Bounded caller tree: who calls the symbol, and who calls them.</summary>
    public string TraceSymbol(string? symbol = null, int? depth = null, int? maxNodes = null)
    {
        CallCount++;
        SymbolToolCalls++;
        return Safe(() => TraceSymbolCore(symbol, depth, maxNodes));
    }

    private string TraceSymbolCore(string? symbol, int? depth, int? maxNodes)
    {
        var (className, name) = SplitSymbol(symbol);
        if (name is null)
        {
            return "(no symbol given)";
        }

        var levels = Math.Clamp(depth is > 0 ? depth.Value : 2, 1, 4);
        var nodeLimit = Math.Clamp(maxNodes is > 0 ? maxNodes.Value : 40, 4, 80);
        var definition = FindDefinition(name, className);
        _investigated.Add(name);
        var builder = new StringBuilder();
        builder.Append(name)
            .Append(definition is null ? " (definition not indexed)" : $" (defined {definition.Path}:{definition.Line})")
            .Append('\n');

        var visited = new HashSet<string>(StringComparer.Ordinal) { name };
        var queue = new Queue<(string Symbol, int Depth)>();
        queue.Enqueue((name, 0));
        var nodes = 0;
        var truncated = false;
        var callersTruncated = false;
        while (queue.Count > 0 && !truncated)
        {
            var (current, currentDepth) = queue.Dequeue();
            if (currentDepth >= levels)
            {
                continue;
            }

            var callers = Callers(current, string.Empty, 8, out var more);
            callersTruncated |= more;
            foreach (var caller in callers)
            {
                if (nodes >= nodeLimit)
                {
                    truncated = true;
                    break;
                }

                nodes++;
                builder.Append(new string(' ', (currentDepth + 1) * 2))
                    .Append(caller.Enclosing ?? "(top-level)").Append(" at ").Append(caller.Path).Append(':').Append(caller.Line)
                    .Append(" calls ").Append(current).Append(caller.Arguments).Append('\n');
                _sites.Add(new SurfacedCallSite(caller.Path, caller.Line, caller.Enclosing, current));
                if (caller.Enclosing is not null && visited.Add(caller.Enclosing))
                {
                    queue.Enqueue((caller.Enclosing, currentDepth + 1));
                }
            }
        }

        if (nodes == 0)
        {
            builder.Append("(no callers found; the symbol may be reached through an interface or reflection)\n");
        }
        else
        {
            builder.Append("(trace followed ").Append(nodes).Append(" call site(s) up to ").Append(levels).Append(" level(s))\n");
        }

        if (callersTruncated)
        {
            builder.Append("... some symbols have more call sites than shown; use find_callers on them for the full list.\n");
        }

        if (truncated)
        {
            builder.Append("... trace truncated; raise maxNodes or lower depth.\n");
        }

        return builder.ToString();
    }

    /// <summary>Browsable commit-pinned URL for a validated relative path.</summary>
    public string BlobUrl(string path, int line) => _snapshot.BlobUrl(Normalize(path), line);

    private List<CallSite> Callers(string symbol, string prefix, int limit, out bool truncated)
    {
        truncated = false;
        var results = new List<CallSite>();
        var pattern = new Regex($@"\b{Regex.Escape(symbol)}\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        foreach (var file in _files)
        {
            if (!IsCodeFile(file) || (prefix.Length > 0 && !file.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            var lines = ReadLines(file);
            if (lines is null)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                Match match;
                try
                {
                    match = pattern.Match(lines[i]);
                }
                catch (RegexMatchTimeoutException)
                {
                    return results;
                }

                if (!match.Success)
                {
                    continue;
                }

                if (SourceIndex.TryDeclaration(lines[i], out var declared) && string.Equals(declared, symbol, StringComparison.Ordinal))
                {
                    continue;
                }

                var open = lines[i].IndexOf('(', match.Index);
                results.Add(new CallSite(file, i + 1, SourceIndex.Enclosing(lines, i), SourceIndex.ArgumentsAt(lines[i], open)));
                if (results.Count >= limit)
                {
                    truncated = true;
                    return results;
                }
            }
        }

        return results;
    }

    private List<Declaration> Declarations()
    {
        if (_declarations is not null)
        {
            return _declarations;
        }

        var results = new List<Declaration>();
        foreach (var file in _files)
        {
            if (!IsCodeFile(file))
            {
                continue;
            }

            var lines = ReadLines(file);
            if (lines is null)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (!SourceIndex.TryDeclaration(lines[i], out var symbol))
                {
                    continue;
                }

                var text = lines[i].Trim();
                if (text.Length > 160)
                {
                    text = text[..160];
                }

                results.Add(new Declaration(symbol, file, i + 1, text));
            }
        }

        _declarations = results;
        return results;
    }

    private string[]? ReadLines(string relative)
    {
        try
        {
            var fullPath = Path.Combine(_snapshot.RootPath, relative);
            if (new FileInfo(fullPath).Length > _options.MaxFileBytes)
            {
                return null;
            }

            return File.ReadAllLines(fullPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsCodeFile(string path) =>
        path.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".java", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".aidl", StringComparison.OrdinalIgnoreCase);

    private bool TryResolve(string? path, out string fullPath, out string relative)
    {
        relative = Normalize(path);
        fullPath = string.Empty;
        if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal) || !_files.Contains(relative, StringComparer.Ordinal))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(_snapshot.RootPath, relative));
        var root = Path.GetFullPath(_snapshot.RootPath) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.Ordinal) || !File.Exists(candidate))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static string Normalize(string? path) => path?.Trim().TrimStart('/').Replace('\\', '/') ?? string.Empty;

    /// <summary>
    /// Accepts the dotted names surface entries use (`Class.method`, `method()`)
    /// and splits them into the optional class hint and the declaration name
    /// the index stores. The class hint resolves a qualified name to the
    /// declaration inside that class instead of the first same-named one.
    /// </summary>
    private static (string? Class, string? Method) SplitSymbol(string? symbol)
    {
        var trimmed = symbol?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return (null, null);
        }

        if (trimmed.EndsWith("()", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^2];
        }

        var lastDot = trimmed.LastIndexOf('.');
        if (lastDot <= 0 || lastDot == trimmed.Length - 1)
        {
            return (null, trimmed.Length == 0 ? null : trimmed);
        }

        var owner = trimmed[..lastDot];
        var ownerDot = owner.LastIndexOf('.');
        if (ownerDot >= 0)
        {
            owner = owner[(ownerDot + 1)..];
        }

        var method = trimmed[(lastDot + 1)..];
        return (owner.Length == 0 ? null : owner, method.Length == 0 ? null : method);
    }

    /// <summary>First declaration of the name, preferring the named class's file.</summary>
    private Declaration? FindDefinition(string name, string? className)
    {
        var matches = Declarations()
            .Where(d => string.Equals(d.Symbol, name, StringComparison.Ordinal))
            .ToList();
        if (className is not null)
        {
            var qualified = matches.Where(d => MatchesClass(d, className)).ToList();
            if (qualified.Count > 0)
            {
                return qualified[0];
            }
        }

        return matches.Count == 0 ? null : matches[0];
    }

    /// <summary>Kotlin/Java convention: the top-level class lives in a file of its name.</summary>
    private static bool MatchesClass(Declaration declaration, string className)
        => string.Equals(
            Path.GetFileNameWithoutExtension(declaration.Path), className, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tool bodies degrade to a readable message instead of failing the model
    /// turn; the model can then retry with different arguments.
    /// </summary>
    private static string Safe(Func<string> body)
    {
        try
        {
            return body();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"(tool error: {ex.GetType().Name}: {ex.Message})";
        }
    }

    private static List<string> EnumerateFiles(string root)
    {
        var results = new List<string>();
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

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    var name = Path.GetFileName(entry);
                    if (name is ".git" or "build" or ".gradle" or "node_modules" or ".idea" or "out")
                    {
                        continue;
                    }

                    stack.Push(entry);
                    continue;
                }

                results.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private sealed record Declaration(string Symbol, string Path, int Line, string Text);

    private sealed record CallSite(string Path, int Line, string? Enclosing, string Arguments);
}

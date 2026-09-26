using System.Text.RegularExpressions;

namespace ShizuAppStoreServer.Core.UsageAnalysis;

/// <summary>
/// Language-agnostic declaration helpers shared by the context pre-scan and the
/// repository tools. The extraction is deliberately shallow: it produces symbol
/// names and brace-matched bodies that give the model a starting point, not a
/// compiler-grade index.
/// </summary>
internal static partial class SourceIndex
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "new", "else", "do", "try",
        "synchronized", "throw", "super", "this", "assert", "yield",
    };

    [GeneratedRegex(@"\b(?:fun|class|object|interface|enum\s+class|data\s+class|sealed\s+class|abstract\s+class|value\s+class)\s+([A-Za-z_]\w*)")]
    private static partial Regex KotlinDeclaration();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|static|final|abstract|synchronized|native|override|suspend|open|internal|inline|operator|external|default|sealed|data)\s+)*(?:[\w<>,.\[\]?@]+\s+)+([A-Za-z_]\w*)\s*\([^;{]*\)\s*(?:throws\s+[\w., ]+)?\{?\s*$")]
    private static partial Regex JavaDeclaration();

    [GeneratedRegex(@"^\s*(?:[\w<>,.\[\]?@]+\s+)?([A-Za-z_]\w*)\s*\([^;{]*\)\s*;\s*$")]
    private static partial Regex AidlMethod();

    [GeneratedRegex(@"^\s*(?:parcelable|interface)\s+([A-Za-z_]\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex AidlType();

    /// <summary>Nearest declaration name for a line, or null when the line is not one.</summary>
    public static bool TryDeclaration(string line, out string symbol)
    {
        symbol = string.Empty;
        if (line.Length == 0 || line.Length > 400)
        {
            return false;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('*') || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith('@') || trimmed.StartsWith('#'))
        {
            return false;
        }

        foreach (var regex in new[] { KotlinDeclaration(), JavaDeclaration(), AidlMethod() })
        {
            var match = regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value;
            if (!Keywords.Contains(name) && name.Length > 1)
            {
                symbol = name;
                return true;
            }
        }

        return false;
    }

    /// <summary>Name of the nearest declaration at or above a line index, or null.</summary>
    public static string? Enclosing(string[] lines, int lineIndex)
    {
        if (lines.Length == 0)
        {
            return null;
        }

        return EnclosingMap(lines)[Math.Clamp(lineIndex, 0, lines.Length - 1)];
    }

    /// <summary>
    /// Innermost enclosing declaration name for every line. A forward scan keeps
    /// the open declarations on a brace-depth stack, so a line after a body
    /// closes is no longer attributed to it. Brace counting is shallow: line and
    /// block comments and quoted strings are skipped, raw strings are not.
    /// </summary>
    public static string?[] EnclosingMap(string[] lines)
    {
        var map = new string?[lines.Length];
        var open = new List<(string Name, int Depth, bool Opened)>();
        var lastAtDepth = new Dictionary<int, string>();
        var depth = 0;
        var blockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (TryDeclaration(line, out var symbol))
            {
                while (open.Count > 0 && open[^1].Depth >= depth)
                {
                    open.RemoveAt(open.Count - 1);
                }

                open.Add((symbol, depth, false));
                lastAtDepth[depth] = symbol;
                map[i] = symbol;
            }
            else
            {
                map[i] = open.Count > 0
                    ? open[^1].Name
                    : lastAtDepth.TryGetValue(depth, out var last) ? last : null;
            }

            var before = depth;
            CountBraces(line, ref blockComment, out var opens, out var closes);
            depth += opens - closes;
            if (depth < 0)
            {
                depth = 0;
            }

            while (open.Count > 0)
            {
                var top = open[^1];
                if (depth > top.Depth)
                {
                    open[^1] = (top.Name, top.Depth, true);
                    break;
                }

                if (top.Opened || (opens > 0 && before == top.Depth))
                {
                    open.RemoveAt(open.Count - 1);
                    continue;
                }

                break;
            }
        }

        return map;
    }

    /// <summary>Braces on a line, skipping line and block comments and quoted strings.</summary>
    private static void CountBraces(string line, ref bool blockComment, out int opens, out int closes)
    {
        opens = 0;
        closes = 0;
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (blockComment)
            {
                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    blockComment = false;
                    i++;
                }

                continue;
            }

            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/')
                {
                    break;
                }

                if (line[i + 1] == '*')
                {
                    blockComment = true;
                    i++;
                    continue;
                }
            }

            if (c is '"' or '\'' or '`')
            {
                quote = c;
                continue;
            }

            if (c == '{')
            {
                opens++;
            }
            else if (c == '}')
            {
                closes++;
            }
        }
    }

    /// <summary>AIDL interface or parcelable name for a line, or null.</summary>
    public static string? AidlTypeName(string line)
    {
        var match = AidlType().Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Argument list text starting at an opening parenthesis, respecting quotes
    /// and nesting. Returns an empty string when the parenthesis does not close
    /// on the same line.
    /// </summary>
    public static string ArgumentsAt(string line, int openIndex)
    {
        if (openIndex < 0 || openIndex >= line.Length || line[openIndex] != '(')
        {
            return string.Empty;
        }

        var depth = 0;
        var quote = '\0';
        for (var i = openIndex; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                quote = c;
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    var text = line[openIndex..(i + 1)];
                    return text.Length > 160 ? text[..160] + "..." : text;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Line index of the closing brace of the body that starts at or shortly
    /// after the declaration line, or -1 when no body was found.
    /// </summary>
    public static int FindClosingBrace(string[] lines, int declarationLine)
    {
        var depth = 0;
        var started = false;
        for (var i = declarationLine; i < lines.Length; i++)
        {
            var line = lines[i];
            var quote = '\0';
            for (var c = 0; c < line.Length; c++)
            {
                var ch = line[c];
                if (quote != '\0')
                {
                    if (ch == '\\')
                    {
                        c++;
                    }
                    else if (ch == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (ch is '"' or '\'' or '`')
                {
                    quote = ch;
                    continue;
                }

                if (ch == '/' && c + 1 < line.Length && line[c + 1] == '/')
                {
                    break;
                }

                if (ch == '{')
                {
                    depth++;
                    started = true;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (started && depth == 0)
                    {
                        return i;
                    }
                }
            }

            if (!started && i - declarationLine > 5)
            {
                break;
            }
        }

        return -1;
    }
}

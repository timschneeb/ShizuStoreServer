// Binary Android XML decoding adapted from ApkQuickReader.cs
// (alisakkaf/ApkShellext, MIT license; Copyright (c) 2015 Jingang Guo, KK
// and Copyright (c) 2026 AliSakkaF). Chunk walk, string pool, resource map,
// and typed values follow the reference; the tree lands in the same
// XmlTreeNode model the text parser uses, so renderers work off either.
namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>Indentation-based tree over <c>aapt2 dump xmltree</c> lines.</summary>
public sealed class XmlTreeNode
{
    public string Name { get; }
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);
    public List<XmlTreeNode> Children { get; } = [];

    public XmlTreeNode(string name) => Name = name;

    public static XmlTreeNode? ParseRoot(string dump)
    {
        XmlTreeNode? root = null;
        var stack = new Stack<(int Indent, XmlTreeNode Node)>();
        foreach (var rawLine in dump.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var indent = rawLine.Length - rawLine.TrimStart().Length;
            var text = line.TrimStart();
            if (text.StartsWith("E: ", StringComparison.Ordinal))
            {
                var name = text[3..].Split(' ')[0];
                var node = new XmlTreeNode(name);
                while (stack.Count > 0 && stack.Peek().Indent >= indent)
                {
                    stack.Pop();
                }

                if (stack.Count > 0)
                {
                    stack.Peek().Node.Children.Add(node);
                }
                else
                {
                    root ??= node;
                }

                stack.Push((indent, node));
            }
            else if (text.StartsWith("A: ", StringComparison.Ordinal) && stack.Count > 0)
            {
                // A: <ns>:<name>(0x...)="value" [(Raw: "...")], or bare name=value.
                var body = text[3..];
                string name;
                string value;
                var nameEnd = body.IndexOf('(');
                var eq = body.LastIndexOf('=');
                if (nameEnd > 0 && eq > nameEnd)
                {
                    name = body[..nameEnd].Split(':')[^1];
                    value = body[(eq + 1)..].Trim();
                    const string rawPrefix = " (Raw: \"";
                    var rawIndex = value.IndexOf(rawPrefix, StringComparison.Ordinal);
                    if (rawIndex >= 0 && value.EndsWith("\")", StringComparison.Ordinal))
                    {
                        value = value[(rawIndex + rawPrefix.Length)..^2];
                    }
                    else if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                    {
                        value = value[1..^1];
                    }
                }
                else
                {
                    eq = body.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    name = body[..eq].Trim().Split(':')[^1];
                    value = body[(eq + 1)..].Trim().Trim('"');
                }

                if (name.Length > 0)
                {
                    stack.Peek().Node.Attributes[name] = value;
                }
            }
        }

        return root;
    }
}

/// <summary>
/// Compiled Android XML (string pool + resource map + typed attributes)
/// decoded straight from APK bytes. Attribute names resolve through the
/// string pool, then a minimal android attribute table (only what icon
/// rendering consumes), else the attribute is skipped.
/// </summary>
public static class BinaryXml
{
    private static readonly Dictionary<uint, string> AndroidAttrs = new()
    {
        [0x01010002] = "icon",
        [0x01010155] = "height",
        [0x01010159] = "width",
        [0x01010199] = "drawable",
        [0x010102be] = "fillAlpha",
        [0x01010324] = "scaleX",
        [0x01010325] = "scaleY",
        [0x01010329] = "rotation",
        [0x0101032a] = "pivotX",
        [0x0101032b] = "pivotY",
        [0x010103e3] = "fillType",
        [0x01010402] = "viewportWidth",
        [0x01010403] = "viewportHeight",
        [0x01010404] = "fillColor",
        [0x01010405] = "pathData",
        [0x01010406] = "strokeColor",
        [0x01010407] = "strokeWidth",
        [0x0101045a] = "translateX",
        [0x0101045b] = "translateY",
        [0x0101045e] = "alpha",
        [0x01010533] = "startColor",
        [0x01010534] = "endColor",
        [0x01010535] = "startX",
        [0x01010536] = "startY",
        [0x01010537] = "endX",
        [0x01010538] = "endY",
        [0x01010539] = "centerX",
        [0x0101053a] = "centerY",
        [0x0101053b] = "gradientRadius",
        [0x0101053c] = "type",
        [0x0101053e] = "color",
        [0x0101053f] = "offset",
        [0x01010568] = "shape",
    };

    /// <returns>Document root, or null on truncated/foreign bytes.</returns>
    public static XmlTreeNode? Parse(byte[] bytes, byte[]? arsc = null)
    {
        try
        {
            return ParseCore(bytes, arsc);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static XmlTreeNode? ParseCore(byte[] bytes, byte[]? arsc)
    {
        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        var chunkType = (ResChunkType)br.ReadInt16();
        var headerSize = br.ReadInt16();
        _ = br.ReadInt32();
        if (chunkType != ResChunkType.Xml)
        {
            return null;
        }

        ms.Seek(headerSize, SeekOrigin.Begin);

        XmlTreeNode? root = null;
        var stack = new Stack<XmlTreeNode>();
        while (ms.Position < ms.Length)
        {
            var chunkPos = ms.Position;
            chunkType = (ResChunkType)br.ReadInt16();
            headerSize = br.ReadInt16();
            var chunkSize = br.ReadInt32();
            ms.Seek(chunkPos + headerSize, SeekOrigin.Begin);

            if (chunkType == ResChunkType.XmlStartElement)
            {
                _ = br.ReadUInt32(); // namespace URI index, unused
                var name = AttrName(bytes, br.ReadUInt32());
                var node = new XmlTreeNode(name);
                if (stack.Count > 0)
                {
                    stack.Peek().Children.Add(node);
                }
                else
                {
                    root ??= node;
                }

                stack.Push(node);

                var attributeStart = br.ReadInt16();
                var attributeSize = br.ReadInt16();
                var attributeCount = br.ReadInt16();
                _ = br.ReadInt16(); // id/class/style index block, unused
                _ = br.ReadInt16();
                _ = br.ReadInt16();
                for (var i = 0; i < attributeCount; i++)
                {
                    // +4 skips the namespace index; layout mirrors the reference.
                    ms.Seek(chunkPos + offset(headerSize, attributeStart, attributeSize, i), SeekOrigin.Begin);
                    var nameId = br.ReadUInt32();
                    var attrName = AttrName(bytes, nameId);
                    ms.Seek(4 + 2 + 1, SeekOrigin.Current); // rawValue, size, res0
                    var dataType = br.ReadByte();
                    var data = br.ReadUInt32();
                    if (attrName.Length > 0)
                    {
                        node.Attributes[attrName] = ConvertValue(bytes, arsc, (ResValueType)dataType, data);
                    }
                }
            }
            else if (chunkType == ResChunkType.XmlEndElement)
            {
                if (stack.Count > 0)
                {
                    stack.Pop();
                }
            }

            ms.Seek(chunkPos + chunkSize, SeekOrigin.Begin);

            static long offset(int headerSize, int attributeStart, int attributeSize, int i) =>
                headerSize + attributeStart + attributeSize * i + 4;
        }

        return root;
    }

    private static string AttrName(byte[] xml, uint id)
    {
        if (id == 0xffffffff)
        {
            return string.Empty;
        }

        var pooled = ApkStringPool.GetString(xml, id);
        if (pooled.Length > 0)
        {
            return pooled;
        }

        return ResourceMapName(xml, id);
    }

    private static string ResourceMapName(byte[] xml, uint index)
    {
        using var ms = new MemoryStream(xml);
        using var br = new BinaryReader(ms);
        _ = br.ReadInt16();
        var headerSize = br.ReadInt16();
        ms.Seek(headerSize, SeekOrigin.Begin);

        while (ms.Position < ms.Length)
        {
            var chunkPos = ms.Position;
            var chunkType = (ResChunkType)br.ReadInt16();
            _ = br.ReadInt16();
            var chunkSize = br.ReadInt32();
            if (chunkType == ResChunkType.XmlResourceMap)
            {
                ms.Seek(index * 4, SeekOrigin.Current);
                var attrId = br.ReadUInt32();
                return AndroidAttrs.TryGetValue(attrId, out var name) ? name : string.Empty;
            }

            ms.Seek(chunkPos + chunkSize, SeekOrigin.Begin);
        }

        return string.Empty;
    }

    private static string ConvertValue(byte[] xml, byte[]? arsc, ResValueType type, uint data)
    {
        if (type == ResValueType.Reference && arsc is not null)
        {
            // One step; config ranking (versioned XML, then density raster,
            // then default) happens inside ResolveString, like on device.
            var resolved = ApkResourceTable.ResolveString(arsc, data, allowXml: true);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return ApkTypedValue.Convert(xml, type, data, id =>
            arsc is null ? $"(0x{id:X8})" : ApkResourceTable.ResolveString(arsc, id, allowXml: true) ?? $"(0x{id:X8})");
    }
}

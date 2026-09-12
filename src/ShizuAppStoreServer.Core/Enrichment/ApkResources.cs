// Resource table + typed-value decoding adapted from ApkQuickReader.cs
// (alisakkaf/ApkShellext, MIT license; Copyright (c) 2015 Jingang Guo, KK
// and Copyright (c) 2026 AliSakkaF). Windows-only pieces (GDI+, WPF,
// SharpZipLib, WebPWrapper) are replaced with BCL/ImageSharp equivalents;
// the chunk layouts, string-pool rules, density selection, and loop guards
// follow the reference.
using System.Text;

namespace ShizuAppStoreServer.Core.Enrichment;

public enum ResChunkType : ushort
{
    Null = 0x0000,
    StringPool = 0x0001,
    Table = 0x0002,
    Xml = 0x0003,
    XmlStartNamespace = 0x0100,
    XmlEndNamespace = 0x0101,
    XmlStartElement = 0x0102,
    XmlEndElement = 0x0103,
    XmlCdata = 0x0104,
    XmlResourceMap = 0x0180,
    TablePackage = 0x0200,
    TableType = 0x0201,
    TableTypeSpec = 0x0202,
}

public enum ResValueType : byte
{
    Null = 0x00,
    Reference = 0x01,
    Attribute = 0x02,
    String = 0x03,
    Float = 0x04,
    Dimension = 0x05,
    Fraction = 0x06,
    IntDec = 0x10,
    IntHex = 0x11,
    IntBoolean = 0x12,
    IntColorArgb8 = 0x1c,
    IntColorRgb8 = 0x1d,
    IntColorArgb4 = 0x1e,
    IntColorRgb4 = 0x1f,
}

/// <summary>Android binary string pool (shared by *.arsc and compiled XML).</summary>
public static class ApkStringPool
{
    public static string GetString(byte[] bytes, uint id)
    {
        if (id == 0xffffffff)
        {
            return string.Empty;
        }

        using var ms = new MemoryStream(bytes);
        using var br = new BinaryReader(ms);
        var chunkType = (ResChunkType)br.ReadInt16();
        var headerSize = br.ReadInt16();
        ms.Seek(headerSize, SeekOrigin.Begin);

        while (ms.Position < ms.Length)
        {
            var chunkPos = ms.Position;
            chunkType = (ResChunkType)br.ReadInt16();
            headerSize = br.ReadInt16();
            var chunkSize = br.ReadInt32();
            if (chunkType == ResChunkType.StringPool)
            {
                var stringCount = br.ReadInt32();
                if (id >= stringCount)
                {
                    return string.Empty;
                }

                ms.Seek(4, SeekOrigin.Current); // styles, unused
                var flags = br.ReadInt32();
                var isUtf8 = (flags & (1 << 8)) != 0;
                var stringsStart = br.ReadInt32();
                ms.Seek(4 + (long)id * 4, SeekOrigin.Current);
                var stringOffset = br.ReadInt32();
                ms.Seek(chunkPos + stringsStart + stringOffset, SeekOrigin.Begin);
                if (isUtf8)
                {
                    var u16Length = (int)br.ReadByte();
                    if ((u16Length & 0x80) != 0)
                    {
                        u16Length = ((u16Length & 0x7F) << 8) + br.ReadByte();
                    }

                    var u8Length = (int)br.ReadByte();
                    if ((u8Length & 0x80) != 0)
                    {
                        u8Length = ((u8Length & 0x7F) << 8) + br.ReadByte();
                    }

                    return Encoding.UTF8.GetString(br.ReadBytes(u8Length));
                }
                else
                {
                    var u16Length = br.ReadUInt16();
                    if ((u16Length & 0x8000) != 0)
                    {
                        u16Length = (ushort)(((u16Length & 0x7FFF) << 16) + br.ReadUInt16());
                    }

                    return Encoding.Unicode.GetString(br.ReadBytes(u16Length * 2));
                }
            }

            ms.Seek(chunkPos + chunkSize, SeekOrigin.Begin);
        }

        return string.Empty;
    }
}

/// <summary>
/// Typed resource values (mirrors the reference convertData): strings via
/// pool, references resolved recursively, colors as #aarrggbb, numbers raw.
/// </summary>
public static class ApkTypedValue
{
    public static string Convert(byte[] poolBytes, ResValueType type, uint data, Func<uint, string>? resolveReference = null)
    {
        switch (type)
        {
            case ResValueType.String:
                return ApkStringPool.GetString(poolBytes, data);
            case ResValueType.Reference:
                return resolveReference is not null ? resolveReference(data) : $"(0x{data:X8})";
            case ResValueType.IntBoolean:
                return data == 0 ? "false" : "true";
            case ResValueType.Float:
                return BitConverter.ToSingle(BitConverter.GetBytes(data), 0)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            case ResValueType.Dimension:
                return DecodeDimension(data);
            case ResValueType.Fraction:
                return Math.Round(DecodeComplex(data) * 100, 4)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
            case ResValueType.IntColorArgb8:
            case ResValueType.IntColorRgb8:
            case ResValueType.IntColorArgb4:
            case ResValueType.IntColorRgb4:
                return $"#{data:X8}";
            default:
                return data.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Android packed complex: 4-bit unit, 2-bit radix, signed 23-bit
    /// mantissa. Mirrors android.util.TypedValue.complexToFloat.
    /// </summary>
    public static float DecodeComplex(uint data)
    {
        var radix = (int)((data >> 4) & 0x3);
        var mantissa = (int)(data >> 8);
        if ((mantissa & 0x800000) != 0)
        {
            mantissa |= unchecked((int)0xFF000000); // sign-extend 23 bits
        }

        return mantissa * RadixMultiplier(radix);

        static float RadixMultiplier(int radix) => radix switch
        {
            0 => 1f,
            1 => 1f / 256,
            2 => 1f / 65536,
            _ => 1f / 16777216,
        };
    }

    private static string DecodeDimension(uint data)
    {
        var value = DecodeComplex(data);
        var unit = data & 0xf;
        var unitName = unit switch
        {
            0 => "px",
            1 => "dp",
            2 => "sp",
            3 => "pt",
            4 => "in",
            5 => "mm",
            _ => string.Empty,
        };
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture) + unitName;
    }

}

/// <summary>
/// resources.arsc lookup with density selection (mirrors QuickSearchResource
/// + applyFilter): collects every config of an entry, then picks the best
/// density, skipping XML files unless explicitly allowed. Reference loops
/// are guarded; only string/reference/integer payloads are interpreted.
/// </summary>
public static class ApkResourceTable
{
    public sealed record Entry(byte[] Config, ResValueType DataType, uint Data);

    // Density lives at config bytes 10-11 (uint16, ResTable_config layout
    // minus the leading size field), matching the reference indices.
    // SdkVersion follows at bytes 20-21 (verified against real tables:
    // anydpi-v26 entries read density 65534 + sdk 26 there).
    private const int DensityOffset = 10;
    private const int SdkVersionOffset = 20;

    private static int SdkVersion(Entry entry) =>
        entry.Config.Length > SdkVersionOffset + 1
            ? entry.Config[SdkVersionOffset] + 256 * entry.Config[SdkVersionOffset + 1]
            : 0;

    public static IReadOnlyList<Entry> Lookup(byte[] arsc, uint id)
    {
        var result = new List<Entry>();
        var packageId = (id & 0xff000000) >> 24;
        var typeId = (id & 0x00ff0000) >> 16;
        var entryId = id & 0x0000ffff;

        using var ms = new MemoryStream(arsc);
        using var br = new BinaryReader(ms);
        try
        {
            ms.Seek(8, SeekOrigin.Begin); // table header
            var packageCount = br.ReadInt32();
            ms.Seek(4, SeekOrigin.Current); // string pool tag
            var poolSize = br.ReadInt32();
            ms.Seek(poolSize - 8, SeekOrigin.Current); // end of pool chunk

            for (var pack = 0; pack < packageCount; pack++)
            {
                var packPos = ms.Position;
                ms.Seek(2, SeekOrigin.Current); // chunk type
                var packHeaderSize = br.ReadInt16();
                var packSize = br.ReadInt32();
                var packId = br.ReadInt32();
                if (packId != packageId)
                {
                    ms.Seek(packPos + packSize, SeekOrigin.Begin);
                    continue;
                }

                ms.Seek(packPos + packHeaderSize, SeekOrigin.Begin);
                SkipChunk(br, ms); // type strings
                SkipChunk(br, ms); // key strings

                while (ms.Position < packPos + packSize)
                {
                    var chunkPos = ms.Position;
                    var chunkType = (ResChunkType)br.ReadInt16();
                    var headerSize = br.ReadInt16();
                    var chunkSize = br.ReadInt32();
                    if (chunkType == ResChunkType.TableType && br.ReadByte() == typeId)
                    {
                        ms.Seek(3, SeekOrigin.Current); // res0/res1
                        var entryCount = br.ReadInt32();
                        var entriesStart = br.ReadInt32();
                        var configSize = br.ReadInt32();
                        var config = br.ReadBytes(configSize - 4);
                        if (entryId < entryCount)
                        {
                            ms.Seek(chunkPos + headerSize + (long)entryId * 4, SeekOrigin.Begin);
                            var entryOffset = br.ReadUInt32();
                            if (entryOffset != 0xffffffff)
                            {
                                ms.Seek(chunkPos + entriesStart + entryOffset, SeekOrigin.Begin);
                                ms.Seek(11, SeekOrigin.Current); // entry header + value prefix
                                var dataType = br.ReadByte();
                                var data = br.ReadUInt32();
                                result.Add(new Entry(config, (ResValueType)dataType, data));
                            }
                        }
                    }

                    ms.Seek(chunkPos + chunkSize, SeekOrigin.Begin);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Truncated table: return whatever resolved so far.
        }
        catch (IOException)
        {
            // Same: best effort, like the reference.
        }

        return result;
    }

    private static void SkipChunk(BinaryReader br, MemoryStream ms)
    {
        ms.Seek(4, SeekOrigin.Current); // tag + header size
        ms.Seek(br.ReadInt32() - 8, SeekOrigin.Current);
    }

    /// <returns>Device-faithful file pick for layer refs. A
    /// version-qualified XML (anydpi-v26 adaptive icons) wins on modern
    /// devices, then an unversioned anydpi XML (the qualifier exists to be
    /// density-independent, and launcher authors ship it as the icon),
    /// then the best-density raster (an exact density match beats a
    /// default-config fallback on every device), then the default XML.
    /// Plain XML-always-first rendered dead fallbacks (a default vector
    /// instead of the density PNGs every real device picks).</returns>
    public static string? ResolveString(
        byte[] arsc, uint id, bool allowXml, HashSet<uint>? seen = null) =>
        allowXml
            ? ResolveVersionedXml(arsc, id, seen)
                ?? ResolveAnyDpiXml(arsc, id, seen)
                ?? ResolveRaster(arsc, id, seen)
                ?? ResolveXml(arsc, id, seen)
            : ResolveRaster(arsc, id, seen);

    /// <summary>Best-density XML file reference, or null. Kept XML-first:
    /// manifest-icon roots use this directly because there the version
    /// qualifier genuinely wins on modern launchers.</summary>
    public static string? ResolveXml(byte[] arsc, uint id, HashSet<uint>? seen = null) =>
        SelectBest(arsc, id, xmlOnly: true, seen);

    /// <summary>XML file reference with a nonzero SDK version qualifier
    /// (anydpi-v26 and friends), or null.</summary>
    public static string? ResolveVersionedXml(byte[] arsc, uint id, HashSet<uint>? seen = null) =>
        SelectBest(arsc, id, xmlOnly: true, seen, versionedOnly: true);

    /// <summary>XML file reference with the anydpi density (unversioned
    /// adaptive icons like HyperBridge's), or null. anydpi is
    /// density-independent by design, so it outranks density rasters;
    /// default-config XMLs stay below rasters (dead fallbacks).</summary>
    public static string? ResolveAnyDpiXml(byte[] arsc, uint id, HashSet<uint>? seen = null) =>
        SelectBest(arsc, id, xmlOnly: true, seen, anyDpiOnly: true);

    /// <summary>Best-density non-XML value (raster file, color, number), or null.</summary>
    public static string? ResolveRaster(byte[] arsc, uint id, HashSet<uint>? seen = null) =>
        SelectBest(arsc, id, xmlOnly: false, seen);

    private static string? SelectBest(
        byte[] arsc, uint id, bool xmlOnly, HashSet<uint>? seen = null, bool versionedOnly = false, bool anyDpiOnly = false)
    {
        seen ??= [];
        if (!seen.Add(id))
        {
            return null; // reference loop
        }

        string? best = null;
        var bestDensity = -1;
        var bestSdk = -1;
        foreach (var entry in Lookup(arsc, id))
        {
            string? value = entry.DataType switch
            {
                ResValueType.String => ApkStringPool.GetString(arsc, entry.Data),
                ResValueType.Reference => entry.Data == 0
                    ? null
                    : SelectBest(arsc, entry.Data, xmlOnly, seen),
                ResValueType.IntColorArgb8 or ResValueType.IntColorRgb8
                    or ResValueType.IntColorArgb4 or ResValueType.IntColorRgb4
                    => $"#{entry.Data:X8}",
                ResValueType.IntDec or ResValueType.IntHex or ResValueType.IntBoolean
                    => entry.Data.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => null,
            };
            if (value is null)
            {
                continue;
            }

            var isXml = value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            if (isXml != xmlOnly)
            {
                continue;
            }

            // A version qualifier outranks density on device; our render
            // target is always modern, so any nonzero SDK version matches.
            if (versionedOnly && SdkVersion(entry) == 0)
            {
                continue;
            }

            var density = entry.Config.Length > DensityOffset + 1
                ? entry.Config[DensityOffset + 1] * 256 + entry.Config[DensityOffset]
                : 0;
            // anydpi (0xFFFE) is density-independent by design.
            if (anyDpiOnly && density != 0xFFFE)
            {
                continue;
            }
            // Density ties break toward the higher SDK version: the render
            // target is always modern, and on device a matching higher
            // version wins over the default config.
            var sdk = SdkVersion(entry);
            if (density > bestDensity || (density == bestDensity && sdk > bestSdk))
            {
                bestDensity = density;
                bestSdk = sdk;
                best = value;
            }
        }

        // No app entry: framework colors (android.R.color ids are frozen
        // public API) inline as literals. Never XML, so XML-only picks
        // stay null and every other framework type stays unstageable.
        if (best is null && !xmlOnly && (id >> 24) == 0x01
            && FrameworkColors.TryGet(id, out var framework))
        {
            return framework;
        }

        return best;
    }
}

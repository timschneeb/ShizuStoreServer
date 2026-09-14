using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ShizuAppStoreServer.Core.Enrichment;

/// <summary>
/// Deterministic letter-avatar fallback (192px PNG) for entries with no
/// extractable APK icon: non-GitHub sources, missing releases, adaptive-icon
/// XML with no raster layer. Self-contained 5×7 pixel font, no system-font
/// dependency, so output is identical on dev machines and the server.
/// </summary>
public static class LetterAvatarGenerator
{
    public const int Size = 192;

    // 5×7 glyphs, '#' = foreground. Covers A–Z, 0–9, '?'.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['B'] = ["####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."],
        ['C'] = [".####", "#....", "#....", "#....", "#....", "#....", ".####"],
        ['D'] = ["####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####."],
        ['E'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#####"],
        ['F'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#...."],
        ['G'] = [".####", "#....", "#....", "#.###", "#...#", "#...#", ".###."],
        ['H'] = ["#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['I'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####"],
        ['J'] = ["..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.."],
        ['K'] = ["#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#"],
        ['L'] = ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
        ['M'] = ["#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#"],
        ['N'] = ["#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#"],
        ['O'] = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['P'] = ["####.", "#...#", "#...#", "####.", "#....", "#....", "#...."],
        ['Q'] = [".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#"],
        ['R'] = ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
        ['S'] = [".####", "#....", "#....", ".###.", "....#", "....#", "####."],
        ['T'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
        ['U'] = ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['V'] = ["#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.."],
        ['W'] = ["#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#"],
        ['X'] = ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
        ['Y'] = ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
        ['Z'] = ["#####", "....#", "...#.", "..#..", ".#...", "#....", "#####"],
        ['0'] = [".###.", "#..##", "#.#.#", "#.#.#", "##..#", "#...#", ".###."],
        ['1'] = ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ['3'] = ["####.", "....#", "....#", ".###.", "....#", "....#", "####."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        ['5'] = ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
        ['6'] = [".###.", "#....", "#....", "####.", "#...#", "#...#", ".###."],
        ['7'] = ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
        ['8'] = [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
        ['9'] = [".###.", "#...#", "#...#", ".####", "....#", "....#", ".###."],
        ['?'] = [".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.."],
    };

    /// <returns>PNG bytes + sha256 of those bytes (the icon-store filename).</returns>
    public static ProcessedIcon Generate(string appName)
    {
        var letter = PickLetter(appName);
        var background = BackgroundFor(appName);
        var foreground = new Rgba32(255, 255, 255);

        using var image = new Image<Rgba32>(Size, Size, background);

        // Scale the 5×7 glyph to fill ~60% of the canvas.
        const int scale = 16; // 5*16=80 wide, 7*16=112 tall
        var offsetX = (Size - 5 * scale) / 2;
        var offsetY = (Size - 7 * scale) / 2;
        var glyph = Glyphs[letter];
        for (var row = 0; row < 7; row++)
        {
            for (var col = 0; col < 5; col++)
            {
                if (glyph[row][col] != '#')
                {
                    continue;
                }

                for (var y = 0; y < scale; y++)
                {
                    for (var x = 0; x < scale; x++)
                    {
                        image[offsetX + col * scale + x, offsetY + row * scale + y] = foreground;
                    }
                }
            }
        }

        using var png = new MemoryStream();
        image.Save(png, new PngEncoder());
        var bytes = png.ToArray();
        return new ProcessedIcon(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
    }

    /// <summary>First displayable glyph for a name (uppercased letter/digit, else <c>'?'</c>).</summary>
    public static char PickLetter(string appName)
    {
        foreach (var ch in appName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                var upper = char.ToUpperInvariant(ch);
                return Glyphs.ContainsKey(upper) ? upper : '?';
            }
        }

        return '?';
    }

    internal static Rgba32 BackgroundFor(string appName)
    {
        // Stable hue from the name hash; fixed saturation/lightness keep
        // white glyphs readable on every background.
        var hue = SHA256.HashData(Encoding.UTF8.GetBytes($"shizu-avatar:{appName}"))[0] * 360.0 / 256;
        var (r, g, b) = HslToRgb(hue, 0.52, 0.42);
        return new Rgba32(r, g, b);
    }

    private static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        var m = l - c / 2;
        var (r1, g1, b1) = (h / 60) switch
        {
            < 1 => (c, x, 0d),
            < 2 => (x, c, 0d),
            < 3 => (0d, c, x),
            < 4 => (0d, x, c),
            < 5 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return ((byte)Math.Round((r1 + m) * 255), (byte)Math.Round((g1 + m) * 255), (byte)Math.Round((b1 + m) * 255));
    }
}

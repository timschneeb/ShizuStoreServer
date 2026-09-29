using System.Globalization;

namespace ShizuAppStoreServer.Web.Mapping;

/// <summary>Human-readable formatting for page values.</summary>
public static class Display
{
    public static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    public static string Count(long? value) =>
        value is { } number ? number.ToString("N0", CultureInfo.InvariantCulture) : "-";

    public static string Date(DateTimeOffset? value) =>
        value is { } moment ? moment.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-";

    public static string Bytes(long? value) => value is { } bytes && bytes > 0 ? FormatBytes(bytes) : "-";

    public static string Sdk(int? api) => api is { } level && level > 0 ? $"API {level}" : "-";

    private static readonly Dictionary<int, string> AndroidVersions = new()
    {
        [1] = "1.0", [2] = "1.1", [3] = "1.5", [4] = "1.6", [5] = "2.0", [6] = "2.0.1", [7] = "2.1",
        [8] = "2.2", [9] = "2.3", [10] = "2.3.3", [11] = "3.0", [12] = "3.1", [13] = "3.2", [14] = "4.0",
        [15] = "4.0.3", [16] = "4.1", [17] = "4.2", [18] = "4.3", [19] = "4.4", [20] = "4.4W",
        [21] = "5.0", [22] = "5.1", [23] = "6.0", [24] = "7.0", [25] = "7.1", [26] = "8.0", [27] = "8.1",
        [28] = "9", [29] = "10", [30] = "11", [31] = "12", [32] = "12L", [33] = "13", [34] = "14",
        [35] = "15", [36] = "16", [37] = "17",
    };

    /// <summary>Minimum Android version label, mirroring the client's AndroidVersion map.</summary>
    public static string AndroidRequirement(int? api)
    {
        if (api is not { } level || level <= 0)
        {
            return "-";
        }

        return AndroidVersions.TryGetValue(level, out var name)
            ? $"Android {name}+"
            : $"API {level}+";
    }

    /// <summary>Compact count mirroring the client's formatCount (1.2k, 3M).</summary>
    public static string CompactCount(long? value) => value is { } number ? FormatCount(number) : "-";

    /// <summary>
    /// Integer-truncated SI size mirroring the client's sizeLabel (12 MB, no
    /// decimals); values at or below 1 byte have no label.
    /// </summary>
    public static string SizeLabel(long? value) => value is { } bytes && bytes > 1 ? FormatSi(bytes) : "-";

    /// <summary>Relative update age mirroring the client's relativeAge strings.</summary>
    public static string RelativeAge(DateTimeOffset? value) =>
        value is { } moment ? FormatAge(DateTimeOffset.UtcNow - moment) : "-";

    private static string FormatCount(long value)
    {
        if (value < 1000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        string[] units = ["k", "M", "B"];
        double scaled = value;
        var unit = -1;
        while (scaled >= 1000 && unit < units.Length - 1)
        {
            scaled /= 1000;
            unit++;
        }

        var text = scaled.ToString("0.0", CultureInfo.InvariantCulture);
        if (text.EndsWith(".0", StringComparison.Ordinal))
        {
            text = text[..^2];
        }

        return text + units[unit];
    }

    private static string FormatSi(long value)
    {
        string[] prefixes = ["", " KB", " MB", " GB"];
        var scaled = value;
        var index = 0;
        while (scaled >= 1000 && index < prefixes.Length - 1)
        {
            scaled /= 1000;
            index++;
        }

        return scaled.ToString(CultureInfo.InvariantCulture) + prefixes[index];
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero || age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return Plural((int)age.TotalMinutes, "minute");
        }

        if (age < TimeSpan.FromDays(1))
        {
            return Plural((int)age.TotalHours, "hour");
        }

        if (age < TimeSpan.FromDays(7))
        {
            return Plural((int)age.TotalDays, "day");
        }

        if (age < TimeSpan.FromDays(30))
        {
            return Plural((int)(age.TotalDays / 7), "week");
        }

        if (age < TimeSpan.FromDays(365))
        {
            return Plural(Math.Min((int)(age.TotalDays / 30), 11), "month");
        }

        return Plural((int)(age.TotalDays / 365), "year");
    }

    private static string Plural(int count, string unit) =>
        count == 1 ? $"{count} {unit} ago" : $"{count} {unit}s ago";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{size.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

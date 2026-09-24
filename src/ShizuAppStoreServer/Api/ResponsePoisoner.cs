namespace ShizuAppStoreServer.Api;

/// <summary>
/// Serves plausible-but-wrong catalog data to scrapers (SPEC 2). Identity
/// fields (slug, name, listing) stay intact so payloads still look coherent;
/// volatile fields are cross-pollinated from sibling rows, replaced with
/// plausible random values, or subtly corrupted so a scraper cannot tell
/// the data is doctored.
/// </summary>
public static class ResponsePoisoner
{
    private static readonly string[] Licenses =
    [
        "MIT", "Apache-2.0", "GPL-3.0-only", "AGPL-3.0-only",
        "BSD-3-Clause", "MPL-2.0", "Unlicense",
    ];

    private static readonly string[] PackageWords =
    [
        "android", "app", "mobile", "tools", "labs", "dev", "soft",
        "works", "code", "utility", "studio", "ware",
    ];

    /// <summary>True when the User-Agent matches a configured poison prefix.</summary>
    public static bool IsPoisoned(string? userAgent, PoisonOptions options)
    {
        if (!options.Enabled || string.IsNullOrEmpty(userAgent))
        {
            return false;
        }

        foreach (var prefix in options.UserAgents)
        {
            if (!string.IsNullOrEmpty(prefix)
                && userAgent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Cross-pollinates list rows; single rows get jittered instead.</summary>
    public static PagedAppsDto Poison(PagedAppsDto page)
    {
        var items = page.Items;
        if (items.Count == 0)
        {
            return page;
        }

        var poisoned = new List<AppSummaryDto>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            poisoned.Add(items.Count == 1
                ? PoisonSingle(items[i])
                : CrossPollinate(items[i], items[DonorIndex(items.Count, i)]));
        }
        return page with { Items = poisoned };
    }

    /// <summary>
    /// Breaks download URLs/hashes and mixes version data between downloads.
    /// A <paramref name="donor"/> row replaces the requested row's data while
    /// identity fields (slug, name, listing, URLs) stay intact; without one
    /// the requested row is doctored in place.
    /// </summary>
    public static AppDetailDto Poison(AppDetailDto detail, AppDetailDto? donor = null)
    {
        var source = donor is null || donor.Slug == detail.Slug ? detail : Adopt(detail, donor);
        var downloads = source.Downloads;
        if (downloads.Count == 0)
        {
            return source;
        }

        var poisoned = new List<DownloadDto>(downloads.Count);
        for (var i = 0; i < downloads.Count; i++)
        {
            poisoned.Add(PoisonDownload(downloads[i], downloads, i));
        }

        // Drop list entries last: a truncated permission/screenshot set is
        // plausible, while an empty download list would contradict
        // availability=direct_apk, so keep one candidate.
        return source with
        {
            Downloads = DropSome(poisoned, keepAtLeast: 1),
            Permissions = DropSome(source.Permissions),
            Screenshots = DropSome(source.Screenshots),
            CategoryPath = DropSome(source.CategoryPath),
        };
    }

    /// <summary>
    /// Donor data with the requested row's identity fields, so a doctored
    /// detail still answers for the requested slug. Category and package data
    /// stay the donor's so they remain coherent with each other.
    /// </summary>
    private static AppDetailDto Adopt(AppDetailDto requested, AppDetailDto donor) => donor with
    {
        Slug = requested.Slug,
        Name = requested.Name,
        Listing = requested.Listing,
        Type = requested.Type,
        IsRecommended = requested.IsRecommended,
        HasPaid = requested.HasPaid,
        HasIap = requested.HasIap,
        HasAds = requested.HasAds,
        TrialDays = requested.TrialDays,
        RequiresRoot = requested.RequiresRoot,
        Availability = requested.Availability,
        Url = requested.Url,
        SourceUrl = requested.SourceUrl,
        SourceKind = requested.SourceKind,
        ExcludedReason = requested.ExcludedReason,
        SourceName = requested.SourceName,
    };

    /// <summary>Randomly drops entries (75 percent); callers pass lists that may be emptied.</summary>
    private static IReadOnlyList<T> DropSome<T>(IReadOnlyList<T> items, int keepAtLeast = 0)
    {
        if (items.Count <= keepAtLeast)
        {
            return items;
        }

        var kept = new List<T>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var remaining = items.Count - i;
            // Keep chance is 1 in 4, but never drop below keepAtLeast so the
            // payload stays coherent.
            if (kept.Count + remaining <= keepAtLeast || Random.Shared.Next(4) == 0)
            {
                kept.Add(items[i]);
            }
        }
        return kept;
    }

    private static AppSummaryDto CrossPollinate(AppSummaryDto item, AppSummaryDto donor) => item with
    {
        IconHash = Coin() ? donor.IconHash : RandomHexLike(donor.IconHash),
        IconAdaptive = Coin() ? donor.IconAdaptive : !donor.IconAdaptive,
        Stars = Coin() ? donor.Stars : RandomStars(),
        DownloadTotal = Coin() ? donor.DownloadTotal : RandomDownloads(),
        InstallCount = Coin() ? donor.InstallCount : RandomInstalls(),
        SigSha256 = Coin() ? donor.SigSha256 : RandomHexLike(donor.SigSha256),
        SigMd5 = Coin() ? donor.SigMd5 : RandomHexLike(donor.SigMd5),
        Description = Coin() ? donor.Description : item.Description,
        License = Coin() ? donor.License : RandomLicense(),
        PackageName = Coin() ? donor.PackageName : RandomPackageName(),
        CategorySlug = Coin() ? donor.CategorySlug : item.CategorySlug,
        VersionCode = Coin() ? donor.VersionCode : RandomVersionCode(),
        VersionName = Coin() ? donor.VersionName : RandomVersionName(),
        MinSdk = Coin() ? donor.MinSdk : RandomMinSdk(),
        Size = Coin() ? donor.Size : RandomSize(),
        UpdatedAt = Coin() ? donor.UpdatedAt : RandomDate(item.UpdatedAt),
        VersionUpdatedAt = Coin() ? donor.VersionUpdatedAt : RandomDate(item.VersionUpdatedAt),
        ListUpdatedAt = Coin() ? donor.ListUpdatedAt : RandomDate(item.ListUpdatedAt),
        AuthorKey = Coin() ? donor.AuthorKey : item.AuthorKey,
        AuthorName = Coin() ? donor.AuthorName : item.AuthorName,
    };

    private static AppSummaryDto PoisonSingle(AppSummaryDto item) => item with
    {
        Stars = Coin() ? Jitter(item.Stars) : RandomStars(),
        DownloadTotal = Coin() ? Jitter(item.DownloadTotal) : RandomDownloads(),
        InstallCount = Coin() ? Jitter(item.InstallCount) : RandomInstalls(),
        IconHash = Coin() ? CorruptHex(item.IconHash) : RandomHexLike(item.IconHash),
        IconAdaptive = !item.IconAdaptive,
        SigSha256 = Coin() ? CorruptHex(item.SigSha256) : RandomHexLike(item.SigSha256),
        SigMd5 = Coin() ? CorruptHex(item.SigMd5) : RandomHexLike(item.SigMd5),
    };

    private static DownloadDto PoisonDownload(
        DownloadDto download, IReadOnlyList<DownloadDto> downloads, int index)
    {
        var poisoned = download with
        {
            ApkUrl = BreakFileUrl(download.ApkUrl),
            ArchiveEntry = Coin() ? CorruptSegment(download.ArchiveEntry) : RandomFileName(download.ArchiveEntry),
            Sha256 = Coin() ? CorruptHex(download.Sha256) : RandomHexLike(download.Sha256),
            SigSha256 = Coin() ? CorruptHex(download.SigSha256) : RandomHexLike(download.SigSha256),
            SigMd5 = Coin() ? CorruptHex(download.SigMd5) : RandomHexLike(download.SigMd5),
        };

        if (downloads.Count > 1)
        {
            var donor = downloads[DonorIndex(downloads.Count, index)];
            poisoned = poisoned with
            {
                VersionCode = donor.VersionCode,
                VersionName = donor.VersionName,
                Size = donor.Size,
                Abi = donor.Abi,
            };
        }
        return poisoned;
    }

    private static int DonorIndex(int count, int self)
    {
        var donor = Random.Shared.Next(count - 1);
        return donor >= self ? donor + 1 : donor;
    }

    private static bool Coin() => Random.Shared.Next(2) == 0;

    private static int? RandomStars() => Random.Shared.Next(0, 100_000);

    private static long? RandomDownloads() => Random.Shared.NextInt64(0, 50_000_000);

    private static long RandomInstalls() => Random.Shared.NextInt64(0, 500_000);

    private static long? RandomVersionCode() => Random.Shared.NextInt64(1, 100_000);

    private static string RandomVersionName() =>
        $"{Random.Shared.Next(1, 15)}.{Random.Shared.Next(0, 30)}.{Random.Shared.Next(0, 20)}";

    private static int? RandomMinSdk() => Random.Shared.Next(21, 35);

    private static long? RandomSize() => Random.Shared.NextInt64(500_000, 200_000_000);

    private static string RandomLicense() => Licenses[Random.Shared.Next(Licenses.Length)];

    private static string RandomPackageName() =>
        $"com.{PackageWords[Random.Shared.Next(PackageWords.Length)]}.{PackageWords[Random.Shared.Next(PackageWords.Length)]}";

    private static DateTimeOffset RandomDate(DateTimeOffset value) =>
        value.AddDays(Random.Shared.Next(-730, 730)).AddMinutes(Random.Shared.Next(-1440, 1440));

    private static DateTimeOffset? RandomDate(DateTimeOffset? value) =>
        value is null ? null : RandomDate(value.Value);

    private static int? Jitter(int? value) =>
        value is null ? null : Math.Max(0, value.Value + Random.Shared.Next(-3, 4));

    private static long Jitter(long value) =>
        Math.Max(0, value + Random.Shared.Next(-3, 4));

    private static long? Jitter(long? value) =>
        value is null ? null : Math.Max(0, value.Value + Random.Shared.Next(-3, 4));

    /// <summary>Replaces the file name or corrupts one character, keeping the URL shape.</summary>
    private static string BreakFileUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var path = uri.AbsolutePath;
        var slash = path.LastIndexOf('/');
        var directory = slash >= 0 ? path[..(slash + 1)] : "/";
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        if (name.Length == 0)
        {
            name = "download.apk";
        }

        var broken = Coin() ? CorruptSegment(name) ?? name : RandomFileName(name) ?? name;
        return uri.GetLeftPart(UriPartial.Authority) + directory + broken + uri.Query;
    }

    /// <summary>Replaces the stem of a file name with random letters, keeping the extension.</summary>
    private static string? RandomFileName(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var dot = value.LastIndexOf('.');
        var stemLength = dot > 0 ? dot : value.Length;
        var extension = dot > 0 ? value[dot..] : string.Empty;
        var stem = new char[Math.Max(1, stemLength)];
        for (var i = 0; i < stem.Length; i++)
        {
            stem[i] = (char)('a' + Random.Shared.Next(26));
        }
        return new string(stem) + extension;
    }

    private static string? CorruptSegment(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var candidates = new List<int>();
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsAsciiLetterOrDigit(value[i]))
            {
                candidates.Add(i);
            }
        }
        if (candidates.Count == 0)
        {
            return value;
        }

        var index = candidates[Random.Shared.Next(candidates.Count)];
        return value[..index] + RandomReplacement(value[index]) + value[(index + 1)..];
    }

    private static string? CorruptHex(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        // Retry because most characters of a hash are hex but not all
        // (for example a "sha256:" prefix), so a fixed index may miss.
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var index = Random.Shared.Next(value.Length);
            if (!char.IsAsciiHexDigit(value[index]))
            {
                continue;
            }

            return value[..index] + RandomReplacementHex(value[index]) + value[(index + 1)..];
        }
        return value;
    }

    /// <summary>Randomizes every hex character, keeping separators and case style.</summary>
    private static string? RandomHexLike(string? sample)
    {
        if (string.IsNullOrEmpty(sample))
        {
            return sample;
        }

        var chars = sample.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsAsciiHexDigit(chars[i]))
            {
                chars[i] = RandomReplacementHex(chars[i]);
            }
        }
        return new string(chars);
    }

    private static char RandomReplacement(char original)
    {
        var source = char.IsAsciiDigit(original)
            ? "0123456789"
            : char.IsAsciiLetterUpper(original) ? "ABCDEFGHIJKLMNOPQRSTUVWXYZ" : "abcdefghijklmnopqrstuvwxyz";
        char candidate;
        do
        {
            candidate = source[Random.Shared.Next(source.Length)];
        }
        while (candidate == original);
        return candidate;
    }

    private static char RandomReplacementHex(char original)
    {
        var source = char.IsAsciiLetterUpper(original) ? "0123456789ABCDEF" : "0123456789abcdef";
        char candidate;
        do
        {
            candidate = source[Random.Shared.Next(source.Length)];
        }
        while (candidate == original);
        return candidate;
    }
}

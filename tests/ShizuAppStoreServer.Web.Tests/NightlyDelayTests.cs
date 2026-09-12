using System.Globalization;
using ShizuAppStoreServer.Sync;
using Xunit;

namespace ShizuAppStoreServer.Web.Tests;

/// <summary>Hermetic schedule math for the nightly worker (no timers involved).</summary>
public sealed class NightlyDelayTests
{
    [Theory]
    [InlineData("2026-01-05T02:00:00Z", "03:00", 60)]
    [InlineData("2026-01-05T02:59:00Z", "03:00", 1)]
    [InlineData("2026-01-05T03:00:00Z", "03:00", 24 * 60)] // exactly now → tomorrow
    [InlineData("2026-01-05T04:00:00Z", "03:00", 23 * 60)]
    [InlineData("2026-01-05T22:30:00Z", "01:15", 165)]
    public void ComputesDelayUntilNextNightly(string now, string nightly, int minutes)
    {
        var delay = NightlyWorker.TimeUntilNextNightly(
            DateTimeOffset.Parse(now, CultureInfo.InvariantCulture), TimeOnly.Parse(nightly));

        Assert.Equal(TimeSpan.FromMinutes(minutes), delay);
    }
}

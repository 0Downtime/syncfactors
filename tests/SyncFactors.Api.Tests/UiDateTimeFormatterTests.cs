using SyncFactors.Api;

namespace SyncFactors.Api.Tests;

public sealed class UiDateTimeFormatterTests
{
    [Fact]
    public void FormatDateTime_UsesUsDateAndTwelveHourClock()
    {
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 4, 14, 17, 5, 0, DateTimeKind.Unspecified));
        var value = new DateTimeOffset(2026, 4, 14, 17, 5, 0, localOffset);

        var formatted = UiDateTimeFormatter.FormatDateTime(value);

        Assert.Equal("04/14/2026 5:05 PM", formatted);
    }

    [Fact]
    public void FormatDate_UsesUsDateFormat()
    {
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 4, 14, 17, 5, 0, DateTimeKind.Unspecified));
        var value = new DateTimeOffset(2026, 4, 14, 17, 5, 0, localOffset);

        var formatted = UiDateTimeFormatter.FormatDate(value);

        Assert.Equal("04/14/2026", formatted);
    }

    [Fact]
    public void FormatDateTime_ReturnsConfiguredFallbackForMissingValue()
    {
        var formatted = UiDateTimeFormatter.FormatDateTime(null, "Never");

        Assert.Equal("Never", formatted);
    }
}

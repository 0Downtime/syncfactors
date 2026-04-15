using System.Globalization;

namespace SyncFactors.Api;

public static class UiDateTimeFormatter
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    public static string FormatDateTime(DateTimeOffset value) =>
        FormatDateTime(value, "Unknown");

    public static string FormatDateTime(DateTimeOffset? value, string emptyText = "Unknown") =>
        value.HasValue
            ? value.Value.ToLocalTime().ToString("MM/dd/yyyy h:mm tt", EnUs)
            : emptyText;

    public static string FormatDate(DateTimeOffset value) =>
        FormatDate(value, "Unknown");

    public static string FormatDate(DateTimeOffset? value, string emptyText = "Unknown") =>
        value.HasValue
            ? value.Value.ToLocalTime().ToString("MM/dd/yyyy", EnUs)
            : emptyText;
}

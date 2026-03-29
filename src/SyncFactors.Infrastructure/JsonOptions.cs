using System.Text.Json;
using System.Text.Json.Serialization;

namespace SyncFactors.Infrastructure;

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

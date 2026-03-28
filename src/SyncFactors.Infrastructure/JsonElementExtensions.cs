using System.Text.Json;

namespace SyncFactors.Infrastructure;

internal static class JsonElementExtensions
{
    public static JsonElement GetRequiredObject(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected object property '{propertyName}'.");
        }

        return property;
    }

    public static bool TryGetObject(this JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.TryGetProperty(propertyName, out property) && property.ValueKind == JsonValueKind.Object)
        {
            property = property.Clone();
            return true;
        }

        property = default;
        return false;
    }

    public static IReadOnlyList<JsonElement> GetRequiredArray(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Expected array property '{propertyName}'.");
        }

        return property.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    public static string GetRequiredString(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Expected string property '{propertyName}'.");
        }

        var value = property.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Property '{propertyName}' must not be empty.");
        }

        return value;
    }

    public static string? TryGetString(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static bool GetRequiredBoolean(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidOperationException($"Expected boolean property '{propertyName}'.");
        }

        return property.GetBoolean();
    }

    public static int GetRequiredInt32(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
        {
            throw new InvalidOperationException($"Expected integer property '{propertyName}'.");
        }

        return value;
    }

    public static IReadOnlyList<string> GetRequiredStringArray(this JsonElement element, string propertyName)
    {
        return element.GetRequiredArray(propertyName)
            .Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    throw new InvalidOperationException($"Property '{propertyName}' must contain only non-empty strings.");
                }

                return item.GetString()!;
            })
            .ToArray();
    }

    public static IReadOnlyList<string>? TryGetStringArray(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return property.EnumerateArray()
            .Select(item =>
            {
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    throw new InvalidOperationException($"Property '{propertyName}' must contain only non-empty strings.");
                }

                return item.GetString()!;
            })
            .ToArray();
    }
}

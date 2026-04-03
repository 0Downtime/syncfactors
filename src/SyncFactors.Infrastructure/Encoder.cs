namespace Microsoft.Security.Application;

internal static class Encoder
{
    public static string LdapFilterEncode(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Concat(value.Select(EscapeCharacter));
    }

    private static string EscapeCharacter(char value)
    {
        return value switch
        {
            '\\' => "\\5c",
            '*' => "\\2a",
            '(' => "\\28",
            ')' => "\\29",
            '\0' => "\\00",
            _ => value.ToString()
        };
    }
}

using System.Net.Http.Headers;
using System.Text;

namespace SyncFactors.MockSuccessFactors;

public static class AuthenticationValidator
{
    public static bool IsAuthorized(HttpRequest request, MockAuthenticationOptions options)
    {
        if (!request.Headers.TryGetValue("Authorization", out var values))
        {
            return false;
        }

        if (!AuthenticationHeaderValue.TryParse(values.ToString(), out var header) || string.IsNullOrWhiteSpace(header.Parameter))
        {
            return false;
        }

        if (string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(header.Parameter, options.BearerToken, StringComparison.Ordinal);
        }

        if (string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(header.Parameter);
                var value = Encoding.UTF8.GetString(bytes);
                return string.Equals(value, $"{options.BasicUsername}:{options.BasicPassword}", StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }
}

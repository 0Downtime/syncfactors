namespace SyncFactors.Infrastructure;

public interface ISyncFactorsSecretResolver
{
    string? GetSecretValue(string? variableName);

    string ResolveSourceLabel(string? variableName, string fallbackSource);
}

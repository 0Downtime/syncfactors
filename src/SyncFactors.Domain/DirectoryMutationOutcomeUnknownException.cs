namespace SyncFactors.Domain;

public sealed class DirectoryMutationOutcomeUnknownException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
}

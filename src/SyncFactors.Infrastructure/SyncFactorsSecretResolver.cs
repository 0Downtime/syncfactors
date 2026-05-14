using System.Runtime.InteropServices;

namespace SyncFactors.Infrastructure;

public sealed class SyncFactorsSecretResolver : ISyncFactorsSecretResolver
{
    public const string WindowsCredentialPrefixEnvironmentVariable = "SYNCFACTORS_WINDOWS_CREDENTIAL_PREFIX";
    public const string DefaultWindowsCredentialPrefix = "SyncFactors";

    public string? GetSecretValue(string? variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return null;
        }

        var environmentValue = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return WindowsCredentialManager.TryRead(GetWindowsCredentialTargetName(variableName), out var credentialValue) &&
            !string.IsNullOrWhiteSpace(credentialValue)
                ? credentialValue
                : null;
    }

    public string ResolveSourceLabel(string? variableName, string fallbackSource)
    {
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return fallbackSource;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variableName)))
        {
            return variableName;
        }

        var credentialTargetName = GetWindowsCredentialTargetName(variableName);
        return WindowsCredentialManager.Exists(credentialTargetName)
            ? $"Windows Credential Manager ({credentialTargetName})"
            : fallbackSource;
    }

    public static string GetWindowsCredentialTargetName(string variableName)
    {
        var prefix = Environment.GetEnvironmentVariable(WindowsCredentialPrefixEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = DefaultWindowsCredentialPrefix;
        }

        return $"{prefix.Trim().TrimEnd('/', '\\')}/{variableName.Trim()}";
    }

    private static class WindowsCredentialManager
    {
        private const uint CredentialTypeGeneric = 1;
        private const int ErrorNotFound = 1168;

        public static bool Exists(string targetName) =>
            TryRead(targetName, out _);

        public static bool TryRead(string targetName, out string? value)
        {
            value = null;
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            var credentialPointer = IntPtr.Zero;
            var readSucceeded = CredRead(targetName, CredentialTypeGeneric, 0, out credentialPointer);
            if (!readSucceeded)
            {
                var errorCode = Marshal.GetLastWin32Error();
                if (errorCode == ErrorNotFound)
                {
                    return false;
                }

                throw new InvalidOperationException($"CredReadW failed for target '{targetName}' with Win32 error {errorCode}.");
            }

            try
            {
                var credential = Marshal.PtrToStructure<Credential>(credentialPointer);
                if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                {
                    value = string.Empty;
                    return true;
                }

                var characterCount = checked((int)credential.CredentialBlobSize / 2);
                value = Marshal.PtrToStringUni(credential.CredentialBlob, characterCount);
                return true;
            }
            finally
            {
                if (credentialPointer != IntPtr.Zero)
                {
                    CredFree(credentialPointer);
                }
            }
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(
            string target,
            uint type,
            uint reservedFlag,
            out IntPtr credentialPointer);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        private static extern void CredFree(IntPtr credentialPointer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public string? TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }
    }
}

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'SF_AD_SYNC_AD_SERVER',
        'SF_AD_SYNC_AD_USERNAME',
        'SF_AD_SYNC_AD_BIND_PASSWORD',
        'SF_AD_SYNC_AD_DEFAULT_PASSWORD',
        'SF_AD_SYNC_SF_USERNAME',
        'SF_AD_SYNC_SF_PASSWORD',
        'SF_AD_SYNC_SF_CLIENT_ID',
        'SF_AD_SYNC_SF_CLIENT_SECRET',
        'SYNCFACTORS__AUTH__OIDC__CLIENTSECRET',
        'SYNCFACTORS__AUTH__BOOTSTRAPADMIN__PASSWORD')]
    [string]$VariableName,
    [securestring]$SecretValue,
    [string]$TargetPrefix = 'SyncFactors',
    [switch]$Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsWindowsHost {
    return [string]::Equals($env:OS, 'Windows_NT', [System.StringComparison]::OrdinalIgnoreCase)
}

function ConvertFrom-SecureStringToPlainText {
    param(
        [Parameter(Mandatory)]
        [securestring]$Value
    )

    $pointer = [IntPtr]::Zero
    try {
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        if ($pointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }
}

function Initialize-WindowsCredentialManagerInterop {
    if ('SyncFactors.WindowsCredentialManager.NativeMethods' -as [type]) {
        return
    }

    Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace SyncFactors.WindowsCredentialManager
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    public static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite([In] ref CREDENTIAL userCredential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);
    }
}
"@
}

function Set-WindowsCredentialValue {
    param(
        [Parameter(Mandatory)]
        [string]$TargetName,
        [Parameter(Mandatory)]
        [string]$Value,
        [Parameter(Mandatory)]
        [string]$UserName
    )

    Initialize-WindowsCredentialManagerInterop

    $blobBytes = [Text.Encoding]::Unicode.GetBytes($Value)
    $blobPointer = [IntPtr]::Zero
    try {
        $blobPointer = [Runtime.InteropServices.Marshal]::AllocCoTaskMem($blobBytes.Length)
        [Runtime.InteropServices.Marshal]::Copy($blobBytes, 0, $blobPointer, $blobBytes.Length)

        $credential = New-Object SyncFactors.WindowsCredentialManager.CREDENTIAL
        $credential.Type = [uint32]1
        $credential.TargetName = $TargetName
        $credential.CredentialBlobSize = [uint32]$blobBytes.Length
        $credential.CredentialBlob = $blobPointer
        $credential.Persist = [uint32]2
        $credential.UserName = $UserName

        $writeSucceeded = [SyncFactors.WindowsCredentialManager.NativeMethods]::CredWrite([ref]$credential, [uint32]0)
        if (-not $writeSucceeded) {
            $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "CredWriteW failed for target '$TargetName' with Win32 error $errorCode."
        }
    }
    finally {
        if ($blobPointer -ne [IntPtr]::Zero) {
            [Runtime.InteropServices.Marshal]::FreeCoTaskMem($blobPointer)
        }
    }
}

function Remove-WindowsCredentialValue {
    param(
        [Parameter(Mandatory)]
        [string]$TargetName
    )

    Initialize-WindowsCredentialManagerInterop

    $deleteSucceeded = [SyncFactors.WindowsCredentialManager.NativeMethods]::CredDelete($TargetName, [uint32]1, [uint32]0)
    if (-not $deleteSucceeded) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($errorCode -ne 1168) {
            throw "CredDeleteW failed for target '$TargetName' with Win32 error $errorCode."
        }
    }
}

if (-not (Test-IsWindowsHost)) {
    throw 'SyncFactors Windows Credential Manager secrets can only be managed on Windows.'
}

$targetName = "$($TargetPrefix.Trim().TrimEnd('/', '\'))/$VariableName"
if ($Remove.IsPresent) {
    if ($PSCmdlet.ShouldProcess($targetName, 'Remove Windows Credential Manager value')) {
        Remove-WindowsCredentialValue -TargetName $targetName
    }

    [pscustomobject]@{
        targetName = $targetName
        removed = $true
    }
    return
}

if ($null -eq $SecretValue) {
    $SecretValue = Read-Host "Value for $VariableName" -AsSecureString
}

$plainTextValue = ConvertFrom-SecureStringToPlainText -Value $SecretValue
if ($PSCmdlet.ShouldProcess($targetName, 'Set Windows Credential Manager value')) {
    Set-WindowsCredentialValue -TargetName $targetName -Value $plainTextValue -UserName $VariableName
}

[pscustomobject]@{
    targetName = $targetName
    currentWindowsIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
}

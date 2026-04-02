[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-WorktreeEnvFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $values = [ordered]@{}
    if (-not (Test-Path $Path)) {
        return $values
    }

    foreach ($rawLine in Get-Content $Path) {
        $line = $rawLine.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
            continue
        }

        $separatorIndex = $line.IndexOf('=')
        if ($separatorIndex -lt 0) {
            continue
        }

        $name = $line.Substring(0, $separatorIndex).Trim()
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        $values[$name] = $line.Substring($separatorIndex + 1)
    }

    return $values
}

function Get-SyncFactorsCredentialNamespace {
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot
    )

    $normalizedRepoRoot = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
    $repoName = [System.IO.Path]::GetFileName($normalizedRepoRoot)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalizedRepoRoot)
        $hashBytes = $sha256.ComputeHash($bytes)
    }
    finally {
        $sha256.Dispose()
    }

    $hash = [System.Convert]::ToHexString($hashBytes).ToLowerInvariant()
    return "SyncFactors/$repoName/$hash"
}

function Get-SyncFactorsCredentialTargetName {
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot,
        [Parameter(Mandatory)]
        [string]$VariableName
    )

    return "$(Get-SyncFactorsCredentialNamespace -RepoRoot $RepoRoot)/$VariableName"
}

function Initialize-WindowsCredentialManagerInterop {
    if (-not [OperatingSystem]::IsWindows()) {
        return
    }

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
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredWrite([In] ref CREDENTIAL userCredential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        public static extern void CredFree([In] IntPtr credentialPtr);
    }
}
"@
}

function Get-WindowsCredentialValue {
    param(
        [Parameter(Mandatory)]
        [string]$TargetName
    )

    if (-not [OperatingSystem]::IsWindows()) {
        return [pscustomobject]@{
            Found = $false
            Value = $null
        }
    }

    Initialize-WindowsCredentialManagerInterop

    $credentialPtr = [IntPtr]::Zero
    $typeGeneric = [uint32]1
    $readSucceeded = [SyncFactors.WindowsCredentialManager.NativeMethods]::CredRead(
        $TargetName,
        $typeGeneric,
        [uint32]0,
        [ref]$credentialPtr)

    if (-not $readSucceeded) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($errorCode -eq 1168) {
            return [pscustomobject]@{
                Found = $false
                Value = $null
            }
        }

        throw "CredReadW failed for target '$TargetName' with Win32 error $errorCode."
    }

    try {
        $credential = [Runtime.InteropServices.Marshal]::PtrToStructure(
            $credentialPtr,
            [type][SyncFactors.WindowsCredentialManager.CREDENTIAL])

        if ($credential.CredentialBlobSize -le 0 -or $credential.CredentialBlob -eq [IntPtr]::Zero) {
            $value = ''
        }
        else {
            $characterCount = [int]($credential.CredentialBlobSize / 2)
            $value = [Runtime.InteropServices.Marshal]::PtrToStringUni($credential.CredentialBlob, $characterCount)
        }

        return [pscustomobject]@{
            Found = $true
            Value = $value
        }
    }
    finally {
        if ($credentialPtr -ne [IntPtr]::Zero) {
            [SyncFactors.WindowsCredentialManager.NativeMethods]::CredFree($credentialPtr)
        }
    }
}

function Set-WindowsCredentialValue {
    param(
        [Parameter(Mandatory)]
        [string]$TargetName,
        [AllowNull()]
        [string]$Value,
        [string]$UserName = 'SyncFactors'
    )

    if (-not [OperatingSystem]::IsWindows()) {
        throw 'Windows Credential Manager is only available on Windows.'
    }

    Initialize-WindowsCredentialManagerInterop

    $secret = if ($null -eq $Value) { '' } else { $Value }
    $blobBytes = [System.Text.Encoding]::Unicode.GetBytes($secret)
    $blobPointer = [IntPtr]::Zero

    try {
        if ($blobBytes.Length -gt 0) {
            $blobPointer = [Runtime.InteropServices.Marshal]::AllocCoTaskMem($blobBytes.Length)
            [Runtime.InteropServices.Marshal]::Copy($blobBytes, 0, $blobPointer, $blobBytes.Length)
        }

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

    if (-not [OperatingSystem]::IsWindows()) {
        return
    }

    Initialize-WindowsCredentialManagerInterop

    $deleteSucceeded = [SyncFactors.WindowsCredentialManager.NativeMethods]::CredDelete(
        $TargetName,
        [uint32]1,
        [uint32]0)

    if (-not $deleteSucceeded) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($errorCode -ne 1168) {
            throw "CredDeleteW failed for target '$TargetName' with Win32 error $errorCode."
        }
    }
}

function Get-SyncFactorsCredentialValue {
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot,
        [Parameter(Mandatory)]
        [string]$VariableName
    )

    $targetName = Get-SyncFactorsCredentialTargetName -RepoRoot $RepoRoot -VariableName $VariableName
    return Get-WindowsCredentialValue -TargetName $targetName
}

function Set-SyncFactorsCredentialValue {
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot,
        [Parameter(Mandatory)]
        [string]$VariableName,
        [AllowNull()]
        [string]$Value
    )

    $targetName = Get-SyncFactorsCredentialTargetName -RepoRoot $RepoRoot -VariableName $VariableName
    Set-WindowsCredentialValue -TargetName $targetName -Value $Value -UserName $VariableName
}

function Remove-SyncFactorsCredentialValue {
    param(
        [Parameter(Mandatory)]
        [string]$RepoRoot,
        [Parameter(Mandatory)]
        [string]$VariableName
    )

    $targetName = Get-SyncFactorsCredentialTargetName -RepoRoot $RepoRoot -VariableName $VariableName
    Remove-WindowsCredentialValue -TargetName $targetName
}

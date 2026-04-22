Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SyncFactorsHashtableValue {
    param(
        [AllowNull()]
        [System.Collections.IDictionary]$Table,
        [Parameter(Mandatory)]
        [string]$Key
    )

    if ($null -eq $Table -or -not $Table.Contains($Key)) {
        return $null
    }

    return $Table[$Key]
}

function Get-SyncFactorsEnvironmentSecretValue {
    param(
        [AllowNull()]
        [string]$VariableName
    )

    if ([string]::IsNullOrWhiteSpace($VariableName)) {
        return $null
    }

    $value = [Environment]::GetEnvironmentVariable($VariableName)
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }

    return $value.Trim()
}

function Resolve-SyncFactorsConfigSecretValue {
    param(
        [AllowNull()]
        [System.Collections.IDictionary]$Secrets,
        [AllowNull()]
        [System.Collections.IDictionary]$Section,
        [Parameter(Mandatory)]
        [string]$SecretKey,
        [Parameter(Mandatory)]
        [string]$LiteralKey
    )

    $environmentVariableName = [string](Get-SyncFactorsHashtableValue -Table $Secrets -Key $SecretKey)
    $environmentValue = Get-SyncFactorsEnvironmentSecretValue -VariableName $environmentVariableName
    if (-not [string]::IsNullOrWhiteSpace($environmentValue)) {
        return $environmentValue
    }

    $literalValue = [string](Get-SyncFactorsHashtableValue -Table $Section -Key $LiteralKey)
    if ([string]::IsNullOrWhiteSpace($literalValue)) {
        return $null
    }

    return $literalValue.Trim()
}

function Get-SyncFactorsConfiguredActiveDirectoryTargets {
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath
    )

    if ([string]::IsNullOrWhiteSpace($ConfigPath) -or -not (Test-Path $ConfigPath)) {
        throw "Sync config '$ConfigPath' could not be found for the Active Directory OU precheck."
    }

    try {
        $document = ConvertFrom-Json -InputObject (Get-Content -Path $ConfigPath -Raw) -AsHashtable
    }
    catch {
        throw "Failed to parse sync config '$ConfigPath' for the Active Directory OU precheck. $_"
    }

    if ($document -isnot [System.Collections.IDictionary]) {
        throw "Sync config '$ConfigPath' must contain a JSON object for the Active Directory OU precheck."
    }

    $secrets = Get-SyncFactorsHashtableValue -Table $document -Key 'secrets'
    $ad = Get-SyncFactorsHashtableValue -Table $document -Key 'ad'
    $transport = Get-SyncFactorsHashtableValue -Table $ad -Key 'transport'

    if ($ad -isnot [System.Collections.IDictionary]) {
        throw "Sync config '$ConfigPath' is missing the top-level 'ad' object required for the Active Directory OU precheck."
    }

    $targets = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @(
        [pscustomobject]@{ Name = 'defaultActiveOu'; DistinguishedName = [string](Get-SyncFactorsHashtableValue -Table $ad -Key 'defaultActiveOu') }
        [pscustomobject]@{ Name = 'prehireOu'; DistinguishedName = [string](Get-SyncFactorsHashtableValue -Table $ad -Key 'prehireOu') }
        [pscustomobject]@{ Name = 'graveyardOu'; DistinguishedName = [string](Get-SyncFactorsHashtableValue -Table $ad -Key 'graveyardOu') }
        [pscustomobject]@{ Name = 'leaveOu'; DistinguishedName = [string](Get-SyncFactorsHashtableValue -Table $ad -Key 'leaveOu') }
    )) {
        if ([string]::IsNullOrWhiteSpace($entry.DistinguishedName)) {
            continue
        }

        $distinguishedName = $entry.DistinguishedName.Trim()
        if ($seen.Add($distinguishedName)) {
            $targets.Add([pscustomobject]@{
                Name = $entry.Name
                DistinguishedName = $distinguishedName
            })
        }
    }

    return [pscustomobject]@{
        Server = Resolve-SyncFactorsConfigSecretValue -Secrets $secrets -Section $ad -SecretKey 'adServerEnv' -LiteralKey 'server'
        Port = Get-SyncFactorsHashtableValue -Table $ad -Key 'port'
        Username = Resolve-SyncFactorsConfigSecretValue -Secrets $secrets -Section $ad -SecretKey 'adUsernameEnv' -LiteralKey 'username'
        BindPassword = Resolve-SyncFactorsConfigSecretValue -Secrets $secrets -Section $ad -SecretKey 'adBindPasswordEnv' -LiteralKey 'bindPassword'
        TransportMode = [string](Get-SyncFactorsHashtableValue -Table $transport -Key 'mode')
        AllowLdapFallback = [bool](Get-SyncFactorsHashtableValue -Table $transport -Key 'allowLdapFallback')
        RequireCertificateValidation = if ($transport -is [System.Collections.IDictionary] -and $transport.Contains('requireCertificateValidation')) { [bool]$transport['requireCertificateValidation'] } else { $true }
        RequireSigning = if ($transport -is [System.Collections.IDictionary] -and $transport.Contains('requireSigning')) { [bool]$transport['requireSigning'] } else { $true }
        TrustedCertificateThumbprints = @(
            Get-SyncFactorsHashtableValue -Table $transport -Key 'trustedCertificateThumbprints' |
                ForEach-Object { $_ } |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
                ForEach-Object { [string]$_ }
        )
        Targets = $targets.ToArray()
    }
}

function Initialize-SyncFactorsActiveDirectoryOuProbeType {
    if ($null -ne ('SyncFactorsLauncherAdOuProbe' -as [type])) {
        return
    }

    Add-Type -Language CSharp -ReferencedAssemblies @(
        'System.Collections',
        'System.Collections.NonGeneric',
        'System.DirectoryServices.Protocols',
        'System.Net.Primitives',
        'System.Security.Cryptography',
        'System.Security.Cryptography.X509Certificates'
    ) -TypeDefinition @"
#nullable enable
using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;

public sealed class SyncFactorsLauncherAdOuProbeConfig
{
    public string Server { get; set; } = string.Empty;
    public int? Port { get; set; }
    public string? Username { get; set; }
    public string? BindPassword { get; set; }
    public string TransportMode { get; set; } = "ldap";
    public bool AllowLdapFallback { get; set; }
    public bool RequireCertificateValidation { get; set; } = true;
    public bool RequireSigning { get; set; } = true;
    public string[] TrustedCertificateThumbprints { get; set; } = Array.Empty<string>();
    public SyncFactorsLauncherAdOuProbeTarget[] Targets { get; set; } = Array.Empty<SyncFactorsLauncherAdOuProbeTarget>();
}

public sealed class SyncFactorsLauncherAdOuProbeTarget
{
    public string Name { get; set; } = string.Empty;
    public string DistinguishedName { get; set; } = string.Empty;
}

public sealed class SyncFactorsLauncherAdOuProbeSession
{
    public string RequestedTransport { get; set; } = string.Empty;
    public string EffectiveTransport { get; set; } = string.Empty;
    public bool UsedFallback { get; set; }
    public SyncFactorsLauncherAdOuProbeCheck[] Checks { get; set; } = Array.Empty<SyncFactorsLauncherAdOuProbeCheck>();
}

public sealed class SyncFactorsLauncherAdOuProbeCheck
{
    public string Name { get; set; } = string.Empty;
    public string DistinguishedName { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool Writable { get; set; }
    public string WriteCheck { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? EvaluationDetails { get; set; }
}

public static class SyncFactorsLauncherAdOuProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static SyncFactorsLauncherAdOuProbeSession Probe(SyncFactorsLauncherAdOuProbeConfig config)
    {
        var requestedTransport = NormalizeMode(config.TransportMode);

        try
        {
            using var primaryConnection = CreateConnectionForMode(config, requestedTransport);
            return CreateSession(config, requestedTransport, requestedTransport, usedFallback: false, primaryConnection);
        }
        catch (LdapException) when (config.AllowLdapFallback && !string.Equals(requestedTransport, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            using var fallbackConnection = CreateConnectionForMode(config, "ldap");
            return CreateSession(config, requestedTransport, "ldap", usedFallback: true, fallbackConnection);
        }
    }

    private static SyncFactorsLauncherAdOuProbeSession CreateSession(
        SyncFactorsLauncherAdOuProbeConfig config,
        string requestedTransport,
        string effectiveTransport,
        bool usedFallback,
        LdapConnection connection)
    {
        return new SyncFactorsLauncherAdOuProbeSession
        {
            RequestedTransport = requestedTransport,
            EffectiveTransport = effectiveTransport,
            UsedFallback = usedFallback,
            Checks = ProbeTargets(connection, config.Targets)
        };
    }

    private static SyncFactorsLauncherAdOuProbeCheck[] ProbeTargets(
        LdapConnection connection,
        SyncFactorsLauncherAdOuProbeTarget[] targets)
    {
        var checks = new SyncFactorsLauncherAdOuProbeCheck[targets.Length];
        for (var index = 0; index < targets.Length; index++)
        {
            checks[index] = ProbeTarget(connection, targets[index]);
        }

        return checks;
    }

    private static SyncFactorsLauncherAdOuProbeCheck ProbeTarget(LdapConnection connection, SyncFactorsLauncherAdOuProbeTarget target)
    {
        var request = new SearchRequest(
            target.DistinguishedName,
            "(objectClass=*)",
            SearchScope.Base,
            "distinguishedName",
            "objectClass",
            "allowedChildClassesEffective",
            "allowedChildClasses");
        request.TimeLimit = Timeout;

        try
        {
            var response = (SearchResponse)connection.SendRequest(request, Timeout);
            if (response.ResultCode == ResultCode.NoSuchObject)
            {
                return CreateFailure(target, exists: false, "missing", "directory object was not found", null);
            }

            if (response.ResultCode != ResultCode.Success)
            {
                var message = string.IsNullOrWhiteSpace(response.ErrorMessage)
                    ? response.ResultCode.ToString()
                    : response.ErrorMessage.Trim();
                return CreateFailure(target, exists: false, "search", message, null);
            }

            var entry = response.Entries.Count > 0
                ? response.Entries[0]
                : null;
            if (entry is null)
            {
                return CreateFailure(target, exists: false, "search", "directory search returned no entry", null);
            }

            var objectClasses = GetAttributeValues(entry, "objectClass");
            var effectiveAllowedChildren = GetAttributeValues(entry, "allowedChildClassesEffective");
            var schemaAllowedChildren = GetAttributeValues(entry, "allowedChildClasses");
            var evaluationDetails = BuildEvaluationDetails(objectClasses, effectiveAllowedChildren, schemaAllowedChildren);
            if (effectiveAllowedChildren.Length == 0)
            {
                return CreateFailure(
                    target,
                    exists: true,
                    "allowedChildClassesEffective",
                    "OU exists, but the server did not return allowedChildClassesEffective so create-child permissions could not be confirmed",
                    evaluationDetails);
            }

            var canCreateUsers = ContainsIgnoreCase(effectiveAllowedChildren, "user");

            if (!canCreateUsers)
            {
                return CreateFailure(
                    target,
                    exists: true,
                    "allowedChildClassesEffective",
                    "OU exists, but the bind account does not have effective permission to create user objects there",
                    evaluationDetails);
            }

            return new SyncFactorsLauncherAdOuProbeCheck
            {
                Name = target.Name,
                DistinguishedName = target.DistinguishedName,
                Exists = true,
                Writable = true,
                WriteCheck = "allowedChildClassesEffective",
                Message = null,
                EvaluationDetails = evaluationDetails
            };
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
        {
            return CreateFailure(target, exists: false, "missing", "directory object was not found", null);
        }
        catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.InsufficientAccessRights)
        {
            return CreateFailure(target, exists: true, "search", "OU exists, but the bind account cannot read it well enough to validate permissions", null);
        }
        catch (LdapException ex)
        {
            return CreateFailure(target, exists: false, "search", ex.Message, null);
        }
    }

    private static SyncFactorsLauncherAdOuProbeCheck CreateFailure(
        SyncFactorsLauncherAdOuProbeTarget target,
        bool exists,
        string writeCheck,
        string message,
        string? evaluationDetails)
    {
        return new SyncFactorsLauncherAdOuProbeCheck
        {
            Name = target.Name,
            DistinguishedName = target.DistinguishedName,
            Exists = exists,
            Writable = false,
            WriteCheck = writeCheck,
            Message = message,
            EvaluationDetails = evaluationDetails
        };
    }

    private static string BuildEvaluationDetails(
        string[] objectClasses,
        string[] effectiveAllowedChildren,
        string[] schemaAllowedChildren)
    {
        return $"objectClass=[{string.Join(",", objectClasses)}]; " +
               $"allowedChildClassesEffectiveContainsUser={ContainsIgnoreCase(effectiveAllowedChildren, "user").ToString().ToLowerInvariant()}; " +
               $"allowedChildClassesEffective=[{string.Join(",", effectiveAllowedChildren)}]; " +
               $"allowedChildClassesContainsUser={ContainsIgnoreCase(schemaAllowedChildren, "user").ToString().ToLowerInvariant()}; " +
               $"allowedChildClasses=[{string.Join(",", schemaAllowedChildren)}]";
    }

    private static bool ContainsIgnoreCase(string[] values, string expected)
    {
        foreach (var value in values)
        {
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] GetAttributeValues(SearchResultEntry entry, string attributeName)
    {
        if (!entry.Attributes.Contains(attributeName))
        {
            return Array.Empty<string>();
        }

        var values = new List<string>();
        foreach (var value in entry.Attributes[attributeName])
        {
            if (value is null)
            {
                continue;
            }

            var text = ConvertAttributeValueToText(value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                values.Add(text.Trim());
            }
        }

        return values.ToArray();
    }

    private static string? ConvertAttributeValueToText(object value)
    {
        if (value is byte[] bytes)
        {
            var decoded = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(decoded)
                ? Convert.ToHexString(bytes)
                : decoded;
        }

        return value.ToString();
    }

    private static LdapConnection CreateConnectionForMode(SyncFactorsLauncherAdOuProbeConfig config, string mode)
    {
        var port = GetPortForMode(config.Port, mode, config.TransportMode);
        var connection = new LdapConnection(new LdapDirectoryIdentifier(config.Server, port))
        {
            AuthType = string.IsNullOrWhiteSpace(config.Username) ? AuthType.Anonymous : AuthType.Basic,
            Timeout = Timeout
        };

        if (!string.IsNullOrWhiteSpace(config.Username))
        {
            connection.Credential = new NetworkCredential(config.Username, config.BindPassword);
        }

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        if (config.RequireSigning)
        {
            connection.SessionOptions.Signing = true;
            connection.SessionOptions.Sealing = true;
        }

        if (!string.Equals(mode, "ldap", StringComparison.OrdinalIgnoreCase))
        {
            if (OperatingSystem.IsWindows())
            {
                connection.SessionOptions.VerifyServerCertificate += (_, certificate) => ValidateServerCertificate(certificate, config);
            }
            else if (RequiresCustomCertificateValidation(config))
            {
                throw new PlatformNotSupportedException("Custom LDAP certificate override is unsupported on this platform. Trust the LDAPS certificate in the OS store, connect with a DNS name that matches the certificate SAN, set requireCertificateValidation=true, and leave trustedCertificateThumbprints empty.");
            }
        }

        if (string.Equals(mode, "ldaps", StringComparison.OrdinalIgnoreCase))
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }

        if (string.Equals(mode, "starttls", StringComparison.OrdinalIgnoreCase))
        {
            connection.SessionOptions.StartTransportLayerSecurity(null);
        }

        connection.Bind();
        return connection;
    }

    private static bool ValidateServerCertificate(X509Certificate? certificate, SyncFactorsLauncherAdOuProbeConfig config)
    {
        if (certificate is null)
        {
            return false;
        }

        if (!config.RequireCertificateValidation)
        {
            return true;
        }

        using var certificate2 = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        var configuredThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var thumbprint in config.TrustedCertificateThumbprints)
        {
            var normalizedThumbprint = NormalizeThumbprint(thumbprint);
            if (!string.IsNullOrWhiteSpace(normalizedThumbprint))
            {
                configuredThumbprints.Add(normalizedThumbprint);
            }
        }

        if (configuredThumbprints.Count > 0)
        {
            return configuredThumbprints.Contains(NormalizeThumbprint(certificate2.Thumbprint));
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(certificate2);
    }

    private static bool RequiresCustomCertificateValidation(SyncFactorsLauncherAdOuProbeConfig config)
    {
        return !config.RequireCertificateValidation ||
               config.TrustedCertificateThumbprints.Any(thumbprint => !string.IsNullOrWhiteSpace(thumbprint));
    }

    private static int GetPortForMode(int? configuredPort, string requestedMode, string configuredMode)
    {
        if (configuredPort is null)
        {
            return GetDefaultPort(requestedMode);
        }

        if (string.Equals(requestedMode, "ldap", StringComparison.OrdinalIgnoreCase) &&
            configuredPort.Value == GetDefaultPort(configuredMode))
        {
            return GetDefaultPort("ldap");
        }

        return configuredPort.Value;
    }

    private static int GetDefaultPort(string mode) =>
        string.Equals(NormalizeMode(mode), "ldaps", StringComparison.OrdinalIgnoreCase) ? 636 : 389;

    private static string NormalizeMode(string? mode)
    {
        return string.IsNullOrWhiteSpace(mode) ? "ldap" : mode.Trim().ToLowerInvariant();
    }

    private static string NormalizeThumbprint(string? thumbprint)
    {
        return string.IsNullOrWhiteSpace(thumbprint)
            ? string.Empty
            : thumbprint.Replace(":", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Trim();
    }
}
"@
}

function Invoke-SyncFactorsConfiguredActiveDirectoryOuProbe {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Configuration
    )

    Initialize-SyncFactorsActiveDirectoryOuProbeType

    $probeConfig = New-Object SyncFactorsLauncherAdOuProbeConfig
    $probeConfig.Server = [string]$Configuration.Server
    if ($null -ne $Configuration.Port -and -not [string]::IsNullOrWhiteSpace([string]$Configuration.Port)) {
        $probeConfig.Port = [int]$Configuration.Port
    }

    $probeConfig.Username = [string]$Configuration.Username
    $probeConfig.BindPassword = [string]$Configuration.BindPassword
    $probeConfig.TransportMode = if ([string]::IsNullOrWhiteSpace([string]$Configuration.TransportMode)) { 'ldap' } else { [string]$Configuration.TransportMode }
    $probeConfig.AllowLdapFallback = [bool]$Configuration.AllowLdapFallback
    $probeConfig.RequireCertificateValidation = [bool]$Configuration.RequireCertificateValidation
    $probeConfig.RequireSigning = [bool]$Configuration.RequireSigning
    $probeConfig.TrustedCertificateThumbprints = @($Configuration.TrustedCertificateThumbprints)
    $probeConfig.Targets = @(
        $Configuration.Targets | ForEach-Object {
            $target = New-Object SyncFactorsLauncherAdOuProbeTarget
            $target.Name = [string]$_.Name
            $target.DistinguishedName = [string]$_.DistinguishedName
            $target
        }
    )

    return [SyncFactorsLauncherAdOuProbe]::Probe($probeConfig)
}

function Get-SyncFactorsBindDomainLabel {
    param(
        [AllowNull()]
        [string]$Username,
        [Parameter(Mandatory)]
        [string]$Server
    )

    if (-not [string]::IsNullOrWhiteSpace($Username)) {
        $trimmedUsername = $Username.Trim()
        $separatorIndex = $trimmedUsername.IndexOf('\', [System.StringComparison]::Ordinal)
        if ($separatorIndex -gt 0) {
            return $trimmedUsername.Substring(0, $separatorIndex)
        }

        $atIndex = $trimmedUsername.LastIndexOf('@')
        if ($atIndex -gt 0 -and $atIndex -lt ($trimmedUsername.Length - 1)) {
            return $trimmedUsername.Substring($atIndex + 1)
        }

        if ($trimmedUsername.Contains('DC=', [System.StringComparison]::OrdinalIgnoreCase)) {
            $components = [regex]::Matches($trimmedUsername, '(?i)(?:^|,)DC=([^,]+)') |
                ForEach-Object { $_.Groups[1].Value.Trim() } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            if ($components.Count -gt 0) {
                return ($components -join '.')
            }
        }
    }

    return $Server
}

function Write-SyncFactorsConfiguredAdBindSummary {
    param(
        [Parameter(Mandatory)]
        [pscustomobject]$Configuration
    )

    $bindUser = if ([string]::IsNullOrWhiteSpace([string]$Configuration.Username)) {
        'anonymous'
    }
    else {
        [string]$Configuration.Username
    }

    $bindDomain = Get-SyncFactorsBindDomainLabel `
        -Username ([string]$Configuration.Username) `
        -Server ([string]$Configuration.Server)
    $transportMode = if ([string]::IsNullOrWhiteSpace([string]$Configuration.TransportMode)) {
        'ldap'
    }
    else {
        [string]$Configuration.TransportMode
    }

    Write-Host "AD OU precheck bind identity: user='$bindUser', domain='$bindDomain', server='$($Configuration.Server)', transport='$transportMode'." -ForegroundColor DarkGray
}

function Write-SyncFactorsConfiguredAdProbeEvaluation {
    param(
        [Parameter(Mandatory)]
        [object]$ProbeResult
    )

    foreach ($check in @($ProbeResult.Checks)) {
        $status = if ($check.Writable) { 'writable' } elseif ($check.Exists) { 'not-writable' } else { 'missing' }
        $details = if ([string]::IsNullOrWhiteSpace([string]$check.EvaluationDetails)) {
            'no attribute diagnostics returned'
        }
        else {
            [string]$check.EvaluationDetails
        }

        Write-Host "AD OU precheck evaluation: name='$($check.Name)', status='$status', writeCheck='$($check.WriteCheck)', dn='$($check.DistinguishedName)', details=$details" -ForegroundColor DarkGray
    }
}

function Format-SyncFactorsActiveDirectoryOuFailureMessage {
    param(
        [Parameter(Mandatory)]
        [string]$Server,
        [Parameter(Mandatory)]
        [object]$ProbeResult
    )

    $summary = [System.Collections.Generic.List[string]]::new()
    $summary.Add("Configured AD OU precheck failed against LDAP server '$Server'.")
    $summary.Add("Requested transport='$($ProbeResult.RequestedTransport)', effective transport='$($ProbeResult.EffectiveTransport)', usedFallback=$($ProbeResult.UsedFallback.ToString().ToLowerInvariant()).")

    foreach ($check in @($ProbeResult.Checks | Where-Object { -not $_.Exists -or -not $_.Writable })) {
        $line = "$($check.Name)='$($check.DistinguishedName)' failed: $($check.Message)"
        if (-not [string]::IsNullOrWhiteSpace([string]$check.EvaluationDetails)) {
            $line = "$line Evaluation: $($check.EvaluationDetails)"
        }

        $summary.Add($line)
    }

    return $summary -join ' '
}

function Assert-SyncFactorsConfiguredAdOusAccessible {
    param(
        [Parameter(Mandatory)]
        [string]$ConfigPath
    )

    $configuration = Get-SyncFactorsConfiguredActiveDirectoryTargets -ConfigPath $ConfigPath
    if ([string]::IsNullOrWhiteSpace([string]$configuration.Server)) {
        throw "Configured AD OU precheck requires an LDAP server, but no Active Directory server was resolved from '$ConfigPath'."
    }

    if ($configuration.Targets.Count -eq 0) {
        throw "Configured AD OU precheck did not find any OU targets in '$ConfigPath'."
    }

    Write-SyncFactorsConfiguredAdBindSummary -Configuration $configuration
    $probeResult = Invoke-SyncFactorsConfiguredActiveDirectoryOuProbe -Configuration $configuration
    Write-SyncFactorsConfiguredAdProbeEvaluation -ProbeResult $probeResult
    $failures = @($probeResult.Checks | Where-Object { -not $_.Exists -or -not $_.Writable })
    if ($failures.Count -gt 0) {
        throw (Format-SyncFactorsActiveDirectoryOuFailureMessage -Server $configuration.Server -ProbeResult $probeResult)
    }

    $targetList = $configuration.Targets | ForEach-Object { "$($_.Name)='$($_.DistinguishedName)'" }
    Write-Host "AD OU precheck passed for $($targetList -join ', ')." -ForegroundColor DarkGray
}

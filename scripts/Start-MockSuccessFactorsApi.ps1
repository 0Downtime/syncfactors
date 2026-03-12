[CmdletBinding()]
param(
    [ValidateRange(1, 50000)]
    [int]$UserCount = 1000,
    [ValidateRange(0, 5000)]
    [int]$InactiveCount = 0,
    [ValidateRange(0, 5000)]
    [int]$DuplicateWorkerIdCount = 0,
    [ValidateRange(1, 5000)]
    [int]$ManagerCount = 50,
    [ValidateRange(1025, 65535)]
    [int]$Port = 18080,
    [string]$AccessToken = 'mock-successfactors-token'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Path $PSScriptRoot -Parent
$moduleRoot = Join-Path -Path $projectRoot -ChildPath 'src/Modules/SfAdSync'
Import-Module (Join-Path $moduleRoot 'SyntheticHarness.psm1') -Force

function Write-MockApiLog {
    param([string]$Message)
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Write-Host "[$timestamp][MockSF] $Message"
}

function Send-JsonResponse {
    param(
        [Parameter(Mandatory)]
        [System.Net.HttpListenerResponse]$Response,
        [Parameter(Mandatory)]
        [object]$Body,
        [int]$StatusCode = 200
    )

    $json = $Body | ConvertTo-Json -Depth 20 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $Response.StatusCode = $StatusCode
    $Response.ContentType = 'application/json'
    $Response.ContentEncoding = [System.Text.Encoding]::UTF8
    $Response.ContentLength64 = $bytes.Length
    $Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Response.OutputStream.Close()
}

function Send-TextResponse {
    param(
        [Parameter(Mandatory)]
        [System.Net.HttpListenerResponse]$Response,
        [Parameter(Mandatory)]
        [string]$Body,
        [int]$StatusCode = 200,
        [string]$ContentType = 'text/plain'
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Body)
    $Response.StatusCode = $StatusCode
    $Response.ContentType = $ContentType
    $Response.ContentEncoding = [System.Text.Encoding]::UTF8
    $Response.ContentLength64 = $bytes.Length
    $Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Response.OutputStream.Close()
}

function Read-RequestBody {
    param(
        [Parameter(Mandatory)]
        [System.Net.HttpListenerRequest]$Request
    )

    $reader = [System.IO.StreamReader]::new($Request.InputStream, $Request.ContentEncoding)
    try {
        return $reader.ReadToEnd()
    } finally {
        $reader.Dispose()
    }
}

function ConvertFrom-FormUrlEncoded {
    param([string]$Body)

    $result = @{}
    if ([string]::IsNullOrWhiteSpace($Body)) {
        return $result
    }

    foreach ($pair in $Body -split '&') {
        if ([string]::IsNullOrWhiteSpace($pair)) {
            continue
        }

        $keyValue = $pair -split '=', 2
        $key = [System.Uri]::UnescapeDataString($keyValue[0])
        $value = if ($keyValue.Count -gt 1) { [System.Uri]::UnescapeDataString($keyValue[1]) } else { '' }
        $result[$key] = $value
    }

    return $result
}

function Test-MockBearerToken {
    param(
        [Parameter(Mandatory)]
        [System.Net.HttpListenerRequest]$Request,
        [Parameter(Mandatory)]
        [string]$ExpectedToken
    )

    $authorizationHeader = $Request.Headers['Authorization']
    return $authorizationHeader -eq "Bearer $ExpectedToken"
}

function Get-MockWorkersByQuery {
    param(
        [Parameter(Mandatory)]
        [object[]]$Workers,
        [System.Collections.Specialized.NameValueCollection]$QueryString
    )

    $results = @($Workers)
    $filter = $QueryString['$filter']
    if ([string]::IsNullOrWhiteSpace($filter)) {
        return $results
    }

    if ($filter -match "personIdExternal eq '(?<workerId>[^']+)'") {
        $workerId = $Matches['workerId']
        return @($results | Where-Object { $_.personIdExternal -eq $workerId })
    }

    if ($filter -match "lastModifiedDateTime ge datetime'(?<checkpoint>[^']+)'") {
        return $results
    }

    return $results
}

$syntheticDirectory = New-SfAdSyntheticWorkers -UserCount $UserCount -InactiveCount $InactiveCount -DuplicateWorkerIdCount $DuplicateWorkerIdCount -ManagerCount $ManagerCount
$workers = @($syntheticDirectory.workers)

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()

Write-MockApiLog -Message "Serving mock SuccessFactors API on http://127.0.0.1:$Port"
Write-MockApiLog -Message "OAuth token URL: http://127.0.0.1:$Port/oauth/token"
Write-MockApiLog -Message "OData base URL: http://127.0.0.1:$Port/odata/v2"
Write-MockApiLog -Message "Users: $UserCount, inactive: $InactiveCount, duplicates: $DuplicateWorkerIdCount, managers: $ManagerCount"
Write-MockApiLog -Message 'Press Ctrl+C to stop.'

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response
        $path = $request.Url.AbsolutePath

        Write-MockApiLog -Message "$($request.HttpMethod) $path"

        try {
            switch -Regex ($path) {
                '^/oauth/token$' {
                    $body = Read-RequestBody -Request $request
                    $form = ConvertFrom-FormUrlEncoded -Body $body
                    if ($form['grant_type'] -ne 'client_credentials') {
                        Send-JsonResponse -Response $response -StatusCode 400 -Body @{ error = 'unsupported_grant_type' }
                        continue
                    }

                    Send-JsonResponse -Response $response -Body @{
                        access_token = $AccessToken
                        token_type = 'Bearer'
                        expires_in = 3600
                    }
                    continue
                }
                '^/odata/v2/\$metadata$' {
                    Send-TextResponse -Response $response -ContentType 'application/xml' -Body @'
<?xml version="1.0" encoding="utf-8"?>
<edmx:Edmx Version="1.0" xmlns:edmx="http://schemas.microsoft.com/ado/2007/06/edmx">
  <edmx:DataServices>
    <Schema Namespace="MockSuccessFactors" xmlns="http://schemas.microsoft.com/ado/2008/09/edm">
      <EntityType Name="PerPerson" />
    </Schema>
  </edmx:DataServices>
</edmx:Edmx>
'@
                    continue
                }
                '^/odata/v2/PerPerson$' {
                    if (-not (Test-MockBearerToken -Request $request -ExpectedToken $AccessToken)) {
                        Send-JsonResponse -Response $response -StatusCode 401 -Body @{ error = 'invalid_token' }
                        continue
                    }

                    $results = @(Get-MockWorkersByQuery -Workers $workers -QueryString $request.QueryString)
                    Send-JsonResponse -Response $response -Body @{
                        d = @{
                            results = $results
                        }
                    }
                    continue
                }
                default {
                    Send-JsonResponse -Response $response -StatusCode 404 -Body @{ error = 'not_found'; path = $path }
                    continue
                }
            }
        } catch {
            Send-JsonResponse -Response $response -StatusCode 500 -Body @{
                error = 'server_error'
                message = $_.Exception.Message
            }
        }
    }
} finally {
    if ($listener.IsListening) {
        $listener.Stop()
    }
    $listener.Close()
}

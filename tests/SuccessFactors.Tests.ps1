Describe 'SuccessFactors module' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/SuccessFactors.psm1" -Force
    }

    BeforeEach {
        $global:SuccessFactorsTestConfig = [pscustomobject]@{
            successFactors = [pscustomobject]@{
                baseUrl = 'https://tenant.example.com/odata/v2'
                oauth = [pscustomobject]@{
                    tokenUrl = 'https://tenant.example.com/oauth/token'
                    clientId = 'client-id'
                    clientSecret = 'client-secret'
                    companyId = 'company-id'
                }
                query = [pscustomobject]@{
                    entitySet = 'PerPerson'
                    identityField = 'personIdExternal'
                    deltaField = 'lastModifiedDateTime'
                    select = @('personIdExternal', 'firstName')
                    expand = @('employmentNav')
                    baseFilter = "status eq 'active'"
                }
            }
        }
    }

    It 'requests an OAuth token with the expected form body' {
        InModuleScope SuccessFactors {
            Mock Invoke-RestMethod {
                $script:CapturedTokenUri = $Uri
                $script:CapturedTokenMethod = $Method
                $script:CapturedTokenContentType = $ContentType
                $script:CapturedTokenBody = $Body
                $script:CapturedTokenHeaders = $Headers
                [pscustomobject]@{ access_token = 'token-123' }
            }

            $token = Get-SfOAuthToken -Config $global:SuccessFactorsTestConfig

            $token | Should -Be 'token-123'
            $script:CapturedTokenUri | Should -Be 'https://tenant.example.com/oauth/token'
            $script:CapturedTokenMethod | Should -Be 'Post'
            $script:CapturedTokenContentType | Should -Be 'application/x-www-form-urlencoded'
            $script:CapturedTokenBody.client_id | Should -Be 'client-id'
            $script:CapturedTokenBody.client_secret | Should -Be 'client-secret'
            $script:CapturedTokenBody.company_id | Should -Be 'company-id'
            $script:CapturedTokenHeaders | Should -BeNullOrEmpty
        }
    }

    It 'supports HTTP Basic client authentication for the OAuth token request' {
        InModuleScope SuccessFactors {
            $basicClientAuthConfig = [pscustomobject]@{
                successFactors = [pscustomobject]@{
                    baseUrl = 'https://tenant.example.com/odata/v2'
                    oauth = [pscustomobject]@{
                        tokenUrl = 'https://tenant.example.com/oauth/token'
                        clientId = 'client-id'
                        clientSecret = 'client-secret'
                        companyId = 'company-id'
                        clientAuthentication = 'basic'
                    }
                }
            }

            Mock Invoke-RestMethod {
                $script:CapturedTokenUri = $Uri
                $script:CapturedTokenMethod = $Method
                $script:CapturedTokenContentType = $ContentType
                $script:CapturedTokenBody = $Body
                $script:CapturedTokenHeaders = $Headers
                [pscustomobject]@{ access_token = 'token-basic' }
            }

            $token = Get-SfOAuthToken -Config $basicClientAuthConfig

            $token | Should -Be 'token-basic'
            $script:CapturedTokenUri | Should -Be 'https://tenant.example.com/oauth/token'
            $script:CapturedTokenMethod | Should -Be 'Post'
            $script:CapturedTokenContentType | Should -Be 'application/x-www-form-urlencoded'
            $script:CapturedTokenBody.grant_type | Should -Be 'client_credentials'
            $script:CapturedTokenBody.company_id | Should -Be 'company-id'
            $script:CapturedTokenBody.ContainsKey('client_id') | Should -BeFalse
            $script:CapturedTokenBody.ContainsKey('client_secret') | Should -BeFalse
            $script:CapturedTokenHeaders.Authorization | Should -Be ('Basic ' + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes('client-id:client-secret')))
        }
    }

    It 'builds bearer auth headers from the token helper' {
        InModuleScope SuccessFactors {
            Mock Get-SfOAuthToken { 'token-abc' }

            $headers = Get-SfAuthHeaders -Config $global:SuccessFactorsTestConfig

            $headers.Authorization | Should -Be 'Bearer token-abc'
            $headers.Accept | Should -Be 'application/json'
        }
    }

    It 'builds basic auth headers when basic auth mode is configured' {
        InModuleScope SuccessFactors {
            $basicConfig = [pscustomobject]@{
                successFactors = [pscustomobject]@{
                    baseUrl = 'https://tenant.example.com/odata/v2'
                    auth = [pscustomobject]@{
                        mode = 'basic'
                        basic = [pscustomobject]@{
                            username = 'sf-user'
                            password = 'sf-password'
                        }
                    }
                }
            }

            $headers = Get-SfAuthHeaders -Config $basicConfig

            $headers.Authorization | Should -Be ('Basic ' + [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes('sf-user:sf-password')))
            $headers.Accept | Should -Be 'application/json'
        }
    }

    It 'builds encoded OData GET URLs with query parameters' {
        InModuleScope SuccessFactors {
            Mock Get-SfAuthHeaders { @{ Authorization = 'Bearer token-1' } }
            Mock Invoke-RestMethod {
                $script:CapturedGetUri = $Uri
                $script:CapturedGetHeaders = $Headers
                [pscustomobject]@{ value = @() }
            }

            Invoke-SfODataGet -Config $global:SuccessFactorsTestConfig -RelativePath 'PerPerson' -Query @{
                '$filter' = "personIdExternal eq '1001'"
                '$select' = 'personIdExternal,firstName'
            } | Out-Null

            $script:CapturedGetUri | Should -Match 'PerPerson'
            $script:CapturedGetUri | Should -Match '%24filter='
            $script:CapturedGetUri | Should -Match 'personIdExternal'
            $script:CapturedGetUri | Should -Match '%271001%27'
            $script:CapturedGetUri | Should -Match '%24select=personIdExternal%2CfirstName'
            $script:CapturedGetHeaders.Authorization | Should -Be 'Bearer token-1'
        }
    }

    It 'omits blank query values from OData GET URLs' {
        InModuleScope SuccessFactors {
            Mock Get-SfAuthHeaders { @{ Authorization = 'Bearer token-1' } }
            Mock Invoke-RestMethod {
                $script:CapturedGetUri = $Uri
                [pscustomobject]@{ value = @() }
            }

            Invoke-SfODataGet -Config $global:SuccessFactorsTestConfig -RelativePath 'PerPerson' -Query @{
                '$filter' = "personIdExternal eq '1001'"
                '$expand' = ''
            } | Out-Null

            $script:CapturedGetUri | Should -Match '%24filter='
            $script:CapturedGetUri | Should -Not -Match '%24expand='
        }
    }

    It 'uses the delta checkpoint filter when fetching workers in delta mode' {
        InModuleScope SuccessFactors {
            Mock Invoke-SfODataGet {
                $script:CapturedWorkerQuery = $Query
                [pscustomobject]@{
                    d = [pscustomobject]@{
                        results = @(
                            [pscustomobject]@{ personIdExternal = '1001' }
                        )
                    }
                }
            }

            $workers = @(Get-SfWorkers -Config $global:SuccessFactorsTestConfig -Mode Delta -Checkpoint '2026-03-01T00:00:00')

            $workers.Count | Should -Be 1
            $script:CapturedWorkerQuery['$filter'] | Should -Be "lastModifiedDateTime ge datetime'2026-03-01T00:00:00'"
        }
    }

    It 'falls back to the base filter in full mode and supports value responses' {
        InModuleScope SuccessFactors {
            Mock Invoke-SfODataGet {
                $script:CapturedWorkerQuery = $Query
                [pscustomobject]@{
                    value = @(
                        [pscustomobject]@{ personIdExternal = '2001' }
                    )
                }
            }

            $workers = @(Get-SfWorkers -Config $global:SuccessFactorsTestConfig -Mode Full)

            $workers.Count | Should -Be 1
            $script:CapturedWorkerQuery['$filter'] | Should -Be "status eq 'active'"
        }
    }

    It 'returns the first worker by identity and null when no worker matches' {
        InModuleScope SuccessFactors {
            Mock Invoke-SfODataGet {
                [pscustomobject]@{
                    value = @(
                        [pscustomobject]@{ personIdExternal = '3001' },
                        [pscustomobject]@{ personIdExternal = '3001-secondary' }
                    )
                }
            }

            $worker = Get-SfWorkerById -Config $global:SuccessFactorsTestConfig -WorkerId '3001'
            $worker.personIdExternal | Should -Be '3001'

            Mock Invoke-SfODataGet { [pscustomobject]@{ value = @() } }
            (Get-SfWorkerById -Config $global:SuccessFactorsTestConfig -WorkerId '9999') | Should -Be $null
        }
    }

    It 'uses previewQuery overrides for single-worker preview requests' {
        InModuleScope SuccessFactors {
            $previewConfig = [pscustomobject]@{
                successFactors = [pscustomobject]@{
                    baseUrl = 'https://tenant.example.com/odata/v2'
                    oauth = [pscustomobject]@{
                        tokenUrl = 'https://tenant.example.com/oauth/token'
                        clientId = 'client-id'
                        clientSecret = 'client-secret'
                    }
                    query = [pscustomobject]@{
                        entitySet = 'PerPerson'
                        identityField = 'personIdExternal'
                        deltaField = 'lastModifiedDateTime'
                        select = @('personIdExternal', 'employmentNav/jobInfoNav/employmentType')
                        expand = @('employmentNav', 'employmentNav/jobInfoNav')
                    }
                    previewQuery = [pscustomobject]@{
                        select = @('personIdExternal', 'firstName', 'lastName')
                        expand = @()
                    }
                }
            }

            Mock Invoke-SfODataGet {
                $script:CapturedPreviewRelativePath = $RelativePath
                $script:CapturedPreviewQuery = $Query
                [pscustomobject]@{ value = @() }
            }

            Get-SfWorkerById -Config $previewConfig -WorkerId '3001' | Out-Null

            $script:CapturedPreviewRelativePath | Should -Be 'PerPerson'
            $script:CapturedPreviewQuery['$select'] | Should -Be 'personIdExternal,firstName,lastName'
            $script:CapturedPreviewQuery.ContainsKey('$expand') | Should -BeFalse
            $script:CapturedPreviewQuery['$filter'] | Should -Be "personIdExternal eq '3001'"
        }
    }

    It 'throws when the OAuth response does not include an access token' {
        InModuleScope SuccessFactors {
            Mock Invoke-RestMethod { [pscustomobject]@{ token_type = 'Bearer' } }

            { Get-SfOAuthToken -Config $global:SuccessFactorsTestConfig } | Should -Throw
        }
    }

    It 'does not leak client secrets when the OAuth request fails' {
        InModuleScope SuccessFactors {
            Mock Invoke-RestMethod { throw "OAuth request failed for client_secret=$($global:SuccessFactorsTestConfig.successFactors.oauth.clientSecret)" }

            try {
                Get-SfOAuthToken -Config $global:SuccessFactorsTestConfig | Out-Null
                throw 'Expected Get-SfOAuthToken to throw.'
            } catch {
                $_.Exception.Message | Should -Match 'OAuth request failed'
                $_.Exception.Message | Should -Match 'SuccessFactors OAuth token request failed'
                $_.Exception.Message | Should -Match ([regex]::Escape($global:SuccessFactorsTestConfig.successFactors.oauth.tokenUrl))
                $_.Exception.Message | Should -Not -Match [regex]::Escape($global:SuccessFactorsTestConfig.successFactors.oauth.clientSecret)
            }
        }
    }

    It 'adds request context to OData request failures without leaking auth data' {
        InModuleScope SuccessFactors {
            Mock Get-SfAuthHeaders { @{ Authorization = 'Bearer token-1' } }
            Mock Invoke-RestMethod { throw 'odata request failed for Authorization=Bearer token-1' }

            try {
                Invoke-SfODataGet -Config $global:SuccessFactorsTestConfig -RelativePath 'PerPerson' -Query @{ '$select' = 'personIdExternal' } | Out-Null
                throw 'Expected Invoke-SfODataGet to throw.'
            } catch {
                $_.Exception.Message | Should -Match 'SuccessFactors OData request failed'
                $_.Exception.Message | Should -Match 'Auth mode: oauth'
                $_.Exception.Message | Should -Match 'Auth scheme: Bearer'
                $_.Exception.Message | Should -Match 'odata request failed'
                $_.Exception.Message | Should -Match 'URI: https://tenant\.example\.com/odata/v2/PerPerson'
                $_.Exception.Message | Should -Not -Match 'Bearer token-1'
            }
        }
    }

    It 'includes HttpResponseMessage bodies in request failures on PowerShell 7' {
        InModuleScope SuccessFactors {
            $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::Unauthorized)
            $response.Content = [System.Net.Http.StringContent]::new('{"error":"invalid_client","error_description":"client secret client-secret rejected"}')

            $exception = [System.Management.Automation.RuntimeException]::new('Response status code does not indicate success: 401 (Unauthorized).')
            $exception | Add-Member -MemberType NoteProperty -Name Response -Value $response -Force

            try {
                throw (New-SfRequestFailure -Operation 'SuccessFactors OAuth token request' -Uri 'https://tenant.example.com/oauth/token' -Exception $exception -Secrets @('client-secret'))
            } catch {
                $_.Exception.Message | Should -Match 'HTTP status: Unauthorized'
                $_.Exception.Message | Should -Match 'Response body:'
                $_.Exception.Message | Should -Match 'invalid_client'
                $_.Exception.Message | Should -Not -Match [regex]::Escape('client-secret')
            } finally {
                $response.Dispose()
            }
        }
    }

    It 'prefers ErrorDetails response bodies when HttpResponseMessage content is already disposed' {
        InModuleScope SuccessFactors {
            $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::Unauthorized)
            $response.Content = [System.Net.Http.StringContent]::new('{"error":"disposed"}')
            $response.Dispose()

            $exception = [System.Management.Automation.RuntimeException]::new('Response status code does not indicate success: 401 (Unauthorized).')
            $exception | Add-Member -MemberType NoteProperty -Name Response -Value $response -Force

            $errorRecord = [System.Management.Automation.ErrorRecord]::new($exception, 'Unauthorized', [System.Management.Automation.ErrorCategory]::InvalidOperation, $null)
            $errorRecord.ErrorDetails = [System.Management.Automation.ErrorDetails]::new('{"error":"invalid_client","error_description":"client secret client-secret rejected"}')

            try {
                throw (New-SfRequestFailure -Operation 'SuccessFactors OData request' -Uri 'https://tenant.example.com/odata/v2/PerPerson' -Exception $exception -ErrorRecord $errorRecord -Secrets @('client-secret'))
            } catch {
                $_.Exception.Message | Should -Match 'HTTP status: Unauthorized'
                $_.Exception.Message | Should -Match 'Response body:'
                $_.Exception.Message | Should -Match 'invalid_client'
                $_.Exception.Message | Should -Not -Match 'Response body read failed'
                $_.Exception.Message | Should -Not -Match [regex]::Escape('client-secret')
            }
        }
    }

    It 'returns an empty worker collection for unexpected response shapes' {
        InModuleScope SuccessFactors {
            Mock Invoke-SfODataGet { [pscustomobject]@{ unexpected = @('x') } }

            @(Get-SfWorkers -Config $global:SuccessFactorsTestConfig -Mode Full).Count | Should -Be 0
        }
    }
}

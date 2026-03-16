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
                $_.Exception.Message | Should -Not -Match [regex]::Escape($global:SuccessFactorsTestConfig.successFactors.oauth.clientSecret)
            }
        }
    }

    It 'propagates OData request failures' {
        InModuleScope SuccessFactors {
            Mock Get-SfAuthHeaders { @{ Authorization = 'Bearer token-1' } }
            Mock Invoke-RestMethod { throw 'odata request failed' }

            { Invoke-SfODataGet -Config $global:SuccessFactorsTestConfig -RelativePath 'PerPerson' -Query @{ '$select' = 'personIdExternal' } } | Should -Throw 'odata request failed'
        }
    }

    It 'returns an empty worker collection for unexpected response shapes' {
        InModuleScope SuccessFactors {
            Mock Invoke-SfODataGet { [pscustomobject]@{ unexpected = @('x') } }

            @(Get-SfWorkers -Config $global:SuccessFactorsTestConfig -Mode Full).Count | Should -Be 0
        }
    }
}

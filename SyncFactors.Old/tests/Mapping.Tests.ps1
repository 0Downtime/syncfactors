Describe 'Get-NestedValue' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Mapping.psm1" -Force
    }

    It 'resolves indexed nested paths for navigation collections' {
        $worker = [pscustomobject]@{
            employmentNav = @(
                [pscustomobject]@{
                    jobInfoNav = @(
                        [pscustomobject]@{
                            jobTitle = 'Senior Systems Analyst'
                            division = 'Shared Services'
                        }
                    )
                }
            )
        }

        Get-NestedValue -InputObject $worker -Path 'employmentNav[0].jobInfoNav[0].jobTitle' | Should -Be 'Senior Systems Analyst'
        Get-NestedValue -InputObject $worker -Path 'employmentNav[0].jobInfoNav[0].division' | Should -Be 'Shared Services'
    }

    It 'returns null for missing indexed values' {
        $worker = [pscustomobject]@{
            employmentNav = @()
        }

        Get-NestedValue -InputObject $worker -Path 'employmentNav[0].jobInfoNav[0].jobTitle' | Should -Be $null
    }

    It 'unwraps OData results collections for indexed navigation paths' {
        $worker = [pscustomobject]@{
            employmentNav = [pscustomobject]@{
                results = @(
                    [pscustomobject]@{
                        startDate = '2019-06-03T00:00:00Z'
                        jobInfoNav = [pscustomobject]@{
                            results = @(
                                [pscustomobject]@{
                                    emplStatus = '64300'
                                    jobTitle = 'Lead Admin, Patching'
                                }
                            )
                        }
                    }
                )
            }
        }

        Get-NestedValue -InputObject $worker -Path 'employmentNav[0].startDate' | Should -Be '2019-06-03T00:00:00Z'
        Get-NestedValue -InputObject $worker -Path 'employmentNav[0].jobInfoNav[0].emplStatus' | Should -Be '64300'
        Get-NestedValue -InputObject $worker -Path 'employmentNav[0].jobInfoNav[0].jobTitle' | Should -Be 'Lead Admin, Patching'
    }
}

Describe 'Get-SyncFactorsAttributeChanges' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SyncFactors/Mapping.psm1" -Force
    }

    It 'preserves the source calendar date for DateOnly transforms' {
        Convert-SyncFactorsMappedValue -Value '2019-06-03T00:00:00Z' -Transform 'DateOnly' | Should -Be '2019-06-03'
    }

    It 'ignores disabled mappings and preserves required validation' {
        $worker = [pscustomobject]@{
            firstName = 'Chris'
            lastName = 'Brien'
            email = 'Chris.Brien@Example.com'
        }

        $existing = [pscustomobject]@{
            GivenName = 'Christopher'
            Surname = 'Brien'
            UserPrincipalName = 'old@example.com'
        }

        $mapping = [pscustomobject]@{
            mappings = @(
                [pscustomobject]@{ source = 'firstName'; target = 'GivenName'; enabled = $true; required = $true; transform = 'Trim' },
                [pscustomobject]@{ source = 'lastName'; target = 'Surname'; enabled = $false; required = $true; transform = 'Trim' },
                [pscustomobject]@{ source = 'email'; target = 'UserPrincipalName'; enabled = $true; required = $true; transform = 'Lower' }
            )
        }

        $result = Get-SyncFactorsAttributeChanges -Worker $worker -ExistingUser $existing -MappingConfig $mapping

        $result.Changes.GivenName | Should -Be 'Chris'
        $result.Changes.UserPrincipalName | Should -Be 'chris.brien@example.com'
        $result.Changes.ContainsKey('Surname') | Should -BeFalse
        $result.MissingRequired.Count | Should -Be 0
    }

    It 'maps indexed nested Core HR attributes' {
        $worker = [pscustomobject]@{
            employmentNav = @(
                [pscustomobject]@{
                    jobInfoNav = @(
                        [pscustomobject]@{
                            jobTitle = 'Director of Infrastructure'
                            costCenter = '  IT-1001  '
                        }
                    )
                }
            )
        }

        $existing = [pscustomobject]@{
            title = 'Infrastructure Director'
            extensionAttribute3 = 'IT-1000'
        }

        $mapping = [pscustomobject]@{
            mappings = @(
                [pscustomobject]@{ source = 'employmentNav[0].jobInfoNav[0].jobTitle'; target = 'title'; enabled = $true; required = $false; transform = 'Trim' },
                [pscustomobject]@{ source = 'employmentNav[0].jobInfoNav[0].costCenter'; target = 'extensionAttribute3'; enabled = $true; required = $false; transform = 'Trim' }
            )
        }

        $result = Get-SyncFactorsAttributeChanges -Worker $worker -ExistingUser $existing -MappingConfig $mapping

        $result.Changes.title | Should -Be 'Director of Infrastructure'
        $result.Changes.extensionAttribute3 | Should -Be 'IT-1001'
        $result.MissingRequired.Count | Should -Be 0
    }
}

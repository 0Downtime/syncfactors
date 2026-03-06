Describe 'Get-NestedValue' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Mapping.psm1" -Force
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
}

Describe 'Get-SfAdAttributeChanges' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Mapping.psm1" -Force
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

        $result = Get-SfAdAttributeChanges -Worker $worker -ExistingUser $existing -MappingConfig $mapping

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

        $result = Get-SfAdAttributeChanges -Worker $worker -ExistingUser $existing -MappingConfig $mapping

        $result.Changes.title | Should -Be 'Director of Infrastructure'
        $result.Changes.extensionAttribute3 | Should -Be 'IT-1001'
        $result.MissingRequired.Count | Should -Be 0
    }
}

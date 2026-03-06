Describe 'Get-SfAdSyncConfig' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Config.psm1" -Force
    }

    It 'loads the sample config successfully' {
        $configPath = Join-Path $PSScriptRoot '../config/sample.sync-config.json'
        $config = Get-SfAdSyncConfig -Path $configPath
        $config.successFactors.baseUrl | Should -Not -BeNullOrEmpty
        $config.ad.graveyardOu | Should -Not -BeNullOrEmpty
    }
}

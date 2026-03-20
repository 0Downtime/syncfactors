Describe 'Get-SfAdSyncConfig' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Config.psm1" -Force
    }

    It 'loads the sample config successfully' {
        $configPath = Join-Path $PSScriptRoot '../config/sample.real-successfactors.real-ad.sync-config.json'
        $config = Get-SfAdSyncConfig -Path $configPath
        $config.successFactors.baseUrl | Should -Not -BeNullOrEmpty
        $config.ad.graveyardOu | Should -Not -BeNullOrEmpty
    }

    It 'prefers environment variables for secret values' {
        $configPath = Join-Path $TestDrive 'sync-config.json'
        @'
{
  "secrets": {
    "successFactorsClientIdEnv": "TEST_SF_CLIENT_ID",
    "successFactorsClientSecretEnv": "TEST_SF_CLIENT_SECRET",
    "adServerEnv": "TEST_AD_SERVER",
    "adUsernameEnv": "TEST_AD_USERNAME",
    "adBindPasswordEnv": "TEST_AD_BIND_PASSWORD",
    "defaultAdPasswordEnv": "TEST_AD_PASSWORD"
  },
  "successFactors": {
    "baseUrl": "https://example.successfactors.com/odata/v2",
    "oauth": {
      "tokenUrl": "https://example.successfactors.com/oauth/token",
      "clientId": "config-client-id",
      "clientSecret": "config-client-secret"
    },
    "query": {
      "entitySet": "PerPerson",
      "identityField": "personIdExternal",
      "deltaField": "lastModifiedDateTime",
      "select": [ "personIdExternal" ],
      "expand": [ "employmentNav" ]
    }
  },
  "ad": {
    "server": "config-dc.example.com",
    "username": "config-user",
    "bindPassword": "config-bind-password",
    "identityAttribute": "employeeID",
    "defaultActiveOu": "OU=Employees,DC=example,DC=com",
    "graveyardOu": "OU=Graveyard,DC=example,DC=com",
    "defaultPassword": "config-password"
  },
  "sync": {
    "enableBeforeStartDays": 7,
    "deletionRetentionDays": 90
  },
  "state": {
    "path": ".\\state\\sync-state.json"
  },
  "reporting": {
    "outputDirectory": ".\\reports\\output"
  }
}
'@ | Set-Content -Path $configPath

        [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_ID', 'env-client-id')
        [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_SECRET', 'env-client-secret')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_SERVER', 'env-dc.example.com')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_USERNAME', 'env-user')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_BIND_PASSWORD', 'env-bind-password')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_PASSWORD', 'env-password')

        try {
            $config = Get-SfAdSyncConfig -Path $configPath
            $config.successFactors.oauth.clientId | Should -Be 'env-client-id'
            $config.successFactors.oauth.clientSecret | Should -Be 'env-client-secret'
            $config.ad.server | Should -Be 'env-dc.example.com'
            $config.ad.username | Should -Be 'env-user'
            $config.ad.bindPassword | Should -Be 'env-bind-password'
            $config.ad.defaultPassword | Should -Be 'env-password'
        } finally {
            [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_ID', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_SECRET', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_SERVER', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_USERNAME', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_BIND_PASSWORD', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_PASSWORD', $null)
        }
    }

    It 'prefers environment variables for basic auth secret values' {
        $configPath = Join-Path $TestDrive 'sync-config-basic-auth.json'
        @'
{
  "secrets": {
    "successFactorsUsernameEnv": "TEST_SF_USERNAME",
    "successFactorsPasswordEnv": "TEST_SF_PASSWORD",
    "defaultAdPasswordEnv": "TEST_AD_PASSWORD"
  },
  "successFactors": {
    "baseUrl": "https://example.successfactors.com/odata/v2",
    "auth": {
      "basic": {
        "username": "config-username",
        "password": "config-password"
      }
    },
    "query": {
      "entitySet": "PerPerson",
      "identityField": "personIdExternal",
      "deltaField": "lastModifiedDateTime",
      "select": [ "personIdExternal" ],
      "expand": [ "employmentNav" ]
    }
  },
  "ad": {
    "identityAttribute": "employeeID",
    "defaultActiveOu": "OU=Employees,DC=example,DC=com",
    "graveyardOu": "OU=Graveyard,DC=example,DC=com",
    "defaultPassword": "config-ad-password"
  },
  "sync": {
    "enableBeforeStartDays": 7,
    "deletionRetentionDays": 90
  },
  "state": {
    "path": ".\\state\\sync-state.json"
  },
  "reporting": {
    "outputDirectory": ".\\reports\\output"
  }
}
'@ | Set-Content -Path $configPath

        [System.Environment]::SetEnvironmentVariable('TEST_SF_USERNAME', 'env-username')
        [System.Environment]::SetEnvironmentVariable('TEST_SF_PASSWORD', 'env-password')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_PASSWORD', 'env-ad-password')

        try {
            $config = Get-SfAdSyncConfig -Path $configPath
            $config.successFactors.auth.mode | Should -Be 'basic'
            $config.successFactors.auth.basic.username | Should -Be 'env-username'
            $config.successFactors.auth.basic.password | Should -Be 'env-password'
            $config.ad.defaultPassword | Should -Be 'env-ad-password'
        } finally {
            [System.Environment]::SetEnvironmentVariable('TEST_SF_USERNAME', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_SF_PASSWORD', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_PASSWORD', $null)
        }
    }

    It 'ignores blank environment variable secrets and keeps configured values' {
        $configPath = Join-Path $TestDrive 'sync-config-blank-env.json'
        @'
{
  "secrets": {
    "successFactorsClientIdEnv": "TEST_SF_CLIENT_ID_BLANK",
    "successFactorsClientSecretEnv": "TEST_SF_CLIENT_SECRET_BLANK",
    "adServerEnv": "TEST_AD_SERVER_BLANK",
    "adUsernameEnv": "TEST_AD_USERNAME_BLANK",
    "adBindPasswordEnv": "TEST_AD_BIND_PASSWORD_BLANK",
    "defaultAdPasswordEnv": "TEST_AD_PASSWORD_BLANK"
  },
  "successFactors": {
    "baseUrl": "https://example.successfactors.com/odata/v2",
    "oauth": {
      "tokenUrl": "https://example.successfactors.com/oauth/token",
      "clientId": "config-client-id",
      "clientSecret": "config-client-secret"
    },
    "query": {
      "entitySet": "PerPerson",
      "identityField": "personIdExternal",
      "deltaField": "lastModifiedDateTime",
      "select": [ "personIdExternal" ],
      "expand": [ "employmentNav" ]
    }
  },
  "ad": {
    "server": "config-dc.example.com",
    "username": "config-user",
    "bindPassword": "config-bind-password",
    "identityAttribute": "employeeID",
    "defaultActiveOu": "OU=Employees,DC=example,DC=com",
    "graveyardOu": "OU=Graveyard,DC=example,DC=com",
    "defaultPassword": "config-password"
  },
  "sync": {
    "enableBeforeStartDays": 7,
    "deletionRetentionDays": 90
  },
  "state": {
    "path": ".\\state\\sync-state.json"
  },
  "reporting": {
    "outputDirectory": ".\\reports\\output"
  }
}
'@ | Set-Content -Path $configPath

        [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_ID_BLANK', ' ')
        [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_SECRET_BLANK', '')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_SERVER_BLANK', '   ')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_USERNAME_BLANK', '')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_BIND_PASSWORD_BLANK', ' ')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_PASSWORD_BLANK', '')

        try {
            $config = Get-SfAdSyncConfig -Path $configPath
            $config.successFactors.oauth.clientId | Should -Be 'config-client-id'
            $config.successFactors.oauth.clientSecret | Should -Be 'config-client-secret'
            $config.ad.server | Should -Be 'config-dc.example.com'
            $config.ad.username | Should -Be 'config-user'
            $config.ad.bindPassword | Should -Be 'config-bind-password'
            $config.ad.defaultPassword | Should -Be 'config-password'
        } finally {
            [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_ID_BLANK', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_SF_CLIENT_SECRET_BLANK', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_SERVER_BLANK', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_USERNAME_BLANK', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_BIND_PASSWORD_BLANK', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_PASSWORD_BLANK', $null)
        }
    }

    It 'rejects alternate AD credentials without a server' {
        $configPath = Join-Path $TestDrive 'invalid-ad-server-sync-config.json'
        @'
{
  "successFactors": {
    "baseUrl": "https://example.successfactors.com/odata/v2",
    "oauth": {
      "tokenUrl": "https://example.successfactors.com/oauth/token",
      "clientId": "client-id",
      "clientSecret": "client-secret"
    },
    "query": {
      "entitySet": "PerPerson",
      "identityField": "personIdExternal",
      "deltaField": "lastModifiedDateTime",
      "select": [ "personIdExternal" ],
      "expand": [ "employmentNav" ]
    }
  },
  "ad": {
    "username": "EXAMPLE\\svc_sfadsync",
    "bindPassword": "password",
    "identityAttribute": "employeeID",
    "defaultActiveOu": "OU=Employees,DC=example,DC=com",
    "graveyardOu": "OU=Graveyard,DC=example,DC=com",
    "defaultPassword": "password"
  },
  "sync": {
    "enableBeforeStartDays": 7,
    "deletionRetentionDays": 90
  },
  "state": {
    "path": ".\\state\\sync-state.json"
  },
  "reporting": {
    "outputDirectory": ".\\reports\\output"
  }
}
'@ | Set-Content -Path $configPath

        { Get-SfAdSyncConfig -Path $configPath } | Should -Throw '*ad.server*'
    }

    It 'rejects environment-provided AD credentials without an environment-provided server' {
        $configPath = Join-Path $TestDrive 'invalid-ad-server-env-sync-config.json'
        @'
{
  "secrets": {
    "adServerEnv": "TEST_AD_SERVER_ENV_ONLY",
    "adUsernameEnv": "TEST_AD_USERNAME_ENV_ONLY",
    "adBindPasswordEnv": "TEST_AD_BIND_PASSWORD_ENV_ONLY"
  },
  "successFactors": {
    "baseUrl": "https://example.successfactors.com/odata/v2",
    "oauth": {
      "tokenUrl": "https://example.successfactors.com/oauth/token",
      "clientId": "client-id",
      "clientSecret": "client-secret"
    },
    "query": {
      "entitySet": "PerPerson",
      "identityField": "personIdExternal",
      "deltaField": "lastModifiedDateTime",
      "select": [ "personIdExternal" ],
      "expand": [ "employmentNav" ]
    }
  },
  "ad": {
    "identityAttribute": "employeeID",
    "defaultActiveOu": "OU=Employees,DC=example,DC=com",
    "graveyardOu": "OU=Graveyard,DC=example,DC=com",
    "defaultPassword": "password"
  },
  "sync": {
    "enableBeforeStartDays": 7,
    "deletionRetentionDays": 90
  },
  "state": {
    "path": ".\\state\\sync-state.json"
  },
  "reporting": {
    "outputDirectory": ".\\reports\\output"
  }
}
'@ | Set-Content -Path $configPath

        [System.Environment]::SetEnvironmentVariable('TEST_AD_SERVER_ENV_ONLY', '')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_USERNAME_ENV_ONLY', 'env-user')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_BIND_PASSWORD_ENV_ONLY', 'env-bind-password')

        try {
            { Get-SfAdSyncConfig -Path $configPath } | Should -Throw '*ad.server*'
        } finally {
            [System.Environment]::SetEnvironmentVariable('TEST_AD_SERVER_ENV_ONLY', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_USERNAME_ENV_ONLY', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_BIND_PASSWORD_ENV_ONLY', $null)
        }
    }

    It 'rejects an environment-provided AD username without an environment-provided bind password' {
        $configPath = Join-Path $TestDrive 'invalid-ad-bind-password-env-sync-config.json'
        @'
{
  "secrets": {
    "adServerEnv": "TEST_AD_SERVER_ENV_MISSING_PASSWORD",
    "adUsernameEnv": "TEST_AD_USERNAME_ENV_MISSING_PASSWORD",
    "adBindPasswordEnv": "TEST_AD_BIND_PASSWORD_ENV_MISSING_PASSWORD"
  },
  "successFactors": {
    "baseUrl": "https://example.successfactors.com/odata/v2",
    "oauth": {
      "tokenUrl": "https://example.successfactors.com/oauth/token",
      "clientId": "client-id",
      "clientSecret": "client-secret"
    },
    "query": {
      "entitySet": "PerPerson",
      "identityField": "personIdExternal",
      "deltaField": "lastModifiedDateTime",
      "select": [ "personIdExternal" ],
      "expand": [ "employmentNav" ]
    }
  },
  "ad": {
    "identityAttribute": "employeeID",
    "defaultActiveOu": "OU=Employees,DC=example,DC=com",
    "graveyardOu": "OU=Graveyard,DC=example,DC=com",
    "defaultPassword": "password"
  },
  "sync": {
    "enableBeforeStartDays": 7,
    "deletionRetentionDays": 90
  },
  "state": {
    "path": ".\\state\\sync-state.json"
  },
  "reporting": {
    "outputDirectory": ".\\reports\\output"
  }
}
'@ | Set-Content -Path $configPath

        [System.Environment]::SetEnvironmentVariable('TEST_AD_SERVER_ENV_MISSING_PASSWORD', 'dc01.example.com')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_USERNAME_ENV_MISSING_PASSWORD', 'env-user')
        [System.Environment]::SetEnvironmentVariable('TEST_AD_BIND_PASSWORD_ENV_MISSING_PASSWORD', '')

        try {
            { Get-SfAdSyncConfig -Path $configPath } | Should -Throw '*ad.bindPassword*'
        } finally {
            [System.Environment]::SetEnvironmentVariable('TEST_AD_SERVER_ENV_MISSING_PASSWORD', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_USERNAME_ENV_MISSING_PASSWORD', $null)
            [System.Environment]::SetEnvironmentVariable('TEST_AD_BIND_PASSWORD_ENV_MISSING_PASSWORD', $null)
        }
    }

    It 'rejects config missing nested required values' {
        $configPath = Join-Path $TestDrive 'invalid-sync-config.json'
        @'
{
  "successFactors": {
    "baseUrl": "https://example.successfactors.com/odata/v2",
    "oauth": {
      "tokenUrl": "",
      "clientId": "client-id",
      "clientSecret": "client-secret"
    },
    "query": {
      "entitySet": "PerPerson",
      "identityField": "personIdExternal",
      "deltaField": "lastModifiedDateTime",
      "select": [ "personIdExternal" ],
      "expand": [ "employmentNav" ]
    }
  },
  "ad": {
    "identityAttribute": "employeeID",
    "defaultActiveOu": "OU=Employees,DC=example,DC=com",
    "graveyardOu": "OU=Graveyard,DC=example,DC=com",
    "defaultPassword": "password"
  },
  "sync": {
    "enableBeforeStartDays": 7,
    "deletionRetentionDays": 90
  },
  "state": {
    "path": ".\\state\\sync-state.json"
  },
  "reporting": {
    "outputDirectory": ".\\reports\\output"
  }
}
'@ | Set-Content -Path $configPath

        { Get-SfAdSyncConfig -Path $configPath } | Should -Throw '*successFactors.auth.oauth.tokenUrl*'
    }
}

Describe 'Get-SfAdSyncMappingConfig' {
    BeforeAll {
        Import-Module "$PSScriptRoot/../src/Modules/SfAdSync/Config.psm1" -Force
    }

    It 'rejects unsupported transforms' {
        $mappingPath = Join-Path $TestDrive 'invalid-mapping.json'
        @'
{
  "mappings": [
    {
      "source": "firstName",
      "target": "givenName",
      "enabled": true,
      "required": true,
      "transform": "SnakeCase"
    }
  ]
}
'@ | Set-Content -Path $mappingPath

        { Get-SfAdSyncMappingConfig -Path $mappingPath } | Should -Throw '*unsupported transform*'
    }
}

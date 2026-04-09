using Microsoft.Extensions.Logging.Abstractions;
using SyncFactors.Contracts;
using SyncFactors.Domain;
using SyncFactors.Infrastructure;

namespace SyncFactors.Infrastructure.Tests;

public sealed class AttributeDiffServiceTests
{
    [Fact]
    public async Task BuildDiffAsync_FormatsODataDateLiteral_WhenConfiguredAsDateOnly()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-attribute-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": ["personIdExternal"],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": [
            {
              "source": "startDate",
              "target": "extensionAttribute1",
              "enabled": true,
              "required": false,
              "transform": "DateOnly"
            }
          ]
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        var diffService = new AttributeDiffService(mappingProvider, new NoopWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance);

        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["startDate"] = "/Date(1777772800000)/"
            });

        var changes = await diffService.BuildDiffAsync(
            worker,
            directoryUser: null,
            proposedEmailAddress: null,
            logPath: null,
            CancellationToken.None);

        var startDateChange = Assert.Single(changes, change => change.Attribute == "extensionAttribute1");
        Assert.Equal("2026-05-03", startDateChange.After);
        Assert.True(startDateChange.Changed);
    }

    [Fact]
    public async Task BuildDiffAsync_StripsCommasAndPeriods_WhenConfigured()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-attribute-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": ["personIdExternal"],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": [
            {
              "source": "employmentNav[0].jobInfoNav[0].companyNav.name_localized",
              "target": "company",
              "enabled": true,
              "required": false,
              "transform": "TrimStripCommasPeriods"
            }
          ]
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        var diffService = new AttributeDiffService(mappingProvider, new NoopWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance);

        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employmentNav[0].jobInfoNav[0].companyNav.name_localized"] = "Example Services, Inc."
            });

        var changes = await diffService.BuildDiffAsync(
            worker,
            directoryUser: null,
            proposedEmailAddress: null,
            logPath: null,
            CancellationToken.None);

        var companyChange = Assert.Single(changes, change => change.Attribute == "company");
        Assert.Equal("Example Services Inc", companyChange.After);
        Assert.True(companyChange.Changed);
    }

    [Fact]
    public async Task BuildDiffAsync_ConcatenatesCostCenterFields_WhenConfigured()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-attribute-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": ["personIdExternal"],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": [
            {
              "source": "Concat(employmentNav[0].jobInfoNav[0].costCenterNav.externalCode, employmentNav[0].jobInfoNav[0].costCenterNav.description_localized)",
              "target": "department",
              "enabled": true,
              "required": false,
              "transform": "Trim"
            }
          ]
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        var diffService = new AttributeDiffService(mappingProvider, new NoopWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance);

        var worker = new WorkerSnapshot(
            WorkerId: "44522",
            PreferredName: "Christopher",
            LastName: "Brien",
            Department: "Infrastructure & Security",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["employmentNav[0].jobInfoNav[0].costCenterNav.externalCode"] = "10450",
                ["employmentNav[0].jobInfoNav[0].costCenterNav.description_localized"] = "Information Technology"
            });

        var changes = await diffService.BuildDiffAsync(
            worker,
            directoryUser: null,
            proposedEmailAddress: null,
            logPath: null,
            CancellationToken.None);

        var departmentChange = Assert.Single(changes, change => change.Attribute == "department");
        Assert.Equal("10450 Information Technology", departmentChange.After);
        Assert.True(departmentChange.Changed);
    }

    [Fact]
    public async Task BuildDiffAsync_PreservesExistingEmailTargets_ForMatchedUsers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-attribute-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": ["personIdExternal"],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": []
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        var diffService = new AttributeDiffService(mappingProvider, new NoopWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance);

        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "John",
            LastName: "Smith",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "10001",
            DistinguishedName: "CN=Smith\\, John,OU=LabUsers,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Smith, John",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["UserPrincipalName"] = "existing.upn@Exampleenergy.com",
                ["mail"] = "existing.mail@Exampleenergy.com"
            });

        var changes = await diffService.BuildDiffAsync(
            worker,
            directoryUser,
            proposedEmailAddress: "john.smith2@Exampleenergy.com",
            logPath: null,
            CancellationToken.None);

        var upnChange = Assert.Single(changes, change => change.Attribute == "UserPrincipalName");
        var mailChange = Assert.Single(changes, change => change.Attribute == "mail");
        Assert.Equal("existing.upn@Exampleenergy.com", upnChange.After);
        Assert.False(upnChange.Changed);
        Assert.Equal("existing.mail@Exampleenergy.com", mailChange.After);
        Assert.False(mailChange.Changed);
    }

    [Fact]
    public async Task BuildDiffAsync_UsesResolvedEmailAddressForEmailTargets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-attribute-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": ["personIdExternal"],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": [
            {
              "source": "emailNav[?(@.isPrimary == true)].emailAddress",
              "target": "UserPrincipalName",
              "enabled": true,
              "required": false,
              "transform": "Trim"
            },
            {
              "source": "emailNav[?(@.isPrimary == true)].emailAddress",
              "target": "mail",
              "enabled": true,
              "required": false,
              "transform": "Trim"
            }
          ]
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        var diffService = new AttributeDiffService(mappingProvider, new NoopWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance);

        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "10001",
            DistinguishedName: "CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Sample101, Winnie",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["displayName"] = "Sample101, Winnie",
                ["UserPrincipalName"] = "winnie.sample1012@Exampleenergy.com",
                ["mail"] = "winnie.sample1012@Exampleenergy.com"
            });

        var changes = await diffService.BuildDiffAsync(
            worker,
            directoryUser,
            proposedEmailAddress: "winnie.sample1012@Exampleenergy.com",
            logPath: null,
            CancellationToken.None);

        Assert.Collection(
            changes.Where(change => change.Attribute is "displayName" or "UserPrincipalName" or "mail")
                .OrderBy(change => change.Attribute, StringComparer.Ordinal),
            change =>
            {
                Assert.Equal("UserPrincipalName", change.Attribute);
                Assert.Equal("winnie.sample1012@Exampleenergy.com", change.Before);
                Assert.Equal("winnie.sample1012@Exampleenergy.com", change.After);
                Assert.False(change.Changed);
            },
            change =>
            {
                Assert.Equal("displayName", change.Attribute);
                Assert.Equal("Sample101, Winnie", change.Before);
                Assert.Equal("Sample101, Winnie", change.After);
                Assert.False(change.Changed);
            },
            change =>
            {
                Assert.Equal("mail", change.Attribute);
                Assert.Equal("winnie.sample1012@Exampleenergy.com", change.Before);
                Assert.Equal("winnie.sample1012@Exampleenergy.com", change.After);
                Assert.False(change.Changed);
            });
    }

    [Fact]
    public async Task BuildDiffAsync_IncludesEnabledOfficeAddressMappings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-attribute-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": ["personIdExternal"],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": [
            {
              "source": "employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.address1",
              "target": "streetAddress",
              "enabled": true,
              "required": false,
              "transform": "Trim"
            },
            {
              "source": "employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.city",
              "target": "l",
              "enabled": true,
              "required": false,
              "transform": "Trim"
            },
            {
              "source": "employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.zipCode",
              "target": "postalCode",
              "enabled": true,
              "required": false,
              "transform": "Trim"
            }
          ]
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        var diffService = new AttributeDiffService(mappingProvider, new NoopWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance);

        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["officeLocationAddress"] = "1 Main St",
                ["officeLocationCity"] = "New York",
                ["officeLocationZipCode"] = "10001",
                ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.address1"] = "1 Main St",
                ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.city"] = "New York",
                ["employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.zipCode"] = "10001"
            });

        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "10001",
            DistinguishedName: "CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Sample101, Winnie",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["displayName"] = "Sample101, Winnie",
                ["streetAddress"] = "Old Office",
                ["l"] = "Old City",
                ["postalCode"] = "99999"
            });

        var changes = await diffService.BuildDiffAsync(worker, directoryUser, proposedEmailAddress: null, logPath: null, CancellationToken.None);

        Assert.Collection(
            changes.Where(change => change.Attribute is "displayName" or "l" or "postalCode" or "streetAddress")
                .OrderBy(change => change.Attribute, StringComparer.Ordinal),
            change =>
            {
                Assert.Equal("displayName", change.Attribute);
                Assert.Equal("Sample101, Winnie", change.Before);
                Assert.Equal("Sample101, Winnie", change.After);
                Assert.False(change.Changed);
            },
            change =>
            {
                Assert.Equal("l", change.Attribute);
                Assert.Equal("Old City", change.Before);
                Assert.Equal("New York", change.After);
                Assert.True(change.Changed);
            },
            change =>
            {
                Assert.Equal("postalCode", change.Attribute);
                Assert.Equal("99999", change.Before);
                Assert.Equal("10001", change.After);
                Assert.True(change.Changed);
            },
            change =>
            {
                Assert.Equal("streetAddress", change.Attribute);
                Assert.Equal("Old Office", change.Before);
                Assert.Equal("1 Main St", change.After);
                Assert.True(change.Changed);
            });
    }

    [Fact]
    public async Task BuildDiffAsync_IncludesSystemManagedIdentityRows_WhenMappingsDoNotDeclareThem()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-attribute-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": ["personIdExternal"],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": [
            {
              "source": "department",
              "target": "department",
              "enabled": true,
              "required": false,
              "transform": "Trim"
            }
          ]
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        var diffService = new AttributeDiffService(mappingProvider, new NoopWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance);

        var worker = new WorkerSnapshot(
            WorkerId: "10001",
            PreferredName: "Winnie",
            LastName: "Sample101",
            Department: "Information Technology",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "10001",
            DistinguishedName: "CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com",
            Enabled: true,
            DisplayName: "Old Display",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["displayName"] = "Old Display",
                ["UserPrincipalName"] = "old.email@Exampleenergy.com",
                ["mail"] = "old.email@Exampleenergy.com",
                ["department"] = "Old Department"
            });

        var changes = await diffService.BuildDiffAsync(
            worker,
            directoryUser,
            proposedEmailAddress: "preview.email@Exampleenergy.com",
            logPath: null,
            CancellationToken.None);

        Assert.Collection(
            changes.Where(change => change.Attribute is "displayName" or "department" or "UserPrincipalName" or "mail")
                .OrderBy(change => change.Attribute, StringComparer.Ordinal),
            change =>
            {
                Assert.Equal("UserPrincipalName", change.Attribute);
                Assert.Equal("old.email@Exampleenergy.com", change.Before);
                Assert.Equal("old.email@Exampleenergy.com", change.After);
                Assert.False(change.Changed);
            },
            change =>
            {
                Assert.Equal("department", change.Attribute);
                Assert.Equal("Old Department", change.Before);
                Assert.Equal("Information Technology", change.After);
                Assert.True(change.Changed);
            },
            change =>
            {
                Assert.Equal("displayName", change.Attribute);
                Assert.Equal("Old Display", change.Before);
                Assert.Equal("Sample101, Winnie", change.After);
                Assert.True(change.Changed);
            },
            change =>
            {
                Assert.Equal("mail", change.Attribute);
                Assert.Equal("old.email@Exampleenergy.com", change.Before);
                Assert.Equal("old.email@Exampleenergy.com", change.After);
                Assert.False(change.Changed);
            });
    }

    [Fact]
    public async Task BuildDiffAsync_IncludesSamAccountNameAndCnSystemRows()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "syncfactors-attribute-diff", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var configPath = Path.Combine(tempRoot, "sync-config.json");
        var mappingConfigPath = Path.Combine(tempRoot, "mapping-config.json");

        await File.WriteAllTextAsync(configPath, """
        {
          "secrets": {
            "adServerEnv": null,
            "adUsernameEnv": null,
            "adBindPasswordEnv": null
          },
          "successFactors": {
            "baseUrl": "http://example.test/odata/v2",
            "auth": {
              "mode": "basic",
              "basic": {
                "username": "mock-user",
                "password": "mock-password"
              }
            },
            "query": {
              "entitySet": "PerPerson",
              "identityField": "personIdExternal",
              "deltaField": "lastModifiedDateTime",
              "select": ["personIdExternal"],
              "expand": []
            }
          },
          "ad": {
            "server": "ldap.example.test",
            "username": "",
            "bindPassword": "",
            "identityAttribute": "employeeID",
            "defaultActiveOu": "OU=LabUsers,DC=example,DC=com",
            "prehireOu": "OU=Prehire,DC=example,DC=com",
            "graveyardOu": "OU=LabGraveyard,DC=example,DC=com"
          },
          "sync": {
            "enableBeforeStartDays": 7,
            "deletionRetentionDays": 90
          },
          "safety": {
            "maxCreatesPerRun": 10,
            "maxDisablesPerRun": 10,
            "maxDeletionsPerRun": 10
          },
          "reporting": {
            "outputDirectory": "/tmp"
          }
        }
        """);

        await File.WriteAllTextAsync(mappingConfigPath, """
        {
          "mappings": []
        }
        """);

        var loader = new SyncFactorsConfigurationLoader(new SyncFactorsConfigPathResolver(configPath, mappingConfigPath));
        var mappingProvider = new AttributeMappingProvider(loader, NullLogger<AttributeMappingProvider>.Instance);
        var diffService = new AttributeDiffService(mappingProvider, new NoopWorkerPreviewLogWriter(), NullLogger<AttributeDiffService>.Instance);

        var worker = new WorkerSnapshot(
            WorkerId: "00051",
            PreferredName: "David",
            LastName: "LaRussa",
            Department: "IT",
            TargetOu: "OU=LabUsers,DC=example,DC=com",
            IsPrehire: false,
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

        var directoryUser = new DirectoryUserSnapshot(
            SamAccountName: "00051",
            DistinguishedName: "CN=LaRussa\\, David,OU=LabUsers,DC=example,DC=com",
            Enabled: true,
            DisplayName: "LaRussa, David",
            Attributes: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sAMAccountName"] = "00051",
                ["cn"] = "LaRussa, David",
                ["displayName"] = "LaRussa, David"
            });

        var changes = await diffService.BuildDiffAsync(
            worker,
            directoryUser,
            proposedEmailAddress: "david.larussa@example.com",
            logPath: null,
            CancellationToken.None);

        var samRow = Assert.Single(changes.Where(change => change.Attribute == "sAMAccountName"));
        Assert.Equal("00051", samRow.Before);
        Assert.Equal("00051", samRow.After);
        Assert.False(samRow.Changed);

        var cnRow = Assert.Single(changes.Where(change => change.Attribute == "cn"));
        Assert.Equal("LaRussa, David", cnRow.Before);
        Assert.Equal("00051", cnRow.After);
        Assert.True(cnRow.Changed);
    }

    private sealed class NoopWorkerPreviewLogWriter : IWorkerPreviewLogWriter
    {
        public string CreateLogPath(string workerId, DateTimeOffset startedAt)
        {
            _ = workerId;
            _ = startedAt;
            return "/tmp/preview.jsonl";
        }

        public Task AppendAsync(string logPath, WorkerPreviewLogEntry entry, CancellationToken cancellationToken)
        {
            _ = logPath;
            _ = entry;
            _ = cancellationToken;
            return Task.CompletedTask;
        }
    }
}

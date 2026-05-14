# EmpJob To AD Mapping

This table reflects the tenant-confirmed `EmpJob` field labels from API Center, the normalized source keys the current sync client exposes, and the default mappings in `config/sample.empjob-confirmed.mapping-config.json`.

| Business field | Confirmed SuccessFactors field | Current source key | Recommended AD target | Default |
| --- | --- | --- | --- | --- |
| Login or worker identifier | `PerPerson.personIdExternal` | `personIdExternal` | `sAMAccountName` | Enabled, required |
| Legal first name | `PerPersonal.firstName` | `firstName` | `GivenName` | Enabled, required |
| Legal last name | `PerPersonal.lastName` | `lastName` | `Surname` | Enabled, required |
| Business email | `PerEmail.emailAddress` | `email` | `UserPrincipalName`, `mail` | Enabled, `UserPrincipalName` required |
| Job title | `EmpJob.jobTitle` | `jobTitle` | `title` | Enabled |
| Company name | `FOCompany.name_localized` | `company` | `company` | Enabled |
| Division or function | `FODivision.name_localized` | `division` | `division` | Enabled |
| Department display | `FOCostCenter.externalCode` + `FOCostCenter.description_localized` | `Concat(costCenterId, costCenterDescription)` | `department` | Enabled |
| Location name | `FOLocation.name` | `location` | `physicalDeliveryOfficeName` | Enabled |
| Employee Type | `EmpJob.employeeType` | `employeeType` | `employeeType` | Disabled |
| Business Unit Name | `FOBusinessUnit.name_localized` | `businessUnit` | `extensionAttribute2` | Disabled |
| Cost Center Code | `FOCostCenter.externalCode` | `costCenterId` | `extensionAttribute3` | Disabled |
| Employee Class | `EmpJob.employeeClass` | `employeeClass` | `extensionAttribute4` | Disabled |
| Region | `EmpJob.customString87` | `region` | `extensionAttribute5` | Disabled |
| Geozone | `EmpJob.customString110` | `geozone` | `extensionAttribute6` | Disabled |
| People Group | `EmpJob.customString3` | `peopleGroup` | `extensionAttribute7` | Disabled |
| Leadership Level | `EmpJob.customString20` | `leadershipLevel` | `extensionAttribute8` | Disabled |
| Bargaining Unit | `EmpJob.customString111` | `bargainingUnit` | `extensionAttribute9` | Disabled |
| Union Job Code | `EmpJob.customString91` | `unionJobCode` | `extensionAttribute10` | Disabled |
| Most Recent Hire Date | `EmpEmployment.startDate` | `startDate` | `extensionAttribute1` | Disabled |
| Office street | `FOLocation.addressNavDEFLT.address1` | `officeLocationAddress` | `streetAddress` | Enabled |
| Office city | `FOLocation.addressNavDEFLT.city` | `officeLocationCity` | `l` | Enabled |
| Office postal code | `FOLocation.addressNavDEFLT.zipCode` | `officeLocationZipCode` | `postalCode` | Enabled |

## Notes

- The default `department` mapping now uses `Concat(costCenterId, costCenterDescription)`, not the department navigation label. The parser still exposes `department`, `departmentnew`, and `employmentNav[0].jobInfoNav[0].departmentNav.*` values for tenants that want a different mapping.
- Division or function resolves through `divisionNav.name_localized` before falling back to direct `EmpJob` string fields.
- The standard AD Address tab is intentionally fed from office location data only: `streetAddress`, `l`, and `postalCode`. Personal `userNav` address fields remain unmapped in this pass.
- `Supervisor` should not be mapped directly from `managerId` into AD. AD `manager` requires resolving the manager to an AD distinguished name first.
- `Direct Reports` should not be synced as an attribute; AD derives it from `manager`.
- The worker parser exposes both normalized keys such as `region` and path-style keys such as `employmentNav[0].jobInfoNav[0].customString87`.
- `customString112` (`Cintas Uniform Category`) and `customString113` (`Cintas Uniform Allotment`) are available in the parser but intentionally not mapped into AD by default.
- `personIdExternal` is the default `sAMAccountName` input. If a tenant also wants AD `employeeID`, add a separate mapping after validating naming and immutability rules.

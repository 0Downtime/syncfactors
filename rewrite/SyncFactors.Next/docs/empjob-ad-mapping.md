# EmpJob To AD Mapping

This table reflects the tenant-confirmed `EmpJob` field labels from API Center and the source keys the current sync client can consume today.

| Business field | Confirmed SuccessFactors field | Current source key | Recommended AD target | Default |
| --- | --- | --- | --- | --- |
| Employee ID | `PerPerson.personIdExternal` | `personIdExternal` | `employeeID` | Enabled |
| Legal First Name | `PerPersonal.firstName` | `firstName` | `GivenName` | Enabled |
| Legal Last Name | `PerPersonal.lastName` | `lastName` | `Surname` | Enabled |
| Business Email | `PerEmail.emailAddress` | `email` | `UserPrincipalName`, `mail` | Enabled |
| Job Title | `EmpJob.jobTitle` | `employmentNav[0].jobInfoNav[0].jobTitle` | `title` | Enabled |
| Company Name | `EmpJob.company` / `companyNav` | `employmentNav[0].jobInfoNav[0].companyNav.company` | `company` | Enabled |
| Function | `EmpJob.division` | `employmentNav[0].jobInfoNav[0].divisionNav.division` | `division` | Enabled |
| Sub Function | `EmpJob.department` | `employmentNav[0].jobInfoNav[0].departmentNav.department` | `department` | Enabled |
| Location Name | `EmpJob.location` / `locationNav` | `employmentNav[0].jobInfoNav[0].locationNav.LocationName` | `physicalDeliveryOfficeName` | Enabled |
| Employee Type | `EmpJob.employeeType` | `employeeType` | `employeeType` | Enabled |
| Business Unit Name | `EmpJob.businessUnit` / `businessUnitNav` | `businessUnit` | `extensionAttribute2` | Disabled |
| Cost Center | `EmpJob.costCenter` / `costCenterNav` | `costCenter` | `extensionAttribute3` | Disabled |
| Employee Class | `EmpJob.employeeClass` | `employeeClass` | `extensionAttribute4` | Disabled |
| Region | `EmpJob.customString87` | `region` | `extensionAttribute5` | Disabled |
| Geozone | `EmpJob.customString110` | `geozone` | `extensionAttribute6` | Disabled |
| People Group | `EmpJob.customString3` | `peopleGroup` | `extensionAttribute7` | Disabled |
| Leadership Level | `EmpJob.customString20` | `leadershipLevel` | `extensionAttribute8` | Disabled |
| Bargaining Unit | `EmpJob.customString111` | `bargainingUnit` | `extensionAttribute9` | Disabled |
| Union Job Code | `EmpJob.customString91` | `unionJobCode` | `extensionAttribute10` | Disabled |
| Most Recent Hire Date | `EmpEmployment.startDate` | `startDate` | `extensionAttribute1` | Disabled |
| Office Street | `locationNav.officeLocationAddress` | `employmentNav[0].jobInfoNav[0].locationNav.officeLocationAddress` | `streetAddress` | Disabled |
| Office City | `locationNav.officeLocationCity` | `employmentNav[0].jobInfoNav[0].locationNav.officeLocationCity` | `l` | Disabled |
| Office Postal Code | `locationNav.officeLocationZipCode` | `employmentNav[0].jobInfoNav[0].locationNav.officeLocationZipCode` | `postalCode` | Disabled |

## Notes

- `Function` and `Sub Function` are tenant labels on standard `EmpJob` fields: `division` and `department`.
- `Supervisor` should not be mapped directly from `managerId` into AD. AD `manager` requires resolving the manager to an AD distinguished name first.
- `Direct Reports` should not be synced as an attribute; AD derives it from `manager`.
- The worker parser now exposes the tenant-confirmed custom `EmpJob` fields used above.
- `customString112` (`Cintas Uniform Category`) and `customString113` (`Cintas Uniform Allotment`) are available in the parser but intentionally not mapped into AD by default.

# EmpJob To AD Mapping

This table reflects the tenant-confirmed `EmpJob` field labels from API Center and the source keys the current sync client can consume today.

| Business field | Confirmed SuccessFactors field | Current source key | Recommended AD target | Default |
| --- | --- | --- | --- | --- |
| Employee ID | `PerPerson.personIdExternal` | `personIdExternal` | `employeeID` | Disabled |
| Legal First Name | `PerPersonal.firstName` | `personalInfoNav[0].firstName` | `GivenName` | Enabled |
| Legal Last Name | `PerPersonal.lastName` | `personalInfoNav[0].lastName` | `Surname` | Enabled |
| Business Email | `PerEmail.emailAddress` | `emailNav[?(@.isPrimary == true)].emailAddress` | `UserPrincipalName`, `mail` | Enabled |
| Job Title | `EmpJob.jobTitle` | `employmentNav[0].jobInfoNav[0].jobTitle` | `title` | Enabled |
| Company Name | `FOCompany.name_localized` | `employmentNav[0].jobInfoNav[0].companyNav.name_localized` | `company` | Enabled |
| Function | `FODivision.name_localized` | `employmentNav[0].jobInfoNav[0].divisionNav.name_localized` | `division` | Enabled |
| Sub Function | `FODepartment.name_localized` | `employmentNav[0].jobInfoNav[0].departmentNav.name_localized` | `department` | Enabled |
| Location Name | `FOLocation.name` | `employmentNav[0].jobInfoNav[0].locationNav.name` | `physicalDeliveryOfficeName` | Enabled |
| Employee Type | `EmpJob.employeeType` | `employmentNav[0].jobInfoNav[0].employeeType` | `employeeType` | Enabled |
| Business Unit Name | `FOBusinessUnit.name_localized` | `employmentNav[0].jobInfoNav[0].businessUnitNav.name_localized` | `extensionAttribute2` | Disabled |
| Cost Center Code | `FOCostCenter.externalCode` | `employmentNav[0].jobInfoNav[0].costCenterNav.externalCode` | `extensionAttribute3` | Disabled |
| Employee Class | `EmpJob.employeeClass` | `employeeClass` | `extensionAttribute4` | Disabled |
| Region | `EmpJob.customString87` | `region` | `extensionAttribute5` | Disabled |
| Geozone | `EmpJob.customString110` | `geozone` | `extensionAttribute6` | Disabled |
| People Group | `EmpJob.customString3` | `peopleGroup` | `extensionAttribute7` | Disabled |
| Leadership Level | `EmpJob.customString20` | `leadershipLevel` | `extensionAttribute8` | Disabled |
| Bargaining Unit | `EmpJob.customString111` | `bargainingUnit` | `extensionAttribute9` | Disabled |
| Union Job Code | `EmpJob.customString91` | `unionJobCode` | `extensionAttribute10` | Disabled |
| Most Recent Hire Date | `EmpEmployment.startDate` | `startDate` | `extensionAttribute1` | Disabled |
| Office Street | `FOLocation.addressNavDEFLT.address1` | `employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.address1` | `streetAddress` | Enabled |
| Office City | `FOLocation.addressNavDEFLT.city` | `employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.city` | `l` | Enabled |
| Office Postal Code | `FOLocation.addressNavDEFLT.zipCode` | `employmentNav[0].jobInfoNav[0].locationNav.addressNavDEFLT.zipCode` | `postalCode` | Enabled |

## Notes

- `Function` and `Sub Function` now resolve through `divisionNav.name_localized` and `departmentNav.name_localized`, which matches the tenant export more closely than the older direct `EmpJob` string fields.
- `employeeID` is disabled by default in the mapping config because some environments cannot reliably read or write it during preview/apply. Re-enable it only if your AD topology and attribute availability are confirmed.
- The standard AD Address tab is intentionally fed from office location data only: `streetAddress`, `l`, and `postalCode`. Personal `userNav` address fields remain unmapped in this pass.
- `Supervisor` should not be mapped directly from `managerId` into AD. AD `manager` requires resolving the manager to an AD distinguished name first.
- `Direct Reports` should not be synced as an attribute; AD derives it from `manager`.
- The worker parser now exposes the tenant-confirmed custom `EmpJob` fields used above.
- `customString112` (`Cintas Uniform Category`) and `customString113` (`Cintas Uniform Allotment`) are available in the parser but intentionally not mapped into AD by default.

using System.Text.Json.Nodes;

namespace SyncFactors.MockSuccessFactors;

public sealed class ODataResponseBuilder
{
    public object Build(MockWorkerFixture? worker, ODataQuery query)
        => Build(worker, query, "PerPerson");

    public object Build(IReadOnlyList<MockWorkerFixture> workers, ODataQuery query, string entitySet)
        => Build(workers, query, entitySet, serviceRoot: null);

    public object Build(IReadOnlyList<MockWorkerFixture> workers, ODataQuery query, string entitySet, string? serviceRoot)
    {
        if (string.Equals(entitySet, "EmpJob", StringComparison.OrdinalIgnoreCase))
        {
            return BuildEmpJob(workers, query, serviceRoot, entitySet);
        }

        return BuildPerPerson(workers, query, serviceRoot, entitySet);
    }

    public object Build(MockWorkerFixture? worker, ODataQuery query, string entitySet)
        => Build(worker, query, entitySet, serviceRoot: null);

    public object Build(MockWorkerFixture? worker, ODataQuery query, string entitySet, string? serviceRoot)
    {
        if (string.Equals(entitySet, "EmpJob", StringComparison.OrdinalIgnoreCase))
        {
            return BuildEmpJob(worker, query, serviceRoot, entitySet);
        }

        return BuildPerPerson(worker, query, serviceRoot, entitySet);
    }

    private static object BuildPerPerson(MockWorkerFixture? worker, ODataQuery query, string? serviceRoot, string entitySet)
        => BuildPerPerson(worker is null ? [] : [worker], query, serviceRoot, entitySet);

    private static object BuildPerPerson(IReadOnlyList<MockWorkerFixture> workers, ODataQuery query, string? serviceRoot, string entitySet)
    {
        var results = ApplyPaging(workers, query)
            .Where(worker => worker.Response?.ForceEmptyResults != true)
            .Select(worker => BuildPerPersonWorker(worker, query))
            .Where(node => node is not null)
            .ToArray();
        if (results.Length == 0)
        {
            return EmptyResults();
        }

        var resultArray = new JsonArray();
        foreach (var result in results)
        {
            resultArray.Add(result);
        }

        return new JsonObject
        {
            ["d"] = new JsonObject
            {
                ["results"] = resultArray,
                ["__next"] = BuildNextPageUrl(serviceRoot, entitySet, query, workers.Count)
            }
        };
    }

    private static JsonObject? BuildPerPersonWorker(MockWorkerFixture worker, ODataQuery query)
    {
        var workerNode = new JsonObject
        {
            ["personIdExternal"] = worker.PersonIdExternal
        };
        AddIfSelected(workerNode, "personId", worker.PersonId, query);
        AddIfSelected(workerNode, "perPersonUuid", worker.PerPersonUuid, query);

        AddPersonalInfo(workerNode, worker, query);
        AddEmail(workerNode, worker, query);
        AddPhone(workerNode, worker, query);
        AddTermination(workerNode, worker, query);
        AddEmployment(workerNode, worker, query);

        return workerNode;
    }

    private static object BuildEmpJob(MockWorkerFixture? worker, ODataQuery query, string? serviceRoot, string entitySet)
        => BuildEmpJob(worker is null ? [] : [worker], query, serviceRoot, entitySet);

    private static object BuildEmpJob(IReadOnlyList<MockWorkerFixture> workers, ODataQuery query, string? serviceRoot, string entitySet)
    {
        var results = ApplyPaging(workers, query)
            .Where(worker => worker.Response?.ForceEmptyResults != true)
            .Select(worker => BuildEmpJobWorker(worker, query))
            .Where(node => node is not null)
            .ToArray();
        if (results.Length == 0)
        {
            return EmptyResults();
        }

        var resultArray = new JsonArray();
        foreach (var result in results)
        {
            resultArray.Add(result);
        }

        return new JsonObject
        {
            ["d"] = new JsonObject
            {
                ["results"] = resultArray,
                ["__next"] = BuildNextPageUrl(serviceRoot, entitySet, query, workers.Count)
            }
        };
    }

    private static JsonObject? BuildEmpJobWorker(MockWorkerFixture worker, ODataQuery query)
    {
        var jobNode = new JsonObject();
        AddIfSelected(jobNode, "userId", worker.UserId ?? worker.UserName, query);
        AddIfSelected(jobNode, "jobTitle", worker.JobTitle, query);
        AddIfSelected(jobNode, "company", worker.Company, query);
        AddIfSelected(jobNode, "department", worker.Department, query);
        AddIfSelected(jobNode, "division", worker.Division, query);
        AddIfSelected(jobNode, "location", worker.Location?.Name, query);
        AddIfSelected(jobNode, "businessUnit", worker.BusinessUnit, query);
        AddIfSelected(jobNode, "costCenter", worker.CostCenter, query);
        AddIfSelected(jobNode, "employeeClass", worker.EmployeeClass, query);
        AddIfSelected(jobNode, "employeeType", worker.EmployeeType, query);
        AddIfSelected(jobNode, "managerId", worker.ManagerId, query);
        AddIfSelected(jobNode, "customString3", worker.PeopleGroup, query);
        AddIfSelected(jobNode, "customString20", worker.LeadershipLevel, query);
        AddIfSelected(jobNode, "customString87", worker.Region, query);
        AddIfSelected(jobNode, "customString110", worker.Geozone, query);
        AddIfSelected(jobNode, "customString111", worker.BargainingUnit, query);
        AddIfSelected(jobNode, "customString91", worker.UnionJobCode, query);
        AddIfSelected(jobNode, "startDate", worker.StartDate, query);

        AddFlatNavigation(
            jobNode,
            "companyNav",
            query,
            ("company", worker.Company),
            ("name_localized", worker.Company),
            ("externalCode", worker.CompanyId));
        AddFlatNavigation(
            jobNode,
            "departmentNav",
            query,
            ("department", worker.Department),
            ("name_localized", worker.Department),
            ("name", worker.DepartmentName ?? worker.Department),
            ("externalCode", worker.DepartmentId),
            ("costCenter", worker.DepartmentCostCenter));
        AddFlatNavigation(
            jobNode,
            "divisionNav",
            query,
            ("division", worker.Division),
            ("name_localized", worker.Division),
            ("externalCode", worker.DivisionId));
        AddFlatNavigation(
            jobNode,
            "businessUnitNav",
            query,
            ("businessUnit", worker.BusinessUnit),
            ("name_localized", worker.BusinessUnit),
            ("externalCode", worker.BusinessUnitId));
        AddFlatNavigation(
            jobNode,
            "costCenterNav",
            query,
            ("costCenterDescription", worker.CostCenterDescription ?? worker.CostCenter),
            ("name_localized", worker.CostCenter),
            ("description_localized", worker.CostCenterDescription ?? worker.CostCenter),
            ("externalCode", worker.CostCenterId));

        if (ShouldIncludeNavigation("locationNav", query) && worker.Location is not null)
        {
            var locationNav = new JsonObject();
            AddExpandedNavigationProperty(locationNav, "locationNav", "name", worker.Location.Name, query);
            AddExpandedNavigationProperty(locationNav, "locationNav", "LocationName", worker.Location.Name, query);
            AddExpandedNavigationProperty(locationNav, "locationNav", "officeLocationAddress", worker.Location.Address, query);
            AddExpandedNavigationProperty(locationNav, "locationNav", "officeLocationCity", worker.Location.City, query);
            AddExpandedNavigationProperty(locationNav, "locationNav", "officeLocationZipCode", worker.Location.ZipCode, query);
            if (locationNav.Count > 0)
            {
                jobNode["locationNav"] = locationNav;
            }
        }

        return jobNode;
    }

    private static void AddPersonalInfo(JsonObject workerNode, MockWorkerFixture worker, ODataQuery query)
    {
        if (!ShouldIncludeNavigation("personalInfoNav", query))
        {
            return;
        }

        var personalInfo = new JsonObject();
        AddIfSelected(personalInfo, "personalInfoNav/firstName", worker.FirstName, query);
        AddIfSelected(personalInfo, "personalInfoNav/lastName", worker.LastName, query);
        AddIfSelected(personalInfo, "personalInfoNav/preferredName", worker.PreferredName, query, "preferredName");
        AddIfSelected(personalInfo, "personalInfoNav/displayName", worker.DisplayName, query, "displayName");

        if (personalInfo.Count > 0)
        {
            workerNode["personalInfoNav"] = ToResultsArray(personalInfo);
        }
    }

    private static void AddEmail(JsonObject workerNode, MockWorkerFixture worker, ODataQuery query)
    {
        if (!ShouldIncludeNavigation("emailNav", query))
        {
            return;
        }

        var email = new JsonObject();
        AddIfSelected(email, "emailNav/emailAddress", worker.Email, query, "emailAddress");
        AddIfSelected(email, "emailNav/emailType", worker.EmailType, query, "emailType");
        AddIfSelected(email, "emailNav/isPrimary", "true", query, "isPrimary");

        if (email.Count > 0)
        {
            workerNode["emailNav"] = ToResultsArray(email);
        }
    }

    private static void AddPhone(JsonObject workerNode, MockWorkerFixture worker, ODataQuery query)
    {
        if (!ShouldIncludeNavigation("phoneNav", query))
        {
            return;
        }

        var phones = new JsonArray();
        AddPhoneIfSelected(
            phones,
            worker.BusinessPhoneAreaCode,
            worker.BusinessPhoneCountryCode,
            worker.BusinessPhoneExtension,
            worker.BusinessPhoneNumber,
            "10605",
            "false",
            query);
        AddPhoneIfSelected(
            phones,
            worker.CellPhoneAreaCode,
            worker.CellPhoneCountryCode,
            null,
            worker.CellPhoneNumber,
            "10606",
            "true",
            query);

        if (phones.Count > 0)
        {
            workerNode["phoneNav"] = new JsonObject
            {
                ["results"] = phones
            };
        }
    }

    private static void AddTermination(JsonObject workerNode, MockWorkerFixture worker, ODataQuery query)
    {
        var hasRequestedPath = query.Select.Any(select => select.StartsWith("personEmpTerminationInfoNav/", StringComparison.OrdinalIgnoreCase));
        if (!hasRequestedPath)
        {
            return;
        }

        var termination = new JsonObject();
        AddIfSelected(termination, "personEmpTerminationInfoNav/activeEmploymentsCount", worker.ActiveEmploymentsCount, query, "activeEmploymentsCount");
        AddIfSelected(termination, "personEmpTerminationInfoNav/latestTerminationDate", worker.LatestTerminationDate, query, "latestTerminationDate");

        if (termination.Count > 0)
        {
            workerNode["personEmpTerminationInfoNav"] = termination;
        }
    }

    private static void AddEmployment(JsonObject workerNode, MockWorkerFixture worker, ODataQuery query)
    {
        if (!ShouldIncludeNavigation("employmentNav", query))
        {
            return;
        }

        var employment = new JsonObject();
        AddIfSelected(employment, "employmentNav/startDate", worker.StartDate, query, "startDate");
        AddIfSelected(employment, "employmentNav/userId", worker.UserId ?? worker.UserName, query, "userId");

        if (ShouldIncludeNavigation("employmentNav/jobInfoNav", query))
        {
            var jobInfo = new JsonObject();
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/jobTitle", worker.JobTitle, query, "jobTitle");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/employeeClass", worker.EmployeeClass, query, "employeeClass");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/employeeType", worker.EmployeeType, query, "employeeType");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/managerId", worker.ManagerId, query, "managerId");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/emplStatus", worker.EmploymentStatus, query, "emplStatus");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/position", worker.Position, query, "position");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/customString3", worker.PeopleGroup, query, "customString3");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/customString20", worker.LeadershipLevel, query, "customString20");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/customString87", worker.Region, query, "customString87");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/customString110", worker.Geozone, query, "customString110");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/customString111", worker.BargainingUnit, query, "customString111");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/customString91", worker.UnionJobCode, query, "customString91");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/customString112", worker.CintasUniformCategory, query, "customString112");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/customString113", worker.CintasUniformAllotment, query, "customString113");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/department", worker.Department, query, "department");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/company", worker.Company, query, "company");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/businessUnit", worker.BusinessUnit, query, "businessUnit");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/division", worker.Division, query, "division");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/costCenter", worker.CostCenter, query, "costCenter");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/location", worker.Location?.Name, query, "location");
            AddPayGradeNavigation(jobInfo, worker, query);
            AddCompanyNavigation(jobInfo, worker, query);
            AddDepartmentNavigation(jobInfo, worker, query);
            AddBusinessUnitNavigation(jobInfo, worker, query);
            AddDivisionNavigation(jobInfo, worker, query);
            AddCostCenterNavigation(jobInfo, worker, query);
            AddLocationNavigation(jobInfo, worker.Location, query);

            if (jobInfo.Count > 0)
            {
                employment["jobInfoNav"] = ToResultsArray(jobInfo);
            }
        }

        AddUserNavigation(employment, worker, query);

        if (employment.Count > 0)
        {
            workerNode["employmentNav"] = ToResultsArray(employment);
        }
    }

    private static void AddUserNavigation(JsonObject employment, MockWorkerFixture worker, ODataQuery query)
    {
        if (!ShouldIncludeNavigation("employmentNav/userNav", query))
        {
            return;
        }

        var userNav = new JsonObject();
        AddIfSelected(userNav, "employmentNav/userNav/username", worker.UserName, query, "username");
        AddIfSelected(userNav, "employmentNav/userNav/businessPhone", worker.BusinessPhoneNumber, query, "businessPhone");
        AddIfSelected(userNav, "employmentNav/userNav/cellPhone", worker.CellPhoneNumber, query, "cellPhone");

        if (ShouldIncludeNavigation("employmentNav/userNav/manager", query) || query.Select.Contains("employmentNav/userNav/manager/empInfo/personIdExternal"))
        {
            var manager = new JsonObject();
            if (ShouldIncludeNavigation("employmentNav/userNav/manager/empInfo", query) || query.Select.Contains("employmentNav/userNav/manager/empInfo/personIdExternal"))
            {
                var empInfo = new JsonObject();
                AddIfSelected(empInfo, "employmentNav/userNav/manager/empInfo/personIdExternal", worker.ManagerId, query, "personIdExternal");
                if (empInfo.Count > 0)
                {
                    manager["empInfo"] = empInfo;
                }
            }

            if (manager.Count > 0)
            {
                userNav["manager"] = manager;
            }
        }

        if (userNav.Count > 0)
        {
            employment["userNav"] = userNav;
        }
    }

    private static void AddLocationNavigation(JsonObject jobInfo, MockLocationFixture? location, ODataQuery query)
    {
        if (!ShouldIncludeNavigation("employmentNav/jobInfoNav/locationNav", query) || location is null)
        {
            return;
        }

        var locationNode = new JsonObject();
        AddIfSelected(locationNode, "employmentNav/jobInfoNav/locationNav/name", location.Name, query, "name");
        AddIfSelected(locationNode, "employmentNav/jobInfoNav/locationNav/LocationName", location.Name, query, "LocationName");
        AddIfSelected(locationNode, "employmentNav/jobInfoNav/locationNav/officeLocationAddress", location.Address, query, "officeLocationAddress");
        AddIfSelected(locationNode, "employmentNav/jobInfoNav/locationNav/officeLocationCity", location.City, query, "officeLocationCity");
        AddIfSelected(locationNode, "employmentNav/jobInfoNav/locationNav/officeLocationZipCode", location.ZipCode, query, "officeLocationZipCode");
        if (ShouldIncludeNavigation("employmentNav/jobInfoNav/locationNav/addressNavDEFLT", query))
        {
            var address = new JsonObject();
            AddIfSelected(address, "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/address1", location.Address, query, "address1");
            AddIfSelected(address, "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/city", location.City, query, "city");
            AddIfSelected(address, "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/zipCode", location.ZipCode, query, "zipCode");
            AddIfSelected(address, "employmentNav/jobInfoNav/locationNav/addressNavDEFLT/customString4", location.CustomString4, query, "customString4");
            if (address.Count > 0)
            {
                locationNode["addressNavDEFLT"] = address;
            }
        }

        if (locationNode.Count > 0)
        {
            jobInfo["locationNav"] = locationNode;
        }
    }

    private static void AddCompanyNavigation(JsonObject jobInfo, MockWorkerFixture worker, ODataQuery query)
    {
        if (!ShouldIncludeNavigation("employmentNav/jobInfoNav/companyNav", query))
        {
            return;
        }

        var companyNav = new JsonObject();
        AddIfSelected(companyNav, "employmentNav/jobInfoNav/companyNav/company", worker.Company, query, "company");
        AddIfSelected(companyNav, "employmentNav/jobInfoNav/companyNav/name_localized", worker.Company, query, "name_localized");
        AddIfSelected(companyNav, "employmentNav/jobInfoNav/companyNav/externalCode", worker.CompanyId, query, "externalCode");
        if (ShouldIncludeNavigation("employmentNav/jobInfoNav/companyNav/countryOfRegistrationNav", query))
        {
            var country = new JsonObject();
            AddIfSelected(country, "employmentNav/jobInfoNav/companyNav/countryOfRegistrationNav/twoCharCountryCode", worker.TwoCharCountryCode, query, "twoCharCountryCode");
            if (country.Count > 0)
            {
                companyNav["countryOfRegistrationNav"] = country;
            }
        }

        if (companyNav.Count > 0)
        {
            jobInfo["companyNav"] = companyNav;
        }
    }

    private static void AddDepartmentNavigation(JsonObject jobInfo, MockWorkerFixture worker, ODataQuery query)
    {
        AddFlatNavigation(
            jobInfo,
            "employmentNav/jobInfoNav/departmentNav",
            query,
            ("department", worker.Department),
            ("name_localized", worker.Department),
            ("name", worker.DepartmentName ?? worker.Department),
            ("externalCode", worker.DepartmentId),
            ("costCenter", worker.DepartmentCostCenter));
    }

    private static void AddBusinessUnitNavigation(JsonObject jobInfo, MockWorkerFixture worker, ODataQuery query)
    {
        AddFlatNavigation(
            jobInfo,
            "employmentNav/jobInfoNav/businessUnitNav",
            query,
            ("businessUnit", worker.BusinessUnit),
            ("name_localized", worker.BusinessUnit),
            ("externalCode", worker.BusinessUnitId));
    }

    private static void AddDivisionNavigation(JsonObject jobInfo, MockWorkerFixture worker, ODataQuery query)
    {
        AddFlatNavigation(
            jobInfo,
            "employmentNav/jobInfoNav/divisionNav",
            query,
            ("division", worker.Division),
            ("name_localized", worker.Division),
            ("externalCode", worker.DivisionId));
    }

    private static void AddCostCenterNavigation(JsonObject jobInfo, MockWorkerFixture worker, ODataQuery query)
    {
        AddFlatNavigation(
            jobInfo,
            "employmentNav/jobInfoNav/costCenterNav",
            query,
            ("costCenterDescription", worker.CostCenterDescription ?? worker.CostCenter),
            ("name_localized", worker.CostCenter),
            ("description_localized", worker.CostCenterDescription ?? worker.CostCenter),
            ("externalCode", worker.CostCenterId));
    }

    private static void AddPayGradeNavigation(JsonObject jobInfo, MockWorkerFixture worker, ODataQuery query)
    {
        AddFlatNavigation(
            jobInfo,
            "employmentNav/jobInfoNav/payGradeNav",
            query,
            ("name", worker.PayGrade));
    }

    private static void AddFlatNavigation(
        JsonObject container,
        string navigationPath,
        ODataQuery query,
        params (string PropertyName, string? Value)[] properties)
    {
        if (!ShouldIncludeNavigation(navigationPath, query))
        {
            return;
        }

        var navigation = new JsonObject();
        foreach (var (propertyName, value) in properties)
        {
            AddExpandedNavigationProperty(navigation, navigationPath, propertyName, value, query);
        }

        if (navigation.Count > 0)
        {
            container[navigationPath.Split('/').Last()] = navigation;
        }
    }

    private static object EmptyResults()
    {
        return new
        {
            d = new
            {
                results = Array.Empty<object>()
            }
        };
    }

    private static void AddPhoneIfSelected(
        JsonArray phones,
        string? areaCode,
        string? countryCode,
        string? extension,
        string? phoneNumber,
        string phoneType,
        string isPrimary,
        ODataQuery query)
    {
        if (string.IsNullOrWhiteSpace(areaCode) &&
            string.IsNullOrWhiteSpace(countryCode) &&
            string.IsNullOrWhiteSpace(extension) &&
            string.IsNullOrWhiteSpace(phoneNumber))
        {
            return;
        }

        var phone = new JsonObject();
        AddIfSelected(phone, "phoneNav/areaCode", areaCode, query, "areaCode");
        AddIfSelected(phone, "phoneNav/countryCode", countryCode, query, "countryCode");
        AddIfSelected(phone, "phoneNav/extension", extension, query, "extension");
        AddIfSelected(phone, "phoneNav/phoneNumber", phoneNumber, query, "phoneNumber");
        AddIfSelected(phone, "phoneNav/phoneType", phoneType, query, "phoneType");
        AddIfSelected(phone, "phoneNav/isPrimary", isPrimary, query, "isPrimary");
        if (phone.Count > 0)
        {
            phones.Add(phone);
        }
    }

    private static void AddExpandedNavigationProperty(
        JsonObject container,
        string navigationPath,
        string propertyName,
        string? value,
        ODataQuery query)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (query.Select.Count > 0 &&
            !query.Select.Contains($"{navigationPath}/{propertyName}") &&
            !query.Expand.Contains(navigationPath))
        {
            return;
        }

        container[propertyName] = value;
    }

    private static void AddIfSelected(
        JsonObject container,
        string selectPath,
        string? value,
        ODataQuery query,
        string? propertyName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (query.Select.Count > 0 && !query.Select.Contains(selectPath))
        {
            return;
        }

        container[propertyName ?? selectPath.Split('/').Last()] = value;
    }

    private static JsonObject ToResultsArray(JsonObject node)
    {
        return new JsonObject
        {
            ["results"] = new JsonArray(node)
        };
    }

    private static bool ShouldIncludeNavigation(string path, ODataQuery query)
    {
        if (query.Expand.Count == 0)
        {
            return true;
        }

        return query.Expand.Contains(path) || query.Select.Any(select => select.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<MockWorkerFixture> ApplyPaging(IReadOnlyList<MockWorkerFixture> workers, ODataQuery query)
    {
        var skip = Math.Max(0, query.Skip);
        var top = query.Top ?? int.MaxValue;
        return workers.Skip(skip).Take(top);
    }

    private static string? BuildNextPageUrl(string? serviceRoot, string entitySet, ODataQuery query, int totalWorkers)
    {
        if (string.IsNullOrWhiteSpace(serviceRoot) || !string.IsNullOrWhiteSpace(query.WorkerId) || query.Top is null)
        {
            return null;
        }

        var nextSkip = Math.Max(0, query.Skip) + query.Top.Value;
        if (nextSkip >= totalWorkers)
        {
            return null;
        }

        var parts = new List<string>
        {
            "$format=json",
            $"customPageSize={query.Top.Value}",
            "paging=snapshot",
            $"$skiptoken={nextSkip}",
            $"$select={Uri.EscapeDataString(string.Join(",", query.Select))}"
        };

        if (!string.IsNullOrWhiteSpace(query.Filter))
        {
            parts.Add($"$filter={Uri.EscapeDataString(query.Filter)}");
        }

        if (!string.IsNullOrWhiteSpace(query.OrderBy))
        {
            parts.Add($"$orderby={Uri.EscapeDataString(query.OrderBy)}");
        }

        if (query.Expand.Count > 0)
        {
            parts.Add($"$expand={Uri.EscapeDataString(string.Join(",", query.Expand))}");
        }

        if (!string.IsNullOrWhiteSpace(query.AsOfDate))
        {
            parts.Add($"asOfDate={Uri.EscapeDataString(query.AsOfDate)}");
        }

        return $"{serviceRoot.TrimEnd('/')}/{entitySet}?{string.Join("&", parts)}";
    }
}

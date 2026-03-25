using System.Text.Json.Nodes;

namespace SyncFactors.MockSuccessFactors;

public sealed class ODataResponseBuilder
{
    public object Build(MockWorkerFixture? worker, ODataQuery query)
    {
        if (worker is null || worker.Response?.ForceEmptyResults == true)
        {
            return new
            {
                d = new
                {
                    results = Array.Empty<object>()
                }
            };
        }

        var workerNode = new JsonObject
        {
            ["personIdExternal"] = worker.PersonIdExternal
        };

        AddPersonalInfo(workerNode, worker, query);
        AddEmail(workerNode, worker, query);
        AddEmployment(workerNode, worker, query);

        return new JsonObject
        {
            ["d"] = new JsonObject
            {
                ["results"] = new JsonArray(workerNode)
            }
        };
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

        if (email.Count > 0)
        {
            workerNode["emailNav"] = ToResultsArray(email);
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

        if (ShouldIncludeNavigation("employmentNav/jobInfoNav", query))
        {
            var jobInfo = new JsonObject();
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/jobTitle", worker.JobTitle, query, "jobTitle");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/employeeClass", worker.EmployeeClass, query, "employeeClass");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/employeeType", worker.EmployeeType, query, "employeeType");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/managerId", worker.ManagerId, query, "managerId");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/department", worker.Department, query, "department");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/company", worker.Company, query, "company");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/businessUnit", worker.BusinessUnit, query, "businessUnit");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/division", worker.Division, query, "division");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/costCenter", worker.CostCenter, query, "costCenter");
            AddIfSelected(jobInfo, "employmentNav/jobInfoNav/location", worker.Location?.Name, query, "location");

            AddIfSelectedNavigation(jobInfo, "employmentNav/jobInfoNav/companyNav", "company", worker.Company, query);
            AddIfSelectedNavigation(jobInfo, "employmentNav/jobInfoNav/departmentNav", "department", worker.Department, query);
            AddIfSelectedNavigation(jobInfo, "employmentNav/jobInfoNav/businessUnitNav", "businessUnit", worker.BusinessUnit, query);
            AddIfSelectedNavigation(jobInfo, "employmentNav/jobInfoNav/divisionNav", "division", worker.Division, query);
            AddIfSelectedNavigation(jobInfo, "employmentNav/jobInfoNav/costCenterNav", "costCenterDescription", worker.CostCenter, query);
            AddLocationNavigation(jobInfo, worker.Location, query);

            if (jobInfo.Count > 0)
            {
                employment["jobInfoNav"] = ToResultsArray(jobInfo);
            }
        }

        if (employment.Count > 0)
        {
            workerNode["employmentNav"] = ToResultsArray(employment);
        }
    }

    private static void AddLocationNavigation(JsonObject jobInfo, MockLocationFixture? location, ODataQuery query)
    {
        if (!ShouldIncludeNavigation("employmentNav/jobInfoNav/locationNav", query) || location is null)
        {
            return;
        }

        var locationNode = new JsonObject();
        AddIfSelected(locationNode, "employmentNav/jobInfoNav/locationNav/LocationName", location.Name, query, "LocationName");
        AddIfSelected(locationNode, "employmentNav/jobInfoNav/locationNav/officeLocationAddress", location.Address, query, "officeLocationAddress");
        AddIfSelected(locationNode, "employmentNav/jobInfoNav/locationNav/officeLocationCity", location.City, query, "officeLocationCity");
        AddIfSelected(locationNode, "employmentNav/jobInfoNav/locationNav/officeLocationZipCode", location.ZipCode, query, "officeLocationZipCode");

        if (locationNode.Count > 0)
        {
            jobInfo["locationNav"] = locationNode;
        }
    }

    private static void AddIfSelectedNavigation(
        JsonObject container,
        string navigationPath,
        string propertyName,
        string? value,
        ODataQuery query)
    {
        if (!ShouldIncludeNavigation(navigationPath, query) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        container[navigationPath.Split('/').Last()] = new JsonObject
        {
            [propertyName] = value
        };
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
}

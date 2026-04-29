import fs from "node:fs/promises";
import path from "node:path";
import { SpreadsheetFile, Workbook } from "@oai/artifact-tool";

const outputDir = path.resolve("outputs/hr-test-scenarios");
const outputPath = path.join(outputDir, "SyncFactors_HR_Test_Scenarios.xlsx");

const scenarios = [
  [1, "Existing active employee, no change", "Pick known active worker already synced", "Dry-run first", "unchanged or only expected updates", "No surprise AD changes", "", "", ""],
  [2, "New future hire / prehire", "New worker; start date after Apr 28, 2026", "Dry-run, then preview/apply one controlled user", "creates; account in Prehire OU; enabled", "Name, email, employeeID, title, company, department, location correct", "", "", ""],
  [3, "Prehire becomes active", "Same worker; start date Apr 28, 2026 or earlier", "Dry-run, then preview/apply controlled user", "enables; moved to Active OU", "Account enabled; Active OU correct", "", "", ""],
  [4, "Job / location update", "Change title, company, division, department, office address", "Dry-run first", "updates only", "AD fields match SuccessFactors values", "", "", ""],
  [5, "Manager assignment", "Assign direct report to known synced manager", "Dry-run first", "updates or creates; manager resolved", "AD manager attribute set correctly", "", "", ""],
  [6, "Name / email change", "Change preferred name, legal name, or business email", "Dry-run first", "updates; same AD account matched by employee ID", "No duplicate AD user created", "", "", ""],
  [7, "Duplicate name collision", "Two test workers with same preferred name and last name", "Dry-run first", "Both processed with unique account/email handling", "No UPN or mail conflict", "", "", ""],
  [8, "Paid leave", "Change worker to leave status", "Dry-run, then preview/apply controlled user", "disables or updates; moved to Leave OU; disabled", "User in Leave OU; account disabled", "", "", ""],
  [9, "Return from leave", "Change same worker back to active status", "Dry-run, then preview/apply controlled user", "enables; moved to Active OU; enabled", "Account active again; correct OU", "", "", ""],
  [10, "Termination", "Set terminated/inactive status and end date", "Dry-run, then preview/apply controlled user only", "graveyardMoves; disabled; moved to Graveyard OU", "No active AD account remains", "", "", ""],
  [11, "Missing required data", "Test worker missing required email, name, or employee ID", "Dry-run only", "manualReview or failed safely; no live mutation", "Clear reason shown in run detail", "", "", ""],
  [12, "Run safety", "Start one run; try starting another while first run active", "Operator test", "Second run blocked or safely queued", "No overlapping syncs", "", "", ""]
];

const fieldRows = [
  ["Employee ID", "employeeID", "PerPerson.personIdExternal"],
  ["Legal First Name", "GivenName", "PerPersonal.firstName"],
  ["Legal Last Name", "Surname", "PerPersonal.lastName"],
  ["Business Email", "UserPrincipalName, mail", "PerEmail.emailAddress"],
  ["Job Title", "title", "EmpJob.jobTitle"],
  ["Company Name", "company", "FOCompany.name_localized"],
  ["Function / Division", "division", "FODivision.name_localized"],
  ["Sub Function / Department", "department", "FODepartment.name_localized"],
  ["Location Name", "physicalDeliveryOfficeName", "FOLocation.name"],
  ["Employee Type", "employeeType", "EmpJob.employeeType"],
  ["Office Street", "streetAddress", "FOLocation.addressNavDEFLT.address1"],
  ["Office City", "l", "FOLocation.addressNavDEFLT.city"],
  ["Office Postal Code", "postalCode", "FOLocation.addressNavDEFLT.zipCode"],
  ["Manager", "manager", "Resolved manager distinguished name"]
];

const preflightRows = [
  ["API health green", "", ""],
  ["Worker heartbeat fresh", "", ""],
  ["AD health green", "", ""],
  ["Dry-run completed before live apply", "", ""],
  ["Only controlled test workers selected", "", ""],
  ["No delete-all reset used in real environment", "", ""],
  ["Run detail reviewed after each scenario", "", ""],
  ["Actual AD state spot-checked after apply", "", ""]
];

const workbook = Workbook.create();
const sheet = workbook.worksheets.add("HR Test Scenarios");
const fields = workbook.worksheets.add("Field Validation");
const checklist = workbook.worksheets.add("Preflight Checklist");

sheet.getRange("A1:I1").values = [["#", "Scenario", "SuccessFactors Test Data / Change", "How To Run", "Expected SyncFactors Result", "HR / IT Validation", "Owner", "Status", "Notes"]];
sheet.getRange(`A2:I${scenarios.length + 1}`).values = scenarios;

fields.getRange("A1:C1").values = [["Business Field", "AD Attribute", "SuccessFactors Source"]];
fields.getRange(`A2:C${fieldRows.length + 1}`).values = fieldRows;

checklist.getRange("A1:C1").values = [["Preflight Item", "Done?", "Notes"]];
checklist.getRange(`A2:C${preflightRows.length + 1}`).values = preflightRows;

for (const ws of [sheet, fields, checklist]) {
  const used = ws.getUsedRange();
  used.format.font.name = "Aptos";
  used.format.font.size = 11;
  used.format.wrapText = true;
  used.format.verticalAlignment = "Top";
}

sheet.getRange("A1:I1").format.fill.color = "#1F4E78";
sheet.getRange("A1:I1").format.font.color = "#FFFFFF";
sheet.getRange("A1:I1").format.font.bold = true;
sheet.getRange("A1:I13").format.borders.lineStyle = "Continuous";
sheet.getRange("A1:I13").format.borders.color = "#D9E2F3";
sheet.getRange("A2:A13").format.horizontalAlignment = "Center";
sheet.getRange("H2:H13").dataValidation = {
  type: "list",
  formula1: '"Not Started,In Progress,Passed,Failed,Blocked"',
  allowBlank: true
};
sheet.getRange("A1:I13").table = { name: "HR_Test_Scenarios", hasHeaders: true };
sheet.freezePanes.freezeRows(1);
sheet.getRange("A:A").format.columnWidthPx = 44;
sheet.getRange("B:B").format.columnWidthPx = 190;
sheet.getRange("C:C").format.columnWidthPx = 260;
sheet.getRange("D:D").format.columnWidthPx = 210;
sheet.getRange("E:E").format.columnWidthPx = 250;
sheet.getRange("F:F").format.columnWidthPx = 260;
sheet.getRange("G:G").format.columnWidthPx = 110;
sheet.getRange("H:H").format.columnWidthPx = 120;
sheet.getRange("I:I").format.columnWidthPx = 220;

fields.getRange("A1:C1").format.fill.color = "#548235";
fields.getRange("A1:C1").format.font.color = "#FFFFFF";
fields.getRange("A1:C1").format.font.bold = true;
fields.getRange(`A1:C${fieldRows.length + 1}`).format.borders.lineStyle = "Continuous";
fields.getRange(`A1:C${fieldRows.length + 1}`).format.borders.color = "#E2F0D9";
fields.getRange(`A1:C${fieldRows.length + 1}`).table = { name: "Field_Validation", hasHeaders: true };
fields.freezePanes.freezeRows(1);
fields.getRange("A:A").format.columnWidthPx = 210;
fields.getRange("B:B").format.columnWidthPx = 220;
fields.getRange("C:C").format.columnWidthPx = 310;

checklist.getRange("A1:C1").format.fill.color = "#7030A0";
checklist.getRange("A1:C1").format.font.color = "#FFFFFF";
checklist.getRange("A1:C1").format.font.bold = true;
checklist.getRange(`A1:C${preflightRows.length + 1}`).format.borders.lineStyle = "Continuous";
checklist.getRange(`A1:C${preflightRows.length + 1}`).format.borders.color = "#EADCF8";
checklist.getRange(`A1:C${preflightRows.length + 1}`).table = { name: "Preflight_Checklist", hasHeaders: true };
checklist.getRange(`B2:B${preflightRows.length + 1}`).dataValidation = {
  type: "list",
  formula1: '"Yes,No,N/A"',
  allowBlank: true
};
checklist.freezePanes.freezeRows(1);
checklist.getRange("A:A").format.columnWidthPx = 300;
checklist.getRange("B:B").format.columnWidthPx = 90;
checklist.getRange("C:C").format.columnWidthPx = 300;

await fs.mkdir(outputDir, { recursive: true });
const output = await SpreadsheetFile.exportXlsx(workbook);
await output.save(outputPath);
console.log(outputPath);

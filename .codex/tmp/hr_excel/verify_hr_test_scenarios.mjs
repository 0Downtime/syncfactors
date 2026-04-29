import { FileBlob, SpreadsheetFile } from "@oai/artifact-tool";

const path = "outputs/hr-test-scenarios/SyncFactors_HR_Test_Scenarios.xlsx";
const input = await FileBlob.load(path);
const workbook = await SpreadsheetFile.importXlsx(input);

const scenarios = await workbook.inspect({
  kind: "table",
  range: "HR Test Scenarios!A1:I13",
  include: "values,formulas",
  tableMaxRows: 13,
  tableMaxCols: 9,
  summary: "scenario table"
});
console.log(scenarios.ndjson);

const errors = await workbook.inspect({
  kind: "match",
  searchTerm: "#REF!|#DIV/0!|#VALUE!|#NAME\\?|#N/A",
  options: { useRegex: true, maxResults: 50 },
  summary: "formula error scan"
});
console.log(errors.ndjson);

await workbook.render({ sheetName: "HR Test Scenarios", range: "A1:I13", scale: 1 });
await workbook.render({ sheetName: "Field Validation", range: "A1:C15", scale: 1 });
await workbook.render({ sheetName: "Preflight Checklist", range: "A1:C9", scale: 1 });
console.log("verified");

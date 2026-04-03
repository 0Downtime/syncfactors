import type { WorkerPreviewResult } from './types.js';

export function buildRiskCallouts(preview: WorkerPreviewResult): string[] {
  const callouts: string[] = [];
  for (const row of preview.diffRows.filter((diff) => diff.changed)) {
    if (equalsIgnoreCase(row.attribute, 'userPrincipalName') || equalsIgnoreCase(row.attribute, 'mail')) {
      callouts.push(`${row.attribute} will change from '${row.before}' to '${row.after}'.`);
    }

    if (equalsIgnoreCase(row.attribute, 'manager')) {
      callouts.push(`Manager assignment will change to '${row.after}'.`);
    }
  }

  if (preview.operationSummary?.fromOu && preview.operationSummary?.toOu &&
      !equalsIgnoreCase(preview.operationSummary.fromOu, preview.operationSummary.toOu)) {
    callouts.push(`The account will move from '${preview.operationSummary.fromOu}' to '${preview.operationSummary.toOu}'.`);
  }

  if (preview.currentEnabled !== preview.proposedEnable && preview.proposedEnable !== null) {
    callouts.push(preview.proposedEnable ? 'The account will be enabled.' : 'The account will be disabled.');
  }

  if (!preview.managerDistinguishedName &&
      preview.diffRows.some((diff) => equalsIgnoreCase(diff.source, 'managerId'))) {
    callouts.push('Manager resolution is still blank for a manager-linked preview.');
  }

  return Array.from(new Set(callouts.map((value) => value.toLowerCase()))).map((lowered) =>
    callouts.find((value) => value.toLowerCase() === lowered) ?? lowered,
  );
}

function equalsIgnoreCase(left: string | null | undefined, right: string | null | undefined) {
  return (left ?? '').localeCompare(right ?? '', undefined, { sensitivity: 'accent' }) === 0;
}

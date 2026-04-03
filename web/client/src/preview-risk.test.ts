import { buildRiskCallouts } from './preview-risk.js';
import type { WorkerPreviewResult } from './types.js';

describe('buildRiskCallouts', () => {
  it('builds the same high-risk warnings as the Razor page rules', () => {
    const preview: WorkerPreviewResult = {
      reportPath: null,
      runId: 'preview-1',
      previousRunId: null,
      fingerprint: 'fingerprint-1',
      mode: 'Preview',
      status: 'Planned',
      errorMessage: null,
      artifactType: 'WorkerPreview',
      successFactorsAuth: null,
      workerId: '10001',
      buckets: ['updates'],
      matchedExistingUser: true,
      reviewCategory: null,
      reviewCaseType: null,
      reason: null,
      operatorActionSummary: null,
      samAccountName: 'winnie',
      managerDistinguishedName: null,
      targetOu: 'OU=LabUsers,DC=example,DC=com',
      currentDistinguishedName: 'CN=Sample101\\, Winnie,OU=Old,DC=example,DC=com',
      currentEnabled: false,
      proposedEnable: true,
      operationSummary: {
        action: 'Update user',
        effect: 'Changes pending.',
        targetOu: 'OU=LabUsers,DC=example,DC=com',
        fromOu: 'OU=Old,DC=example,DC=com',
        toOu: 'OU=LabUsers,DC=example,DC=com',
      },
      diffRows: [
        { attribute: 'mail', source: 'email', before: 'old@example.com', after: 'new@example.com', changed: true },
        { attribute: 'manager', source: 'managerId', before: '', after: 'CN=Manager,DC=example,DC=com', changed: true },
      ],
      sourceAttributes: [],
      usedSourceAttributes: [],
      unusedSourceAttributes: [],
      missingSourceAttributes: [],
      entries: [],
    };

    expect(buildRiskCallouts(preview)).toEqual(
      expect.arrayContaining([
        "mail will change from 'old@example.com' to 'new@example.com'.",
        "Manager assignment will change to 'CN=Manager,DC=example,DC=com'.",
        "The account will move from 'OU=Old,DC=example,DC=com' to 'OU=LabUsers,DC=example,DC=com'.",
        'The account will be enabled.',
        'Manager resolution is still blank for a manager-linked preview.',
      ]),
    );
  });
});

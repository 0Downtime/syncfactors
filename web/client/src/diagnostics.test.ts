import { parseFailureDiagnostics } from './diagnostics.js';

describe('parseFailureDiagnostics', () => {
  it('parses detail fields and next check guidance', () => {
    const diagnostics = parseFailureDiagnostics(
      "Active Directory command 'UpdateUser' failed against LDAP server 'localhost'. The server cannot handle directory requests. Details: Step=ModifyAttributes WorkerId=10001 SamAccountName=winnie DistinguishedName=CN=Sample101\\, Winnie,OU=LabUsers,DC=example,DC=com Attributes=displayName,department ManagerId=90001 Next check: Check the target OU and manager resolution.",
    );

    expect(diagnostics).not.toBeNull();
    expect(diagnostics?.guidance).toBe('Check the target OU and manager resolution.');
    expect(diagnostics?.details).toEqual(
      expect.arrayContaining([
        { label: 'Step', value: 'ModifyAttributes' },
        { label: 'Worker ID', value: '10001' },
        { label: 'SAM', value: 'winnie' },
        { label: 'Manager ID', value: '90001' },
      ]),
    );
  });
});

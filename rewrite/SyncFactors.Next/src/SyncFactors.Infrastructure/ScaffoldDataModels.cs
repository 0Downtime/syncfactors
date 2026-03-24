using SyncFactors.Contracts;

namespace SyncFactors.Infrastructure;

public sealed record ScaffoldDataDocument(
    IReadOnlyList<ScaffoldWorkerRecord> Workers,
    IReadOnlyList<ScaffoldDirectoryRecord> DirectoryUsers);

public sealed record ScaffoldWorkerRecord(
    string WorkerId,
    string PreferredName,
    string LastName,
    string Department,
    string TargetOu,
    bool IsPrehire)
{
    public WorkerSnapshot ToSnapshot() => new(
        WorkerId,
        PreferredName,
        LastName,
        Department,
        TargetOu,
        IsPrehire);
}

public sealed record ScaffoldDirectoryRecord(
    string WorkerId,
    string? SamAccountName,
    string? DistinguishedName,
    bool? Enabled,
    string? DisplayName)
{
    public DirectoryUserSnapshot ToSnapshot() => new(
        SamAccountName,
        DistinguishedName,
        Enabled,
        DisplayName);
}

using System.Text.Json;

namespace SyncFactors.Contracts;

public sealed record RuntimeStatus(
    string Status,
    string Stage,
    string? RunId,
    string? Mode,
    bool DryRun,
    int ProcessedWorkers,
    int TotalWorkers,
    string? CurrentWorkerId,
    string? LastAction,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastUpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);

public sealed record RunRecord(
    string RunId,
    string? Path,
    string ArtifactType,
    string? ConfigPath,
    string? MappingConfigPath,
    string Mode,
    bool DryRun,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationSeconds,
    int Creates,
    int Updates,
    int Enables,
    int Disables,
    int GraveyardMoves,
    int Deletions,
    int Quarantined,
    int Conflicts,
    int GuardrailFailures,
    int ManualReview,
    int Unchanged,
    JsonElement Report);

public sealed record RunSummary(
    string RunId,
    string? Path,
    string ArtifactType,
    string? ConfigPath,
    string? MappingConfigPath,
    string Mode,
    bool DryRun,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? DurationSeconds,
    int ProcessedWorkers,
    int TotalWorkers,
    int Creates,
    int Updates,
    int Enables,
    int Disables,
    int GraveyardMoves,
    int Deletions,
    int Quarantined,
    int Conflicts,
    int GuardrailFailures,
    int ManualReview,
    int Unchanged);

public sealed record RunDetail(
    RunSummary Run,
    JsonElement Report,
    IReadOnlyDictionary<string, int> BucketCounts);

public sealed record RunEntry(
    string EntryId,
    string RunId,
    string ArtifactType,
    string Mode,
    string Bucket,
    string BucketLabel,
    string? WorkerId,
    string? SamAccountName,
    string? Reason,
    string? ReviewCategory,
    string? ReviewCaseType,
    DateTimeOffset? StartedAt,
    int ChangeCount,
    OperationSummary? OperationSummary,
    IReadOnlyList<DiffRow> DiffRows,
    JsonElement Item);

public sealed record RunEntryRecord(
    string EntryId,
    string RunId,
    string Bucket,
    int BucketIndex,
    string? WorkerId,
    string? SamAccountName,
    string? Reason,
    string? ReviewCategory,
    string? ReviewCaseType,
    DateTimeOffset? StartedAt,
    JsonElement Item);

public sealed record RunPlan(
    string RunId,
    string ArtifactType,
    string Mode,
    bool DryRun,
    int TotalWorkers,
    string? InitialWorkerId,
    string? InitialAction,
    JsonElement Report,
    IReadOnlyList<RunEntryRecord> Entries,
    RunTally Tally);

public sealed record RunTally(
    int Creates,
    int Updates,
    int Enables,
    int Disables,
    int GraveyardMoves,
    int Deletions,
    int Quarantined,
    int Conflicts,
    int GuardrailFailures,
    int ManualReview,
    int Unchanged);

public sealed record WorkerSnapshot(
    string WorkerId,
    string PreferredName,
    string LastName,
    string Department,
    string TargetOu,
    bool IsPrehire,
    IReadOnlyDictionary<string, string?> Attributes);

public sealed record DirectoryUserSnapshot(
    string? SamAccountName,
    string? DistinguishedName,
    bool? Enabled,
    string? DisplayName,
    IReadOnlyDictionary<string, string?> Attributes);

public sealed record IdentityMatchResult(
    string Bucket,
    bool MatchedExistingUser,
    string SamAccountName,
    string? Reason,
    string? OperatorActionSummary);

public sealed record AttributeChange(
    string Attribute,
    string? Source,
    string Before,
    string After,
    bool Changed);

public sealed record DirectoryMutationCommand(
    string Action,
    string WorkerId,
    string? ManagerId,
    string SamAccountName,
    string UserPrincipalName,
    string Mail,
    string TargetOu,
    string DisplayName,
    bool EnableAccount);

public sealed record DirectoryCommandResult(
    bool Succeeded,
    string Action,
    string SamAccountName,
    string? DistinguishedName,
    string Message,
    string? RunId);

public sealed record DiffRow(
    string Attribute,
    string? Source,
    string Before,
    string After,
    bool Changed);

public sealed record SourceAttributeRow(
    string Attribute,
    string Value);

public sealed record OperationSummary(
    string Action,
    string? Effect,
    string? TargetOu,
    string? FromOu,
    string? ToOu);

public sealed record WorkerPreviewResult(
    string? ReportPath,
    string? RunId,
    string? Mode,
    string? Status,
    string? ErrorMessage,
    string? ArtifactType,
    string? SuccessFactorsAuth,
    string WorkerId,
    IReadOnlyList<string> Buckets,
    bool? MatchedExistingUser,
    string? ReviewCategory,
    string? ReviewCaseType,
    string? Reason,
    string? OperatorActionSummary,
    string? SamAccountName,
    string? ManagerDistinguishedName,
    string? TargetOu,
    string? CurrentDistinguishedName,
    bool? CurrentEnabled,
    bool? ProposedEnable,
    OperationSummary? OperationSummary,
    IReadOnlyList<DiffRow> DiffRows,
    IReadOnlyList<SourceAttributeRow> SourceAttributes,
    IReadOnlyList<WorkerPreviewEntry> Entries);

public sealed record WorkerPreviewEntry(
    string Bucket,
    JsonElement Item);

public sealed record WorkerPreviewLogEntry(
    string Event,
    string WorkerId,
    DateTimeOffset Timestamp,
    string? Target,
    string? Source,
    string? SourceValue,
    string? CurrentValue,
    string? ProposedValue,
    bool? Changed,
    string? Message);

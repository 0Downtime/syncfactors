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

public sealed record DiffRow(
    string Attribute,
    string? Source,
    string Before,
    string After,
    bool Changed);

public sealed record OperationSummary(
    string Action,
    string? Effect,
    string? TargetOu,
    string? FromOu,
    string? ToOu);

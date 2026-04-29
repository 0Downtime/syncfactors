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

public sealed record DashboardSnapshot(
    RuntimeStatus Status,
    IReadOnlyList<RunSummary> Runs,
    RunSummary? ActiveRun,
    RunSummary? LastCompletedRun,
    bool RequiresAttention,
    string? AttentionMessage,
    DateTimeOffset CheckedAt);

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
    JsonElement Report,
    string RunTrigger = "AdHoc",
    string? RequestedBy = null);

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
    int Unchanged,
    string SyncScope = "Unknown",
    string RunTrigger = "AdHoc",
    string? RequestedBy = null);

public sealed record RunDetail(
    RunSummary Run,
    JsonElement Report,
    IReadOnlyDictionary<string, int> BucketCounts);

public sealed record WorkerPreviewHistoryItem(
    string RunId,
    string WorkerId,
    string? SamAccountName,
    string Bucket,
    string? Status,
    DateTimeOffset StartedAt,
    int ChangeCount,
    string? Action,
    string? Reason,
    string Fingerprint);

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
    string? FailureSummary,
    string? PrimarySummary,
    IReadOnlyList<string> TopChangedAttributes,
    IReadOnlyList<DiffRow> DiffRows,
    JsonElement Item);

public sealed record ChangedAttributeTotal(
    string Attribute,
    int Count);

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

public sealed record RunQueueRequest(
    string RequestId,
    string Mode,
    bool DryRun,
    string RunTrigger,
    string? RequestedBy,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? RunId,
    string? ErrorMessage);

public sealed record StartRunRequest(
    bool DryRun,
    string Mode = "BulkSync",
    string RunTrigger = "AdHoc",
    string? RequestedBy = null);

public sealed record RunQueueRecoveryProbeRequest(
    string? RequestId,
    string Status,
    string? ExpectedStatus = null,
    string Mode = "BulkSync",
    bool DryRun = false,
    string RunTrigger = "AutomationRecoveryProbe",
    string? RequestedBy = "Automation",
    string? RunId = null,
    string? WorkerName = "automation-recovery-probe",
    int StartedMinutesAgo = 10,
    bool Force = true);

public sealed record SyncScheduleStatus(
    bool Enabled,
    int IntervalMinutes,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastScheduledRunAt,
    DateTimeOffset? LastEnqueueAttemptAt,
    string? LastEnqueueError);

public sealed record UpdateSyncScheduleRequest(
    bool Enabled,
    int IntervalMinutes);

public sealed record RealSyncSettings(
    bool Enabled = true);

public sealed record WorkerRunSettings(
    int MaxCreatesPerRun,
    int MaxDisablesPerRun = int.MaxValue,
    int MaxDeletionsPerRun = int.MaxValue);

public sealed record GraveyardDeletionQueueSettings(
    int RetentionDays,
    bool AutoDeleteEnabled);

public sealed record LifecyclePolicySettings(
    string ActiveOu,
    string PrehireOu,
    string GraveyardOu,
    string InactiveStatusField,
    IReadOnlyList<string> InactiveStatusValues,
    string? LeaveOu = null,
    IReadOnlyList<string>? LeaveStatusValues = null,
    string DirectoryIdentityAttribute = "sAMAccountName");

public sealed record IdentityCorrelationSettings(
    bool Enabled,
    string IdentityAttribute,
    string? SuccessorPersonIdExternalAttribute,
    string? PreviousPersonIdExternalAttribute);

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

public sealed record ProvisioningDecisionStep(
    string Step,
    string Outcome,
    string Detail,
    string Tone = "neutral");

public sealed record PlannedWorkerAction(
    WorkerSnapshot Worker,
    DirectoryUserSnapshot DirectoryUser,
    IdentityMatchResult Identity,
    string? ManagerDistinguishedName,
    string ProposedEmailAddress,
    IReadOnlyList<AttributeChange> AttributeChanges,
    IReadOnlyList<MissingSourceAttributeRow> MissingSourceAttributes,
    string Bucket,
    string CurrentOu,
    string TargetOu,
    bool? CurrentEnabled,
    bool TargetEnabled,
    string PrimaryAction,
    IReadOnlyList<DirectoryOperation> Operations,
    string? ReviewCategory,
    string? ReviewCaseType,
    string? Reason,
    bool CanAutoApply,
    IReadOnlyList<ProvisioningDecisionStep>? DecisionSteps = null);

public sealed record DirectoryOperation(
    string Kind,
    string? TargetOu = null);

public sealed record DirectoryMutationCommand(
    string Action,
    string WorkerId,
    string? ManagerId,
    string? ManagerDistinguishedName,
    string SamAccountName,
    string CommonName,
    string UserPrincipalName,
    string Mail,
    string TargetOu,
    string DisplayName,
    string? CurrentDistinguishedName,
    bool EnableAccount,
    IReadOnlyList<DirectoryOperation> Operations,
    IReadOnlyDictionary<string, string?> Attributes);

public sealed record DirectoryCommandResult(
    bool Succeeded,
    string Action,
    string SamAccountName,
    string? DistinguishedName,
    string Message,
    string? RunId,
    bool? VerifiedEnabled = null,
    string? VerifiedDistinguishedName = null,
    string? VerifiedParentOu = null);

public sealed record RunCaptureMetadata(
    int SchemaVersion,
    string RunId,
    bool DryRun,
    string SyncScope,
    RunConfigFingerprint? SyncConfig,
    RunConfigFingerprint? MappingConfig);

public sealed record RunConfigFingerprint(
    string Path,
    string? Sha256);

public sealed record DiffRow(
    string Attribute,
    string? Source,
    string Before,
    string After,
    bool Changed);

public sealed record SourceAttributeRow(
    string Attribute,
    string Value);

public sealed record MissingSourceAttributeRow(
    string Attribute,
    string Reason);

public sealed record OperationSummary(
    string Action,
    string? Effect,
    string? TargetOu,
    string? FromOu,
    string? ToOu);

public sealed record GraveyardRetentionRecord(
    string WorkerId,
    string? SamAccountName,
    string? DisplayName,
    string? DistinguishedName,
    string Status,
    DateTimeOffset? EndDateUtc,
    DateTimeOffset LastObservedAtUtc,
    bool Active,
    bool IsOnHold = false,
    DateTimeOffset? HoldPlacedAtUtc = null,
    string? HoldPlacedBy = null);

public sealed record GraveyardRetentionReportStatus(
    DateTimeOffset? LastSentAtUtc,
    DateTimeOffset? LastAttemptedAtUtc,
    string? LastError);

public sealed record GraveyardRetentionNotificationSettings(
    bool Enabled,
    int IntervalDays,
    int RetentionDays,
    string SubjectPrefix,
    IReadOnlyList<string> Recipients);

public sealed record ApplyPreviewRequest(
    string WorkerId,
    string PreviewRunId,
    string PreviewFingerprint,
    bool AcknowledgeRealSync);

public sealed record LaunchFullRunRequest(
    bool DryRun,
    bool AcknowledgeRealSync);

public sealed record RunLaunchResult(
    string RunId,
    string Status,
    bool DryRun,
    string Message);

public sealed record WorkerPreviewResult(
    string? ReportPath,
    string? RunId,
    string? PreviousRunId,
    string Fingerprint,
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
    IReadOnlyList<SourceAttributeRow> UsedSourceAttributes,
    IReadOnlyList<SourceAttributeRow> UnusedSourceAttributes,
    IReadOnlyList<MissingSourceAttributeRow> MissingSourceAttributes,
    IReadOnlyList<WorkerPreviewEntry> Entries,
    IReadOnlyList<ProvisioningDecisionStep>? DecisionSteps = null);

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

public sealed record WorkerRunResult(
    string WorkerId,
    string Bucket,
    string? SamAccountName,
    string? Reason,
    string? ReviewCategory,
    string? ReviewCaseType,
    string? Action,
    bool Applied,
    bool Succeeded,
    OperationSummary? OperationSummary,
    IReadOnlyList<DiffRow> DiffRows,
    JsonElement Item);

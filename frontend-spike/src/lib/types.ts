export type Session = {
  isAuthenticated: boolean
  userId: string | null
  username: string | null
  role: string | null
  isAdmin: boolean
}

export type RuntimeStatus = {
  status: string
  stage: string
  runId: string | null
  mode: string | null
  dryRun: boolean
  processedWorkers: number
  totalWorkers: number
  currentWorkerId: string | null
  lastAction: string | null
  startedAt: string | null
  lastUpdatedAt: string | null
  completedAt: string | null
  errorMessage: string | null
}

export type RunSummary = {
  runId: string
  path: string | null
  artifactType: string
  configPath: string | null
  mappingConfigPath: string | null
  mode: string
  dryRun: boolean
  status: string
  startedAt: string
  completedAt: string | null
  durationSeconds: number | null
  processedWorkers: number
  totalWorkers: number
  creates: number
  updates: number
  enables: number
  disables: number
  graveyardMoves: number
  deletions: number
  quarantined: number
  conflicts: number
  guardrailFailures: number
  manualReview: number
  unchanged: number
  syncScope: string
  runTrigger: string
  requestedBy: string | null
}

export type DashboardSnapshot = {
  status: RuntimeStatus
  runs: RunSummary[]
  activeRun: RunSummary | null
  lastCompletedRun: RunSummary | null
  requiresAttention: boolean
  attentionMessage: string | null
  checkedAt: string
}

export type DependencyProbeResult = {
  dependency: string
  status: string
  summary: string
  details: string | null
  checkedAt: string
  durationMilliseconds: number
  observedAt: string | null
  isStale: boolean
}

export type DependencyHealthSnapshot = {
  status: string
  checkedAt: string
  probes: DependencyProbeResult[]
}

export type RunQueueRequest = {
  requestId: string
  mode: string
  dryRun: boolean
  runTrigger: string
  requestedBy: string | null
  status: string
  requestedAt: string
  startedAt: string | null
  completedAt: string | null
  runId: string | null
  errorMessage: string | null
}

export type SyncScheduleStatus = {
  enabled: boolean
  intervalMinutes: number
  nextRunAt: string | null
  lastScheduledRunAt: string | null
  lastEnqueueAttemptAt: string | null
  lastEnqueueError: string | null
}

export type RunsResponse = {
  runs: RunSummary[]
  total: number
  page: number
  pageSize: number
}

export type RunDetail = {
  run: RunSummary
  report: Record<string, unknown>
  bucketCounts: Record<string, number>
}

export type RunEntriesResponse = {
  run: RunSummary
  entries: RunEntry[]
  total: number
  page: number
  pageSize: number
}

export type OperationSummary = {
  action: string
  effect: string | null
  targetOu: string | null
  fromOu: string | null
  toOu: string | null
}

export type DiffRow = {
  attribute: string
  source: string | null
  before: string
  after: string
  changed: boolean
}

export type SourceAttributeRow = {
  attribute: string
  value: string
}

export type MissingSourceAttributeRow = {
  attribute: string
  reason: string
}

export type WorkerPreviewHistoryItem = {
  runId: string
  workerId: string
  samAccountName: string | null
  bucket: string
  status: string | null
  startedAt: string
  changeCount: number
  action: string | null
  reason: string | null
  fingerprint: string
}

export type WorkerPreviewEntry = {
  bucket: string
  item: Record<string, unknown>
}

export type WorkerPreviewResult = {
  reportPath: string | null
  runId: string | null
  previousRunId: string | null
  fingerprint: string
  mode: string | null
  status: string | null
  errorMessage: string | null
  artifactType: string | null
  successFactorsAuth: string | null
  workerId: string
  buckets: string[]
  matchedExistingUser: boolean | null
  reviewCategory: string | null
  reviewCaseType: string | null
  reason: string | null
  operatorActionSummary: string | null
  samAccountName: string | null
  managerDistinguishedName: string | null
  targetOu: string | null
  currentDistinguishedName: string | null
  currentEnabled: boolean | null
  proposedEnable: boolean | null
  operationSummary: OperationSummary | null
  diffRows: DiffRow[]
  sourceAttributes: SourceAttributeRow[]
  usedSourceAttributes: SourceAttributeRow[]
  unusedSourceAttributes: SourceAttributeRow[]
  missingSourceAttributes: MissingSourceAttributeRow[]
  entries: WorkerPreviewEntry[]
}

export type DirectoryCommandResult = {
  succeeded: boolean
  action: string
  samAccountName: string
  distinguishedName: string | null
  message: string
  runId: string | null
}

export type LocalUserSummary = {
  userId: string
  username: string
  role: string
  isActive: boolean
  createdAt: string
  updatedAt: string
  lastLoginAt: string | null
}

export type LocalUserCommandResult = {
  succeeded: boolean
  message: string
}

export type RunEntry = {
  entryId: string
  runId: string
  artifactType: string
  mode: string
  bucket: string
  bucketLabel: string
  workerId: string | null
  samAccountName: string | null
  reason: string | null
  reviewCategory: string | null
  reviewCaseType: string | null
  startedAt: string | null
  changeCount: number
  operationSummary: OperationSummary | null
  failureSummary: string | null
  primarySummary: string | null
  topChangedAttributes: string[]
  diffRows: DiffRow[]
  item: Record<string, unknown>
}

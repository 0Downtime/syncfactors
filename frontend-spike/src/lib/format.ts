import type { RunEntry, RunSummary, WorkerPreviewResult } from '@/lib/types'

export function formatDate(value: string | null | undefined) {
  if (!value) {
    return 'Unknown'
  }

  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? 'Unknown' : parsed.toLocaleString()
}

export function statusTone(status: string | null | undefined) {
  switch ((status ?? '').toLowerCase()) {
    case 'healthy':
    case 'succeeded':
    case 'completed':
    case 'good':
    case 'idle':
      return 'good'
    case 'degraded':
    case 'warning':
    case 'warn':
    case 'cancelrequested':
    case 'pending':
    case 'planned':
    case 'queued':
    case 'running':
    case 'inprogress':
      return 'warn'
    case 'unhealthy':
    case 'failed':
    case 'bad':
    case 'error':
      return 'bad'
    case 'inactive':
    case 'canceled':
      return 'dim'
    case 'info':
    case 'admin':
      return 'info'
    default:
      return 'neutral'
  }
}

export function runSummaryLine(run: RunSummary) {
  const parts: string[] = []
  if (run.creates) parts.push(`${run.creates} creates`)
  if (run.updates) parts.push(`${run.updates} updates`)
  if (run.disables) parts.push(`${run.disables} disables`)
  if (run.deletions) parts.push(`${run.deletions} deletions`)
  if (run.conflicts) parts.push(`${run.conflicts} conflicts`)
  if (run.manualReview) parts.push(`${run.manualReview} manual review`)
  if (run.guardrailFailures) parts.push(`${run.guardrailFailures} guardrails`)
  return parts.length ? parts.join(' • ') : 'No materialized bucket counts yet'
}

export function displayBool(value: boolean | null) {
  if (value == null) return 'Unknown'
  return value ? 'Yes' : 'No'
}

export function enableTransition(current: boolean | null, proposed: boolean | null) {
  if (current === null || proposed === null) {
    return 'Unknown'
  }
  if (current === proposed) {
    return proposed ? 'Enabled' : 'Disabled'
  }
  return `${current ? 'Enabled' : 'Disabled'} -> ${proposed ? 'Enabled' : 'Disabled'}`
}

export function isPathLikeAttribute(attribute: string) {
  return attribute.includes('[') || attribute.includes('.')
}

export function buildRiskCallouts(preview: WorkerPreviewResult) {
  const callouts: string[] = []
  for (const row of preview.diffRows.filter((diff) => diff.changed)) {
    if (equalsIgnoreCase(row.attribute, 'userPrincipalName') || equalsIgnoreCase(row.attribute, 'mail')) {
      callouts.push(`${row.attribute} will change from '${row.before}' to '${row.after}'.`)
    }

    if (equalsIgnoreCase(row.attribute, 'manager')) {
      callouts.push(`Manager assignment will change to '${row.after}'.`)
    }
  }

  if (
    preview.operationSummary?.fromOu &&
    preview.operationSummary?.toOu &&
    !equalsIgnoreCase(preview.operationSummary.fromOu, preview.operationSummary.toOu)
  ) {
    callouts.push(
      `The account will move from '${preview.operationSummary.fromOu}' to '${preview.operationSummary.toOu}'.`,
    )
  }

  if (preview.currentEnabled !== preview.proposedEnable && preview.proposedEnable !== null) {
    callouts.push(preview.proposedEnable ? 'The account will be enabled.' : 'The account will be disabled.')
  }

  if (!preview.managerDistinguishedName && preview.diffRows.some((diff) => equalsIgnoreCase(diff.source, 'managerId'))) {
    callouts.push('Manager resolution is still blank for a manager-linked preview.')
  }

  return Array.from(new Set(callouts.map((value) => value.toLowerCase()))).map(
    (lowered) => callouts.find((value) => value.toLowerCase() === lowered) ?? lowered,
  )
}

export function getSavedPreviewRunId(entry: RunEntry) {
  if (entry.artifactType.toLowerCase() === 'workerpreview') {
    return entry.runId
  }

  const sourcePreviewRunId = entry.item.sourcePreviewRunId
  return typeof sourcePreviewRunId === 'string' ? sourcePreviewRunId : null
}

function equalsIgnoreCase(left: string | null | undefined, right: string | null | undefined) {
  return (left ?? '').localeCompare(right ?? '', undefined, { sensitivity: 'accent' }) === 0
}

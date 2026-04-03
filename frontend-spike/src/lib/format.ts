import type { RunSummary, WorkerPreviewResult } from '@/lib/types'

export function formatDate(value: string | null) {
  if (!value) {
    return 'Unknown'
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value))
}

export function statusTone(status: string) {
  switch (status.toLowerCase()) {
    case 'healthy':
    case 'succeeded':
    case 'completed':
    case 'idle':
      return 'good'
    case 'degraded':
    case 'queued':
    case 'running':
    case 'inprogress':
      return 'warn'
    case 'failed':
    case 'unhealthy':
    case 'error':
      return 'bad'
    default:
      return 'neutral'
  }
}

export function runSummaryLine(run: RunSummary) {
  const parts: string[] = []

  if (run.creates) parts.push(`${run.creates} creates`)
  if (run.updates) parts.push(`${run.updates} updates`)
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
  if (current == null && proposed != null) {
    return proposed ? 'Enabled on create' : 'Disabled on create'
  }

  if (current != null && proposed != null) {
    return `${current ? 'Enabled' : 'Disabled'} -> ${proposed ? 'Enabled' : 'Disabled'}`
  }

  return 'Unknown'
}

export function isPathLikeAttribute(attribute: string) {
  return attribute.includes('[') || attribute.includes('.')
}

export function buildRiskCallouts(preview: WorkerPreviewResult) {
  const callouts = new Set<string>()

  for (const row of preview.diffRows.filter((item) => item.changed)) {
    if (row.attribute === 'userPrincipalName' || row.attribute === 'mail') {
      callouts.add(`${row.attribute} will change from '${row.before}' to '${row.after}'.`)
    }

    if (row.attribute === 'manager') {
      callouts.add(`Manager assignment will change to '${row.after}'.`)
    }
  }

  if (
    preview.operationSummary?.fromOu &&
    preview.operationSummary.toOu &&
    preview.operationSummary.fromOu !== preview.operationSummary.toOu
  ) {
    callouts.add(
      `The account will move from '${preview.operationSummary.fromOu}' to '${preview.operationSummary.toOu}'.`,
    )
  }

  if (preview.currentEnabled !== preview.proposedEnable && preview.proposedEnable != null) {
    callouts.add(preview.proposedEnable ? 'The account will be enabled.' : 'The account will be disabled.')
  }

  if (!preview.managerDistinguishedName && preview.diffRows.some((item) => item.source === 'managerId')) {
    callouts.add('Manager resolution is still blank for a manager-linked preview.')
  }

  return [...callouts]
}

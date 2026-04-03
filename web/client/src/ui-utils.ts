import type { DashboardSnapshot, RunEntriesResponse, RunSummary } from './types.js';

export const THEME_KEY = 'syncfactors-next-theme';

export function getInitialTheme() {
  try {
    const stored = window.localStorage.getItem(THEME_KEY);
    if (stored === 'light' || stored === 'dark') {
      return stored;
    }
  } catch {
    // Ignore storage errors.
  }

  return typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

export function badgeClass(status: string | null | undefined) {
  switch ((status ?? '').toLowerCase()) {
    case 'healthy':
    case 'succeeded':
    case 'good':
      return 'good';
    case 'degraded':
    case 'warning':
    case 'warn':
    case 'cancelrequested':
    case 'pending':
    case 'planned':
    case 'inprogress':
      return 'warn';
    case 'unhealthy':
    case 'failed':
    case 'bad':
      return 'bad';
    case 'info':
    case 'admin':
      return 'info';
    case 'inactive':
    case 'canceled':
      return 'dim';
    default:
      return 'neutral';
  }
}

export function formatTimestamp(value: string | null | undefined) {
  if (!value) {
    return 'Unknown';
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? 'Unknown' : parsed.toLocaleString();
}

export function displayBool(value: boolean | null) {
  if (value === null) {
    return 'Unknown';
  }
  return value ? 'Yes' : 'No';
}

export function enableTransition(currentEnabled: boolean | null, proposedEnable: boolean | null) {
  if (currentEnabled === null || proposedEnable === null) {
    return 'Unknown';
  }
  if (currentEnabled === proposedEnable) {
    return proposedEnable ? 'Enabled' : 'Disabled';
  }
  return `${currentEnabled ? 'Enabled' : 'Disabled'} → ${proposedEnable ? 'Enabled' : 'Disabled'}`;
}

export function runSummary(run: DashboardSnapshot['runs'][number] | RunSummary) {
  const parts: string[] = [];
  if (run.creates) {
    parts.push(`${run.creates} creates`);
  }
  if (run.updates) {
    parts.push(`${run.updates} updates`);
  }
  if (run.disables) {
    parts.push(`${run.disables} disables`);
  }
  if (run.deletions) {
    parts.push(`${run.deletions} deletions`);
  }
  return parts.length ? parts.join(', ') : 'No changes';
}

export function getSavedPreviewRunId(entry: RunEntriesResponse['entries'][number]) {
  if (entry.artifactType.toLowerCase() === 'workerpreview') {
    return entry.runId;
  }

  const sourcePreviewRunId = entry.item.sourcePreviewRunId;
  return typeof sourcePreviewRunId === 'string' ? sourcePreviewRunId : null;
}

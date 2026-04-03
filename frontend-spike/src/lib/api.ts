import type {
  DashboardSnapshot,
  DependencyHealthSnapshot,
  DirectoryCommandResult,
  RunDetail,
  RunEntry,
  RunQueueRequest,
  RunSummary,
  SyncScheduleStatus,
  WorkerPreviewHistoryItem,
  WorkerPreviewResult,
} from '@/lib/types'

async function readError(response: Response) {
  try {
    const data = (await response.json()) as { error?: string; message?: string }
    return data.error ?? data.message ?? response.statusText
  } catch {
    return response.statusText
  }
}

export async function apiFetch<T>(path: string, init?: RequestInit) {
  const response = await fetch(path, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
  })

  if (!response.ok) {
    throw new Error(await readError(response))
  }

  return response.json() as Promise<T>
}

export const api = {
  dashboard: () => apiFetch<DashboardSnapshot>('/api/dashboard'),
  health: () => apiFetch<DependencyHealthSnapshot>('/api/health'),
  runs: () => apiFetch<{ runs: RunSummary[] }>('/api/runs'),
  queue: () => apiFetch<{ request: RunQueueRequest | null }>('/api/runs/queue'),
  createRun: (payload: { dryRun: boolean; mode?: string; runTrigger?: string }) =>
    apiFetch<RunQueueRequest>('/api/runs', {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  cancelRun: () =>
    apiFetch<{ status: string }>('/api/runs/cancel', {
      method: 'POST',
      body: JSON.stringify({}),
    }),
  schedule: () => apiFetch<{ schedule: SyncScheduleStatus }>('/api/sync/schedule'),
  updateSchedule: (payload: { enabled: boolean; intervalMinutes: number }) =>
    apiFetch<{ schedule: SyncScheduleStatus }>('/api/sync/schedule', {
      method: 'PUT',
      body: JSON.stringify(payload),
    }),
  runDetail: (runId: string) => apiFetch<RunDetail>(`/api/runs/${runId}`),
  runEntries: (runId: string, params: URLSearchParams) =>
    apiFetch<{ run: RunSummary; entries: RunEntry[]; total: number; page: number; pageSize: number }>(
      `/api/runs/${runId}/entries?${params.toString()}`,
    ),
  previewByRunId: (runId: string) => apiFetch<WorkerPreviewResult>(`/api/previews/${runId}`),
  previewByWorkerId: (workerId: string) => apiFetch<WorkerPreviewResult>(`/api/workers/${workerId}/preview`),
  previewHistory: (workerId: string, take = 6) =>
    apiFetch<{ workerId: string; previews: WorkerPreviewHistoryItem[] }>(
      `/api/workers/${workerId}/previews?take=${take}`,
    ),
  applyPreview: (workerId: string, payload: { workerId: string; previewRunId: string; previewFingerprint: string; acknowledgeRealSync: boolean }) =>
    apiFetch<DirectoryCommandResult>(`/api/preview/${workerId}/apply`, {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
}

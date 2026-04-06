import type {
  DashboardSnapshot,
  DependencyHealthSnapshot,
  DirectoryCommandResult,
  LocalUserCommandResult,
  LocalUserSummary,
  RunDetail,
  RunEntriesResponse,
  RunsResponse,
  Session,
  SyncScheduleStatus,
  WorkerPreviewHistoryItem,
  WorkerPreviewResult,
} from '@/lib/types'

async function apiFetch<T>(path: string, init?: RequestInit) {
  const response = await fetch(path, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
  })

  if (!response.ok) {
    const payload = (await response.json().catch(() => null)) as { error?: string; message?: string } | null
    throw new Error(payload?.error ?? payload?.message ?? `Request failed: ${response.status}`)
  }

  return response.json() as Promise<T>
}

export const api = {
  session: () => apiFetch<Session>('/api/session'),
  login: (username: string, password: string, rememberMe: boolean, returnUrl?: string | null) =>
    apiFetch<Session>('/api/session/login', {
      method: 'POST',
      body: JSON.stringify({ username, password, rememberMe, returnUrl }),
    }),
  logout: () => apiFetch<Session>('/api/session/logout', { method: 'POST' }),
  dashboard: () => apiFetch<DashboardSnapshot>('/api/dashboard'),
  health: () => apiFetch<DependencyHealthSnapshot>('/api/health'),
  runs: (page: number, pageSize: number) =>
    apiFetch<RunsResponse>(`/api/runs?${new URLSearchParams({ page: String(page), pageSize: String(pageSize) })}`),
  createRun: (payload: { dryRun: boolean; mode: string; runTrigger: string }) =>
    apiFetch<unknown>('/api/runs', {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  cancelRun: () => apiFetch<{ status: string }>('/api/runs/cancel', { method: 'POST' }),
  queueDeleteAllUsers: (confirmationText: string) =>
    apiFetch<unknown>('/api/runs/delete-all', {
      method: 'POST',
      body: JSON.stringify({ confirmationText }),
    }),
  schedule: () => apiFetch<{ schedule: SyncScheduleStatus }>('/api/sync/schedule'),
  updateSchedule: (payload: { enabled: boolean; intervalMinutes: number }) =>
    apiFetch<{ schedule: SyncScheduleStatus }>('/api/sync/schedule', {
      method: 'PUT',
      body: JSON.stringify(payload),
    }),
  runDetail: (runId: string) => apiFetch<RunDetail>(`/api/runs/${encodeURIComponent(runId)}`),
  runEntries: (runId: string, query: Record<string, string | number | undefined>) => {
    const params = new URLSearchParams()
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== '') {
        params.set(key, String(value))
      }
    }

    return apiFetch<RunEntriesResponse>(`/api/runs/${encodeURIComponent(runId)}/entries?${params.toString()}`)
  },
  previewByRunId: (runId: string) => apiFetch<WorkerPreviewResult>(`/api/previews/${encodeURIComponent(runId)}`),
  createPreview: (workerId: string) =>
    apiFetch<WorkerPreviewResult>('/api/previews', {
      method: 'POST',
      body: JSON.stringify({ workerId }),
    }),
  previewHistory: (workerId: string) =>
    apiFetch<{ workerId: string; previews: WorkerPreviewHistoryItem[] }>(
      `/api/workers/${encodeURIComponent(workerId)}/previews`,
    ),
  applyPreview: (
    workerId: string,
    payload: {
      workerId: string
      previewRunId: string
      previewFingerprint: string
      acknowledgeRealSync: boolean
    },
  ) =>
    apiFetch<DirectoryCommandResult>(`/api/preview/${encodeURIComponent(workerId)}/apply`, {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  users: () => apiFetch<{ users: LocalUserSummary[] }>('/api/admin/users'),
  createUser: (username: string, password: string, isAdmin: boolean) =>
    apiFetch<LocalUserCommandResult>('/api/admin/users', {
      method: 'POST',
      body: JSON.stringify({ username, password, isAdmin }),
    }),
  resetUserPassword: (userId: string, newPassword: string) =>
    apiFetch<LocalUserCommandResult>(`/api/admin/users/${encodeURIComponent(userId)}/password`, {
      method: 'POST',
      body: JSON.stringify({ newPassword }),
    }),
  setUserRole: (userId: string, isAdmin: boolean) =>
    apiFetch<LocalUserCommandResult>(`/api/admin/users/${encodeURIComponent(userId)}/role`, {
      method: 'POST',
      body: JSON.stringify({ isAdmin }),
    }),
  setUserActive: (userId: string, isActive: boolean) =>
    apiFetch<LocalUserCommandResult>(`/api/admin/users/${encodeURIComponent(userId)}/active`, {
      method: 'POST',
      body: JSON.stringify({ isActive }),
    }),
  deleteUser: (userId: string) =>
    apiFetch<LocalUserCommandResult>(`/api/admin/users/${encodeURIComponent(userId)}`, {
      method: 'DELETE',
    }),
}

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
} from './types.js';

async function fetchJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {}),
    },
  });

  if (!response.ok) {
    const payload = (await response.json().catch(() => null)) as { error?: string; message?: string } | null;
    throw new Error(payload?.error ?? payload?.message ?? `Request failed: ${response.status}`);
  }

  return (await response.json()) as T;
}

export function getSession() {
  return fetchJson<Session>('/api/session');
}

export function login(username: string, password: string, rememberMe: boolean, returnUrl?: string | null) {
  return fetchJson<Session>('/api/session/login', {
    method: 'POST',
    body: JSON.stringify({ username, password, rememberMe, returnUrl }),
  });
}

export function logout() {
  return fetchJson<Session>('/api/session/logout', { method: 'POST' });
}

export function getDashboard() {
  return fetchJson<DashboardSnapshot>('/api/dashboard');
}

export function getHealth() {
  return fetchJson<DependencyHealthSnapshot>('/api/health');
}

export function listRuns(page: number, pageSize: number) {
  const params = new URLSearchParams({
    page: String(page),
    pageSize: String(pageSize),
  });
  return fetchJson<RunsResponse>(`/api/runs?${params.toString()}`);
}

export function startRun(dryRun: boolean) {
  return fetchJson<unknown>('/api/runs', {
    method: 'POST',
    body: JSON.stringify({ dryRun, mode: 'BulkSync', runTrigger: 'AdHoc' }),
  });
}

export function cancelRun() {
  return fetchJson<{ status: string }>('/api/runs/cancel', { method: 'POST' });
}

export function getSyncSchedule() {
  return fetchJson<{ schedule: SyncScheduleStatus }>('/api/sync/schedule');
}

export function saveSyncSchedule(enabled: boolean, intervalMinutes: number) {
  return fetchJson<{ schedule: SyncScheduleStatus }>('/api/sync/schedule', {
    method: 'PUT',
    body: JSON.stringify({ enabled, intervalMinutes }),
  });
}

export function queueDeleteAllUsers(confirmationText: string) {
  return fetchJson<unknown>('/api/runs/delete-all', {
    method: 'POST',
    body: JSON.stringify({ confirmationText }),
  });
}

export function getRun(runId: string) {
  return fetchJson<RunDetail>(`/api/runs/${encodeURIComponent(runId)}`);
}

export function getRunEntries(runId: string, query: Record<string, string | number | undefined>) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== '') {
      params.set(key, String(value));
    }
  }
  return fetchJson<RunEntriesResponse>(`/api/runs/${encodeURIComponent(runId)}/entries?${params.toString()}`);
}

export function getPreview(runId: string) {
  return fetchJson<WorkerPreviewResult>(`/api/previews/${encodeURIComponent(runId)}`);
}

export function createPreview(workerId: string) {
  return fetchJson<WorkerPreviewResult>('/api/previews', {
    method: 'POST',
    body: JSON.stringify({ workerId }),
  });
}

export function getPreviewHistory(workerId: string) {
  return fetchJson<{ workerId: string; previews: WorkerPreviewHistoryItem[] }>(`/api/workers/${encodeURIComponent(workerId)}/previews`);
}

export function applyPreview(workerId: string, previewRunId: string, previewFingerprint: string, acknowledgeRealSync: boolean) {
  return fetchJson<DirectoryCommandResult>(`/api/preview/${encodeURIComponent(workerId)}/apply`, {
    method: 'POST',
    body: JSON.stringify({ workerId, previewRunId, previewFingerprint, acknowledgeRealSync }),
  });
}

export function listUsers() {
  return fetchJson<{ users: LocalUserSummary[] }>('/api/admin/users');
}

export function createUser(username: string, password: string, isAdmin: boolean) {
  return fetchJson<LocalUserCommandResult>('/api/admin/users', {
    method: 'POST',
    body: JSON.stringify({ username, password, isAdmin }),
  });
}

export function resetUserPassword(userId: string, newPassword: string) {
  return fetchJson<LocalUserCommandResult>(`/api/admin/users/${encodeURIComponent(userId)}/password`, {
    method: 'POST',
    body: JSON.stringify({ newPassword }),
  });
}

export function setUserRole(userId: string, isAdmin: boolean) {
  return fetchJson<LocalUserCommandResult>(`/api/admin/users/${encodeURIComponent(userId)}/role`, {
    method: 'POST',
    body: JSON.stringify({ isAdmin }),
  });
}

export function setUserActive(userId: string, isActive: boolean) {
  return fetchJson<LocalUserCommandResult>(`/api/admin/users/${encodeURIComponent(userId)}/active`, {
    method: 'POST',
    body: JSON.stringify({ isActive }),
  });
}

export function deleteUser(userId: string) {
  return fetchJson<LocalUserCommandResult>(`/api/admin/users/${encodeURIComponent(userId)}`, {
    method: 'DELETE',
  });
}

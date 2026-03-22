import type {
  DashboardStatus,
  EntryListResponse,
  QueueName,
  QueueResponse,
  RunDetailResponse,
  WorkerDetailResponse,
  WorkerHistoryResponse,
} from './types.js';

async function fetchJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  if (!response.ok) {
    const payload = (await response.json().catch(() => null)) as { error?: string; detail?: string } | null;
    throw new Error(payload?.detail ?? payload?.error ?? `Request failed: ${response.status}`);
  }

  return (await response.json()) as T;
}

export async function getStatus(): Promise<DashboardStatus> {
  const response = await fetchJson<{ status: DashboardStatus }>('/api/status');
  return response.status;
}

export async function getRun(runId: string): Promise<RunDetailResponse> {
  return fetchJson<RunDetailResponse>(`/api/runs/${encodeURIComponent(runId)}`);
}

export async function getRunEntries(
  runId: string,
  query: { bucket?: string; filter?: string; workerId?: string; reason?: string; entryId?: string } = {},
): Promise<EntryListResponse> {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value) {
      params.set(key, value);
    }
  }

  const suffix = params.toString() ? `?${params.toString()}` : '';
  return fetchJson<EntryListResponse>(`/api/runs/${encodeURIComponent(runId)}/entries${suffix}`);
}

export async function getQueue(
  queueName: QueueName,
  query: { reason?: string; reviewCaseType?: string; workerId?: string; filter?: string; page?: number; pageSize?: number } = {},
): Promise<QueueResponse> {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '') {
      params.set(key, String(value));
    }
  }

  const suffix = params.toString() ? `?${params.toString()}` : '';
  return fetchJson<QueueResponse>(`/api/queues/${encodeURIComponent(queueName)}${suffix}`);
}

export async function getWorkerHistory(workerId: string): Promise<WorkerHistoryResponse> {
  return fetchJson<WorkerHistoryResponse>(`/api/workers/${encodeURIComponent(workerId)}/history`);
}

export async function getWorkerDetail(workerId: string): Promise<WorkerDetailResponse> {
  return fetchJson<WorkerDetailResponse>(`/api/workers/${encodeURIComponent(workerId)}`);
}

export async function openLocalPath(path: string): Promise<void> {
  await fetchJson<{ ok: boolean }>('/api/open-path', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ path }),
  } as RequestInit);
}

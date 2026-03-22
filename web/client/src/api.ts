import type {
  OperatorActionKind,
  OperatorCommandResult,
  DashboardStatus,
  EntryListResponse,
  QueueName,
  QueueResponse,
  RunDetailResponse,
  WorkerActionKind,
  WorkerActionResponse,
  WorkerDetailResponse,
  WorkerHistoryResponse,
  WorkerPreviewMode,
  WorkerPreviewResponse,
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

export async function runWorkerAction(workerId: string, action: WorkerActionKind): Promise<WorkerActionResponse> {
  return fetchJson<WorkerActionResponse>(`/api/workers/${encodeURIComponent(workerId)}/actions`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ action }),
  } as RequestInit);
}

export async function previewWorker(workerId: string, previewMode: WorkerPreviewMode): Promise<WorkerPreviewResponse> {
  return fetchJson<WorkerPreviewResponse>(`/api/workers/${encodeURIComponent(workerId)}/preview`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ previewMode }),
  } as RequestInit);
}

export async function applyWorker(workerId: string, confirmText: string): Promise<WorkerActionResponse> {
  return fetchJson<WorkerActionResponse>(`/api/workers/${encodeURIComponent(workerId)}/apply`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ confirmText }),
  } as RequestInit);
}

export async function runOperatorAction(action: OperatorActionKind): Promise<OperatorCommandResult> {
  return fetchJson<OperatorCommandResult>('/api/actions/runs', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ action }),
  } as RequestInit);
}

export async function runPreflight(): Promise<OperatorCommandResult> {
  return fetchJson<OperatorCommandResult>('/api/actions/preflight', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
  } as RequestInit);
}

export async function runFreshReset(confirmText: string, additionalConfirmations: string[]): Promise<OperatorCommandResult> {
  return fetchJson<OperatorCommandResult>('/api/actions/fresh-reset', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ confirmText, additionalConfirmations }),
  } as RequestInit);
}

export async function exportRunBucket(runId: string, bucket: string, filter: string, confirmText: string): Promise<OperatorCommandResult> {
  return fetchJson<OperatorCommandResult>(`/api/runs/${encodeURIComponent(runId)}/export`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ scope: 'selected-bucket', bucket, filter, confirmText }),
  } as RequestInit);
}

export async function openRunReport(runId: string, confirmText: string): Promise<OperatorCommandResult> {
  return fetchJson<OperatorCommandResult>(`/api/runs/${encodeURIComponent(runId)}/open`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ confirmText }),
  } as RequestInit);
}

export async function copyRunReportPath(runId: string, confirmText: string): Promise<OperatorCommandResult> {
  return fetchJson<OperatorCommandResult>(`/api/runs/${encodeURIComponent(runId)}/copy-path`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ confirmText }),
  } as RequestInit);
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

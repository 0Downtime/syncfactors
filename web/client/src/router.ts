export type Route =
  | { kind: 'login'; returnUrl: string | null }
  | { kind: 'dashboard' }
  | { kind: 'sync'; page: number }
  | { kind: 'preview'; runId: string | null; workerId: string | null; showAllAttributes: boolean }
  | { kind: 'run'; runId: string; bucket: string; workerId: string; filter: string; page: number }
  | { kind: 'users' };

export function parseRoute(location: Location): Route {
  const url = new URL(location.href);
  const path = url.pathname;
  if (path === '/login') {
    return { kind: 'login', returnUrl: url.searchParams.get('returnUrl') };
  }
  if (path === '/sync') {
    return { kind: 'sync', page: parsePositiveInt(url.searchParams.get('page'), 1) };
  }
  if (path === '/preview') {
    return {
      kind: 'preview',
      runId: url.searchParams.get('runId'),
      workerId: url.searchParams.get('workerId'),
      showAllAttributes: url.searchParams.get('showAllAttributes') === 'true',
    };
  }
  if (path.startsWith('/runs/')) {
    return {
      kind: 'run',
      runId: decodeURIComponent(path.slice('/runs/'.length)),
      bucket: url.searchParams.get('bucket') ?? '',
      workerId: url.searchParams.get('workerId') ?? '',
      filter: url.searchParams.get('filter') ?? '',
      page: parsePositiveInt(url.searchParams.get('page'), 1),
    };
  }
  if (path === '/admin/users') {
    return { kind: 'users' };
  }
  return { kind: 'dashboard' };
}

export function navigate(path: string, setRoute: (route: Route) => void, replace = false) {
  const method = replace ? 'replaceState' : 'pushState';
  window.history[method](null, '', path);
  setRoute(parseRoute(window.location));
}

export function buildLoginPath(returnUrl: string) {
  return `/login?returnUrl=${encodeURIComponent(returnUrl)}`;
}

export function buildRunPath(runId: string, route: { bucket: string; workerId: string; filter: string; page: number }) {
  const params = new URLSearchParams();
  if (route.bucket) {
    params.set('bucket', route.bucket);
  }
  if (route.workerId) {
    params.set('workerId', route.workerId);
  }
  if (route.filter) {
    params.set('filter', route.filter);
  }
  if (route.page > 1) {
    params.set('page', String(route.page));
  }
  return `/runs/${encodeURIComponent(runId)}${params.toString() ? `?${params.toString()}` : ''}`;
}

function parsePositiveInt(value: string | null, fallback: number) {
  const parsed = Number.parseInt(value ?? '', 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
}

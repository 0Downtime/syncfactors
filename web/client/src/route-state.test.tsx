// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { getRouteState, syncRouteState, type RouteState } from './route-state.js';

describe('route-state', () => {
  it('preserves changed review explorer mode through the URL', () => {
    window.history.replaceState(null, '', '/');

    const route: RouteState = {
      view: 'dashboard',
      runId: 'run-1',
      bucket: 'updates',
      entryId: 'run-1:updates:1001:0',
      filter: 'rehire',
      queueName: 'manual-review',
      reason: '',
      reviewCaseType: '',
      workerId: '1001',
      diffMode: 'changed',
      reviewExplorer: 'changed',
      page: 1,
      pageSize: 25,
    };

    syncRouteState(route, false);

    expect(window.location.search).toContain('reviewExplorer=changed');
    expect(getRouteState().reviewExplorer).toBe('changed');
  });
});

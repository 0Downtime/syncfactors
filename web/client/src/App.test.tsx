// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from './App.js';

const fetchMock = vi.fn<typeof fetch>();

describe('App', () => {
  beforeEach(() => {
    fetchMock.mockReset();
    vi.stubGlobal('fetch', fetchMock);
    window.history.replaceState(null, '', '/');
  });

  it('redirects unauthenticated users to login and signs in', async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ isAuthenticated: false, userId: null, username: null, role: null, isAdmin: false }))
      .mockResolvedValueOnce(jsonResponse({ isAuthenticated: true, userId: 'user-1', username: 'operator', role: 'Admin', isAdmin: true }));

    render(<App />);

    await screen.findByRole('heading', { name: 'Sign in' });
    fireEvent.change(screen.getByLabelText('Username'), { target: { value: 'operator' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith('/api/session/login', expect.anything()));
  });

  it('renders shell navigation for authenticated admins', async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ isAuthenticated: true, userId: 'user-1', username: 'operator', role: 'Admin', isAdmin: true }))
      .mockResolvedValueOnce(jsonResponse({
        status: { status: 'Idle', stage: 'NotStarted', runId: null, mode: null, dryRun: true, processedWorkers: 0, totalWorkers: 0, currentWorkerId: null, lastAction: null, startedAt: null, lastUpdatedAt: null, completedAt: null, errorMessage: null },
        runs: [],
        activeRun: null,
        lastCompletedRun: null,
        requiresAttention: false,
        attentionMessage: null,
        checkedAt: '2026-04-02T12:00:00Z',
      }))
      .mockResolvedValueOnce(jsonResponse({ status: 'Healthy', checkedAt: '2026-04-02T12:00:00Z', probes: [] }));

    render(<App />);

    expect(await screen.findByText('SyncFactors')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Users' })).toBeInTheDocument();
  });
});

function jsonResponse(body: unknown) {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

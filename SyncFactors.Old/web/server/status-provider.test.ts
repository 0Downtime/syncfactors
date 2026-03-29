import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('node:child_process', () => ({
  execFile: vi.fn(),
}));

describe('PowerShellStatusProvider', () => {
  beforeEach(() => {
    vi.resetModules();
    vi.mocked(execFile).mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('reuses one in-flight PowerShell request for concurrent callers', async () => {
    const outputDirectory = path.join(os.tmpdir(), 'syncfactors-web-status');
    fs.mkdirSync(outputDirectory, { recursive: true });
    const outputPath = path.join(outputDirectory, `status-${process.pid}.json`);
    fs.writeFileSync(outputPath, JSON.stringify({
      currentRun: { status: 'Idle' },
      recentRuns: [],
    }));

    let release: (() => void) | null = null;
    vi.mocked(execFile).mockImplementation((_file, _args, _options, callback) => {
      release = () => callback(null, { stdout: '', stderr: '' });
      return {} as never;
    });

    const { PowerShellStatusProvider } = await import('./status-provider.js');
    const provider = new PowerShellStatusProvider();

    const first = provider.getStatus('/tmp/config.json', 25);
    const second = provider.getStatus('/tmp/config.json', 25);

    expect(execFile).toHaveBeenCalledTimes(1);
    release?.();

    const [firstStatus, secondStatus] = await Promise.all([first, second]);
    expect(firstStatus).toEqual(secondStatus);
  });

  it('uses a longer cache window while idle and refreshes more quickly during active runs', async () => {
    vi.useFakeTimers();
    const outputDirectory = path.join(os.tmpdir(), 'syncfactors-web-status');
    fs.mkdirSync(outputDirectory, { recursive: true });
    const outputPath = path.join(outputDirectory, `status-${process.pid}.json`);
    const statuses = [
      { currentRun: { status: 'Idle' }, recentRuns: [] },
      { currentRun: { status: 'InProgress' }, recentRuns: [] },
      { currentRun: { status: 'InProgress' }, recentRuns: [] },
    ];

    vi.mocked(execFile).mockImplementation((_file, _args, _options, callback) => {
      const nextStatus = statuses.shift() ?? { currentRun: { status: 'Idle' }, recentRuns: [] };
      fs.writeFileSync(outputPath, JSON.stringify(nextStatus));
      callback(null, { stdout: '', stderr: '' });
      return {} as never;
    });

    const { PowerShellStatusProvider } = await import('./status-provider.js');
    const provider = new PowerShellStatusProvider(60_000, 10_000);

    vi.setSystemTime(0);
    const idleFirst = await provider.getStatus('/tmp/config.json', 25);

    vi.setSystemTime(30_000);
    const idleSecond = await provider.getStatus('/tmp/config.json', 25);

    vi.setSystemTime(61_000);
    const activeFirst = await provider.getStatus('/tmp/config.json', 25);

    vi.setSystemTime(65_000);
    const activeSecond = await provider.getStatus('/tmp/config.json', 25);

    vi.setSystemTime(72_000);
    const activeThird = await provider.getStatus('/tmp/config.json', 25);

    expect(idleFirst.currentRun.status).toBe('Idle');
    expect(idleSecond.currentRun.status).toBe('Idle');
    expect(activeFirst.currentRun.status).toBe('InProgress');
    expect(activeSecond.currentRun.status).toBe('InProgress');
    expect(activeThird.currentRun.status).toBe('InProgress');
    expect(execFile).toHaveBeenCalledTimes(3);
  });
});

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import type { DashboardStatus } from './types.js';

const execFileAsync = promisify(execFile);

export interface StatusProvider {
  getStatus(configPath: string, historyLimit: number): Promise<DashboardStatus>;
  invalidate?(configPath?: string): void;
}

type CacheEntry = {
  expiresAt: number;
  value: DashboardStatus;
};

export class PowerShellStatusProvider implements StatusProvider {
  private readonly cache = new Map<string, CacheEntry>();
  private readonly inFlight = new Map<string, Promise<DashboardStatus>>();
  private readonly isolatedHome = path.join(os.tmpdir(), 'syncfactors-web-pwsh-home');
  private readonly outputDirectory = path.join(os.tmpdir(), 'syncfactors-web-status');

  constructor(
    private readonly idleTtlMs = 60_000,
    private readonly activeTtlMs = 10_000,
  ) {}

  async getStatus(configPath: string, historyLimit: number): Promise<DashboardStatus> {
    const cacheKey = `${configPath}:${historyLimit}`;
    const cached = this.cache.get(cacheKey);
    if (cached && cached.expiresAt > Date.now()) {
      return cached.value;
    }

    const pending = this.inFlight.get(cacheKey);
    if (pending) {
      return pending;
    }

    const request = this.loadStatus(configPath, historyLimit)
      .then((value) => {
        this.cache.set(cacheKey, {
          expiresAt: Date.now() + this.getTtlMs(value),
          value,
        });
        return value;
      })
      .finally(() => {
        this.inFlight.delete(cacheKey);
      });

    this.inFlight.set(cacheKey, request);
    return request;
  }

  private getTtlMs(status: DashboardStatus): number {
    return status.currentRun?.status === 'InProgress' ? this.activeTtlMs : this.idleTtlMs;
  }

  private async loadStatus(configPath: string, historyLimit: number): Promise<DashboardStatus> {
    fs.mkdirSync(this.isolatedHome, { recursive: true });
    fs.mkdirSync(this.outputDirectory, { recursive: true });
    const outputPath = path.join(this.outputDirectory, `status-${process.pid}.json`);
    const pwshEnv = {
      ...process.env,
      HOME: this.isolatedHome,
      XDG_CACHE_HOME: path.join(this.isolatedHome, '.cache'),
    };

    await execFileAsync(
      'pwsh',
      [
        '-NoLogo',
        '-NoProfile',
        '-File',
        'scripts/Get-SyncFactorsWebStatus.ps1',
        '-ConfigPath',
        configPath,
        '-HistoryLimit',
        String(historyLimit),
        '-AsJson',
        '-OutputPath',
        outputPath,
      ],
      {
        cwd: process.cwd(),
        env: pwshEnv,
        maxBuffer: 1024 * 1024 * 10,
      },
    );

    return JSON.parse(fs.readFileSync(outputPath, 'utf8')) as DashboardStatus;
  }

  invalidate(configPath?: string): void {
    if (!configPath) {
      this.cache.clear();
      this.inFlight.clear();
      return;
    }

    for (const key of this.cache.keys()) {
      if (key.startsWith(`${configPath}:`)) {
        this.cache.delete(key);
      }
    }

    for (const key of this.inFlight.keys()) {
      if (key.startsWith(`${configPath}:`)) {
        this.inFlight.delete(key);
      }
    }
  }
}

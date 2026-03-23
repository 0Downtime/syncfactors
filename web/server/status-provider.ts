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

export class PowerShellStatusProvider implements StatusProvider {
  private readonly cache = new Map<string, { expiresAt: number; value: DashboardStatus }>();
  private readonly isolatedHome = path.join(os.tmpdir(), 'syncfactors-web-pwsh-home');
  private readonly outputDirectory = path.join(os.tmpdir(), 'syncfactors-web-status');

  constructor(private readonly ttlMs = 1000) {}

  async getStatus(configPath: string, historyLimit: number): Promise<DashboardStatus> {
    const cacheKey = `${configPath}:${historyLimit}`;
    const cached = this.cache.get(cacheKey);
    if (cached && cached.expiresAt > Date.now()) {
      return cached.value;
    }

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

    const value = JSON.parse(fs.readFileSync(outputPath, 'utf8')) as DashboardStatus;
    this.cache.set(cacheKey, { expiresAt: Date.now() + this.ttlMs, value });
    return value;
  }

  invalidate(configPath?: string): void {
    if (!configPath) {
      this.cache.clear();
      return;
    }

    for (const key of this.cache.keys()) {
      if (key.startsWith(`${configPath}:`)) {
        this.cache.delete(key);
      }
    }
  }
}

import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import express from 'express';
import type { Express, Response } from 'express';
import type { DashboardStatus, QueueName, WorkerActionKind, WorkerActionResponse } from './types.js';
import { PowerShellStatusProvider, type StatusProvider } from './status-provider.js';
import { ReportService } from './report-service.js';

const execFileAsync = promisify(execFile);

export type AppDependencies = {
  configPath: string;
  historyLimit?: number;
  statusProvider?: StatusProvider;
  reportService?: ReportService;
  workerActionRunner?: typeof runWorkerAction;
};

export function createApp(dependencies: AppDependencies): Express {
  const app = express();
  app.use(express.json());
  const historyLimit = dependencies.historyLimit ?? 25;
  const statusProvider = dependencies.statusProvider ?? new PowerShellStatusProvider();
  const reportService = dependencies.reportService ?? new ReportService();
  const workerActionRunner = dependencies.workerActionRunner ?? runWorkerAction;

  app.get('/api/status', async (_request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      response.json({ status });
    } catch (error) {
      respondWithError(response, 500, 'Failed to load dashboard status.', error);
    }
  });

  app.get('/api/runs', async (request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const result = await reportService.listRuns(status, {
        mode: asQueryString(request.query.mode),
        artifact: asQueryString(request.query.artifact),
        status: asQueryString(request.query.status),
        page: asQueryNumber(request.query.page),
        pageSize: asQueryNumber(request.query.pageSize),
      });
      response.json(result);
    } catch (error) {
      respondWithError(response, 500, 'Failed to list runs.', error);
    }
  });

  app.get('/api/runs/:runId', async (request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const result = await reportService.getRun(status, request.params.runId);
      response.json(result);
    } catch (error) {
      respondWithError(response, 404, 'Failed to load the selected run.', error);
    }
  });

  app.get('/api/runs/:runId/entries', async (request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const result = await reportService.getRunEntries(status, request.params.runId, {
        bucket: asQueryString(request.query.bucket),
        workerId: asQueryString(request.query.workerId),
        reason: asQueryString(request.query.reason),
        filter: asQueryString(request.query.filter),
        entryId: asQueryString(request.query.entryId),
      });
      response.json(result);
    } catch (error) {
      respondWithError(response, 404, 'Failed to load run entries.', error);
    }
  });

  app.get('/api/queues/:queueName', async (request, response) => {
    try {
      const queueName = request.params.queueName as QueueName;
      if (!['manual-review', 'quarantined', 'conflicts', 'guardrails'].includes(queueName)) {
        respondWithError(response, 404, 'Unknown queue.', new Error(`Queue '${request.params.queueName}' is not supported.`));
        return;
      }

      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const result = await reportService.getQueue(status, queueName, {
        reason: asQueryString(request.query.reason),
        reviewCaseType: asQueryString(request.query.reviewCaseType),
        workerId: asQueryString(request.query.workerId),
        filter: asQueryString(request.query.filter),
        page: asQueryNumber(request.query.page),
        pageSize: asQueryNumber(request.query.pageSize),
      });
      response.json(result);
    } catch (error) {
      respondWithError(response, 500, 'Failed to load queue entries.', error);
    }
  });

  app.get('/api/workers/:workerId/history', async (request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const result = await reportService.getWorkerHistory(
        status,
        request.params.workerId,
        asQueryNumber(request.query.limit) ?? 100,
      );
      response.json(result);
    } catch (error) {
      respondWithError(response, 500, 'Failed to load worker history.', error);
    }
  });

  app.get('/api/workers/:workerId', async (request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const result = await reportService.getWorkerDetail(
        status,
        request.params.workerId,
        asQueryNumber(request.query.limit) ?? 100,
      );
      response.json(result);
    } catch (error) {
      respondWithError(response, 500, 'Failed to load worker detail.', error);
    }
  });

  app.post('/api/workers/:workerId/actions', async (request, response) => {
    try {
      const action = asWorkerAction(request.body?.action);
      if (!action) {
        respondWithError(response, 400, 'Unknown worker action.', new Error('Request body must include action: test-sync, review-sync, or real-sync.'));
        return;
      }

      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const mappingConfigPath = resolveMappingConfigPath(status);
      const result = await workerActionRunner({
        action,
        workerId: request.params.workerId,
        configPath: status.paths.configPath || dependencies.configPath,
        mappingConfigPath,
      });

      statusProvider.invalidate?.(dependencies.configPath);
      response.json({
        action,
        workerId: request.params.workerId,
        result,
      } satisfies WorkerActionResponse);
    } catch (error) {
      respondWithError(response, 500, 'Failed to execute worker action.', error);
    }
  });

  app.get('/api/health', async (_request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      response.json({
        ok: true,
        configPath: dependencies.configPath,
        currentRunStatus: (status.currentRun.status as string | undefined) ?? null,
      });
    } catch (error) {
      respondWithError(response, 500, 'Dashboard health check failed.', error);
    }
  });

  app.post('/api/open-path', async (request, response) => {
    try {
      const targetPath = typeof request.body?.path === 'string' ? request.body.path : '';
      if (!targetPath.trim()) {
        respondWithError(response, 400, 'Missing path.', new Error('Request body must include a path.'));
        return;
      }

      const resolvedPath = path.resolve(targetPath);
      await fs.access(resolvedPath);
      const stats = await fs.stat(resolvedPath);
      await openPathInDefaultApp(resolvedPath, stats.isDirectory());
      response.json({ ok: true });
    } catch (error) {
      respondWithError(response, 500, 'Failed to open path.', error);
    }
  });

  return app;
}

function respondWithError(response: Response, statusCode: number, message: string, error: unknown): void {
  response.status(statusCode).json({
    error: message,
    detail: error instanceof Error ? error.message : 'Unknown error.',
  });
}

function asQueryString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value : undefined;
}

function asQueryNumber(value: unknown): number | undefined {
  if (typeof value !== 'string' || !value.trim()) {
    return undefined;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function asWorkerAction(value: unknown): WorkerActionKind | null {
  return value === 'test-sync' || value === 'review-sync' || value === 'real-sync' ? value : null;
}

function resolveMappingConfigPath(status: DashboardStatus): string {
  const candidates = [status.latestRun, ...status.recentRuns];
  for (const run of candidates) {
    if (run?.mappingConfigPath) {
      return run.mappingConfigPath;
    }
  }

  throw new Error('No mapping config path is available from recent runs. Run at least one sync or review with mapping metadata first.');
}

async function runWorkerAction(args: {
  action: WorkerActionKind;
  workerId: string;
  configPath: string;
  mappingConfigPath: string;
}): Promise<WorkerActionResponse['result']> {
  const pwshArgs = ['-NoLogo', '-NoProfile', '-File'];
  if (args.action === 'real-sync') {
    pwshArgs.push(
      'scripts/Invoke-SyncFactorsWorkerSync.ps1',
      '-ConfigPath',
      args.configPath,
      '-MappingConfigPath',
      args.mappingConfigPath,
      '-WorkerId',
      args.workerId,
      '-AsJson',
    );
  } else {
    pwshArgs.push(
      'scripts/Invoke-SyncFactorsWorkerPreview.ps1',
      '-ConfigPath',
      args.configPath,
      '-MappingConfigPath',
      args.mappingConfigPath,
      '-WorkerId',
      args.workerId,
      '-PreviewMode',
      args.action === 'test-sync' ? 'Minimal' : 'Full',
      '-AsJson',
    );
  }

  const { stdout } = await execFileAsync('pwsh', pwshArgs, {
    cwd: process.cwd(),
    env: process.env,
    maxBuffer: 1024 * 1024 * 10,
  });

  const parsed = JSON.parse(stdout) as Record<string, unknown>;
  const workerScope = parsed.workerScope && typeof parsed.workerScope === 'object'
    ? parsed.workerScope as WorkerActionResponse['result']['workerScope']
    : null;

  return {
    reportPath: asNullableString(parsed.reportPath),
    runId: asNullableString(parsed.runId),
    mode: asNullableString(parsed.mode),
    status: asNullableString(parsed.status),
    artifactType: asNullableString(parsed.artifactType),
    previewMode: asNullableString(parsed.previewMode),
    successFactorsAuth: asNullableString(parsed.successFactorsAuth),
    workerScope,
  };
}

function asNullableString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null;
}

async function openPathInDefaultApp(targetPath: string, isDirectory: boolean): Promise<void> {
  const platform = os.platform();
  let command: string;
  let args: string[];

  if (platform === 'darwin') {
    command = 'open';
    args = isDirectory ? [targetPath] : ['-t', targetPath];
  } else if (platform === 'win32') {
    command = 'cmd';
    args = ['/c', 'start', '', targetPath];
  } else {
    command = 'xdg-open';
    args = [targetPath];
  }

  await new Promise<void>((resolve, reject) => {
    const child = spawn(command, args, {
      detached: true,
      stdio: 'ignore',
    });

    child.once('error', reject);
    child.once('spawn', () => {
      child.unref();
      resolve();
    });
  });
}

export function createMockStatusProvider(status: DashboardStatus): StatusProvider {
  return {
    async getStatus() {
      return status;
    },
    invalidate() {
      return;
    },
  };
}

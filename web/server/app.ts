import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import express from 'express';
import type { Express, Response } from 'express';
import type { DashboardStatus, QueueName } from './types.js';
import { PowerShellStatusProvider, type StatusProvider } from './status-provider.js';
import { ReportService } from './report-service.js';

export type AppDependencies = {
  configPath: string;
  historyLimit?: number;
  statusProvider?: StatusProvider;
  reportService?: ReportService;
};

export function createApp(dependencies: AppDependencies): Express {
  const app = express();
  app.use(express.json());
  const historyLimit = dependencies.historyLimit ?? 25;
  const statusProvider = dependencies.statusProvider ?? new PowerShellStatusProvider();
  const reportService = dependencies.reportService ?? new ReportService();

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
  };
}

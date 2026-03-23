import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';
import express from 'express';
import type { Express, Response } from 'express';
import type {
  ConfirmationDescriptor,
  DashboardStatus,
  DiffRow,
  OperatorActionKind,
  OperatorCommandResult,
  OperationSummary,
  QueueName,
  WorkerActionKind,
  WorkerActionResponse,
  WorkerPreviewMode,
  WorkerPreviewResponse,
} from './types.js';
import { PowerShellStatusProvider, type StatusProvider } from './status-provider.js';
import { ReportService } from './report-service.js';

const execFileAsync = promisify(execFile);

export type AppDependencies = {
  configPath: string;
  historyLimit?: number;
  statusProvider?: StatusProvider;
  reportService?: ReportService;
  workerActionRunner?: typeof runWorkerAction;
  workerPreviewRunner?: typeof runWorkerPreview;
  operatorActionRunner?: typeof runOperatorAction;
  freshResetRunner?: typeof runFreshReset;
};

export function createApp(dependencies: AppDependencies): Express {
  const app = express();
  app.use(express.json());
  const historyLimit = dependencies.historyLimit ?? 25;
  const statusProvider = dependencies.statusProvider ?? new PowerShellStatusProvider();
  const reportService = dependencies.reportService ?? new ReportService();
  const workerActionRunner = dependencies.workerActionRunner ?? runWorkerAction;
  const workerPreviewRunner = dependencies.workerPreviewRunner ?? runWorkerPreview;
  const operatorActionRunner = dependencies.operatorActionRunner ?? runOperatorAction;
  const freshResetRunner = dependencies.freshResetRunner ?? runFreshReset;

  app.get('/api/status', async (_request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      response.json({ status });
    } catch (error) {
      respondWithError(response, 500, 'Failed to load dashboard status.', error);
    }
  });

  app.get('/api/status/stream', async (request, response) => {
    setupSseResponse(response);
    let closed = false;
    let lastPayload = '';

    const sendStatus = async () => {
      if (closed) {
        return;
      }

      try {
        const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
        const payload = JSON.stringify({ status });
        if (payload !== lastPayload) {
          response.write(`event: status\n`);
          response.write(`data: ${payload}\n\n`);
          lastPayload = payload;
        }
      } catch (error) {
        const detail = error instanceof Error ? error.message : 'Unknown status stream error.';
        response.write(`event: error\n`);
        response.write(`data: ${JSON.stringify({ error: 'Failed to load dashboard status.', detail })}\n\n`);
      }
    };

    void sendStatus();
    const interval = setInterval(() => {
      void sendStatus();
    }, 2000);

    const close = () => {
      closed = true;
      clearInterval(interval);
      response.end();
    };

    request.on('close', close);
    request.on('aborted', close);
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

  app.post('/api/actions/preflight', async (_request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const mappingConfigPath = resolveMappingConfigPath(status);
      const result = await operatorActionRunner({
        action: 'preflight',
        configPath: status.paths.configPath || dependencies.configPath,
        mappingConfigPath,
      });
      response.json(result);
    } catch (error) {
      respondWithError(response, 500, 'Failed to execute preflight.', error);
    }
  });

  app.post('/api/actions/runs', async (request, response) => {
    try {
      const action = asOperatorAction(request.body?.action);
      if (!action) {
        respondWithError(response, 400, 'Unknown run action.', new Error('Request body must include a valid action.'));
        return;
      }

      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const mappingConfigPath = resolveMappingConfigPath(status);
      const result = await operatorActionRunner({
        action,
        configPath: status.paths.configPath || dependencies.configPath,
        mappingConfigPath,
      });
      statusProvider.invalidate?.(dependencies.configPath);
      response.json(result);
    } catch (error) {
      respondWithError(response, 500, 'Failed to execute run action.', error);
    }
  });

  app.post('/api/actions/fresh-reset', async (request, response) => {
    try {
      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const configPath = status.paths.configPath || dependencies.configPath;
      const previewReportPath = path.join(os.tmpdir(), `syncfactors-ResetPreview-${Date.now()}.json`);
      const logPath = path.join(os.tmpdir(), `syncfactors-fresh-reset-${Date.now()}.log`);
      const objectCount = await reportService.getFreshResetDeletionCount(configPath);
      const confirmation = buildFreshResetConfirmation(objectCount);
      const suppliedConfirmations = [
        asQueryString(request.body?.confirmText),
        ...asStringArray(request.body?.additionalConfirmations),
      ];

      if (!matchesConfirmation(confirmation, suppliedConfirmations)) {
        response.status(400).json({
          error: 'Fresh reset confirmation failed.',
          detail: 'Typed confirmations did not match the required values.',
          confirmation,
        });
        return;
      }

      const result = await freshResetRunner({
        configPath,
        logPath,
        previewReportPath,
        confirmations: confirmation.requiredText.split('\n'),
      });
      statusProvider.invalidate?.(dependencies.configPath);
      response.json(result);
    } catch (error) {
      respondWithError(response, 500, 'Failed to execute fresh reset.', error);
    }
  });

  app.post('/api/workers/:workerId/preview', async (request, response) => {
    try {
      const previewMode = asWorkerPreviewMode(request.body?.previewMode);
      if (!previewMode) {
        respondWithError(response, 400, 'Unknown worker preview mode.', new Error('Request body must include previewMode: minimal or full.'));
        return;
      }

      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const mappingConfigPath = resolveMappingConfigPath(status);
      const result = await workerPreviewRunner({
        workerId: request.params.workerId,
        previewMode,
        configPath: status.paths.configPath || dependencies.configPath,
        mappingConfigPath,
      });
      statusProvider.invalidate?.(dependencies.configPath);
      response.json(result);
    } catch (error) {
      statusProvider.invalidate?.(dependencies.configPath);
      respondWithError(response, 500, 'Failed to preview worker.', error);
    }
  });

  app.post('/api/workers/:workerId/apply', async (request, response) => {
    try {
      const confirmText = asQueryString(request.body?.confirmText);
      const confirmation = buildSimpleConfirmation(
        'Apply worker sync',
        `Apply worker sync for ${request.params.workerId}. This may write AD objects, sync state, runtime status, and report files.`,
        'YES',
        'high',
      );
      if (confirmText !== confirmation.requiredText) {
        response.status(400).json({
          error: 'Worker apply confirmation failed.',
          detail: `Type ${confirmation.requiredText} to continue.`,
          confirmation,
        });
        return;
      }

      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const mappingConfigPath = resolveMappingConfigPath(status);
      const result = await workerActionRunner({
        action: 'real-sync',
        workerId: request.params.workerId,
        configPath: status.paths.configPath || dependencies.configPath,
        mappingConfigPath,
      });

      statusProvider.invalidate?.(dependencies.configPath);
      response.json({
        action: 'real-sync',
        workerId: request.params.workerId,
        result,
      } satisfies WorkerActionResponse);
    } catch (error) {
      respondWithError(response, 500, 'Failed to apply worker sync.', error);
    }
  });

  app.post('/api/runs/:runId/export', async (request, response) => {
    try {
      const scope = asQueryString(request.body?.scope);
      const bucket = asQueryString(request.body?.bucket);
      if (scope !== 'selected-bucket' || !bucket) {
        respondWithError(response, 400, 'Invalid export request.', new Error('Request body must include scope=selected-bucket and a bucket.'));
        return;
      }

      const confirmText = asQueryString(request.body?.confirmText);
      const confirmation = buildSimpleConfirmation(
        'Export bucket selection',
        'Export the selected bucket and active filter to a JSON file in the temp directory.',
        'YES',
        'medium',
      );
      if (confirmText !== confirmation.requiredText) {
        response.status(400).json({
          error: 'Export confirmation failed.',
          detail: `Type ${confirmation.requiredText} to continue.`,
          confirmation,
        });
        return;
      }

      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const runEntries = await reportService.getRunEntries(status, request.params.runId, {
        bucket,
        filter: asQueryString(request.body?.filter),
      });
      const exportPath = path.join(os.tmpdir(), `syncfactors-monitor-${bucket}-${Date.now()}.json`);
      await fs.writeFile(exportPath, JSON.stringify(runEntries.entries.map((entry) => entry.item), null, 2));
      response.json({
        status: 'completed',
        started: false,
        completed: true,
        message: `Exported ${runEntries.entries.length} entries.`,
        commandSummary: [`Bucket=${bucket}`, `Path=${exportPath}`],
        runId: request.params.runId,
        reportPath: exportPath,
        outputLines: [exportPath],
      } satisfies OperatorCommandResult);
    } catch (error) {
      respondWithError(response, 500, 'Failed to export selected bucket.', error);
    }
  });

  app.post('/api/runs/:runId/open', async (request, response) => {
    try {
      const confirmText = asQueryString(request.body?.confirmText);
      const confirmation = buildSimpleConfirmation(
        'Open report path',
        'Open the selected report path in the default app.',
        'YES',
        'low',
      );
      if (confirmText !== confirmation.requiredText) {
        response.status(400).json({
          error: 'Open report confirmation failed.',
          detail: `Type ${confirmation.requiredText} to continue.`,
          confirmation,
        });
        return;
      }

      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const run = await reportService.getRun(status, request.params.runId);
      const targetPath = run.run.path;
      if (!targetPath) {
        throw new Error('The selected run does not have a report path.');
      }
      const resolvedPath = path.resolve(targetPath);
      const stats = await fs.stat(resolvedPath);
      await openPathInDefaultApp(resolvedPath, stats.isDirectory());
      response.json({
        status: 'completed',
        started: false,
        completed: true,
        message: 'Opened selected report path.',
        commandSummary: [resolvedPath],
        runId: request.params.runId,
        reportPath: resolvedPath,
        outputLines: [resolvedPath],
      } satisfies OperatorCommandResult);
    } catch (error) {
      respondWithError(response, 500, 'Failed to open report path.', error);
    }
  });

  app.post('/api/runs/:runId/copy-path', async (request, response) => {
    try {
      const confirmText = asQueryString(request.body?.confirmText);
      const confirmation = buildSimpleConfirmation(
        'Copy report path',
        'Copy the selected report path to the clipboard.',
        'YES',
        'medium',
      );
      if (confirmText !== confirmation.requiredText) {
        response.status(400).json({
          error: 'Copy report path confirmation failed.',
          detail: `Type ${confirmation.requiredText} to continue.`,
          confirmation,
        });
        return;
      }

      const status = await statusProvider.getStatus(dependencies.configPath, historyLimit);
      const run = await reportService.getRun(status, request.params.runId);
      const targetPath = run.run.path;
      if (!targetPath) {
        throw new Error('The selected run does not have a report path.');
      }
      response.json({
        status: 'completed',
        started: false,
        completed: true,
        message: 'Resolved selected report path for clipboard copy.',
        commandSummary: [targetPath],
        runId: request.params.runId,
        reportPath: targetPath,
        outputLines: [targetPath],
      } satisfies OperatorCommandResult);
    } catch (error) {
      respondWithError(response, 500, 'Failed to resolve report path.', error);
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

function setupSseResponse(response: Response): void {
  response.status(200);
  response.setHeader('Content-Type', 'text/event-stream');
  response.setHeader('Cache-Control', 'no-cache, no-transform');
  response.setHeader('Connection', 'keep-alive');
  response.flushHeaders?.();
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

function asOperatorAction(value: unknown): OperatorActionKind | null {
  return value === 'delta-dry-run'
    || value === 'delta-sync'
    || value === 'full-dry-run'
    || value === 'full-sync'
    || value === 'review-run'
    ? value
    : null;
}

function asWorkerPreviewMode(value: unknown): WorkerPreviewMode | null {
  return value === 'minimal' || value === 'full' ? value : null;
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

function asStringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : [];
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

  const parsed = await runPowerShellJson(pwshArgs);
  const workerScope = parsed.workerScope && typeof parsed.workerScope === 'object'
    ? parsed.workerScope as WorkerActionResponse['result']['workerScope']
    : null;

  return {
    reportPath: asNullableString(parsed.reportPath),
    runId: asNullableString(parsed.runId),
    mode: asNullableString(parsed.mode),
    status: asNullableString(parsed.status),
    errorMessage: asNullableString(parsed.errorMessage),
    artifactType: asNullableString(parsed.artifactType),
    previewMode: asNullableString(parsed.previewMode),
    successFactorsAuth: asNullableString(parsed.successFactorsAuth),
    workerScope,
  };
}

async function runWorkerPreview(args: {
  workerId: string;
  previewMode: WorkerPreviewMode;
  configPath: string;
  mappingConfigPath: string;
}): Promise<WorkerPreviewResponse> {
  const parsed = await runPowerShellJson([
    '-NoLogo',
    '-NoProfile',
    '-File',
    'scripts/Invoke-SyncFactorsWorkerPreview.ps1',
    '-ConfigPath',
    args.configPath,
    '-MappingConfigPath',
    args.mappingConfigPath,
    '-WorkerId',
    args.workerId,
    '-PreviewMode',
    args.previewMode === 'minimal' ? 'Minimal' : 'Full',
    '-AsJson',
  ]);

  return normalizeWorkerPreview(args.previewMode, parsed);
}

async function runOperatorAction(args: {
  action: OperatorActionKind | 'preflight';
  configPath: string;
  mappingConfigPath: string;
}): Promise<OperatorCommandResult> {
  if (args.action === 'preflight') {
    const lines = await runPowerShellText([
      '-NoLogo',
      '-NoProfile',
      '-File',
      'scripts/Invoke-SyncFactorsPreflight.ps1',
      '-ConfigPath',
      args.configPath,
      '-MappingConfigPath',
      args.mappingConfigPath,
    ]);

    return {
      status: 'completed',
      started: false,
      completed: true,
      message: 'Preflight completed.',
      commandSummary: [`Config=${args.configPath}`, `Mapping=${args.mappingConfigPath}`],
      runId: null,
      reportPath: null,
      outputLines: lines,
    };
  }

  const { scriptPath, scriptArgs, message } = buildRunActionCommand(args);
  await startDetachedPowerShell(['-NoLogo', '-NoProfile', '-File', scriptPath, ...scriptArgs]);
  return {
    status: 'accepted',
    started: true,
    completed: false,
    message,
    commandSummary: [`Config=${args.configPath}`, `Mapping=${args.mappingConfigPath}`],
    runId: null,
    reportPath: null,
    outputLines: [],
  };
}

async function runFreshReset(args: {
  configPath: string;
  logPath: string;
  previewReportPath: string;
  confirmations: string[];
}): Promise<OperatorCommandResult> {
  const lines = await runPowerShellText(
    [
      '-NoLogo',
      '-NoProfile',
      '-File',
      'scripts/Invoke-SyncFactorsFreshSyncReset.ps1',
      '-ConfigPath',
      args.configPath,
      '-LogPath',
      args.logPath,
      '-PreviewReportPath',
      args.previewReportPath,
    ],
    `${args.confirmations.join('\n')}\n`,
  );

  return {
    status: 'completed',
    started: false,
    completed: true,
    message: 'Fresh sync reset completed.',
    commandSummary: [`Config=${args.configPath}`, `PreviewReport=${args.previewReportPath}`, `Log=${args.logPath}`],
    runId: null,
    reportPath: args.previewReportPath,
    outputLines: lines,
  };
}

function asNullableString(value: unknown): string | null {
  return typeof value === 'string' && value.trim() ? value : null;
}

export function extractFirstJsonPayload(output: string): string {
  const text = output.trim();
  for (let start = 0; start < text.length; start += 1) {
    const openingChar = text[start];
    if (openingChar !== '{' && openingChar !== '[') {
      continue;
    }

    let depth = 0;
    let inString = false;
    let escaping = false;

    for (let index = start; index < text.length; index += 1) {
      const char = text[index];

      if (inString) {
        if (escaping) {
          escaping = false;
          continue;
        }

        if (char === '\\') {
          escaping = true;
          continue;
        }

        if (char === '"') {
          inString = false;
        }

        continue;
      }

      if (char === '"') {
        inString = true;
        continue;
      }

      if (char === '{' || char === '[') {
        depth += 1;
        continue;
      }

      if (char === '}' || char === ']') {
        depth -= 1;
        if (depth === 0) {
          const candidate = text.slice(start, index + 1);
          try {
            JSON.parse(candidate);
            return candidate;
          } catch {
            break;
          }
        }
      }
    }
  }

  throw new Error('PowerShell output did not contain a valid JSON object or array.');
}

async function runPowerShellJson(args: string[]): Promise<Record<string, unknown>> {
  const { stdout } = await execFileAsync('pwsh', args, {
    cwd: process.cwd(),
    env: process.env,
    maxBuffer: 1024 * 1024 * 10,
  });
  const payload = extractFirstJsonPayload(stdout);
  return JSON.parse(payload) as Record<string, unknown>;
}

async function runPowerShellText(args: string[], stdin = ''): Promise<string[]> {
  return new Promise<string[]>((resolve, reject) => {
    const child = spawn('pwsh', args, {
      cwd: process.cwd(),
      env: process.env,
      stdio: 'pipe',
    });
    let stdout = '';
    let stderr = '';

    child.stdout.on('data', (chunk) => {
      stdout += String(chunk);
    });
    child.stderr.on('data', (chunk) => {
      stderr += String(chunk);
    });
    child.once('error', reject);
    child.once('close', (code) => {
      if (code !== 0) {
        reject(new Error(stderr.trim() || stdout.trim() || `PowerShell exited with code ${code}`));
        return;
      }
      resolve(`${stdout}${stderr}`.split(/\r?\n/).filter(Boolean));
    });

    if (stdin) {
      child.stdin.write(stdin);
    }
    child.stdin.end();
  });
}

async function startDetachedPowerShell(args: string[]): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const child = spawn('pwsh', args, {
      cwd: process.cwd(),
      env: process.env,
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

function buildRunActionCommand(args: {
  action: OperatorActionKind;
  configPath: string;
  mappingConfigPath: string;
}): { scriptPath: string; scriptArgs: string[]; message: string } {
  switch (args.action) {
    case 'delta-dry-run':
      return {
        scriptPath: 'src/Invoke-SyncFactors.ps1',
        scriptArgs: ['-ConfigPath', args.configPath, '-MappingConfigPath', args.mappingConfigPath, '-Mode', 'Delta', '-DryRun'],
        message: 'Started delta dry-run in a new PowerShell process.',
      };
    case 'delta-sync':
      return {
        scriptPath: 'src/Invoke-SyncFactors.ps1',
        scriptArgs: ['-ConfigPath', args.configPath, '-MappingConfigPath', args.mappingConfigPath, '-Mode', 'Delta'],
        message: 'Started delta sync in a new PowerShell process.',
      };
    case 'full-dry-run':
      return {
        scriptPath: 'src/Invoke-SyncFactors.ps1',
        scriptArgs: ['-ConfigPath', args.configPath, '-MappingConfigPath', args.mappingConfigPath, '-Mode', 'Full', '-DryRun'],
        message: 'Started full dry-run in a new PowerShell process.',
      };
    case 'full-sync':
      return {
        scriptPath: 'src/Invoke-SyncFactors.ps1',
        scriptArgs: ['-ConfigPath', args.configPath, '-MappingConfigPath', args.mappingConfigPath, '-Mode', 'Full'],
        message: 'Started full sync in a new PowerShell process.',
      };
    case 'review-run':
      return {
        scriptPath: 'scripts/Invoke-SyncFactorsFirstSyncReview.ps1',
        scriptArgs: ['-ConfigPath', args.configPath, '-MappingConfigPath', args.mappingConfigPath],
        message: 'Started first-sync review in a new PowerShell process.',
      };
  }
}

function normalizeWorkerPreview(previewMode: WorkerPreviewMode, parsed: Record<string, unknown>): WorkerPreviewResponse {
  const operations = asRecordArray(parsed.operations);
  const changedAttributes = asRecordArray(parsed.changedAttributes);
  const diffRows = changedAttributes.length > 0
    ? changedAttributes
      .map((row) => ({
        attribute: asNullableString(row.targetAttribute) ?? 'attribute',
        source: asNullableString(row.sourceField),
        before: inlineValue(row.currentAdValue),
        after: inlineValue(row.proposedValue),
        changed: inlineValue(row.currentAdValue) !== inlineValue(row.proposedValue),
      }))
      .filter((row) => row.changed)
    : getDiffRowsFromOperations(operations);

  return {
    reportPath: asNullableString(parsed.reportPath),
    runId: asNullableString(parsed.runId),
    mode: asNullableString(parsed.mode),
    status: asNullableString(parsed.status),
    artifactType: asNullableString(parsed.artifactType),
    successFactorsAuth: asNullableString(parsed.successFactorsAuth),
    previewMode,
    workerScope: parsed.workerScope && typeof parsed.workerScope === 'object'
      ? parsed.workerScope as WorkerPreviewResponse['workerScope']
      : null,
    reviewSummary: parsed.reviewSummary && typeof parsed.reviewSummary === 'object'
      ? parsed.reviewSummary as Record<string, unknown>
      : null,
    preview: {
      workerId: asNullableString(asRecord(parsed.preview).workerId) ?? '',
      buckets: asStringArray(asRecord(parsed.preview).buckets),
      matchedExistingUser: asNullableBoolean(asRecord(parsed.preview).matchedExistingUser),
      reviewCategory: asNullableString(asRecord(parsed.preview).reviewCategory),
      reviewCaseType: asNullableString(asRecord(parsed.preview).reviewCaseType),
      reason: asNullableString(asRecord(parsed.preview).reason),
      operatorActionSummary: asNullableString(asRecord(parsed.preview).operatorActionSummary),
      operatorActions: Array.isArray(asRecord(parsed.preview).operatorActions)
        ? asRecord(parsed.preview).operatorActions as WorkerPreviewResponse['preview']['operatorActions']
        : [],
      samAccountName: asNullableString(asRecord(parsed.preview).samAccountName),
      targetOu: asNullableString(asRecord(parsed.preview).targetOu),
      currentDistinguishedName: asNullableString(asRecord(parsed.preview).currentDistinguishedName),
      currentEnabled: asNullableBoolean(asRecord(parsed.preview).currentEnabled),
      proposedEnable: asNullableBoolean(asRecord(parsed.preview).proposedEnable),
    },
    diffRows,
    operationSummary: summarizePreviewOperation(operations),
    entries: Array.isArray(parsed.entries)
      ? parsed.entries.map((entry) => ({
        bucket: asNullableString(asRecord(entry).bucket) ?? 'unknown',
        item: asRecord(asRecord(entry).item),
      }))
      : [],
    rawWorker: parsed.rawWorker && typeof parsed.rawWorker === 'object' ? asRecord(parsed.rawWorker) : null,
    rawPropertyNames: asStringArray(parsed.rawPropertyNames),
  };
}

function getDiffRowsFromOperations(operations: Record<string, unknown>[]): DiffRow[] {
  const results: DiffRow[] = [];
  for (const operation of operations) {
    const before = asRecord(operation.before);
    const after = asRecord(operation.after);
    const keys = [...new Set([...Object.keys(before), ...Object.keys(after)])];
    for (const key of keys) {
      results.push({
        attribute: key,
        source: null,
        before: inlineValue(before[key]),
        after: inlineValue(after[key]),
        changed: inlineValue(before[key]) !== inlineValue(after[key]),
      });
    }
  }

  return results.filter((row) => row.changed);
}

function summarizePreviewOperation(operations: Record<string, unknown>[]): OperationSummary | null {
  const operation = operations[0];
  if (!operation) {
    return null;
  }
  const operationType = asNullableString(operation.operationType) ?? 'Preview';
  const before = asRecord(operation.before);
  const after = asRecord(operation.after);
  return {
    action: operationType,
    effect: null,
    targetOu: asNullableString(after.targetOu) ?? null,
    fromOu: asNullableString(before.parentOu) ?? null,
    toOu: asNullableString(after.targetOu) ?? null,
  };
}

function buildSimpleConfirmation(
  title: string,
  message: string,
  requiredText: string,
  riskLevel: ConfirmationDescriptor['riskLevel'],
): ConfirmationDescriptor {
  return { title, message, requiredText, riskLevel };
}

function buildFreshResetConfirmation(objectCount: number): ConfirmationDescriptor {
  return {
    title: 'Fresh sync reset',
    message: `Delete ${objectCount} managed AD user objects and reset local sync state.`,
    requiredText: ['DELETE', String(objectCount), 'DELETE ALL SYNCED OU USERS'].join('\n'),
    riskLevel: 'critical',
  };
}

function matchesConfirmation(confirmation: ConfirmationDescriptor, suppliedValues: string[]): boolean {
  const required = confirmation.requiredText.split('\n');
  if (suppliedValues.length < required.length) {
    return false;
  }
  return required.every((value, index) => suppliedValues[index] === value);
}

function asNullableBoolean(value: unknown): boolean | null {
  return typeof value === 'boolean' ? value : null;
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

function asRecordArray(value: unknown): Record<string, unknown>[] {
  return Array.isArray(value) ? value.map((item) => asRecord(item)) : [];
}

function inlineValue(value: unknown): string {
  if (value === null || value === undefined || value === '') {
    return '(unset)';
  }
  if (Array.isArray(value)) {
    return value.map((item) => inlineValue(item)).join(', ');
  }
  if (typeof value === 'object') {
    return JSON.stringify(value);
  }
  return String(value);
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

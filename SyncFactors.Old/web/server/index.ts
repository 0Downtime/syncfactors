import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import express from 'express';
import { createServer as createViteServer } from 'vite';
import { createApp } from './app.js';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const repoRoot = path.resolve(__dirname, '..', '..');
const clientRoot = path.resolve(repoRoot, 'web/client');
const distRoot = path.resolve(repoRoot, 'web/dist');

async function main(): Promise<void> {
  const args = parseArgs(process.argv.slice(2));
  const configPath = args.config ?? process.env.SYNCFACTORS_CONFIG_PATH;
  if (!configPath) {
    throw new Error('Missing --config <path>. Example: npm run web:dev -- --config ./config/local.real-successfactors.real-ad.sync-config.json');
  }

  const port = Number.parseInt(args.port ?? process.env.PORT ?? '4280', 10);
  const app = createApp({
    configPath: path.resolve(process.cwd(), configPath),
    historyLimit: Number.parseInt(args.historyLimit ?? '25', 10),
  });

  if (process.env.NODE_ENV === 'production') {
    app.use(express.static(distRoot));
    app.get('/{*path}', async (_request, response) => {
      response.sendFile(path.join(distRoot, 'index.html'));
    });
  } else {
    const vite = await createViteServer({
      root: clientRoot,
      server: { middlewareMode: true },
      appType: 'spa',
    });

    app.use(vite.middlewares);
    app.get('/{*path}', async (request, response, next) => {
      try {
        const templatePath = path.join(clientRoot, 'index.html');
        const template = await fs.readFile(templatePath, 'utf8');
        const html = await vite.transformIndexHtml(request.originalUrl, template);
        response.status(200).set({ 'Content-Type': 'text/html' }).end(html);
      } catch (error) {
        next(error);
      }
    });
  }

  app.listen(port, '127.0.0.1', () => {
    process.stdout.write(`SyncFactors web dashboard listening on http://127.0.0.1:${port}\n`);
  });
}

function parseArgs(argv: string[]): Record<string, string> {
  const result: Record<string, string> = {};
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith('--')) {
      continue;
    }

    const key = token.slice(2);
    const value = argv[index + 1];
    if (!value || value.startsWith('--')) {
      result[key] = 'true';
      continue;
    }

    result[key] = value;
    index += 1;
  }

  return result;
}

main().catch((error) => {
  process.stderr.write(`${error instanceof Error ? error.message : 'Unknown startup failure.'}\n`);
  process.exit(1);
});

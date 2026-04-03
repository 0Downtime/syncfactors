import fs from 'node:fs/promises';
import path from 'node:path';
import express from 'express';
import type { Express, Request, Response } from 'express';

export type AppDependencies = {
  apiBaseUrl: string;
  distRoot?: string;
};

export function createApp(dependencies: AppDependencies): Express {
  const app = express();

  app.use('/api', async (request, response) => {
    try {
      await proxyRequest(request, response, dependencies.apiBaseUrl);
    } catch {
      response.status(502).json({
        error: `Unable to reach the SyncFactors API at ${dependencies.apiBaseUrl}.`,
      });
    }
  });

  if (dependencies.distRoot) {
    app.use(express.static(dependencies.distRoot));
    app.get('/{*path}', async (_request, response, next) => {
      try {
        response.sendFile(path.join(dependencies.distRoot!, 'index.html'));
      } catch (error) {
        next(error);
      }
    });
  }

  return app;
}

async function proxyRequest(request: Request, response: Response, apiBaseUrl: string) {
  const upstreamUrl = new URL(request.originalUrl, ensureTrailingSlash(apiBaseUrl));
  const headers = new Headers();

  for (const [key, value] of Object.entries(request.headers)) {
    if (!value || key.toLowerCase() === 'host' || key.toLowerCase() === 'content-length') {
      continue;
    }

    if (Array.isArray(value)) {
      for (const item of value) {
        headers.append(key, item);
      }
      continue;
    }

    headers.set(key, value);
  }

  const body = request.method === 'GET' || request.method === 'HEAD'
    ? undefined
    : Buffer.from(await readRequestBody(request));

  const upstream = await fetch(upstreamUrl, {
    method: request.method,
    headers,
    body,
    redirect: 'manual',
  });

  response.status(upstream.status);
  upstream.headers.forEach((value, key) => {
    if (key.toLowerCase() === 'set-cookie') {
      return;
    }

    response.setHeader(key, value);
  });

  for (const cookie of getSetCookieHeaders(upstream.headers)) {
    response.append('set-cookie', cookie);
  }

  const payload = Buffer.from(await upstream.arrayBuffer());
  response.send(payload);
}

function ensureTrailingSlash(value: string) {
  return value.endsWith('/') ? value : `${value}/`;
}

function readRequestBody(request: Request): Promise<Uint8Array> {
  return new Promise((resolve, reject) => {
    const chunks: Uint8Array[] = [];
    request.on('data', (chunk) => chunks.push(typeof chunk === 'string' ? Buffer.from(chunk) : chunk));
    request.on('end', () => resolve(Buffer.concat(chunks)));
    request.on('error', reject);
  });
}

function getSetCookieHeaders(headers: Headers) {
  const withGetSetCookie = headers as Headers & { getSetCookie?: () => string[] };
  return typeof withGetSetCookie.getSetCookie === 'function' ? withGetSetCookie.getSetCookie() : [];
}

export async function serveSpaIndex(response: Response, clientRoot: string) {
  const template = await fs.readFile(path.join(clientRoot, 'index.html'), 'utf8');
  response.status(200).set({ 'Content-Type': 'text/html' }).end(template);
}

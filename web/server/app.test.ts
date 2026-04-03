// @vitest-environment node
import express from 'express';
import request from 'supertest';
import { createApp } from './app.js';

describe('createApp', () => {
  it('proxies api requests to the configured backend', async () => {
    const upstream = express();
    upstream.use(express.json());
    upstream.post('/api/session/login', (req, res) => {
      res.cookie('SyncFactors.Auth', 'cookie-value');
      res.json({ username: req.body.username });
    });

    const server = await new Promise<ReturnType<typeof upstream.listen>>((resolve) => {
      const nextServer = upstream.listen(0, '127.0.0.1', () => resolve(nextServer));
    });
    const address = server.address();
    if (!address || typeof address === 'string') {
      throw new Error('Upstream server did not bind a port.');
    }

    const app = createApp({ apiBaseUrl: `http://127.0.0.1:${address.port}` });
    const response = await request(app)
      .post('/api/session/login')
      .send({ username: 'operator' })
      .expect(200);

    expect(response.body).toEqual({ username: 'operator' });
    expect(response.headers['set-cookie']).toBeDefined();
    server.close();
  });

  it('returns 502 when the configured backend is unreachable', async () => {
    const app = createApp({ apiBaseUrl: 'http://127.0.0.1:1' });

    const response = await request(app)
      .get('/api/session')
      .expect(502);

    expect(response.body).toEqual({
      error: 'Unable to reach the SyncFactors API at http://127.0.0.1:1.',
    });
  });
});

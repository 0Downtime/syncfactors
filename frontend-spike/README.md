# SyncFactors Frontend Spike

This is an isolated `shadcn/ui` experiment that lives alongside the main .NET codebase but does not replace the current Razor Pages UI.

## Why it is safe

- It lives in a separate git worktree on branch `0Downtime/shadcn-spike`.
- The app is contained under `frontend-spike/`.
- It reads from the existing API instead of modifying the current ASP.NET operator pages.

## Start it

1. Start the existing SyncFactors API on its usual port.
2. In this folder, install dependencies if needed:

```bash
npm install
```

3. Copy `.env.example` to `.env` if your API runs on a different port, then update `VITE_API_TARGET`.
4. Start the spike:

```bash
npm run dev
```

The Vite dev server proxies `/api/*` requests to `VITE_API_TARGET`, so no backend CORS changes are needed for the experiment.

## Suggested next steps

- Rebuild one page at a time, starting with the dashboard.
- Keep using the existing API contracts as the seam.
- If the spike proves worthwhile, extract shared API types and migrate workflows incrementally instead of rewriting everything at once.

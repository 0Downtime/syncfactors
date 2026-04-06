import { useEffect, useState } from 'react'
import { Navigate, Outlet, Route, Routes, useLocation } from 'react-router-dom'

import { AppShell } from '@/components/app-shell'
import { api } from '@/lib/api'
import type { Session } from '@/lib/types'
import { DashboardPage } from '@/pages/dashboard-page'
import { LoginPage } from '@/pages/login-page'
import { PreviewPage } from '@/pages/preview-page'
import { RunDetailPage } from '@/pages/run-detail-page'
import { SyncPage } from '@/pages/sync-page'
import { UsersPage } from '@/pages/users-page'

type Flash = { tone: 'good' | 'danger' | 'warn'; message: string } | null

function App() {
  const [session, setSession] = useState<Session | null>(null)
  const [sessionReady, setSessionReady] = useState(false)
  const [flash, setFlash] = useState<Flash>(null)

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const nextSession = await api.session()
        if (!cancelled) {
          setSession(nextSession)
        }
      } catch {
        if (!cancelled) {
          setSession({
            isAuthenticated: false,
            userId: null,
            username: null,
            role: null,
            isAdmin: false,
          })
        }
      } finally {
        if (!cancelled) {
          setSessionReady(true)
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [])

  if (!sessionReady || !session) {
    return (
      <div className="vf-app-shell">
        <div className="vf-page">
          <section className="vf-panel">
            <p className="vf-muted-text">Loading session...</p>
          </section>
        </div>
      </div>
    )
  }

  return (
    <Routes>
      <Route
        path="/login"
        element={
          session.isAuthenticated ? (
            <LoginRedirect />
          ) : (
            <LoginPage
              onLoggedIn={(nextSession, returnUrl) => {
                setSession(nextSession)
                setFlash({ tone: 'good', message: 'Signed in.' })
                returnUrl = normalizeReturnUrl(returnUrl)
                window.history.replaceState(null, '', returnUrl)
                window.dispatchEvent(new PopStateEvent('popstate'))
              }}
            />
          )
        }
      />
      <Route
        path="/"
        element={
          <RequireAuth session={session}>
            <AppShell
              flash={flash}
              session={session}
              onLogout={async () => {
                await api.logout()
                setSession({
                  isAuthenticated: false,
                  userId: null,
                  username: null,
                  role: null,
                  isAdmin: false,
                })
                setFlash(null)
              }}
            >
              <Outlet />
            </AppShell>
          </RequireAuth>
        }
      >
        <Route index element={<DashboardPage />} />
        <Route path="sync" element={<SyncPage onFlash={setFlash} />} />
        <Route path="preview" element={<PreviewPage onFlash={setFlash} />} />
        <Route path="runs/:runId" element={<RunDetailPage />} />
        <Route
          path="admin/users"
          element={session.isAdmin ? <UsersPage currentUserId={session.userId} onFlash={setFlash} /> : <Navigate to="/" replace />}
        />
      </Route>
      <Route path="*" element={<Navigate to={session.isAuthenticated ? '/' : '/login'} replace />} />
    </Routes>
  )
}

function RequireAuth({ children, session }: { children: React.ReactNode; session: Session }) {
  const location = useLocation()

  if (!session.isAuthenticated) {
    const returnUrl = `${location.pathname}${location.search}`
    return <Navigate replace to={`/login?returnUrl=${encodeURIComponent(returnUrl)}`} />
  }

  return <>{children}</>
}

function LoginRedirect() {
  const location = useLocation()
  return <Navigate replace to={normalizeReturnUrl(new URLSearchParams(location.search).get('returnUrl'))} />
}

function normalizeReturnUrl(returnUrl: string | null) {
  if (!returnUrl || !returnUrl.startsWith('/')) {
    return '/'
  }

  return returnUrl
}

export default App

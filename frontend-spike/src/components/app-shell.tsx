import { type PropsWithChildren, useEffect, useState } from 'react'
import { NavLink } from 'react-router-dom'
import { Activity, RefreshCcw } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { formatDate } from '@/lib/format'

export function AppShell({ children }: PropsWithChildren) {
  const [lastRefreshAt, setLastRefreshAt] = useState(() => new Date().toISOString())

  useEffect(() => {
    const timer = window.setInterval(() => setLastRefreshAt(new Date().toISOString()), 30000)
    return () => window.clearInterval(timer)
  }, [])

  return (
    <div className="vf-app-shell">
      <div className="vf-topbar-wrap">
        <div className="vf-topbar">
          <div className="vf-topbar-main">
            <div className="vf-topbar-brand">
              <NavLink className="text-[13px] font-medium tracking-[-0.01em] text-white no-underline" to="/">
                SyncFactors
              </NavLink>
              <Badge className="vf-nav-badge">Ops Console</Badge>
              <div className="vf-topbar-copy">
                <p className="vf-topbar-title">Runtime cockpit</p>
                <p className="vf-topbar-subtitle">Live visibility for sync, preview, and operator actions</p>
              </div>
            </div>
            <div className="hidden items-center gap-1 md:flex">
              <NavLink className={({ isActive }) => `vf-nav-link${isActive ? ' active' : ''}`} to="/">
                Dashboard
              </NavLink>
              <NavLink className={({ isActive }) => `vf-nav-link${isActive ? ' active' : ''}`} to="/sync">
                Sync
              </NavLink>
              <NavLink className={({ isActive }) => `vf-nav-link${isActive ? ' active' : ''}`} to="/preview">
                Worker Preview
              </NavLink>
            </div>
          </div>

          <div className="flex items-center gap-3 max-md:flex-wrap">
            <div className="vf-topbar-status">
              <span className="vf-status-dot good" aria-hidden="true" />
              <Activity className="size-3.5" />
              <span>Console live</span>
              <span className="text-white/40">·</span>
              <span>Refreshed {formatDate(lastRefreshAt)}</span>
            </div>
            <Button
              className="vf-nav-button"
              onClick={() => {
                setLastRefreshAt(new Date().toISOString())
                window.location.reload()
              }}
            >
              <RefreshCcw className="size-4" />
              Refresh
            </Button>
          </div>
        </div>
      </div>

      <div className="vf-page">{children}</div>
    </div>
  )
}

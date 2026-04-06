import type { PropsWithChildren } from 'react'
import { NavLink } from 'react-router-dom'
import { Activity } from 'lucide-react'

import type { Session } from '@/lib/types'

export function AppShell({
  children,
  session,
  flash,
  onLogout,
}: PropsWithChildren<{
  session: Session
  flash: { tone: 'good' | 'danger' | 'warn'; message: string } | null
  onLogout: () => Promise<void>
}>) {
  return (
    <div className="vf-app-shell">
      <div className="vf-topbar-wrap">
        <div className="vf-topbar">
          <div className="vf-topbar-main">
            <div className="vf-topbar-brand">
              <NavLink className="text-[13px] font-medium tracking-[-0.01em] text-white no-underline" to="/">
                SyncFactors
              </NavLink>
              <span className="vf-nav-badge">Ops Console</span>
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
              {session.isAdmin ? (
                <NavLink className={({ isActive }) => `vf-nav-link${isActive ? ' active' : ''}`} to="/admin/users">
                  Users
                </NavLink>
              ) : null}
            </div>
          </div>

          <div className="flex items-center gap-3 max-md:flex-wrap">
            <div className="vf-topbar-status">
              <span className="vf-status-dot good" aria-hidden="true" />
              <Activity className="size-3.5" />
              <span>Signed in as {session.username ?? 'unknown'}</span>
              <span className="text-white/40">·</span>
              <span>{session.isAdmin ? 'Admin' : 'Operator'}</span>
            </div>
            <button className="vf-nav-button" type="button" onClick={() => void onLogout()}>
              Logout
            </button>
          </div>
        </div>
        <div className="vf-mobile-nav md:hidden">
          <NavLink className={({ isActive }) => `vf-nav-link${isActive ? ' active' : ''}`} to="/">
            Dashboard
          </NavLink>
          <NavLink className={({ isActive }) => `vf-nav-link${isActive ? ' active' : ''}`} to="/sync">
            Sync
          </NavLink>
          <NavLink className={({ isActive }) => `vf-nav-link${isActive ? ' active' : ''}`} to="/preview">
            Worker Preview
          </NavLink>
          {session.isAdmin ? (
            <NavLink className={({ isActive }) => `vf-nav-link${isActive ? ' active' : ''}`} to="/admin/users">
              Users
            </NavLink>
          ) : null}
        </div>
      </div>

      <div className="vf-page">
        {flash ? (
          <section className="vf-panel">
            <p className={`vf-callout vf-callout-${flash.tone === 'danger' ? 'danger' : flash.tone === 'warn' ? 'warn' : 'good'}`}>
              {flash.message}
            </p>
          </section>
        ) : null}
        {children}
      </div>
    </div>
  )
}

import { type PropsWithChildren } from 'react'
import { NavLink } from 'react-router-dom'
import { RefreshCcw } from 'lucide-react'

import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'

export function AppShell({ children }: PropsWithChildren) {
  return (
    <div className="min-h-screen bg-[#f5f5f7] text-[#1d1d1f]">
      <div className="sticky top-0 z-50 px-4 pt-3 md:px-6">
        <div className="mx-auto flex h-12 w-full max-w-[1200px] items-center justify-between rounded-full border border-white/10 bg-black/80 px-4 text-white backdrop-blur-[20px]">
          <div className="flex items-center gap-3">
            <NavLink className="text-[13px] font-medium tracking-[-0.01em] text-white no-underline" to="/">
              SyncFactors
            </NavLink>
            <Badge className="vf-nav-badge">Vite Dashboard</Badge>
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
          <Button className="vf-nav-button" onClick={() => window.location.reload()}>
            <RefreshCcw className="size-4" />
            Refresh
          </Button>
        </div>
      </div>

      <div className="mx-auto flex w-full max-w-[1200px] flex-col gap-5 px-4 py-5 md:px-6 md:pb-10">
        {children}
      </div>
    </div>
  )
}

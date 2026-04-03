import { Navigate, Route, Routes } from 'react-router-dom'

import { AppShell } from '@/components/app-shell'
import { DashboardPage } from '@/pages/dashboard-page'
import { PreviewPage } from '@/pages/preview-page'
import { RunDetailPage } from '@/pages/run-detail-page'
import { SyncPage } from '@/pages/sync-page'

function App() {
  return (
    <AppShell>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/sync" element={<SyncPage />} />
        <Route path="/preview" element={<PreviewPage />} />
        <Route path="/runs/:runId" element={<RunDetailPage />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </AppShell>
  )
}

export default App

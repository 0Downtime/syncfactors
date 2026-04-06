import { useState, type FormEvent } from 'react'
import { useSearchParams } from 'react-router-dom'

import { api } from '@/lib/api'
import type { Session } from '@/lib/types'

export function LoginPage(props: {
  onLoggedIn: (session: Session, returnUrl: string | null) => void
}) {
  const [searchParams] = useSearchParams()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [rememberMe, setRememberMe] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [pending, setPending] = useState(false)

  const returnUrl = searchParams.get('returnUrl')

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setPending(true)
    setError(null)

    try {
      const session = await api.login(username, password, rememberMe, returnUrl)
      props.onLoggedIn(session, returnUrl)
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : 'Sign-in failed.')
    } finally {
      setPending(false)
    }
  }

  return (
    <div className="vf-app-shell">
      <div className="vf-page">
        <section className="vf-hero vf-hero-compact">
          <div className="max-w-[54rem]">
            <p className="vf-eyebrow vf-eyebrow-dark">Local Access</p>
            <h1 className="vf-hero-title">Sign in</h1>
            <p className="vf-hero-lede">Use a local operator account to access the SyncFactors portal.</p>
          </div>
        </section>

        <section className="vf-panel">
          {error ? <p className="vf-callout vf-callout-danger">{error}</p> : null}
          <form className="vf-form-grid" onSubmit={handleSubmit}>
            <label>
              <span>Username</span>
              <input className="vf-input" value={username} onChange={(event) => setUsername(event.target.value)} autoComplete="username" />
            </label>
            <label>
              <span>Password</span>
              <input className="vf-input" type="password" value={password} onChange={(event) => setPassword(event.target.value)} autoComplete="current-password" />
            </label>
            <label className="vf-checkbox-row">
              <span>Remember my login</span>
              <input type="checkbox" checked={rememberMe} onChange={(event) => setRememberMe(event.target.checked)} />
            </label>
            <div className="vf-filter-actions">
              <button className="vf-primary-button" type="submit" disabled={pending}>
                {pending ? 'Signing in...' : 'Sign in'}
              </button>
            </div>
          </form>
        </section>
      </div>
    </div>
  )
}

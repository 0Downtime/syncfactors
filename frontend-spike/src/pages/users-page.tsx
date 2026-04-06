import { useEffect, useState } from 'react'

import { api } from '@/lib/api'
import { formatDate, statusTone } from '@/lib/format'
import type { LocalUserSummary } from '@/lib/types'

export function UsersPage(props: {
  currentUserId: string | null
  onFlash: (flash: { tone: 'good' | 'danger' | 'warn'; message: string } | null) => void
}) {
  const [users, setUsers] = useState<LocalUserSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [createUsername, setCreateUsername] = useState('')
  const [createPassword, setCreatePassword] = useState('')
  const [createPasswordConfirmation, setCreatePasswordConfirmation] = useState('')
  const [createIsAdmin, setCreateIsAdmin] = useState(false)
  const [resetPasswords, setResetPasswords] = useState<Record<string, { password: string; confirmation: string }>>({})

  async function refresh() {
    const response = await api.users()
    setUsers(response.users)
  }

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const response = await api.users()
        if (!cancelled) {
          setUsers(response.users)
        }
      } catch (loadError) {
        if (!cancelled) {
          setError(loadError instanceof Error ? loadError.message : 'Failed to load users.')
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [])

  return (
    <>
      <section className="vf-hero vf-hero-compact">
        <div className="max-w-[54rem]">
          <p className="vf-eyebrow vf-eyebrow-dark">Administration</p>
          <h1 className="vf-hero-title">User Access</h1>
          <p className="vf-hero-lede">Create operator accounts, promote admins, reset passwords, and disable or delete local logins.</p>
        </div>
      </section>

      {error ? (
        <section className="vf-panel">
          <p className="vf-callout vf-callout-danger">{error}</p>
        </section>
      ) : null}

      <section className="vf-preview-grid">
        <article className="vf-panel">
          <div className="vf-section-heading">
            <div>
              <p className="vf-panel-kicker">Provision access</p>
              <h2 className="vf-panel-title">Create user</h2>
            </div>
          </div>
          <form
            className="vf-form-grid mt-5"
            onSubmit={(event) => {
              event.preventDefault()
              if (createPassword !== createPasswordConfirmation) {
                setError('Create password and confirmation must match.')
                return
              }

              void api
                .createUser(createUsername, createPassword, createIsAdmin)
                .then(async (result) => {
                  props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message })
                  setCreateUsername('')
                  setCreatePassword('')
                  setCreatePasswordConfirmation('')
                  setCreateIsAdmin(false)
                  setError(null)
                  await refresh()
                })
                .catch((createError) => {
                  setError(createError instanceof Error ? createError.message : 'Failed to create user.')
                })
            }}
          >
            <label><span>Username</span><input className="vf-input" value={createUsername} onChange={(event) => setCreateUsername(event.target.value)} autoComplete="off" /></label>
            <label><span>Password</span><input className="vf-input" type="password" value={createPassword} onChange={(event) => setCreatePassword(event.target.value)} autoComplete="new-password" /></label>
            <label><span>Confirm password</span><input className="vf-input" type="password" value={createPasswordConfirmation} onChange={(event) => setCreatePasswordConfirmation(event.target.value)} autoComplete="new-password" /></label>
            <label className="vf-checkbox-row"><span>Create as admin</span><input type="checkbox" checked={createIsAdmin} onChange={(event) => setCreateIsAdmin(event.target.checked)} /></label>
            <div className="vf-filter-actions"><button className="vf-primary-button" type="submit">Create user</button></div>
          </form>
        </article>

        <article className="vf-panel">
          <div className="vf-section-heading">
            <div>
              <p className="vf-panel-kicker">Guardrails</p>
              <h2 className="vf-panel-title">Security notes</h2>
            </div>
          </div>
          <div className="vf-section-stack mt-5">
            <p className="vf-callout">Passwords are stored as one-way hashes.</p>
            <p className="vf-callout">New passwords must be at least 12 characters and include uppercase, lowercase, and numeric characters.</p>
            <p className="vf-callout vf-callout-warn">This page cannot disable or delete your own account, and it will not remove the last active admin.</p>
          </div>
        </article>
      </section>

      <section className="vf-panel">
        <div className="vf-section-heading">
          <div>
            <p className="vf-panel-kicker">Directory</p>
            <h2 className="vf-panel-title">Local users</h2>
          </div>
        </div>

        <div className="mt-5 overflow-x-auto">
          <table className="vf-data-table">
            <thead>
              <tr>
                <th>Username</th>
                <th>Role</th>
                <th>Status</th>
                <th>Created</th>
                <th>Last Login</th>
                <th>Reset Password</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {users.map((user) => {
                const resetState = resetPasswords[user.userId] ?? { password: '', confirmation: '' }
                return (
                  <tr key={user.userId}>
                    <td>{user.username}</td>
                    <td>
                      <div className="vf-inline-actions">
                        <span className={`vf-badge ${statusTone(user.role)}`}>{user.role}</span>
                        <button
                          className="vf-secondary-button"
                          type="button"
                          onClick={() => {
                            void api
                              .setUserRole(user.userId, user.role.toLowerCase() !== 'admin')
                              .then(async (result) => {
                                props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message })
                                await refresh()
                              })
                              .catch((roleError) => {
                                setError(roleError instanceof Error ? roleError.message : 'Failed to change role.')
                              })
                          }}
                        >
                          {user.role.toLowerCase() === 'admin' ? 'Set regular' : 'Set admin'}
                        </button>
                      </div>
                    </td>
                    <td><span className={`vf-badge ${user.isActive ? 'good' : 'dim'}`}>{user.isActive ? 'Active' : 'Inactive'}</span></td>
                    <td>{formatDate(user.createdAt)}</td>
                    <td>{formatDate(user.lastLoginAt)}</td>
                    <td>
                      <div className="vf-inline-actions">
                        <input className="vf-input" type="password" value={resetState.password} onChange={(event) => setResetPasswords((current) => ({ ...current, [user.userId]: { ...resetState, password: event.target.value } }))} placeholder="New" autoComplete="new-password" />
                        <input className="vf-input" type="password" value={resetState.confirmation} onChange={(event) => setResetPasswords((current) => ({ ...current, [user.userId]: { ...resetState, confirmation: event.target.value } }))} placeholder="Confirm" autoComplete="new-password" />
                        <button
                          className="vf-secondary-button"
                          type="button"
                          onClick={() => {
                            if (resetState.password !== resetState.confirmation) {
                              setError('Reset password and confirmation must match.')
                              return
                            }

                            void api
                              .resetUserPassword(user.userId, resetState.password)
                              .then((result) => {
                                props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message })
                                setResetPasswords((current) => ({
                                  ...current,
                                  [user.userId]: { password: '', confirmation: '' },
                                }))
                              })
                              .catch((passwordError) => {
                                setError(passwordError instanceof Error ? passwordError.message : 'Failed to reset password.')
                              })
                          }}
                        >
                          Reset
                        </button>
                      </div>
                    </td>
                    <td>
                      <div className="vf-inline-actions">
                        <button
                          className="vf-secondary-button"
                          type="button"
                          onClick={() => {
                            void api
                              .setUserActive(user.userId, !user.isActive)
                              .then(async (result) => {
                                props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message })
                                await refresh()
                              })
                              .catch((activeError) => {
                                setError(activeError instanceof Error ? activeError.message : 'Failed to update user state.')
                              })
                          }}
                        >
                          {user.isActive ? 'Disable' : 'Enable'}
                        </button>
                        <button
                          className="vf-danger-button"
                          type="button"
                          disabled={props.currentUserId === user.userId}
                          onClick={() => {
                            void api
                              .deleteUser(user.userId)
                              .then(async (result) => {
                                props.onFlash({ tone: result.succeeded ? 'good' : 'danger', message: result.message })
                                await refresh()
                              })
                              .catch((deleteError) => {
                                setError(deleteError instanceof Error ? deleteError.message : 'Failed to delete user.')
                              })
                          }}
                        >
                          Delete
                        </button>
                      </div>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      </section>
    </>
  )
}

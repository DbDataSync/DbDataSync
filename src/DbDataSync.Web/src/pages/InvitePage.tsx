import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { ErrorBanner } from '../components/ErrorBanner'
import { Field } from '../components/Field'
import { createCredential, isSupported } from '../components/webauthn'

/**
 * Redeeming an invitation by enrolling a passkey.
 *
 * **The code lives in the URL fragment**, which browsers do not send to a server — so it never lands
 * in an access log between the person who sent it and the person who opens it. Read here and posted
 * deliberately.
 */
export function InvitePage() {
  const queryClient = useQueryClient()
  const [code] = useState(() => window.location.hash.replace(/^#/, ''))
  const [displayName, setDisplayName] = useState('')
  const [email, setEmail] = useState('')
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  // A query rather than an effect writing state: whether the code is usable is a function of what
  // came back, and an effect would render once claiming nothing before correcting itself.
  const check = useQuery({
    queryKey: ['invite', code] as const,
    queryFn: () => api.invites.check(code),
    enabled: code !== '',
    retry: false,
  })

  const valid = code === '' ? false : check.isSuccess ? true : check.isError ? false : null

  const enrol = async () => {
    setBusy(true)
    setError(null)
    try {
      const options = await api.invites.beginRegistration(code, displayName, email || null)
      const attestation = await createCredential(options)
      await api.invites.completeRegistration(code, displayName, email || null, attestation)
      // Signed in as of that call. Everything on screen was fetched as nobody.
      await queryClient.invalidateQueries()
      window.location.href = '/'
    } catch (ex) {
      setError(ex)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="pane" data-testid="invite-page">
      <div className="card" style={{ maxWidth: 460, margin: '10vh auto' }}>
        <div className="card-head"><span className="card-title">Join DbDataSync</span></div>
        <div className="card-body">
          <ErrorBanner error={error} />

          {valid === false && (
            <span className="hint" data-testid="invite-invalid">
              This invitation is not valid. It may have been used already, or expired — ask whoever
              sent it for another.
            </span>
          )}

          {valid && !isSupported() && (
            <span className="hint">
              This browser does not support passkeys, so there is no way to finish here.
            </span>
          )}

          {valid && isSupported() && (
            <>
              <Field label="Your name">
                <input
                  className="input"
                  value={displayName}
                  onChange={(e) => setDisplayName(e.target.value)}
                  data-testid="invite-name-input"
                />
              </Field>
              <Field label="Email">
                <span className="hint">
                  Used to attribute the config changes you make, in the Version Control tab.
                </span>
                <input
                  className="input"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  data-testid="invite-email-input"
                />
              </Field>
              <button
                type="button"
                className="btn btn-primary"
                disabled={busy || !displayName.trim()}
                onClick={enrol}
                data-testid="invite-enrol-button"
              >
                {busy ? 'Waiting for your passkey…' : 'Create a passkey'}
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  )
}

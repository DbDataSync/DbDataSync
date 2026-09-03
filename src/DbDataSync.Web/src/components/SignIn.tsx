import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api } from '../api/client'
import { useAuthStatus, useSignInWithWindows, useSignOut } from '../api/hooks'
import { ErrorBanner } from './ErrorBanner'
import { getCredential, isSupported } from './webauthn'

/**
 * Whether anybody is signed in, and how to change that.
 *
 * The sign-in screen offers the methods this deployment actually configured, which the server says —
 * a screen offering Windows authentication on a Linux host would be a button that cannot work.
 */
export function SignInScreen() {
  const { data: status } = useAuthStatus()
  const signIn = useSignInWithWindows()
  const queryClient = useQueryClient()
  const [passkeyError, setPasskeyError] = useState<unknown>(null)
  const [passkeyBusy, setPasskeyBusy] = useState(false)

  // Always offered, whatever the server lists: a passkey sign-in needs no server-side configuration
  // beyond a relying-party id that already has a working default, and somebody who was invited has
  // one whether or not this deployment also does Windows.
  const signInWithPasskey = async () => {
    setPasskeyBusy(true)
    setPasskeyError(null)
    try {
      const options = await api.auth.beginPasskey()
      const assertion = await getCredential(options)
      await api.auth.completePasskey(assertion)
      await queryClient.invalidateQueries()
    } catch (ex) {
      setPasskeyError(ex)
    } finally {
      setPasskeyBusy(false)
    }
  }

  return (
    <div className="pane" data-testid="sign-in-screen">
      <div className="card" style={{ maxWidth: 460, margin: '10vh auto' }}>
        <div className="card-head"><span className="card-title">Sign in to DbDataSync</span></div>
        <div className="card-body">
          <ErrorBanner error={signIn.error ?? passkeyError} />

          {status?.methods.includes('windows') && (
            <button
              type="button"
              className="btn btn-primary"
              disabled={signIn.isPending}
              onClick={() => signIn.mutate()}
              data-testid="sign-in-windows"
            >
              {signIn.isPending ? 'Signing in…' : 'Sign in with Windows'}
            </button>
          )}

          {isSupported() && (
            <button
              type="button"
              className="btn"
              disabled={passkeyBusy}
              onClick={signInWithPasskey}
              data-testid="sign-in-passkey"
            >
              {passkeyBusy ? 'Waiting for your passkey…' : 'Sign in with a passkey'}
            </button>
          )}

          {status && status.methods.length === 0 && (
            <span className="hint">
              No Windows groups are configured on this deployment. Sign in with a passkey if you have
              been invited, or ask an administrator to run <code>dbdatasync invite</code>.
            </span>
          )}
        </div>
      </div>
    </div>
  )
}

/** Who is signed in, in the chrome. Absent entirely where the deployment does not authenticate —
 * a "signed in as nobody" badge would be noise on a single-user trusted-network install. */
export function SignedInAs() {
  const { data: status } = useAuthStatus()
  const signOut = useSignOut()

  if (!status?.authenticated || !status.displayName) return null

  return (
    <span className="row" style={{ gap: 8 }} data-testid="signed-in-as">
      <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>
        {status.displayName}
        {status.role === 'Viewer' && <span className="badge" style={{ marginLeft: 6 }}>VIEWER</span>}
      </span>
      <button
        type="button"
        className="btn btn-chrome"
        disabled={signOut.isPending}
        onClick={() => signOut.mutate()}
        data-testid="sign-out-button"
      >
        Sign out
      </button>
    </span>
  )
}

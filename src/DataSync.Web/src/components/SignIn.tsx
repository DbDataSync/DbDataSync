import { useAuthStatus, useSignInWithWindows, useSignOut } from '../api/hooks'
import { ErrorBanner } from './ErrorBanner'

/**
 * Whether anybody is signed in, and how to change that.
 *
 * The sign-in screen offers the methods this deployment actually configured, which the server says —
 * a screen offering Windows authentication on a Linux host would be a button that cannot work.
 */
export function SignInScreen() {
  const { data: status } = useAuthStatus()
  const signIn = useSignInWithWindows()

  return (
    <div className="pane" data-testid="sign-in-screen">
      <div className="card" style={{ maxWidth: 460, margin: '10vh auto' }}>
        <div className="card-head"><span className="card-title">Sign in to DataSync</span></div>
        <div className="card-body">
          <ErrorBanner error={signIn.error} />

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

          {status && status.methods.length === 0 && (
            <span className="hint">
              No sign-in method is configured on this deployment. An administrator sets
              <code> DataSync:Auth:AdminGroup</code> for Windows authentication, or turns
              authentication off deliberately with <code>DataSync:Auth:Disabled</code>.
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

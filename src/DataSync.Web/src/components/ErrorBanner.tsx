import { ApiError } from '../api/client'

export function ErrorBanner({ error }: { error: unknown }) {
  if (!error) return null
  const message = error instanceof ApiError || error instanceof Error ? error.message : String(error)

  return (
    <div className="banner error" role="alert" data-testid="error-banner">
      <span className="mark">!</span>
      <span>{message}</span>
    </div>
  )
}

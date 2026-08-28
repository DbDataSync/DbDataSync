import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useApplyProvisioning, useProvisioning } from '../../api/hooks'
import type { ApplyResult, ProvisioningConfig, ProvisioningPlan, ProvisioningState } from '../../api/types'

const dotByState: Record<ProvisioningState, string> = {
  Satisfied: 'dot-ok',
  Missing: 'dot-warn',
  Unsupported: 'dot-bad',
  Unknown: 'dot-idle',
}

function ProvisioningStateBadge({ state }: { state: ProvisioningState }) {
  return (
    <span className="status">
      <span className={`dot ${dotByState[state]}`} />
      {state.toLowerCase()}
    </span>
  )
}

function PlanPanel({ label, testId, plan, onApply, applying, result }: {
  label: string
  testId: string
  plan: ProvisioningPlan
  onApply: () => void
  applying: boolean
  /** The last Apply's per-step outcome, if there has been one in this session. */
  result: ApplyResult | undefined
}) {
  const sql = plan.steps.map((s) => s.commandText).join('\n')

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(sql)
    } catch {
      // Clipboard access can be denied by the browser; the SQL is still selectable in the block below.
    }
  }

  const apply = () => {
    const summary = plan.steps.map((s) => `- ${s.title}`).join('\n')
    if (window.confirm(`This will run the following against ${label.toLowerCase()}:\n\n${summary}\n\nContinue?`))
      onApply()
  }

  return (
    <div className="card" data-testid={testId}>
      <div className="card-head">
        <span className="card-title">{label}</span>
        <ProvisioningStateBadge state={plan.state} />
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        {plan.warnings.length > 0 && (
          <div className="banner warn" role="alert">
            {plan.warnings.map((w, i) => <div key={i}>{w}</div>)}
          </div>
        )}

        {plan.steps.length > 0 && (
          <>
            <pre className="mono" style={{
              background: 'var(--sunken)', padding: 10, borderRadius: 6, overflowX: 'auto', margin: 0,
            }}>
              {sql}
            </pre>
            <div style={{ display: 'flex', gap: 8 }}>
              <button type="button" className="btn" onClick={copy}>Copy</button>
              <button
                type="button"
                className="btn btn-primary"
                onClick={apply}
                disabled={applying}
                data-testid={`${testId}-apply`}
              >
                {applying ? 'Applying…' : 'Apply'}
              </button>
            </div>
          </>
        )}

        {/* A step can fail without the request failing — a least-privileged connection that cannot
            run ALTER DATABASE is the enterprise-normal case, and the server reports it as a result
            rather than an error. Without this the operator sees a plan that still says "missing" and
            nothing at all to explain why. */}
        {result && (
          <div
            className={`banner ${result.steps.every((r) => r.succeeded) ? '' : 'warn'}`}
            role="status"
            data-testid={`${testId}-result`}
          >
            {result.steps.map((r, i) => (
              <div key={i}>
                {r.succeeded ? '✓' : '✗'} {r.title}
                {r.error && <div className="mono" style={{ fontSize: 12 }}>{r.error}</div>}
              </div>
            ))}
            {result.steps.length === 0 && <div>Nothing was run.</div>}
          </div>
        )}

        {plan.steps.length === 0 && plan.state === 'Satisfied' && (
          <span style={{ color: 'var(--ink-4)' }}>Nothing to do.</span>
        )}
      </div>
    </div>
  )
}

interface Props {
  replicationName: string
  mappingName: string
  provisioning: ProvisioningConfig
  onChangeProvisioning: (value: ProvisioningConfig) => void
  /** The target has been retyped since the last save, so these plans describe the previous one. */
  stale?: boolean
}

/** The Setup card: both sides' provisioning plans for a saved table mapping, previewed and applied
 * live against the database — separate from the mapping's own Save, since Apply runs DDL immediately
 * rather than writing config. See phase 25 §7. */
export function ProvisioningCard({ replicationName, mappingName, provisioning, onChangeProvisioning, stale = false }: Props) {
  const { data: plans, error, isLoading } = useProvisioning(replicationName, mappingName)
  const applySource = useApplyProvisioning(replicationName, mappingName)
  const applyTarget = useApplyProvisioning(replicationName, mappingName)
  const [lastApplyError, setLastApplyError] = useState<unknown>(null)

  if (isLoading) return null

  const apply = async (mutation: ReturnType<typeof useApplyProvisioning>, action: string) => {
    setLastApplyError(null)
    try {
      await mutation.mutateAsync(action)
    } catch (e) {
      setLastApplyError(e)
    }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
      <ErrorBanner error={error ?? lastApplyError} />

      {/* The API plans against the saved mapping, which is the right thing for it to do — Apply runs
          DDL, and running it against something that only exists in an unsaved form would be worse
          than saying so. */}
      {stale && (
        <div className="banner warn" role="alert" data-testid="provisioning-stale">
          These plans are for the mapping as saved. Save to plan against the target you just named.
        </div>
      )}

      {plans && (
        <div className="form-grid">
          <PlanPanel
            label="Source"
            testId="provisioning-plan-source"
            plan={plans.source}
            applying={applySource.isPending}
            result={applySource.data}
            onApply={() => apply(applySource, plans.source.action)}
          />
          <PlanPanel
            label="Target"
            testId="provisioning-plan-target"
            plan={plans.target}
            applying={applyTarget.isPending}
            result={applyTarget.data}
            onApply={() => apply(applyTarget, plans.target.action)}
          />
        </div>
      )}

      <div className="card">
        <div className="card-body">
          <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <input
              type="checkbox"
              checked={provisioning.createTargetTableIfMissing}
              onChange={(e) => onChangeProvisioning({ ...provisioning, createTargetTableIfMissing: e.target.checked })}
            />
            Create target table if missing
          </label>
          <div style={{ color: 'var(--ink-4)', fontSize: 12, marginTop: 4 }}>
            Creates the table only if it does not exist; never alters an existing one.
          </div>
        </div>
      </div>
    </div>
  )
}

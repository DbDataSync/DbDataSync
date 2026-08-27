import { useState } from 'react'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useApplyProvisioning, useProvisioning } from '../../api/hooks'
import type { ProvisioningConfig, ProvisioningPlan, ProvisioningState } from '../../api/types'

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

function PlanPanel({ label, plan, onApply, applying }: {
  label: string
  plan: ProvisioningPlan
  onApply: () => void
  applying: boolean
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
    <div className="card">
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
              <button type="button" className="btn btn-primary" onClick={apply} disabled={applying}>
                {applying ? 'Applying…' : 'Apply'}
              </button>
            </div>
          </>
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
}

/** The Setup card: both sides' provisioning plans for a saved table mapping, previewed and applied
 * live against the database — separate from the mapping's own Save, since Apply runs DDL immediately
 * rather than writing config. See phase 25 §7. */
export function ProvisioningCard({ replicationName, mappingName, provisioning, onChangeProvisioning }: Props) {
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

      {plans && (
        <div className="form-grid">
          <PlanPanel
            label="Source"
            plan={plans.source}
            applying={applySource.isPending}
            onApply={() => apply(applySource, plans.source.action)}
          />
          <PlanPanel
            label="Target"
            plan={plans.target}
            applying={applyTarget.isPending}
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

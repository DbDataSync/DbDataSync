import { useState } from 'react'
import { CodeEditor } from '../../components/CodeEditor'
import { InheritableToggle } from '../../components/InheritableToggle'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useApplyProvisioning, useProvisioning } from '../../api/hooks'
import type { EndpointSide } from '../../components/EndpointSidePair'
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

function PlanPanel({ label, side, testId, plan, onApply, applying, result }: {
  label: string
  /** The same accent the endpoint cards use. The colour-to-side association is worth holding
   * everywhere a side is shown; the arrow is not, because these two are setup steps rather than a
   * flow from one to the other. */
  side: EndpointSide
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
    <div className={`card side-${side}`} data-testid={testId} data-side={side}>
      <div className="card-head">
        <span className={`card-title side-${side}`}>{label}</span>
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
            {/* Monaco, not a <pre>. The <pre>'s overflowX never contained a long statement — it
                widened the page and produced a page-level horizontal scrollbar — and the preview
                screen had already solved exactly this. Same component, same props. */}
            <CodeEditor
              value={sql}
              language="sql"
              readOnly
              onChange={() => {}}
              minLines={2}
              maxLines={24}
              testId={`${testId}-sql`}
            />
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
  /** The replication's answer, shown while this mapping inherits it. */
  inherited: ProvisioningConfig | undefined
  onChangeProvisioning: (value: ProvisioningConfig) => void
  /** The target has been retyped since the last save, so these plans describe the previous one. */
  stale?: boolean
}

/** The Setup card: both sides' provisioning plans for a saved table mapping, previewed and applied
 * live against the database — separate from the mapping's own Save, since Apply runs DDL immediately
 * rather than writing config. See phase 25 §7. */
export function ProvisioningCard({
  replicationName, mappingName, provisioning, inherited, onChangeProvisioning, stale = false,
}: Props) {
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

      <div className="card">
        <div className="card-head">
          <span className="card-title">Provisioning</span>
          <span className="card-note">inherited from the replication unless overridden here</span>
        </div>
        <div className="card-body" style={{ gap: 14 }}>
          <InheritableToggle
            label="Create target table if missing"
            description="Creates the table only when it does not exist. Never alters one that does."
            value={provisioning.createTargetTableIfMissing}
            inherited={inherited?.createTargetTableIfMissing ?? false}
            onChange={(next) => onChangeProvisioning({ ...provisioning, createTargetTableIfMissing: next })}
            testId="provisioning-create"
          />
          <div className="divider" />
          <InheritableToggle
            label="Alter target columns if missing or changed"
            description="Adds a mapped column the target lacks and changes one whose type no longer matches. Never drops a column."
            value={provisioning.alterTargetTableColumnsIfMissingOrChanged}
            inherited={inherited?.alterTargetTableColumnsIfMissingOrChanged ?? false}
            onChange={(next) =>
              onChangeProvisioning({ ...provisioning, alterTargetTableColumnsIfMissingOrChanged: next })}
            testId="provisioning-alter"
          />
        </div>
      </div>

      {/* Below the settings, not above them. The settings are what this mapping *asks for*; the two
          plans are what that currently amounts to against these databases. Reading the consequence
          before the decision meant the DDL was the first thing on the tab and the toggles that
          produced it were somewhere past it. */}
      {plans && (
        <div className="form-grid">
          <PlanPanel
            label="Source"
            side="source"
            testId="provisioning-plan-source"
            plan={plans.source}
            applying={applySource.isPending}
            result={applySource.data}
            onApply={() => apply(applySource, plans.source.action)}
          />
          <PlanPanel
            label="Target"
            side="target"
            testId="provisioning-plan-target"
            plan={plans.target}
            applying={applyTarget.isPending}
            result={applyTarget.data}
            onApply={() => apply(applyTarget, plans.target.action)}
          />
        </div>
      )}
    </div>
  )
}

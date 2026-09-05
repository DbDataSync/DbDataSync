import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useApplyReplicationProvisioningPlan, useReplicationProvisioningPlan } from '../../api/hooks'
import type {
  ReplicationProvisioningGroup, ReplicationProvisioningPlan, ReplicationProvisioningStep,
  ReplicationProvisioningStepResult,
} from '../../api/types'

function allStepIds(plan: ReplicationProvisioningPlan): string[] {
  return plan.groups.flatMap((g) => g.steps.map((s) => s.id))
}

/** A group's own Database-scope steps that are not currently selected — the one declared dependency
 * rule (phase 105 §3): a Table-scope step in the same group warns, but still runs, when one of these
 * was left out. Computed client-side so unticking updates the warning immediately, without a round
 * trip — the same rule Apply itself checks before running. */
function unselectedDatabaseIds(group: ReplicationProvisioningGroup, selected: Set<string>): Set<string> {
  return new Set(group.steps.filter((s) => s.scope === 'Database' && !selected.has(s.id)).map((s) => s.id))
}

/** A header comment naming the replication, the endpoint and when it was generated — useful to a DBA
 * receiving this out of context, and every engine in scope treats `--` as a comment, so it costs the
 * script nothing to be "purely executable". */
function scriptFor(replicationName: string, group: ReplicationProvisioningGroup, selected: Set<string>): string {
  const steps = group.steps.filter((s) => selected.has(s.id))
  const header = [
    '-- DbDataSync provisioning script',
    `-- Replication: ${replicationName}`,
    `-- Connection: ${group.connectionName} / ${group.database} (${group.side.toLowerCase()})`,
    `-- Generated: ${new Date().toISOString()}`,
    '',
    '',
  ].join('\n')
  return header + steps.map((s) => s.commandText).join('\n')
}

function outcomeLabel(outcome: ReplicationProvisioningStepResult['outcome']): string {
  switch (outcome) {
    case 'Applied': return '✓ applied'
    case 'Failed': return '✗ failed'
    case 'NoLongerNeeded': return 'no longer needed — already satisfied'
    case 'NotAttempted': return 'not attempted'
  }
}

function StepRow({ step, checked, onToggle, warning, result, mappingsBase }: {
  step: ReplicationProvisioningStep
  checked: boolean
  onToggle: () => void
  warning: string | null
  result: ReplicationProvisioningStepResult | undefined
  mappingsBase: string
}) {
  return (
    <div className="provisioning-step" data-testid="provisioning-step" data-step-title={step.title}>
      <label style={{ display: 'flex', gap: 8, alignItems: 'flex-start', cursor: 'pointer' }}>
        <input
          type="checkbox"
          checked={checked}
          onChange={onToggle}
          data-testid="provisioning-step-checkbox"
        />
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4, flex: 1, minWidth: 0 }}>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <span>{step.title}</span>
            <span className="badge" data-testid="provisioning-step-scope">{step.scope}</span>
            {step.automatic && (
              <span className="badge badge-accent" data-testid="provisioning-step-automatic">AUTOMATIC</span>
            )}
          </div>
          <div className="mono" style={{ fontSize: 12, color: 'var(--ink-4)', overflowX: 'auto' }}>
            {step.commandText}
          </div>
          {step.contributingMappings.length > 0 && (
            <div style={{ fontSize: 12, color: 'var(--ink-4)' }}>
              {step.contributingMappings.map((m, i) => (
                <span key={m}>
                  {i > 0 && ', '}
                  <Link to={`${mappingsBase}/${encodeURIComponent(m)}/provisioning`}>{m}</Link>
                </span>
              ))}
            </div>
          )}
          {warning && (
            <div className="banner warn" role="alert" data-testid="provisioning-step-warning">{warning}</div>
          )}
          {result && (
            <div
              className={`banner ${result.outcome === 'Applied' ? '' : 'warn'}`}
              role="status"
              data-testid="provisioning-step-result"
            >
              {outcomeLabel(result.outcome)}
              {result.error && <div className="mono" style={{ fontSize: 12 }}>{result.error}</div>}
              {result.warning && <div style={{ fontSize: 12 }}>{result.warning}</div>}
            </div>
          )}
        </div>
      </label>
    </div>
  )
}

function GroupPanel({ replicationName, group, selected, onToggle, resultsById, mappingsBase }: {
  replicationName: string
  group: ReplicationProvisioningGroup
  selected: Set<string>
  onToggle: (id: string) => void
  resultsById: Map<string, ReplicationProvisioningStepResult>
  mappingsBase: string
}) {
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(scriptFor(replicationName, group, selected))
    } catch {
      // Clipboard access can be denied by the browser; the statements are still readable below.
    }
  }

  const unselectedDb = unselectedDatabaseIds(group, selected)

  return (
    <div
      className="card"
      data-testid="provisioning-group"
      data-connection={group.connectionName}
      data-database={group.database}
      data-side={group.side}
    >
      <div className="card-head">
        <span className={`card-title side-${group.side.toLowerCase()}`}>
          {group.connectionName} · {group.database}
        </span>
        <span className="status">{group.side}</span>
        <button type="button" className="btn spacer" onClick={copy} data-testid="provisioning-group-copy">
          Copy
        </button>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
        {group.steps.map((step) => (
          <StepRow
            key={step.id}
            step={step}
            checked={selected.has(step.id)}
            onToggle={() => onToggle(step.id)}
            warning={
              step.scope === 'Table' && unselectedDb.size > 0
                ? "This step's database-level prerequisite is not selected. If it has not already " +
                  'been applied out of band, this statement may fail.'
                : null
            }
            result={resultsById.get(step.id)}
            mappingsBase={mappingsBase}
          />
        ))}
      </div>
    </div>
  )
}

/**
 * The replication-wide Provisioning tab's aggregate — phase 105. Below the two target auto-flag
 * settings (`TargetProvisioningTab`'s own card, unchanged): every table mapping's provisioning steps,
 * grouped by server, deduplicated, individually selectable, copyable per group, and runnable as one
 * batch across every group at once.
 * <para>
 * Selection is never persisted — every fresh plan (on load, and again after Run refetches) starts with
 * everything ticked, since a remembered exclusion could hide a step that has since become necessary for
 * a different reason (phase 105 §3).
 * </para>
 */
export function ReplicationProvisioningPanel({ replicationName }: { replicationName: string }) {
  const { data: plan, error, isLoading } = useReplicationProvisioningPlan(replicationName)
  const apply = useApplyReplicationProvisioningPlan(replicationName)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const mappingsBase = `/replications/${encodeURIComponent(replicationName)}/mappings`

  useEffect(() => {
    if (plan) setSelected(new Set(allStepIds(plan)))
  }, [plan])

  if (isLoading) return null

  const toggle = (id: string) =>
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const resultsById = new Map((apply.data?.steps ?? []).map((r) => [r.id, r] as const))

  const run = () => {
    const count = plan?.groups.flatMap((g) => g.steps).filter((s) => selected.has(s.id)).length ?? 0
    if (count === 0) return
    if (window.confirm(`Run ${count} selected provisioning statement(s) against their databases?`))
      apply.mutate([...selected])
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }} data-testid="replication-provisioning-panel">
      <ErrorBanner error={error ?? apply.error} />

      {plan && (
        <>
          <div className="row" style={{ justifyContent: 'flex-end' }}>
            <button
              type="button"
              className="btn btn-primary"
              onClick={run}
              disabled={apply.isPending || selected.size === 0}
              data-testid="provisioning-run-selected"
            >
              {apply.isPending ? 'Running…' : `Run selected (${selected.size})`}
            </button>
          </div>

          {plan.groups.length === 0 && plan.excluded.length === 0 && (
            <span style={{ color: 'var(--ink-4)' }}>Nothing to provision.</span>
          )}

          {plan.groups.map((group) => (
            <GroupPanel
              key={`${group.connectionName}/${group.database}/${group.side}`}
              replicationName={replicationName}
              group={group}
              selected={selected}
              onToggle={toggle}
              resultsById={resultsById}
              mappingsBase={mappingsBase}
            />
          ))}

          {plan.excluded.length > 0 && (
            <div className="card" data-testid="provisioning-excluded">
              <div className="card-head">
                <span className="card-title">Not included</span>
                <span className="card-note">mappings this page could not plan for</span>
              </div>
              <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {plan.excluded.map((x) => (
                  <div key={`${x.mappingName}-${x.side ?? ''}`} data-testid="provisioning-excluded-row">
                    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                      <Link to={`${mappingsBase}/${encodeURIComponent(x.mappingName)}/provisioning`}>
                        {x.mappingName}
                      </Link>
                      {x.side && <span className="badge">{x.side}</span>}
                    </div>
                    <div style={{ fontSize: 12, color: 'var(--ink-4)' }}>{x.reason}</div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </>
      )}
    </div>
  )
}

import { useNavigate } from 'react-router-dom'
import { AppShell, SectionTabs } from '../components/AppShell'
import { ErrorBanner } from '../components/ErrorBanner'
import { useScripts } from '../api/hooks'
import type { ScriptUsage } from '../api/types'

const COLUMNS = '1.1fr .6fr 1.1fr 1.6fr 1.4fr .6fr'

/**
 * The global script registry. A script is registered once here and then *bound* wherever it applies —
 * on a connection, a replication or a table mapping — with the most specific level winning.
 */
export function ScriptsPage() {
  const { data: scripts, isLoading, error } = useScripts()
  const navigate = useNavigate()

  return (
    <AppShell crumbs={[{ label: 'Scripts' }]} tabs={<SectionTabs />}>
      <div className="pane">
        <div className="page-head">
          <h1 className="page-title">Scripts</h1>
          <span className="page-note">
            {scripts ? `${scripts.length} registered` : '…'} · C# compiled or SQL token-checked on save,
            bound per connection, replication or table mapping
          </span>
          <div className="right">
            <button className="btn btn-primary" onClick={() => navigate('/scripts/new')} data-testid="new-script-button">
              New script
            </button>
          </div>
        </div>

        <ErrorBanner error={error} />

        <div className="card flush" data-testid="scripts-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Name</span><span>Language</span><span>Kind</span><span>Used by</span><span>Description</span><span>Enabled</span>
          </div>
          {isLoading && <div className="empty">Loading…</div>}
          {scripts?.length === 0 && <div className="empty">No scripts yet.</div>}
          {(scripts ?? []).map(({ manifest: s, usedBy }) => (
            <button
              key={s.name}
              className="grid-row"
              style={{ gridTemplateColumns: COLUMNS, gap: 14 }}
              onClick={() => navigate(`/scripts/${encodeURIComponent(s.name)}`)}
              data-testid={`script-row-${s.name}`}
            >
              <span className="name">{s.name}</span>
              <span className="dim">{s.language === 'Sql' ? 'SQL' : 'C#'}</span>
              <span className="dim">{s.kind}</span>
              <UsedBy usages={usedBy} scriptName={s.name} />
              <span className="dim" style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {s.description ?? '—'}
              </span>
              <span className="status">
                <span className={`dot ${s.enabled ? 'dot-ok' : 'dot-idle'}`} />
                {s.enabled ? 'enabled' : 'disabled'}
              </span>
            </button>
          ))}
        </div>
      </div>
    </AppShell>
  )
}

/**
 * Where a script is bound, or that it is bound nowhere.
 *
 * "Unused" is the answer worth showing loudly — it is usually either a mistake or something safe to
 * delete, and it is the one thing a list of names, kinds and descriptions cannot tell you. The bound
 * case names the first site and counts the rest rather than listing them all: a row has one line, and
 * knowing *that* it is used plus one example is what decides whether to open it.
 */
function UsedBy({ usages, scriptName }: { usages: ScriptUsage[]; scriptName: string }) {
  if (usages.length === 0) {
    return (
      <span className="status" data-testid={`script-usage-${scriptName}`}>
        <span className="dot dot-idle" />
        unused
      </span>
    )
  }

  const [first, ...rest] = usages
  return (
    <span
      className="dim"
      style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
      title={usages.map((u) => `${u.owner} (${u.level}, ${u.slot})`).join('\n')}
      data-testid={`script-usage-${scriptName}`}
    >
      {first.owner}
      {rest.length > 0 && ` +${rest.length}`}
    </span>
  )
}

import { useMemo, useState } from 'react'
import * as signalR from '@microsoft/signalr'
import { useNavigate, useOutletContext } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useBulkCreateMappings, useReplication, useTableMappingDetails, useTables } from '../../api/hooks'
import type { BulkCreateProgress } from '../../api/types'
import type { MappingsOutletContext } from './TableMappingsPanel'

const COLUMNS = '26px 1fr 90px'

/**
 * A table's identity, for the selection set and the mapping counts.
 *
 * Not `${schema}.${table}`, readable as that is: schema `dbo` with table `My.Table` and schema
 * `dbo.My` with table `Table` produce the same string, and two tables sharing a key means ticking one
 * ticks the other. The label below is the combined form, which is only ever displayed.
 */
const keyOf = (t: { schema: string; table: string }) => JSON.stringify([t.schema, t.table])
const labelOf = (t: { schema: string; table: string }) => `${t.schema}.${t.table}`

/**
 * Every table in the replication's default source database, and how many mappings each already has.
 *
 * The screen this replaces is picking tables one at a time through a form: forty tables meant forty
 * passes through the same six fields, all of which had the same answer. What makes that worth
 * replacing is not the typing but the *decision* — a mapping created here states its source and its
 * target and leaves everything else unset, so it inherits the replication rather than carrying its
 * own copy of settings nobody chose deliberately.
 *
 * The mapping counts come from the mappings the sidebar has already loaded (see
 * `useTableMappingDetails`), so this screen makes no requests the page was not making anyway.
 */
export function MappingsOverview() {
  const { replicationName, names, base } = useOutletContext<MappingsOutletContext>()
  const navigate = useNavigate()
  const { data: task } = useReplication(replicationName)
  const source = task?.endpoints?.source

  const { data: tables, error } = useTables(source?.connectionName ?? undefined, source?.database ?? undefined)
  const mappings = useTableMappingDetails(replicationName, names)

  const [filter, setFilter] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [progress, setProgress] = useState<BulkCreateProgress | null>(null)
  const create = useBulkCreateMappings(replicationName)

  /**
   * How many mappings read from each table, keyed `schema.table`. A count rather than a yes/no
   * because fan-in is a real shape — two mappings can legitimately read the same source — and
   * showing "mapped" for both would hide the second one.
   */
  const mappedCounts = useMemo(() => {
    const counts = new Map<string, number>()
    for (const query of mappings) {
      for (const spec of query.data?.sources ?? []) {
        counts.set(keyOf(spec), (counts.get(keyOf(spec)) ?? 0) + 1)
      }
    }
    return counts
  }, [mappings])

  const needle = filter.trim().toLowerCase()
  const shown = (tables ?? []).filter(
    (t) => needle === '' || labelOf(t).toLowerCase().includes(needle))

  const toggle = (key: string) => setSelected((prev) => {
    const next = new Set(prev)
    if (!next.delete(key)) next.add(key)
    return next
  })

  // Select-all acts on what is *shown*, not on every table there is: a filtered list that quietly
  // ticked three hundred hidden rows would create three hundred mappings nobody looked at.
  const allShownSelected = shown.length > 0 && shown.every((t) => selected.has(keyOf(t)))
  const toggleAll = () => setSelected(allShownSelected ? new Set() : new Set(shown.map(keyOf)))

  const createSelected = async () => {
    const chosen = shown.filter((t) => selected.has(keyOf(t)))
    if (chosen.length === 0) return

    const batchId = crypto.randomUUID()
    setProgress({ done: 0, total: chosen.length, name: '' })

    // Joined *before* the request goes out, so the first table's event is not lost to a handshake
    // that is still in flight. A dropped connection costs granularity, never correctness — the
    // response carries the whole result.
    const connection = await joinBatch(batchId, setProgress)
    try {
      const result = await create.mutateAsync({
        tables: chosen.map((t) => ({ schema: t.schema, table: t.table })),
        batchId,
      })
      setSelected(new Set())
      if (result.created.length > 0) navigate(`${base}/${encodeURIComponent(result.created[0])}`)
    } finally {
      setProgress(null)
      await connection?.stop()
    }
  }

  return (
    <>
      <ErrorBanner error={error ?? create.error} />

      <div className="card flush" data-testid="mappings-overview">
        <div className="card-head tight">
          <span className="card-title sm">Source tables</span>
          <span className="card-note">
            {source?.connectionName
              ? <>{source.connectionName} · {source.database}</>
              : <>this replication has no default source endpoint — set one on the Overview tab</>}
          </span>
          <input
            className="input sm spacer"
            style={{ maxWidth: 220 }}
            placeholder="Filter tables…"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            aria-label="Filter tables"
            data-testid="table-filter"
          />
          <button
            type="button"
            className="btn btn-sm btn-primary"
            disabled={selected.size === 0 || create.isPending}
            onClick={createSelected}
            data-testid="create-mappings-button"
          >
            {progress
              ? `Created ${progress.done} of ${progress.total}…`
              : selected.size === 0
                ? 'Create mappings'
                : `Create ${selected.size} mapping${selected.size === 1 ? '' : 's'}`}
          </button>
        </div>

        <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 0, height: 29 }}>
          <input
            type="checkbox"
            checked={allShownSelected}
            onChange={toggleAll}
            aria-label="Select all tables"
            data-testid="select-all-tables"
          />
          <span>Table</span><span>Mappings</span>
        </div>

        {shown.map((t) => {
          const key = keyOf(t)
          const label = labelOf(t)
          const count = mappedCounts.get(key) ?? 0
          return (
            <div key={key} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 0 }}>
              <input
                type="checkbox"
                checked={selected.has(key)}
                onChange={() => toggle(key)}
                aria-label={`Select ${label}`}
                data-testid={`select-table-${label}`}
              />
              <span className="name">{label}</span>
              <span data-testid={`table-mapping-count-${label}`}>
                {count === 0
                  ? <span className="faint">unmapped</span>
                  : <span className="badge badge-accent">{count}</span>}
              </span>
            </div>
          )
        })}

        {tables && shown.length === 0 && (
          <div className="empty">{needle ? 'No table matches that filter.' : 'No tables in this database.'}</div>
        )}
        {!tables && !error && <div className="empty">Loading tables…</div>}
      </div>
    </>
  )
}

/**
 * Subscribes to one bulk-create batch on the run hub — the same push channel the live run log uses.
 * Returns null if the hub cannot be reached, which is not a failure worth surfacing: the request is
 * still going to run and still going to answer, and the button falls back to being a plain
 * in-progress state.
 */
async function joinBatch(batchId: string, onProgress: (p: BulkCreateProgress) => void) {
  const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/run').build()
  connection.on('bulkMappingProgress', (p: BulkCreateProgress) => onProgress(p))
  try {
    await connection.start()
    await connection.invoke('JoinRun', batchId)
    return connection
  } catch {
    return null
  }
}

import { Navigate, NavLink, Outlet, useMatch, useNavigate, useOutletContext, useParams } from 'react-router-dom'
import { ErrorBanner } from '../../components/ErrorBanner'
import { useTableMapping, useTableMappings } from '../../api/hooks'
import { TableMappingForm } from './TableMappingForm'

/**
 * The design replaces the list-then-form stack with a mappings sidebar and the selected mapping's
 * editor filling the pane — the editor is the screen, not a card appended below a table.
 *
 * Which mapping is open is in the URL, so this is itself a layout route: the sidebar stays mounted
 * while the editor beside it swaps, and a link to one mapping is a link someone can send.
 */
export function TableMappingsPanel({ replicationName }: { replicationName: string }) {
  const { data: names, error } = useTableMappings(replicationName)
  const base = `/replications/${encodeURIComponent(replicationName)}/mappings`
  // Asked of the route rather than inferred from the absence of a mapping param: the index route has
  // no param either, and it is mid-redirect to the first mapping, not creating one.
  const creating = useMatch(`${base}/new`) !== null

  return (
    <>
      <aside className="sidebar">
        <div className="sidebar-head">
          <span>Mappings</span>
          <NavLink to={`${base}/new`} className="btn-link" data-testid="new-mapping-button" title="New mapping">+</NavLink>
        </div>
        <div className="sidebar-list" data-testid="mappings-sidebar">
          {/* A sibling of the list, not a replacement for it: picking one mapping is still how the
              editor is reached, and this is where "which tables are mapped at all" is answered. */}
          <NavLink
            to={`${base}/overview`}
            className={({ isActive }) => `sidebar-item strong ${isActive ? 'active' : ''}`}
            data-testid="mappings-overview-link"
          >
            Overview
          </NavLink>
          {(names ?? []).map((n) => (
            <MappingSidebarItem key={n} replicationName={replicationName} name={n} to={`${base}/${encodeURIComponent(n)}`} />
          ))}
          {names?.length === 0 && <span className="hint" style={{ padding: '6px 7px' }}>No mappings yet.</span>}
          {creating && <NewMappingPlaceholder base={base} />}
        </div>
      </aside>

      <div className="pane">
        <ErrorBanner error={error} />
        <Outlet context={{ replicationName, names, base } satisfies MappingsOutletContext} />
      </div>
    </>
  )
}

export interface MappingsOutletContext {
  replicationName: string
  names: string[] | undefined
  base: string
}

/** Stands in for the mapping being created, which has no row in the list until it is saved. */
function NewMappingPlaceholder({ base }: { base: string }) {
  return (
    <NavLink to={`${base}/new`} className={({ isActive }) => `sidebar-item ${isActive ? 'active' : ''}`}>
      new mapping
    </NavLink>
  )
}

function MappingSidebarItem({ replicationName, name, to }: { replicationName: string; name: string; to: string }) {
  const { data } = useTableMapping(replicationName, name)
  return (
    <NavLink
      to={to}
      className={({ isActive }) => `sidebar-item ${isActive ? 'active' : ''}`}
      data-testid={`mapping-item-${name}`}
    >
      {/* The name is the only part allowed to give way: a long one truncates to an ellipsis with the
          whole thing on hover, rather than widening the fixed-width sidebar into a scrollbar. */}
      <span className="sidebar-item-name" title={name}>{name}</span>
      <span className="badge">{data ? data.columnMappings.length : '…'}</span>
    </NavLink>
  )
}

/**
 * `/mappings` with nothing selected lands on the overview.
 *
 * It used to open whichever mapping happened to be first, which was better than an empty pane beside
 * a populated sidebar and worse than an answer: "the first one alphabetically" is not a thing anyone
 * asked for. The overview is what the section is *about* — which tables are mapped and which are not
 * — and it is equally right for a replication with forty mappings and one with none.
 *
 * `replace`, so Back leaves the tab rather than bouncing off the redirect.
 */
export function MappingsIndex() {
  const { base } = useOutletContext<MappingsOutletContext>()
  return <Navigate to={`${base}/overview`} replace />
}

/** The editor, for both `/mappings/new` and `/mappings/:mappingName`. */
export function MappingEditorRoute() {
  const { replicationName, base } = useOutletContext<MappingsOutletContext>()
  const { mappingName } = useParams<{ mappingName: string }>()
  const navigate = useNavigate()

  const { data: existing, isLoading } = useTableMapping(replicationName, mappingName)
  if (mappingName && isLoading) return <div className="empty">Loading…</div>

  return (
    <TableMappingForm
      // Remounts on selection so the form's own draft state starts from the right mapping.
      key={mappingName ?? 'new'}
      replicationName={replicationName}
      existing={existing}
      base={base}
      // A save can rename, and a create names something that had no route a moment ago — so the URL
      // follows what was actually saved rather than what was open.
      onSaved={(savedName) => navigate(`${base}/${encodeURIComponent(savedName)}`, { replace: true })}
      onRemoved={() => navigate(base, { replace: true })}
      onCancel={() => navigate(base)}
    />
  )
}

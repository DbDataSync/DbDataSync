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
      {name}
      <span className="meta">{data ? `${data.columnMappings.length} cols` : '…'}</span>
    </NavLink>
  )
}

/**
 * `/mappings` with nothing selected. Opens the first one — the list is the navigation, so landing on
 * an empty pane beside a populated sidebar would just be a click nobody wanted to make. `replace`, so
 * Back leaves the tab rather than bouncing off the redirect.
 */
export function MappingsIndex() {
  const { names, base } = useOutletContext<MappingsOutletContext>()

  if (names === undefined) return <div className="empty">Loading…</div>
  if (names.length === 0) return <div className="empty">No mappings yet — add one.</div>

  return <Navigate to={`${base}/${encodeURIComponent(names[0])}`} replace />
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
      // A save can rename, and a create names something that had no route a moment ago — so the URL
      // follows what was actually saved rather than what was open.
      onSaved={(savedName) => navigate(`${base}/${encodeURIComponent(savedName)}`, { replace: true })}
      onRemoved={() => navigate(base, { replace: true })}
      onCancel={() => navigate(base)}
    />
  )
}

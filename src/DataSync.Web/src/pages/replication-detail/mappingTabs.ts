import type { SubTab } from '../../components/SubTabs'

/**
 * The mapping editor's tabs. Notes is the index — no segment — so a link to a mapping is just the
 * mapping's URL.
 *
 * Preview SQL and Verify are `to` rather than `path`: those routes already existed, beside the editor
 * rather than inside it, deliberately — both are about the mapping *as saved*, which is not what an
 * editor holding unsaved changes is showing. They become tabs without moving, so the URLs that were
 * being linked to still work and the distinction survives.
 */
export function mappingTabs(base: string, mappingName: string | undefined, pendingSteps: number): SubTab[] {
  const tabs: SubTab[] = [
    { path: null, label: 'Notes', testId: 'mapping-tab-notes' },
    { path: 'columns', label: 'Column Mapping', testId: 'mapping-tab-columns' },
    { path: 'transforms', label: 'Custom Transforms', testId: 'mapping-tab-transforms' },
    { path: 'segmenting', label: 'Reload Segmenting', testId: 'mapping-tab-segmenting' },
    { path: 'provisioning', label: 'Provisioning', testId: 'mapping-tab-provisioning', badge: pendingSteps },
  ]

  // Both need a saved mapping to be about. An unsaved one has nothing to preview and nothing to
  // compare, and offering the tabs anyway would be two dead ends on a screen somebody is mid-way
  // through filling in.
  if (mappingName) {
    const encoded = encodeURIComponent(mappingName)
    tabs.push(
      { path: 'preview', label: 'Preview SQL', testId: 'mapping-tab-preview', to: `${base}/${encoded}/preview` },
      { path: 'verification', label: 'Verify', testId: 'mapping-tab-verify', to: `${base}/${encoded}/verification` },
    )
  }

  return tabs
}

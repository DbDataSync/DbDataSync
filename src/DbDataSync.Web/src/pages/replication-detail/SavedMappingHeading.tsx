import { EndpointSidePair } from '../../components/EndpointSidePair'
import { MappingSide } from './MappingSide'
import { SourceFilterCard } from './SourceFilterCard'
import { useReplication, useTableMapping } from '../../api/hooks'

/**
 * The Source/Target pair and the source filter, for the two routes that sit *beside* the mapping
 * editor rather than inside it — Preview SQL and Verify.
 *
 * Every tab reached through the editor's `Outlet` sees this heading without having to re-establish
 * what the mapping connects. These two do not go through that `Outlet` — deliberately, since they are
 * about the mapping *as saved* — and so they used to open on a bare name and a one-line note, with no
 * way to see which two tables the screen was even about.
 *
 * **The same components the editor renders, not a read-only retelling of them.** A second
 * presentation of the same facts is a second thing to keep true. What differs is that nothing here
 * can be edited: the fieldset is disabled, so the controls are the familiar ones and it is visible at
 * a glance that this screen shows the mapping rather than changes it.
 */
export function SavedMappingHeading({ replicationName, mappingName }: {
  replicationName: string
  mappingName: string | undefined
}) {
  const { data: mapping } = useTableMapping(replicationName, mappingName)
  const { data: task } = useReplication(replicationName)

  const source = mapping?.sources[0]
  const target = mapping?.targets[0]
  if (!source || !target) return null

  return (
    <fieldset disabled style={{ border: 'none', padding: 0, margin: 0, display: 'flex', flexDirection: 'column', gap: 14 }}>
      <EndpointSidePair
        source={
          <MappingSide
            side="source"
            label="Source"
            inherited={task?.endpoints?.source ?? null}
            spec={source}
            onChange={noop}
            testIdPrefix="source"
          />
        }
        target={
          <MappingSide
            side="target"
            label="Target"
            inherited={task?.endpoints?.target ?? null}
            spec={target}
            onChange={noop}
            testIdPrefix="target"
            allowNewTable
          />
        }
      />

      <SourceFilterCard filter={source.filter ?? null} onChange={noop} />
    </fieldset>
  )
}

/** Nothing here writes: the fieldset above already stops the controls from firing these. */
function noop() {}

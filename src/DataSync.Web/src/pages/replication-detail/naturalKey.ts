import type { ParameterDescriptor } from '../../api/types'

/** The writer Kind that versions rows rather than replacing them. */
export const SCD2_WRITER = 'Scd2'

/** Its one declared setting, and the reason phase 68 exists. */
export const NATURAL_KEY = 'naturalKey'

/**
 * Whether a natural key is a question *this* writer Kind has.
 *
 * String-matched on the Kind, the same way the Pipeline tab's SCD2-delete-blind warning already is —
 * "which columns identify a row across its versions" is a property of what SCD Type 2 means, not
 * something a generic writer declaration can be asked.
 */
export const versionsRows = (writerKind: string | undefined) => writerKind === SCD2_WRITER

/**
 * The declared settings with `naturalKey` taken out, for a form that is rendering it some other way.
 *
 * Both Pipeline tabs do — the replication's replaces it with "auto-derived from each mapping's
 * primary key", and a mapping's with the derived columns and an override toggle — so the filter is
 * here rather than written twice with two chances to disagree about the field's name.
 */
export const withoutNaturalKey = (parameters: ParameterDescriptor[]) =>
  parameters.filter((p) => p.name !== NATURAL_KEY)

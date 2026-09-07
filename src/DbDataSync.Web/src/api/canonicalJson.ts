/**
 * A value serialised so that two structurally equal values produce the same string regardless of
 * how their object keys are ordered.
 *
 * `JSON.stringify` walks objects in insertion order, so a form control that rebuilds a value with a
 * spread — `{ ...spec, filter }` — can reorder keys without changing meaning, and a naive
 * draft-vs-saved string comparison then reads that as an edit. Sorting keys at every level removes
 * that false signal. `undefined` members are dropped (as `JSON.stringify` already does); `null` is
 * kept, because clearing a field to null is a real change.
 *
 * This is for change detection, not for anything the server parses — the ordering it imposes is not
 * a wire format.
 */
export function canonicalJson(value: unknown): string {
  return JSON.stringify(sortKeys(value))
}

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeys)
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .filter(([, v]) => v !== undefined)
        .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))
        .map(([k, v]) => [k, sortKeys(v)]),
    )
  }
  return value
}

/**
 * Splitting a `driver.yaml` into "the fields this form has structured controls for" (id, displayName,
 * library, base, jdbc, capabilities) and "everything else" (dialect, typeMap, metadataQueries — edited
 * as one raw block, per `driver-yaml-authoring-ui.md`'s own simplification: those three are tightly
 * related "how this engine's SQL looks" concerns a vendor-familiar operator would naturally edit
 * together, and splitting metadata-queries out as its own structured control would need parsing the
 * raw dialect text to know whether `catalog: query` is already set — real complexity a v1 doesn't need).
 * <para>
 * **Not a YAML parser.** Top-level keys are found by "starts at column 0, matches `key:`" — reliable
 * for this app's own generated shape (every writer in this codebase produces exactly that), not a
 * general-purpose YAML reader. A hand-authored file that puts a top-level key's first value on the same
 * line with unusual spacing, or otherwise deviates from that shape, may not split cleanly — the raw
 * editor is always there as the escape hatch if a load doesn't look right.
 * </para>
 */

const STRUCTURED_KEYS = new Set(['id', 'displayName', 'library', 'base', 'jdbc', 'capabilities'])

interface TopLevelBlock {
  key: string
  block: string
}

function splitTopLevelBlocks(yaml: string): TopLevelBlock[] {
  const lines = yaml.split('\n')
  const blocks: TopLevelBlock[] = []
  let current: { key: string; lines: string[] } | null = null

  for (const line of lines) {
    const match = /^([A-Za-z_][A-Za-z0-9_]*):/.exec(line)
    if (match) {
      if (current) blocks.push({ key: current.key, block: current.lines.join('\n') })
      current = { key: match[1], lines: [line] }
    } else if (current) {
      current.lines.push(line)
    }
  }
  if (current) blocks.push({ key: current.key, block: current.lines.join('\n') })
  return blocks
}

/** A scalar value from a `key: value` line, at any indent — `id`/`displayName`/`library` sit at column
 * 0, `driverClass` sits indented inside the `jdbc:` block, so this has to allow leading whitespace
 * rather than anchor to column 0 the way `splitTopLevelBlocks`' own top-level-key detection does. */
function scalarValue(yaml: string, key: string): string | undefined {
  const match = new RegExp(`^\\s*${key}:\\s*(.+)$`, 'm').exec(yaml)
  return match?.[1]?.trim().replace(/^["']|["']$/g, '')
}

/** A flow-style list (`key: [a, b, c]`) — every list this form itself ever writes is flow-style. */
function listValue(yaml: string, key: string): string[] {
  const match = new RegExp(`^\\s*${key}:\\s*\\[([^\\]]*)\\]`, 'm').exec(yaml)
  if (!match) return []
  return match[1].split(',').map((s) => s.trim().replace(/^["']|["']$/g, '')).filter(Boolean)
}

export type Base = 'adonet' | 'jdbc'

export interface ParsedDriverYaml {
  id: string
  displayName: string
  base: Base
  library: string
  driverClass: string
  driverJarPaths: string[]
  readers: string[]
  staging: string[]
  writers: string[]
  /** dialect + typeMap + metadataQueries, verbatim — everything not in `STRUCTURED_KEYS`. */
  rawBody: string
}

export function parseDriverYaml(yaml: string): ParsedDriverYaml {
  const blocks = splitTopLevelBlocks(yaml)
  const byKey = new Map(blocks.map((b) => [b.key, b.block]))
  const isJdbc = byKey.has('base') && (byKey.get('base') ?? '').includes('JdbcGenericDriver')
  const jdbcBlock = byKey.get('jdbc') ?? ''

  return {
    id: scalarValue(yaml, 'id') ?? '',
    displayName: scalarValue(yaml, 'displayName') ?? '',
    base: isJdbc ? 'jdbc' : 'adonet',
    library: scalarValue(yaml, 'library') ?? '',
    driverClass: scalarValue(jdbcBlock, 'driverClass') ?? '',
    driverJarPaths: listValue(jdbcBlock, 'driverJarPaths'),
    readers: listValue(byKey.get('capabilities') ?? '', 'readers'),
    staging: listValue(byKey.get('capabilities') ?? '', 'staging'),
    writers: listValue(byKey.get('capabilities') ?? '', 'writers'),
    rawBody: blocks.filter((b) => !STRUCTURED_KEYS.has(b.key)).map((b) => b.block).join('\n').trim(),
  }
}

/** The skeleton a brand-new driver's raw body starts from — enough of a shape to edit rather than a
 * blank page, matching every other block-style dialect this repo's own `driver.yaml`s use. */
export const RAW_BODY_SKELETON = `dialect:
  quoteIdentifier: doubleQuote
  parameterPrefix: "@"
  rowLimit: limitOffset
typeMap:
  int: Int32`

export function assembleDriverYaml(form: {
  id: string
  displayName: string
  base: Base
  library: string
  driverClass: string
  driverJarPaths: string[]
  readers: string[]
  staging: string[]
  writers: string[]
  rawBody: string
}): string {
  const lines: string[] = [
    `id: ${form.id}`,
    `displayName: ${form.displayName}`,
    `library: ${form.base === 'jdbc' ? 'ikvm' : form.library}`,
  ]

  if (form.base === 'jdbc') {
    lines.push('base: DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc')
    lines.push('jdbc:')
    lines.push(`  driverClass: ${form.driverClass}`)
    lines.push(`  driverJarPaths: [${form.driverJarPaths.join(', ')}]`)
  }

  lines.push(form.rawBody)

  lines.push('capabilities:')
  lines.push(`  readers: [${form.readers.join(', ')}]`)
  lines.push(`  staging: [${form.staging.join(', ')}]`)
  lines.push(`  writers: [${form.writers.join(', ')}]`)

  return lines.join('\n') + '\n'
}

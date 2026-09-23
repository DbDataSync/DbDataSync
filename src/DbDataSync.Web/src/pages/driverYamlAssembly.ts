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

/** The indented lines under a bare `key:` block header inside `text` (the header itself excluded) —
 * `connectionStringKeys:` nested inside a `jdbc:` block, in the same "starts at some indent, matches
 * key:" spirit `splitTopLevelBlocks` already uses for the top level. Empty string if `key` isn't
 * present as a block header (a scalar `key: value` line doesn't match — the trailing `\s*$` requires
 * nothing after the colon). */
function nestedBlock(text: string, key: string): string {
  const lines = text.split('\n')
  const headerIndex = lines.findIndex((l) => new RegExp(`^\\s*${key}:\\s*$`).test(l))
  if (headerIndex === -1) return ''
  const headerIndent = /^\s*/.exec(lines[headerIndex])![0].length
  const body: string[] = []
  for (let i = headerIndex + 1; i < lines.length; i++) {
    const line = lines[i]
    if (line.trim() === '') { body.push(line); continue }
    if (/^\s*/.exec(line)![0].length <= headerIndent) break
    body.push(line)
  }
  return body.join('\n')
}

/** `text` with the entire `key:` block (header line plus its indented body) removed — the complement
 * of `nestedBlock`, for splicing a nested block back out of a larger passage (`jdbcExtra`'s own use:
 * `connectionStringKeys` now has a structured field, so it must not also appear in the raw leftovers). */
function withoutNestedBlock(text: string, key: string): string {
  const lines = text.split('\n')
  const headerIndex = lines.findIndex((l) => new RegExp(`^\\s*${key}:\\s*$`).test(l))
  if (headerIndex === -1) return text
  const headerIndent = /^\s*/.exec(lines[headerIndex])![0].length
  let end = headerIndex + 1
  while (end < lines.length) {
    const line = lines[end]
    if (line.trim() === '') { end++; continue }
    if (/^\s*/.exec(line)![0].length <= headerIndent) break
    end++
  }
  return [...lines.slice(0, headerIndex), ...lines.slice(end)].join('\n')
}

export type Base = 'adonet' | 'jdbc'

/** Phase 179N. Blank means "use `JdbcGenericDriver.DefaultConnectionStringKeys`" for every field except
 * `integratedSecurity`, which has no default key at all (unset unless an operator sets one) — this form
 * has no field for it, so it only ever survives via `jdbcExtra`. */
export interface JdbcConnectionStringKeysForm {
  host: string
  port: string
  database: string
  username: string
  password: string
  connectTimeout: string
}

const EMPTY_CONNECTION_STRING_KEYS: JdbcConnectionStringKeysForm = {
  host: '', port: '', database: '', username: '', password: '', connectTimeout: '',
}

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
  /** Phase 179N — the JDBC `UrlTemplate`, e.g. `jdbc:postgresql://{host}:{port}/{database}`. Empty
   * means "no template set" (the pre-178N contract: `ConnectionConfig.ConnectionString` must carry the
   * whole URL). */
  urlTemplate: string
  /** Phase 179N — per-field overrides of `JdbcGenericDriver.DefaultConnectionStringKeys`. */
  connectionStringKeys: JdbcConnectionStringKeysForm
  /**
   * Phase 179N — lines from the `connectionStringKeys:` block that aren't one of this form's six known
   * keys (`integratedSecurity` is the only real example today — see `JdbcConnectionStringKeysForm`'s own
   * doc comment). Kept separate from `jdbcExtra` (rather than embedding a second `connectionStringKeys:`
   * header inside it) specifically so `assembleDriverYaml` can merge these lines with the structured
   * fields' own under **one** header — two separate `connectionStringKeys:` blocks in the same `jdbc:`
   * map would be a real, duplicate-key correctness bug the moment both are populated at once (a
   * structured `username` plus a hand-authored `integratedSecurity`, say).
   */
  connectionStringKeysExtra: string
  /**
   * Phase 178N. Whatever the `jdbc:` block's own lines were, verbatim, minus the ones the structured
   * fields above already own (`driverClass`/`driverJarPaths`/`urlTemplate`, and the whole
   * `connectionStringKeys:` block — see `connectionStringKeysExtra` for what survives of that one) — a
   * hand-authored key this form still has no field for at all round-trips through an edit-and-save cycle
   * unchanged. See follow-up-jdbc-url-template-unreachable-from-driver-yaml.md part 2 — before this,
   * saving any other field on a JDBC driver silently stripped everything not already structured.
   */
  jdbcExtra: string
}

const JDBC_OWNED_KEYS = /^\s*(driverClass|driverJarPaths|urlTemplate):/
const KNOWN_CONNECTION_STRING_KEYS = /^\s*(host|port|database|username|password|connectTimeout):/

export function parseDriverYaml(yaml: string): ParsedDriverYaml {
  const blocks = splitTopLevelBlocks(yaml)
  const byKey = new Map(blocks.map((b) => [b.key, b.block]))
  const isJdbc = byKey.has('base') && (byKey.get('base') ?? '').includes('JdbcGenericDriver')
  const jdbcBlock = byKey.get('jdbc') ?? ''
  const connectionStringKeysBlock = nestedBlock(jdbcBlock, 'connectionStringKeys')
  const connectionStringKeysExtra = connectionStringKeysBlock
    .split('\n')
    .filter((line) => line.trim() !== '' && !KNOWN_CONNECTION_STRING_KEYS.test(line))
    .join('\n')

  // The block's own first line is `jdbc:` itself (see splitTopLevelBlocks) — never part of "extra".
  // connectionStringKeys is removed as a whole block (it's multi-line and now has its own structured
  // field, plus connectionStringKeysExtra above for whatever that doesn't cover), then the remaining
  // single-line owned keys are filtered out.
  const jdbcExtra = withoutNestedBlock(jdbcBlock.split('\n').slice(1).join('\n'), 'connectionStringKeys')
    .split('\n')
    .filter((line) => !JDBC_OWNED_KEYS.test(line))
    .join('\n')

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
    urlTemplate: scalarValue(jdbcBlock, 'urlTemplate') ?? '',
    connectionStringKeys: connectionStringKeysBlock
      ? {
          host: scalarValue(connectionStringKeysBlock, 'host') ?? '',
          port: scalarValue(connectionStringKeysBlock, 'port') ?? '',
          database: scalarValue(connectionStringKeysBlock, 'database') ?? '',
          username: scalarValue(connectionStringKeysBlock, 'username') ?? '',
          password: scalarValue(connectionStringKeysBlock, 'password') ?? '',
          connectTimeout: scalarValue(connectionStringKeysBlock, 'connectTimeout') ?? '',
        }
      : EMPTY_CONNECTION_STRING_KEYS,
    connectionStringKeysExtra,
    jdbcExtra,
  }
}

/** The skeleton a brand-new driver's raw body starts from — enough of a shape to edit rather than a
 * blank page, matching every other block-style dialect this repo's own `driver.yaml`s use. The typeMap
 * here is lifted from the bundled `mysql.generic.driver.yaml` resource — a real, complete example
 * covering every shape `TypeMapEntryYaml`'s DSL actually has (a bare scalar, the `(p,s)`-placeholder
 * substitution form, a `{ kind, length, unicode }` object, and a `{ kind, max }` object) rather than the
 * single `int: Int32` line this used to be, plus a commented reminder of the canonical kinds that
 * example doesn't happen to use. Every native type name on the left still has to be edited for whatever
 * engine is actually being described — the point is giving a real shape to edit, not a finished map. */
export const RAW_BODY_SKELETON = `dialect:
  quoteIdentifier: doubleQuote        # doubleQuote | backtick | bracket
  parameterPrefix: "@"                # "@" -> @p , ":" -> :p , "?" -> positional
  rowLimit: limitOffset               # limitOffset (LIMIT n OFFSET m) | offsetFetch (OFFSET..FETCH)
  # catalog: query                    # uncomment (+ a metadataQueries block) if information_schema
                                       # doesn't fit this engine

# Native type name (with its (p,s) args) -> canonical. Anything unlisted -> Unmappable, which
# provisioning reports as unsupported rather than guessing a rendering. Edit the native names on the
# left for the engine this driver is actually for — these are MySQL's, as a worked example.
typeMap:
  tinyint:        Int8
  smallint:       Int16
  int:            Int32
  bigint:         Int64
  "decimal(p,s)": { kind: Decimal, precision: p, scale: s }
  double:         Double
  "varchar(n)":   { kind: String, length: n, unicode: true }
  text:           { kind: String, max: true }
  datetime:       Timestamp
  date:           Date
  json:           Json
  blob:           { kind: Binary, max: true }
  # Canonical kinds this example doesn't use — add if this engine needs them:
  # bit:               Boolean
  # real:              Float
  # time:              Time
  # timestamptz:       TimestampTz
  # uniqueidentifier:  Guid
  # xml:               Xml`

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
  urlTemplate?: string
  connectionStringKeys?: JdbcConnectionStringKeysForm
  connectionStringKeysExtra?: string
  jdbcExtra?: string
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
    // Emitted only for a value the operator actually typed — a blank field must stay omitted, not
    // become `host: ""` (a real, different value from "not set": it would send an empty JDBC property
    // key, not "apply JdbcGenericDriver's own default").
    if (form.urlTemplate) lines.push(`  urlTemplate: "${form.urlTemplate}"`)
    // Structured fields and connectionStringKeysExtra's own leftover lines (a key this form has no
    // input for, e.g. integratedSecurity) merge under **one** connectionStringKeys: header — never two,
    // which would be a duplicate YAML key the moment both are populated at once.
    const structuredKeyLines = form.connectionStringKeys
      ? (Object.entries(form.connectionStringKeys) as [string, string][])
          .filter(([, value]) => value)
          .map(([key, value]) => `    ${key}: ${value}`)
      : []
    const extraKeyLines = (form.connectionStringKeysExtra ?? '').split('\n').filter((line) => line.trim() !== '')
    const keyLines = [...structuredKeyLines, ...extraKeyLines]
    if (keyLines.length > 0) {
      lines.push('  connectionStringKeys:')
      lines.push(...keyLines)
    }
    if (form.jdbcExtra) lines.push(form.jdbcExtra)
  }

  lines.push(form.rawBody)

  lines.push('capabilities:')
  lines.push(`  readers: [${form.readers.join(', ')}]`)
  lines.push(`  staging: [${form.staging.join(', ')}]`)
  lines.push(`  writers: [${form.writers.join(', ')}]`)

  return lines.join('\n') + '\n'
}

/**
 * Phase 180N. Whether `yaml` splits into structured fields and reassembles back to itself, byte for
 * byte — the mechanical test for whether the structured editor is safe to use on this file at all. Every
 * driver.yaml this app itself has ever written passes; one edited by hand into a shape
 * `parseDriverYaml`'s own doc comment already admits it can't split cleanly (unusual spacing, a
 * differently-placed comment, a key this form doesn't model) does not — and that's the whole point: a
 * `false` here is the signal to open in raw mode instead of silently reinterpreting the file.
 */
export function roundTripsCleanly(yaml: string): boolean {
  return assembleDriverYaml(parseDriverYaml(yaml)) === yaml
}

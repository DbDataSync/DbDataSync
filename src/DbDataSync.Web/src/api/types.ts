// Mirrors the DbDataSync.Api DTOs exactly (see src/DbDataSync.Core/Config/*.cs and
// src/DbDataSync.Api/Controllers/*.cs). Hand-written rather than generated: the author of both sides
// already knows the exact contract, and a codegen step would add a moving part (fetching a live
// OpenAPI doc) without reducing risk here.

// Was a closed union of the three built-ins; now a plain string so a driver installed at runtime
// (a YAML descriptor or a compiled plugin) is just another value of it — see `GET /api/drivers`
// (added in phase 109d), which is what the connection editor's picker will read from instead of a
// hardcoded list.
export type DriverType = string
/** What DbDataSync supplies when connecting. `None` is what a wallet, a DSN with stored credentials, a
 * .pgpass file or a credential-bearing URL all look like from here — the address or the environment
 * provides it and DbDataSync passes nothing. */
export type AuthMode = 'SqlAuth' | 'IntegratedAuth' | 'None'

export interface ConnectionConfig {
  name: string
  driverType: DriverType
  host: string | null
  port: number | null
  /** The engine-native address, when host and port cannot express it. Never carries a credential. */
  connectionString: string | null
  database: string | null
  authMode: AuthMode
  userId: string | null
  credentialSecretRef: string | null
  /** Seconds to wait for the connection to open. Null means the default (30); 0 means no limit. */
  connectTimeoutSeconds: number | null
  /** Seconds any one query may run. Null means the default (1800); 0 means no limit. */
  commandTimeoutSeconds: number | null
  properties: Record<string, string>
  scripts?: ScriptBindings
  hooks?: Hooks
}

export interface ConnectionInput {
  name: string
  driverType: DriverType
  host?: string | null
  port?: number | null
  connectionString?: string | null
  database?: string | null
  authMode: AuthMode
  userId?: string | null
  password?: string | null
  connectTimeoutSeconds?: number | null
  commandTimeoutSeconds?: number | null
  properties?: Record<string, string>
  scripts?: ScriptBindings
  hooks?: Hooks
}

/**
 * What a mapping's next pass is meant to do — see phase 100. Named "intent" rather than "state"
 * deliberately: a config setting or an operator can only ever ask for a pass, never assert that one
 * already happened.
 */
export type ReadIntent = 'InitialLoad' | 'Changes' | 'ChangesFromEarliest' | 'ChangesFromLatest'

/** Why a mapping's next scheduled `Primary` pass is not going to run — a reason, never a fifth
 * `ReadIntent`. See phase 100/101. */
export type ReadHold = 'None' | 'PositionExpired' | 'Paused'

/** One mapping's read intent and hold, resolved — never the raw absence of a stored row. See phase 100. */
export interface MappingReadState {
  intent: ReadIntent
  hold: ReadHold
  /** Null for a mapping that has an intent/hold stored but has not yet completed a pass under it. */
  watermark: string | null
  watermarkTimeUtc: string | null
}

/** Both required, deliberately: recovering from a hold sets the intent and clears the hold as one
 * call, so there is never a window where the hold is gone and the old intent is still what the next
 * pass would honour. See phase 100. */
export interface SetMappingReadStateRequest {
  intent: ReadIntent
  hold: ReadHold
}

export type ScheduleMode = 'Continuous' | 'Periodic'

export interface SchedulingConfig {
  mode: ScheduleMode
  frequencySeconds: number | null
  /** How long a continuous worker keeps looking and finding nothing before it exits. Null means the
   * server's default of 60 seconds. Must be longer than the frequency when it is set, or the worker
   * gives up before it has looked even once. */
  idleTimeoutSeconds?: number | null
  cronExpression: string | null
}

export interface ReaderConfig {
  kind: string
  options: Record<string, string>
}

export interface CacheConfig {
  kind: string
  options: Record<string, string>
}

export interface WriterConfig {
  kind: string
  options: Record<string, string>
}

export interface ChangeProcessingConfig {
  reader: ReaderConfig
  cache: CacheConfig
  writer: WriterConfig
  /** Consumers on the worker's change-processing lane — how many incremental (Primary) passes run at
   * once. The server defaults it to 4 when config says nothing. */
  degreeOfParallelism: number
  /** Consumers on the worker's backfill lane — backfill segments and verifications. Its own budget so
   * a large reload never takes a slot an incremental pass needs. Defaults to 4. */
  backfillDegreeOfParallelism: number
}

// Where a replication reads from and writes to. Every table mapping inherits these unless it sets
// its own — see TableSpec.
export interface EndpointRef {
  connectionName: string | null
  database: string | null
}

export interface TaskEndpoints {
  source: EndpointRef | null
  target: EndpointRef | null
}

export interface ReplicationTaskConfig {
  name: string
  enabled: boolean
  scheduling: SchedulingConfig
  changeProcessing: ChangeProcessingConfig
  endpoints: TaskEndpoints
  scripts?: ScriptBindings
  hooks?: Hooks
  /** What every mapping under this replication may do to its target, unless the mapping overrides. */
  provisioning?: ProvisioningConfig
  /** Automated delete reconciliation for every mapping under this replication, unless a mapping
   * overrides it (`TableMappingConfig.reconcileOverride`) — phase 125, built on phase 124's on-demand
   * sweep. Always present (never optional) — a fresh replication gets a disabled one, the same way
   * `provisioning` above always has a value even when nobody has touched it. */
  reconcile: ReconcileConfig
  /**
   * Named segmenting strategies any of this replication's mappings may reference. At the replication
   * because a set of tables replicated together usually segments the same way.
   */
  segmentingStrategies?: SegmentingStrategyConfig[]
  /** Markdown, git-tracked. What the next person needs to know about this replication. */
  notes?: string | null
  /** What every table mapping under this replication reads next, unless the mapping says otherwise.
   * Null means nobody has said, which resolves to `InitialLoad` — see phase 100/102. */
  defaultReadIntent?: ReadIntent | null
}

/** One side of a table mapping as configured: null connection/database inherit the replication's
 * endpoint, set values override it. Each falls back independently. */
export interface TableSpec {
  connectionName: string | null
  database: string | null
  schema: string
  table: string
}

export interface SourceTableSpec extends TableSpec {
  filter: string | null
}

/** A mapping side with its endpoint resolved — what the metadata pickers and the drivers work from. */
export interface ResolvedRef {
  connectionName: string
  database: string
  schema: string
  table: string
}

export interface ColumnMapping {
  sourceColumn: string
  targetColumn: string
  transform: string | null

  /**
   * The target type an operator chose, in the target's own dialect. **Null is the normal case** —
   * the type is inferred from the source's, and writing that inference down would freeze today's
   * answer against a source column that later changes. See `InferredColumnType`.
   */
  targetType?: string | null

  /**
   * Every rename this target column has been through, oldest first. Absent for the columns nobody
   * has renamed, which is nearly all of them.
   */
  renames?: RenameStep[]
}

/** One rename of a target column. `applied` records whether provisioning has run it. */
export interface RenameStep {
  from: string
  to: string
  applied: boolean
}

/**
 * What a source column would become on the target if nobody overrode it. Computed server-side
 * because the canonical type system lives there: the SPA has no way to know that a SQL Server
 * `nvarchar(50)` lands as a Postgres `varchar(50)`.
 */
/**
 * What the SCD Type 2 writer's natural key would be for a mapping if nobody stated one — derived from
 * the source's primary key, said in the target's column names.
 */
export interface InferredNaturalKey {
  /** Empty when nothing could be derived, in which case `problem` says why. */
  columns: string[]
  problem: string | null
}

export interface InferredColumnType {
  sourceColumn: string
  sourceType: string
  targetType: string | null
  /** What the translation approximates, when it does. */
  fidelity: string | null
  /** Why there is no inferred type. Never set alongside `targetType`. */
  problem: string | null
}

/**
 * What DbDataSync may do to a target's shape without being asked.
 *
 * Null at either level means "nobody here has said": on a mapping that inherits the replication's
 * answer, on a replication it resolves to off. Nullable is also what lets an explicit `false` survive
 * being saved — see phase 46.
 */
export interface ProvisioningConfig {
  createTargetTableIfMissing: boolean | null
  /** Schema evolution for a table that already exists. Additive and modifying only, never DROP. */
  alterTargetTableColumnsIfMissingOrChanged: boolean | null
}

// Phase 125 — automated delete reconciliation, built on phase 124's on-demand KeyReconcile sweep.

/** Whether a mapping's reconcile sweep should also fire in response to changes a Primary pass just
 * found, rather than only on its own `every` cadence. The mode picker's whole option set — the next
 * variant lands with its own use case, the same way `DeleteGuard`'s does. */
export type AfterChangeStrategy =
  | { mode: 'none' }
  | { mode: 'any' }

/** The sanity check a scheduled (or on-demand, via `overrideGuard`) sweep runs before committing a
 * delete: how much of a segment's rows may be removed before the writer refuses instead. */
export type DeleteGuard =
  | { mode: 'none' }
  | { mode: 'ratio'; maxRatio: number }

export interface ReconcileConfig {
  enabled: boolean
  /** How often a sweep runs on its own, independent of any change activity — reuses `SchedulingConfig`
   * wholesale, so the same editor and the same due-ness rules already used for a replication's own
   * schedule apply here too. Null means no cadence at all — a sweep only runs from `afterChange`, or
   * an operator's own on-demand trigger. */
  every: SchedulingConfig | null
  afterChange: AfterChangeStrategy
  deleteGuard: DeleteGuard
  /** Null means the only real answer for each — `KeyReconcile`/`StagingTable`/`KeyReconcileDelete` —
   * present for the same structural reason `ChangeProcessingConfig`'s stages carry `options`, not
   * because a different Kind is meaningful here. */
  reader?: ReaderConfig | null
  cache?: CacheConfig | null
  writer?: WriterConfig | null
}

export interface TableMappingConfig {
  name: string
  sources: SourceTableSpec[]
  targets: TableSpec[]
  columnMappings: ColumnMapping[]
  scripts?: ScriptBindings
  hooks?: Hooks
  provisioning?: ProvisioningConfig
  /** This mapping's own delete-reconciliation settings, in place of the replication's. Null inherits
   * `ReplicationTaskConfig.reconcile` entirely — phase 125. */
  reconcileOverride?: ReconcileConfig | null
  verification?: VerificationCheckConfig[]
  /** How this table divides for a reload. Empty means Full — the whole table, unsegmented. */
  defaultSegmenting?: BatchReloadSegment[]
  /** Records how long each stage of a pass took, onto the run itself. Off means not measured at all. */
  traceTiming?: boolean
  /** Markdown, git-tracked. Not inherited from the replication — a note that applied to every mapping
   * would be a note about the replication, and that field exists too. */
  notes?: string | null

  /**
   * This mapping's own pipeline stages, in place of the replication's. Null or absent inherits the
   * replication's stage entirely — Kind and options together, never merged (phase 68).
   *
   * Each is independent: a mapping can override just the writer and still inherit reader and cache.
   */
  readerOverride?: ReaderConfig | null
  cacheOverride?: CacheConfig | null
  writerOverride?: WriterConfig | null

  /**
   * The source and target tables' shape as of the last capture — phase 90.
   *
   * **Nothing reads these yet.** Every reader, writer and staging provider still queries the live
   * catalog on every pass, exactly as before; this phase builds the cache and the action that
   * refreshes it, and switching consumers onto it is a behaviour change with its own later phase.
   *
   * Written by the editor from the column lists it already fetched to draw its pickers, and only
   * when a side's table changed or nothing was cached yet — never on an ordinary re-save. The
   * server enforces both halves of that; see `MappingMetadataCapture`.
   */
  sourceColumns?: ColumnMetadata[]
  targetColumns?: ColumnMetadata[]
  /** When the two lists above were last written. Null for a mapping nobody has captured. */
  columnsCapturedUtc?: string | null
  /** What this mapping reads next, in place of the replication's `defaultReadIntent`. Null means
   * inherit. See phase 100/102. */
  defaultReadIntent?: ReadIntent | null
}

export interface TableMetadata {
  schema: string
  table: string
}

export interface ColumnMetadata {
  name: string
  nativeType: string
  isNullable: boolean
  isPrimaryKey: boolean
  isIdentity: boolean
}

/**
 * What a metadata refresh did to one side's cache.
 *
 * Columns are named rather than counted, because "show the operator what changed" is the whole
 * difference between this and a silent update — a card reporting "1 changed" leaves them to go and
 * find which one.
 */
export interface MetadataRefreshSide {
  side: 'source' | 'target'
  /** False when the side could not be read — `unavailable` says why, and its cache is untouched. */
  refreshed: boolean
  unavailable: string | null
  columnCount: number
  added: string[]
  removed: string[]
  changed: string[]
}

export interface MetadataRefreshResult {
  /** The mapping as saved, so a client that just refreshed need not re-fetch to see the cache. */
  mapping: TableMappingConfig
  source: MetadataRefreshSide
  target: MetadataRefreshSide
}

/** A script bound to a slot. Name and parameters are replaced together by the most specific level
 * that sets them — never merged, so reading one level tells you what runs. */
export interface ScriptBinding {
  scriptName: string
  parameters: Record<string, string>
}

/** Keyed by slot. An absent key inherits from a broader level; a key present with a null value is
 * "explicitly none" and overrides an inherited binding. */
export type ScriptBindings = Record<string, ScriptBinding | null>

// Lifecycle hooks around staging and loading — see architecture/implementation/done/phase-026-lifecycle-hooks.md.
export type HookConnectionSide = 'Target' | 'Source'
export type HookErrorMode = 'Fail' | 'Warn'

/** One entry in a hook point's list — either inline SQL or a reference to a reusable named hook
 * (Hook non-null). Never both. */
export interface HookConfig {
  name: string | null
  sql: string | null
  hook: string | null
  parameters: Record<string, string>
  connection: HookConnectionSide
  onError: HookErrorMode
}

/** Keyed by point (beforeStage/afterStage/beforeLoad/afterLoad). Same inheritance rule as
 * ScriptBindings: absent inherits, present-with-null-or-empty-list is what the most specific level
 * declared, and it replaces rather than merges with a broader level's list. */
export type Hooks = Record<string, HookConfig[] | null>

/** What kind of value a parameter takes, and therefore what control it gets. A closed set: the point
 * of declaring a parameter is that this app can render it without knowing what it is for. */
export type ParameterType =
  | 'Text' | 'Number' | 'Bool' | 'Date' | 'DateTime' | 'Dropdown' | 'ColumnPicker' | 'Property' | 'Secret'
  /** A SQL statement, which `ParameterForm` renders as a real editor rather than a one-line input. */
  | 'Sql'

export interface ParameterCardinality {
  min: number
  max: number
}

/** Hints, not layout. A declaration that says nothing still renders. */
export interface ParameterLayout {
  card: string
  group: string
  /** Relative width within its group, in flex units. */
  size: number
}

/** One setting an author declared and an operator fills in — a driver's, a handler's, or a
 * script's. The server says what exists and this app renders it generically, the same relationship
 * DriverCapabilities already has with the Kind pickers. */
export interface ParameterDescriptor {
  name: string
  label: string | null
  description: string | null
  type: ParameterType
  required: boolean
  /** Null — the common case — means a single value. */
  cardinality: ParameterCardinality | null
  dropdownOptions: string[] | null
  /** How to spell each option for a person, keyed by the stored value. Null where the value already
   * reads as itself, which is most of them. */
  dropdownLabels: Record<string, string> | null
  /** What the form pre-fills. Null means empty, which is distinct from an empty string. */
  default: string | null
  layout: ParameterLayout | null

  /** Whether this parameter applies at all, given the other values. Decided by whoever declared it —
   * a driver knows that Host is beside the point in connection-string mode — so this app renders
   * what it is told rather than holding a second copy of the rule that could disagree. */
  visible: boolean

  /** Whether changing this value changes what the thing takes, and so whether the form has to ask
   * again. Declared, so a form refetches on the dropdown that matters and not on every keystroke. */
  recalc: boolean
}

export type ScriptLanguage = 'CSharp' | 'Sql'

export interface ScriptConfig {
  name: string
  /** Which extension point this implements — see GET /api/scripts/slots. */
  kind: string
  language: ScriptLanguage
  /** The type in the code implementing the slot's contract. Null for a Sql hook, which has no entry
   * point to compile. */
  entryType: string | null
  description: string | null
  parameters: ParameterDescriptor[]
  enabled: boolean
}

export interface ScriptDefinition {
  manifest: ScriptConfig
  code: string
}

/** A slot this build supports, and the binding levels it may be bound at. `metadataProvider` is
 * connection-only — metadata describes an engine, not a mapping. */
export interface ScriptSlotInfo {
  slot: string
  levels: ('connection' | 'replication' | 'mapping')[]
  /** What the slot is called in the UI — `sqlColumnExpression` is a good key and a bad label. */
  label: string
  description: string
}

/** Where a script is bound. `owner` is a connection, a replication, or "replication / mapping". */
export interface ScriptUsage {
  level: 'connection' | 'replication' | 'mapping'
  owner: string
  /** The script slot, or the hook point for a hook binding. */
  slot: string
}

/** A script as the list shows it: its manifest, and every place it is bound. */
export interface ScriptListItem {
  manifest: ScriptConfig
  usedBy: ScriptUsage[]
}

/** Where a previewed statement came from, which is the first thing to know about an unexpected one. */
export type PreviewOrigin = 'BuiltIn' | 'OperatorSql' | 'Script'

export interface PreviewStatement {
  stage: string
  title: string
  /** Null for a step that generates no SQL — an in-process transform is C# running over rows. */
  sql: string | null
  origin: PreviewOrigin
  detail: string | null
  /**
   * Declares, as variables, every runtime parameter `sql` references — each one the literal value
   * that would actually be bound right now, not the bare placeholder `sql` shows in its place. Null
   * for a statement with no runtime parameters, or on an engine with no notion of a variable outside
   * a query itself.
   */
  declaredParameters: string | null
}

export interface PreviewReport {
  statements: PreviewStatement[]
  /** Things that would stop this pass running, said plainly rather than left to be discovered. */
  problems: string[]
}

/** What a script did to one sample input. */
export interface ScriptTestCase {
  input: string
  output: string | null
  note: string | null
}

export interface ScriptTestResult {
  mode: 'generated' | 'live'
  /** Where the input came from, in words. A safety property: "generated sample" and "live query
   * against 'prod-src'" must never be confusable, because one of them touched a real system. */
  source: string
  cases: ScriptTestCase[]
  log: string[]
  statement: string | null
  error: string | null
}

export interface ScriptTestRequest {
  script: ScriptDefinition
  replicationName?: string
  mappingName?: string
  /** Set only for a live test, and only because the operator chose one. */
  connectionName?: string
  sampleRows?: number
}

export interface RunMetricsBucket {
  startUtc: string
  runs: number
  failures: number
  rowsWritten: number
}

export interface RunMetrics {
  taskName: string
  runKind: RunKind | null
  fromUtc: string
  toUtc: string
  runs: number
  failures: number
  rowsRead: number
  rowsWritten: number
  /**
   * Processing time: `endedAtUtc - startedAtUtc`, so the run itself with no queue in it. Queue time is
   * a separate figure, per run, from `enqueuedAtUtc` and `startedAtUtc` — not aggregated here.
   *
   * Null when nothing in the window finished — a run still going has no processing time — or when
   * nothing in it ever started.
   */
  processingP50Ms: number | null
  processingP95Ms: number | null
  processingMaxMs: number | null
  /** When the most recent *successful* pass finished, unbounded by the window. */
  lastCompletedPassUtc: string | null
  buckets: RunMetricsBucket[]
}

export type MetricsWindow = '1h' | '24h' | '7d'

export type VerificationCheckKind = 'RowCount' | 'Sum' | 'Sql' | 'Script'

/** One comparison between a mapping's source and its target. Columns are named by their *target*
 * names and each side's statement is derived, so an aliased column is one selection. */
export interface VerificationCheckConfig {
  name: string
  kind: VerificationCheckKind
  groupBy: string[]
  measures: string[]
  sourceSql: string | null
  targetSql: string | null
  scriptName: string | null
  parameters: Record<string, string>
  filter: string | null
  /** Compares the target's current rows only. A historized target holds more rows than its source by
   * design, so a check against one otherwise reports the feature working as a defect. */
  compareCurrentOnly?: boolean
  /** Which column marks a target row current. Null means the default for the mapping's own writer. */
  currentColumn?: string | null
  /** How far apart two measures may be before it is worth pointing at, as a fraction of the larger
   * side. Zero means any difference at all. */
  differenceThreshold: number
}

export type VerificationRowStatus = 'Match' | 'Differs' | 'MissingFromTarget' | 'MissingFromSource'

export interface VerificationResultRow {
  group: string[]
  /** Null when the group was absent from that side — distinct from a zero, which is a number
   * somebody measured. */
  source: Record<string, number> | null
  target: Record<string, number> | null
  differences: Record<string, number>
  status: VerificationRowStatus
}

/**
 * One page of a check's result.
 *
 * A page, not the result. A check over a large table produces a row per group — millions of them —
 * and this used to arrive whole: tens of megabytes of JSON and a DOM node per cell, to show a
 * screenful. The counts describe the *file*, so the pager and the summary line have something to
 * count against.
 */
export interface VerificationResultPage {
  checkName: string
  groupColumns: string[]
  measureColumns: string[]
  differenceThreshold: number
  /** Each side's read time, separately: the gap is what a difference has to be weighed against.
   * On every page, because page four needs it as much as page one. */
  sourceReadAtUtc: string
  targetReadAtUtc: string
  /** Every compared group in the file, whatever the current filter is. */
  totalRows: number
  /** Groups the two sides disagreed on, or that only one side had. */
  differingRows: number
  offset: number
  rows: VerificationResultRow[]
}

/** Where a result is, not what it says. */
export interface VerificationResultRecord {
  id: number
  runId: string
  taskName: string
  mappingName: string
  checkName: string
  completedAtUtc: string
  sourceReadAtUtc: string
  targetReadAtUtc: string
  groupsCompared: number
  differingGroups: number
  resultPath: string
}

/** What a replication's worker process is doing right now. Live only — phase 36 answers "what has
 * been happening", which is a different question. */
export interface ReplicationStatus {
  /** False is the common state, not a fault: a worker drains its queue and exits, so a replication
   * that is caught up has no process between cycles. */
  running: boolean
  pid: number | null
  memoryBytes: number | null
  cpuMilliseconds: number | null
  startedAtUtc: string | null
  /** Whether the scheduler may enqueue anything: enabled and not paused. The server's answer rather
   * than the rule, so the two gates are defined in one place — see TaskScheduling.ShouldRun. */
  shouldRun: boolean
  /** Config's durable intent, git-tracked. */
  enabled: boolean
  /** State's temporary hold. Never committed. */
  paused: boolean
  /** Why it is held, when whoever held it said. Null when not paused, and when they cleared it. */
  pauseNote: string | null
}

export interface ScriptDiagnostic {
  line: number
  column: number
  message: string
}

export interface ScriptCompileResult {
  manifest: ScriptConfig
  diagnostics: ScriptDiagnostic[]
  compiles: boolean
}

// What the registered driver behind a connection actually supports. Queried live rather than
// hardcoded per engine — see GET /api/connections/{name}/capabilities. Readers come from the
// *source* connection's driver, staging providers and writers from the *target*'s.
export interface ReaderCapability {
  kind: string
  /** The settings this Kind reads out of its options bag, declared beside the code that reads them. */
  parameters: ParameterDescriptor[]
  supportsSegmentation: boolean
  /** Whether a row deleted at the source reaches the target as a delete. False for a watermark scan,
   * which can only see rows that still exist, and for a batch reload, whose deletes are the
   * reconciling writer's job rather than the reader's. */
  detectsDeletes: boolean
  /**
   * `Changes` / `ChangesFromEarliest` / `ChangesFromLatest` this reader can honestly carry out — empty
   * for a reader with no incremental mode at all (batch reload). **Never carries `InitialLoad`**: every
   * reader can be asked for one regardless of what is declared here, so a picker adds it itself rather
   * than expecting it in this list. See phase 102.
   */
  supportedIntents: ReadIntent[]
}

export interface ConnectionTestReport {
  succeeded: boolean
  /** Opening the connection — usually the dominant cost, and what fails on a wrong host or port. */
  connectMs: number
  /** The driver's own round trip once connected. */
  probeMs: number
  serverVersion: string | null
  error: string | null
}

/** Which secret a connection resolves through. Read-only while there is one store to resolve from. */
export interface CredentialSource {
  store: string
  secretRef: string
  environmentVariable: string
  requiresCredential: boolean
}

export interface StagingCapability {
  kind: string
  parameters: ParameterDescriptor[]
}

export interface WriterCapability {
  kind: string
  parameters: ParameterDescriptor[]
  /** Removes target rows that are absent from the change set, within the scope it was given. False
   * for upsert-only writers, which can add and update but never notice an absence. */
  supportsReconciliation: boolean
}

export interface DriverCapabilities {
  driverType: DriverType
  readers: ReaderCapability[]
  stagingProviders: StagingCapability[]
  writers: WriterCapability[]
  /** Whether the driver can prove this connection reaches its engine. False is not a defect — a
   * driver reaching an arbitrary engine may have no probe it can name — so the UI hides the Test
   * affordance rather than offering one that could never work. */
  supportsConnectionTest: boolean
  /** Which provisioning actions (see ProvisioningPlan) this driver can plan. Empty for a driver that
   * implements no provisioning at all. */
  supportedProvisioningActions: string[]
}

/** Kind-name-only view of a driver's capabilities for a catalogue listing (phase 118, the admin
 * Drivers screen) — the full per-Kind parameter detail lives on `DriverCapabilities` instead, which is
 * connection-scoped. */
export interface DriverCapabilitySummary {
  readers: string[]
  staging: string[]
  writers: string[]
}

/** One entry from `GET /api/drivers` — every driver currently registered: built-in, added from a
 * `driver.yaml` descriptor (phase 109d), or a compiled plugin (phase 109e). */
export interface DriverSummary {
  id: DriverType
  displayName: string
  builtIn: boolean
  source: 'builtin' | 'descriptor' | 'compiled'
  /** The bound `DbDataSync.Libraries` id for a descriptor driver; null for a built-in or a compiled
   * plugin (phase 118). */
  library: string | null
  capabilities: DriverCapabilitySummary
}

/** One installed library — `GET /api/libraries` (phase 118). */
export interface LibrarySummary {
  id: string
  packages: { id: string; version: string }[]
  factoryType: string
  /** A real `DbProviderFactory` resolution probe, not just "the manifest parsed". */
  resolves: boolean
  /** Every descriptor driver on disk whose `library:` names this id. */
  usedBy: string[]
  /** Whether this id matches a bundled `KnownLibraries` entry. */
  curated: boolean
  /** Phase 121: true when this library was written with no SDK available to restore it (the
   * runtime-only image) and no in-image catalog cache hit for it — `config library sync` on a host
   * with the SDK finishes it. Distinct from `resolves` being false, which also covers a library whose
   * files are just broken. */
  pendingRestore: boolean
}

/** One bundled, vetted library — `GET /api/known-libraries` (phase 117/118), the "available to add"
 * list for the Libraries screen. */
export interface KnownLibrarySummary {
  id: string
  displayName: string
  description: string
  packageId: string
}

/** One bundled, ready-made driver descriptor — `GET /api/known-drivers` (phase 117/118), the
 * "available to add" list for the Drivers screen. */
export interface KnownDriverSummary {
  id: string
  displayName: string
  description: string
  boundLibrary: string
}

/** One row from `GET /api/libraries/search` (phase 119) — a NuGet package, reshaped from the public
 * search index. `id` is what a caller would pass as `packageId` to `config library install`. */
export interface LibrarySearchResult {
  id: string
  description: string
  latestVersion: string
  versions: string[]
  totalDownloads: number
  verified: boolean
}

/** `"ok"` (`results` may still be empty), `"disabled"` (`DbDataSync:NuGetSearchEnabled` is false — the
 * server never called out), or `"unavailable"` (the call was made but failed or timed out). The SPA
 * falls back to manual package-id/version entry for anything other than `"ok"`. */
export type LibrarySearchStatus = 'ok' | 'disabled' | 'unavailable'

export interface LibrarySearchResponse {
  status: LibrarySearchStatus
  results: LibrarySearchResult[] | null
}

/** `POST /api/libraries` (phase 120) response — the manifest `LibraryInstaller` wrote. */
export interface LibraryManifest {
  id: string
  factoryType: string
  packages: { id: string; version: string }[]
}

/** `POST /api/drivers/from-catalog` (phase 120) response. */
export interface FromCatalogResult {
  id: string
  library: string
}

/** `GET /api/admin/restart-required` (phase 120) — whether this process has a config value, library,
 * or driver change on disk it hasn't picked up yet. */
export interface RestartRequiredStatus {
  required: boolean
}

// The Setup card — see architecture/implementation/todo/phase-025-database-provisioning.md.
export type ProvisioningState = 'Satisfied' | 'Missing' | 'Unsupported' | 'Unknown'
export type ProvisioningStepScope = 'Database' | 'Table'

export interface ProvisioningStep {
  title: string
  commandText: string
  rationale: string | null
  scope: ProvisioningStepScope
}

export interface ProvisioningPlan {
  action: string
  state: ProvisioningState
  steps: ProvisioningStep[]
  warnings: string[]
}

export interface ProvisioningPlanReport {
  source: ProvisioningPlan
  target: ProvisioningPlan
}

export interface ApplyStepResult {
  title: string
  succeeded: boolean
  error: string | null
  elapsedMs: number
}

export interface ApplyResult {
  steps: ApplyStepResult[]
  state: ProvisioningState
}

// The replication-wide Provisioning tab's aggregate plan — phase 105.
export type ProvisioningEndpointSide = 'Source' | 'Target'

export interface ReplicationProvisioningStep {
  id: string
  title: string
  commandText: string
  rationale: string | null
  scope: ProvisioningStepScope
  side: ProvisioningEndpointSide
  /** Whether the target's own auto-flags would run this exact statement anyway on the next
   * unattended pass. Still selectable — see the phase doc — never hidden. */
  automatic: boolean
  contributingMappings: string[]
}

export interface ReplicationProvisioningGroup {
  connectionName: string
  database: string
  side: ProvisioningEndpointSide
  steps: ReplicationProvisioningStep[]
}

export interface ExcludedMapping {
  mappingName: string
  side: ProvisioningEndpointSide | null
  reason: string
}

export interface ReplicationProvisioningPlan {
  groups: ReplicationProvisioningGroup[]
  excluded: ExcludedMapping[]
}

export type ProvisioningStepOutcome = 'Applied' | 'Failed' | 'NoLongerNeeded' | 'NotAttempted'

export interface ReplicationProvisioningStepResult {
  id: string
  title: string
  outcome: ProvisioningStepOutcome
  error: string | null
  warning: string | null
  elapsedMs: number
}

export interface ReplicationProvisioningApplyResult {
  steps: ReplicationProvisioningStepResult[]
}

// Which slice of a source table one reload covers. The discriminator property is "mode", matching
// DbDataSync.Drivers.Abstractions.BatchReloadSegment's JsonPolymorphic configuration exactly.
export type SegmentMode = 'full' | 'list' | 'range' | 'auto' | 'custom'

export type BatchReloadSegment =
  | { mode: 'full' }
  | { mode: 'list'; column: string; values: string[] }
  /**
   * Half-open: rangeMin inclusive, rangeMax exclusive. `label` is what a segmenting strategy called
   * this range — it becomes the run's SegmentLabel in place of the generated bounds text.
   */
  | { mode: 'range'; column: string; rangeMin: string; rangeMax: string; label?: string | null }
  /** Expanded server-side into bucketCount concrete range segments before anything is enqueued. */
  | { mode: 'auto'; column: string; bucketCount: number }
  /**
   * A reference to a named strategy, expanded server-side into its *selected* candidates every time
   * it runs — never a frozen list, or a strategy tracking "the last three months" would stop moving.
   */
  | { mode: 'custom'; strategyName: string }

export type SegmentingStrategyKind = 'DuckDb' | 'SourceSql' | 'TargetSql' | 'Script'

/**
 * A named way of dividing a table for reload, defined on the replication and referenced by name.
 * Every kind returns the same four columns: label, range_start, range_end and selected.
 */
export interface SegmentingStrategyConfig {
  name: string
  kind: SegmentingStrategyKind
  sql?: string | null
  scriptName?: string | null
  column?: string | null
  parameters?: Record<string, string>
}

/** One row of a strategy's proposal, as the Backfill checklist renders it. */
export interface SegmentCandidate {
  label: string
  segment: BatchReloadSegment
  selected: boolean
}

/**
 * Whether running this kind reaches a real database. The one fact the UI has to state out loud
 * before a strategy is bound as a mapping's default, because a default runs unattended on the
 * replication's own schedule — every pass, forever.
 */
export function runsAgainstAConnection(kind: SegmentingStrategyKind): boolean {
  return kind !== 'DuckDb'
}

// A backfill is a run, not a config change — it produces no git commit, unlike every other write in
// this API. Kinds are null to mean "use the replication's own configured pipeline"; a backfill of an
// incrementally-synced replication has to override at least the reader.
export interface BackfillRequest {
  readerKind?: string | null
  cacheKind?: string | null
  writerKind?: string | null
  segments: BatchReloadSegment[]
}

/** `POST .../reconcile-deletes` (phase 124) — no reader/cache/writer Kinds, unlike `BackfillRequest`:
 * a delete-diff sweep always runs KeyReconcile/StagingTable/KeyReconcileDelete, so there is nothing
 * else to pick. */
export interface ReconcileDeletesRequest {
  segments: BatchReloadSegment[]
  /** Replaces the configured/default delete guard with "none" for this request — an operator
   * confirming "yes, delete this many rows" after a guarded attempt refused. */
  overrideGuard?: boolean
}

export type RunStatus = 'Queued' | 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'
export type RunKind = 'Primary' | 'Backfill' | 'ReconcileDeletes'

/** Phase 124's `KeyReconcile`/`KeyReconcileDelete` pair exists only for a delete-diff sweep — never
 * offered in an ordinary Change Processing or Backfill reader/writer picker, which is what every
 * `capabilities.readers`/`.writers` list gets filtered against before rendering one. */
export const RECONCILE_ONLY_KINDS: ReadonlySet<string> = new Set(['KeyReconcile', 'KeyReconcileDelete'])

export interface TaskRunRecord {
  runId: string
  taskName: string
  pid: number | null
  status: RunStatus
  runKind: RunKind
  mappingName: string
  segmentLabel: string | null
  /**
   * When the work was queued. Written by the enqueue, so every run has one — null only for a row
   * predating phase 73's migration, which backfills it.
   */
  enqueuedAtUtc: string | null
  /**
   * When a worker took the item off the queue — earlier than `startedAtUtc` by however long the worker
   * took to get going, and a different moment from it.
   *
   * Null for a run nobody ever claimed: still queued, or cancelled first.
   */
  claimedAtUtc: string | null
  /**
   * When the run actually began executing (phase 73 — before that this held the enqueue time, which is
   * now `enqueuedAtUtc`).
   *
   * The boundary between the two figures the run list shows: queue time is
   * `startedAtUtc - enqueuedAtUtc` and processing time is `endedAtUtc - startedAtUtc`. Raw timestamps
   * rather than precomputed deltas, matching how the run's watermark pair is exposed.
   *
   * Null for a run that never started — still queued, or cancelled before a worker took it — and for
   * runs recorded before this meant what it says. Such a run has no processing time to show.
   */
  startedAtUtc: string | null
  endedAtUtc: string | null
  rowsRead: number
  rowsWritten: number
  errorSummary: string | null
  /**
   * The full exception a failed run raised — type, message, stack trace, and every inner exception's
   * own — for the Runs tab's failure popup. `errorSummary` stays the short one-liner used everywhere
   * else; this is the separate, longer field for the one place that wants the whole picture. Null on
   * success, and null for a run recorded before this field existed, in which case the popup falls
   * back to `errorSummary`.
   */
  errorDetail: string | null
  /** Why it failed, when the product can act on it — `"PositionExpired"` means the source discarded
   * the history this pass needed, and the fix is a resync rather than a retry. Null for the ordinary
   * case, which is nearly all of them. */
  failureKind: string | null
  /**
   * What this pass actually did and how long each stage took — null unless the mapping opted into
   * tracing (phase 59's `TraceTiming`).
   *
   * Nested rather than seven fields on the run, matching the API: null is a clean "this run was never
   * measured", which is what lets an untraced row render exactly as it always has.
   */
  timing: RunTiming | null
  /**
   * Where this pass's watermark started and where it ended — the history behind `ChangeWatermarks`'
   * single current value (phase 71), surfaced in the run list since phase 88.
   *
   * Raw source positions: a CDC LSN or a Change Tracking version, in the encoding the source stores
   * them in. Not a time and not readable as one, which is why the run list dates them through
   * `useRunWatermarkTimes` and keeps these as the tooltip.
   *
   * Both null for a run that made no new position durable — a backfill, a verification, or any
   * failed pass.
   */
  previousWatermark: string | null
  newWatermark: string | null
}

/** Where a backfill is, taken as a whole rather than one segment at a time. */
export type BackfillState = 'Running' | 'Completed' | 'CompletedWithFailures'

/**
 * A backfill rolled up across its segment runs — the Monitoring screen's "Batch reload" card. A
 * backfill enqueues one independently-scheduled run per segment; this ties them back together by the
 * batch id minted at enqueue.
 */
export interface BackfillBatchProgress {
  batchId: string
  mappingName: string
  createdAtUtc: string
  /** Segments planned at enqueue — not a count of the runs, which can be short if an equivalent
   * segment was already in flight. */
  segmentCount: number
  segmentsSucceeded: number
  segmentsFailed: number
  segmentsRunning: number
  rowsRead: number
  /** Rows written to the target across the segments that have finished — steps up per segment, since
   * a segment run records its totals only on completion. */
  rowsCopied: number
  /** One whole-table estimate from the source engine's catalog statistics, read once at enqueue.
   * Null when there was no table to estimate (a query source) or the driver has no catalog. */
  estimatedRows: number | null
  /** Why the estimate should be read loosely, if it should — e.g. `"ignores row filter"`. Null when
   * it is clean. */
  estimateCaveat: string | null
  startedAtUtc: string | null
  lastActivityUtc: string | null
  state: BackfillState
}

/**
 * The query parameters `runs` and `runs/watermark-times` both take, since phase 104 — kept as one
 * type so the two client calls building a query string from it cannot quietly drift apart, which
 * would blank the watermark column for a filtered or paged run that has one.
 */
export interface RunHistoryFilters {
  kind?: RunKind
  mappingName?: string
  status?: RunStatus
  /** Opaque — round-tripped verbatim from a previous page's `nextCursor`, never built by hand. */
  cursor?: string
}

/**
 * The run-history endpoint's shape since phase 104 added server-side filtering and paging — a page
 * of runs, and where the next one starts.
 *
 * `nextCursor` is opaque: round-trip it verbatim as the next request's `cursor`, and never construct
 * or parse one — the two column values it is built from are free to change without every caller
 * needing to learn about it. `null` means this page reached the end of the history, never a cursor
 * that would loop back to the first page.
 */
export interface RunHistoryPage {
  runs: TaskRunRecord[]
  nextCursor: string | null
}

/**
 * When a run's stored watermarks were the source's own position — see phase 88.
 *
 * Resolved on read out of `ChangeCheckHistory`, the polling history that spans time, rather than
 * stored on the run: the answer depends on history written after the run ended, and stops existing
 * once that history is purged.
 *
 * Either field can be null on its own, and a run absent from the map has neither — its watermarks
 * have aged past the retention window, or it never stored one. That absence is rendered as no
 * timestamp, never as a zero or a guess.
 */
export interface RunWatermarkTimes {
  previousWatermarkTimeUtc: string | null
  newWatermarkTimeUtc: string | null
}

/**
 * One traced pass's stage timings.
 *
 * The Kinds travel with the numbers because a unit of work may override the replication's configured
 * pipeline — so "which reader produced this number" is not answerable from the replication's config
 * after the fact.
 */
export interface RunTiming {
  readerKind: string | null
  /** From the read starting to the first row arriving. Always a prefix of the lifetime. */
  readerTimeToFirstRowMs: number | null
  /** From the same start to the row stream being disposed. */
  readerLifetimeMs: number | null
  stagingKind: string | null
  /** The whole staging call — longer than the reader's lifetime for a provider that does work after
   * the stream is exhausted, which is the difference worth seeing. */
  stagingDurationMs: number | null
  writerKind: string | null
  writerDurationMs: number | null
}

export type LogSeverity = 'Trace' | 'Debug' | 'Info' | 'Warning' | 'Error'

export interface LogEntryRecord {
  id: number
  runId: string
  timestampUtc: string
  level: LogSeverity
  message: string
}

/**
 * One notification from the global feed — see phase 77.
 *
 * `kind` is an open set: the server may write a kind this build has never heard of, which is why
 * nothing keys rendering off it. `message` is what the server said at the time, stored rather than
 * recomposed, so an old row still reads correctly after the run it describes has been pruned.
 */
export interface NotificationRecord {
  id: number
  kind: string
  createdAtUtc: string
  taskName: string | null
  mappingName: string | null
  runId: string | null
  message: string
}

/**
 * `personalized` is false where the deployment does not authenticate: there is no user to key a read
 * cursor to, so everything reads as unread permanently. The bell says so rather than offering a
 * dismissal that would not stick.
 */
export interface NotificationFeed {
  notifications: NotificationRecord[]
  lastSeenNotificationId: number | null
  unreadCount: number
  personalized: boolean
}

export interface CommitInfo {
  sha: string
  message: string
  authorName: string
  authorEmail: string
  whenUtc: string
}

// A trigger now enqueues one Primary pass per table mapping the replication has, not one run for the
// whole replication — see architecture/implementation/done/phase-008-work-queue-schema.md.
export interface TriggerResponse {
  runIds: string[]
}

export interface ApiErrorBody {
  error?: string
  title?: string
}

/**
 * A request to create one mapping per named table. `batchId` is the SignalR group progress is
 * reported on — optional, since the response carries the full result either way.
 */
export interface BulkCreateRequest {
  tables: { schema: string; table: string }[]
  batchId?: string
}

export interface BulkCreateResult {
  created: string[]
  /** Tables that already had a mapping — reported rather than dropped, so "create 40" answering with
   * 12 is explained on screen instead of looking like a failure. */
  skipped: string[]
  /** Mappings created without a full capture, and why. Empty in the ordinary case. */
  notes: BulkCreateNote[]
}

/**
 * One side of one created mapping that could not be captured — a target provisioning has yet to
 * create, a source that could not be read. Named rather than counted: the operator has to know which
 * mapping to go and refresh.
 */
export interface BulkCreateNote {
  mapping: string
  /** `source` or `target`. */
  side: string
  reason: string
}

export interface BulkCreateProgress {
  done: number
  total: number
  name: string
  /** `reading` while this table's two catalogs are being read, `created` once it is saved. Reading is
   * the slow part, so a button that only counted saves would look stalled through it. */
  stage: string
}

/**
 * How far behind its source one mapping is — see phase 85.
 *
 * **Three separately named fields, and never one coalesced number.** `exactLagMs` is a duration the
 * source engine itself stated at both ends; `estimatedLagMs` is reconstructed from how often this
 * system happened to poll, so its error is the poll interval — a property of our configuration
 * rather than of the replication; `versionsBehind` is a count whose meaning depends entirely on how
 * often the source's tables are written to, and is not convertible into a time. A UI rendering any
 * of these has to say which one it is showing.
 *
 * `exactLagMs` and `estimatedLagMs` are never both set. CDC always answers in the first. Change
 * Tracking answers in the first while its version is recent enough for `sys.dm_tran_commit_table`
 * to place, and drops to the second once it is not — so the same mapping can move between them as
 * it falls further behind.
 */
export interface MappingLag {
  readerKind: string
  /** False when the mechanism has no lag to report at all, as opposed to none yet: render "not
   * applicable", never a dash that reads like zero. */
  supported: boolean
  exactLagMs: number | null
  versionsBehind: number | null
  estimatedLagMs: number | null
  /**
   * Where the source had got to, at the moment the figures above are measured against — the exact
   * polling-history row this mapping's own comparison used, never the clock (phase 88).
   *
   * The engine's own commit time for that position where it has one, the moment we polled where it
   * does not. Null when no history row was consulted: an unsupported reader, or a mapping that has
   * never stored a position.
   */
  asOfUtc: string | null
}

/**
 * Every mapping's lag in one payload, and the range across them — see phase 86.
 *
 * The range is computed on the server so the Monitoring tab and the replications list cannot arrive
 * at two different answers from the same data.
 */
export interface ReplicationLag {
  /** Keyed by mapping name. Every mapping is present, whether or not it has a figure — an absent
   * key and a mapping reporting nothing would look identical, and they are different states. */
  mappings: Record<string, MappingLag>
  /** Smallest and largest `exactLagMs ?? estimatedLagMs` among mappings that have one. Null — both
   * of them — when no mapping does. Mappings with nothing to report are excluded rather than
   * counted as zero, so a replication is never shown as caught up on the strength of the mappings
   * that cannot say. */
  lowestLagMs: number | null
  highestLagMs: number | null
  /** True when any mapping reached the range through `estimatedLagMs`, whose error is this system's
   * poll interval. The range is a coalesce of two figures phase 85 deliberately keeps apart; this
   * is what lets a screen label the result as approximate instead of quietly promising it is not. */
  rangeIncludesEstimates: boolean
}

/** Who the caller is, and how they could sign in. The one endpoint the app can always call — the
 * answer to "am I signed in" cannot itself require being signed in. */
export interface AuthStatus {
  authenticated: boolean
  userId: string | null
  displayName: string | null
  /** `Admin` or `Viewer`. Null when nobody is signed in, and on a deployment that has deliberately
   * turned authentication off — where everything is permitted and nobody has a name. */
  role: string | null
  /** Sign-in methods this deployment offers, so the sign-in screen shows the ones that exist. */
  methods: string[]
}

export interface UserCredentialSummary {
  id: string
  method: string
  label: string | null
  createdAtUtc: string
  lastUsedAtUtc: string | null
}

export interface UserSummary {
  id: string
  displayName: string
  email: string | null
  role: string
  enabled: boolean
  credentials: UserCredentialSummary[]
}

/**
 * One row of the admin config screen (phase 81) — a DbDataSync:* key CONFIG.md documents, its live
 * effective value and where that came from, and what this screen can do about it.
 *
 * `value` is null both for a genuinely unset key and — StateConnectionString only — when `masked` is
 * true: the server never sends a raw credential it found in a non-file source, so the two cases are
 * indistinguishable from `value` alone and the SPA must check `masked` to tell them apart.
 */
export interface AdminConfigEntry {
  key: string
  /** The configured value — what `source` says right now, and what `editable` lets you change.
   * Independent of whether the running process has picked it up yet; see `runningValue`. */
  value: string | null
  /** What this process actually loaded at startup and is running with right now, frozen at boot.
   * Differs from `value` exactly when a change is queued and not yet applied — a save made here, or
   * an override that just moved this key from its environment/CLI/default source into the file. */
  runningValue: string | null
  source: 'file' | 'environment variable' | 'command line' | 'appsettings.json' | 'default'
  editable: boolean
  canAdopt: boolean
  /** True for a file-sourced key with a known application default that `value` doesn't already equal —
   * the inverse of `canAdopt`: putting the factory value back rather than taking a non-file one out. */
  canReset: boolean
  /** What Reset would write, or null when this key's default is contextual (a machine-derived path)
   * rather than a fixed literal. `canReset` is never true when this is null. */
  defaultValue: string | null
  masked: boolean
  description: string
  /** What a numeric value is counted in ("days", "minutes", "runs") — null for a key that isn't a
   * plain magnitude. Shown as a pill only beside a value that's actually numeric. */
  unit: string | null
}

// The Certificates section of the Admin screen (phase 83) — a second door onto the operations
// phase 82's `dbdatasync cert …` already exposes. See src/DbDataSync.Api/Services/AdminCertificateService.cs.

export interface CurrentCertificateInfo {
  thumbprint: string
  subjectCommonName: string
  dnsNames: string[]
  issuer: string
  notBefore: string
  notAfter: string
  daysRemaining: number
  selfSigned: boolean
}

/** `subject` is null only when nothing has ever been bound. `certificateFound` is false when a bound
 * subject no longer matches anything in the store — the same case `dbdatasync cert status` reports as an
 * error, shown here rather than thrown. */
export interface BindingInfo {
  subject: string | null
  store: string
  location: string
  allowInvalid: boolean
  certificateFound: boolean
}

/**
 * Three states, because "unknown" is real: on a host with no DbDataSync Windows service installed (or no
 * certificate bound yet to check), the question does not apply, and this deliberately does not fall back
 * to a green "ok" the way the CLI's own convenience default would — see
 * AdminCertificateService.EvaluateKeyAccess's own doc comment.
 */
export type KeyAccessState = 'Ok' | 'Warning' | 'Unknown'

/** `account` is null only for the `Unknown` case caused by no service being installed at all — every
 * other state, including the other `Unknown` case (no certificate to check), names a real account. */
export interface KeyAccessInfo {
  state: KeyAccessState
  account: string | null
  detail: string | null
}

export interface PendingEnrollmentSummary {
  requestId: string
  subjectCommonName: string
  dnsNames: string[]
  submittedAtUtc: string
}

/** Why `templates` came back empty — `Available` is the one case it did not (and even then the CA may
 * legitimately publish zero templates). */
export type TemplateListReason =
  | 'Available' | 'CaConfigNotSet' | 'NotDomainJoined' | 'DirectoryUnreachable' | 'AccessDenied' | 'Unknown'

/** Backs the Template field's picker. When `reason` is not `Available`, the field renders as free text
 * showing `detail` instead — never disabled, and enrollment never blocked on this having worked. */
export interface TemplateListResult {
  templates: string[]
  reason: TemplateListReason
  detail: string | null
}

export interface CertificateCandidate {
  thumbprint: string
  subjectCommonName: string
  dnsNames: string[]
  notAfter: string
  daysRemaining: number
  selfSigned: boolean
}

/** `available` is false on a non-Windows host, in which case every other field but
 * `unavailableReason` is null/empty — the SPA renders one explanatory line rather than an empty
 * section. */
export interface AdminCertificateStatus {
  available: boolean
  unavailableReason: string | null
  certificate: CurrentCertificateInfo | null
  binding: BindingInfo | null
  keyAccess: KeyAccessInfo | null
  pendingEnrollments: PendingEnrollmentSummary[]
  templates: TemplateListResult | null
  /** The same threshold the daily expiry check raises a notification at — "days remaining" is coloured
   * against this number, not a second one the SPA invented. */
  expiryWarningDays: number
}

/**
 * `succeeded` is false only for a genuine failure — the API sends that case as a 400 with `{ error }`,
 * which `ErrorBanner` already renders, so a mutation's `onError` is what a page actually reads for a
 * failure. This type is what a *successful* response carries: `requestId` is set only when an
 * enrollment came back pending (a template requiring approval, not a failure), and `status` is the
 * refreshed AdminCertificateStatus so the page does not need a second round trip.
 */
export interface CertificateActionResult {
  succeeded: boolean
  message: string
  requestId: string | null
  status: AdminCertificateStatus | null
}

/**
 * What a query returned when an operator pressed Preview — see `POST /api/connections/{name}/query-preview`.
 *
 * `rows` is stringified server-side, one cell per column in `columns` order, so a grid renders it
 * without needing to know a single source type. A null cell stays null: an empty string and a NULL
 * have to look different, and the server deliberately does not spell either of them as a word.
 */
export interface QueryPreviewResult {
  /** Where the rows came from, in the operator's words — this touched a real system and says which. */
  source: string
  columns: string[]
  rows: (string | null)[][]
  /** Whether the query had more rows than were read. A limit an operator can see beats one they cannot. */
  truncated: boolean
  /** What the engine said about a query that would not run. Not a failed request — an answer. */
  error: string | null
}

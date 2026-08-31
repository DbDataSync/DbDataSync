// Mirrors the DataSync.Api DTOs exactly (see src/DataSync.Core/Config/*.cs and
// src/DataSync.Api/Controllers/*.cs). Hand-written rather than generated: the author of both sides
// already knows the exact contract, and a codegen step would add a moving part (fetching a live
// OpenAPI doc) without reducing risk here.

export type DriverType = 'MsSql' | 'Postgres'
/** What DataSync supplies when connecting. `None` is what a wallet, a DSN with stored credentials, a
 * .pgpass file or a credential-bearing URL all look like from here — the address or the environment
 * provides it and DataSync passes nothing. */
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
  properties?: Record<string, string>
  scripts?: ScriptBindings
  hooks?: Hooks
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
  parallelism: number
  options: Record<string, string>
}

export interface CacheConfig {
  kind: string
  options: Record<string, string>
}

export interface WriterConfig {
  kind: string
  parallelism: number
  options: Record<string, string>
}

export interface ChangeProcessingConfig {
  reader: ReaderConfig
  cache: CacheConfig
  writer: WriterConfig
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
  /**
   * Named segmenting strategies any of this replication's mappings may reference. At the replication
   * because a set of tables replicated together usually segments the same way.
   */
  segmentingStrategies?: SegmentingStrategyConfig[]
  /** Markdown, git-tracked. What the next person needs to know about this replication. */
  notes?: string | null
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
 * What DataSync may do to a target's shape without being asked.
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

export interface TableMappingConfig {
  name: string
  sources: SourceTableSpec[]
  targets: TableSpec[]
  columnMappings: ColumnMapping[]
  scripts?: ScriptBindings
  hooks?: Hooks
  provisioning?: ProvisioningConfig
  verification?: VerificationCheckConfig[]
  /** How this table divides for a reload. Empty means Full — the whole table, unsegmented. */
  defaultSegmenting?: BatchReloadSegment[]
  /** Records how long each stage of a pass took, onto the run itself. Off means not measured at all. */
  traceTiming?: boolean
  /** Markdown, git-tracked. Not inherited from the replication — a note that applied to every mapping
   * would be a note about the replication, and that field exists too. */
  notes?: string | null
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
  /** Null when nothing in the window finished — a run still going has no duration. */
  durationP50Ms: number | null
  durationP95Ms: number | null
  durationMaxMs: number | null
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

// Which slice of a source table one reload covers. The discriminator property is "mode", matching
// DataSync.Drivers.Abstractions.BatchReloadSegment's JsonPolymorphic configuration exactly.
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

export type RunStatus = 'Queued' | 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'
export type RunKind = 'Primary' | 'Backfill'

export interface TaskRunRecord {
  runId: string
  taskName: string
  pid: number | null
  status: RunStatus
  runKind: RunKind
  mappingName: string
  segmentLabel: string | null
  startedAtUtc: string
  endedAtUtc: string | null
  rowsRead: number
  rowsWritten: number
  errorSummary: string | null
  /** Why it failed, when the product can act on it — `"PositionExpired"` means the source discarded
   * the history this pass needed, and the fix is a resync rather than a retry. Null for the ordinary
   * case, which is nearly all of them. */
  failureKind: string | null
}

export type LogSeverity = 'Trace' | 'Debug' | 'Info' | 'Warning' | 'Error'

export interface LogEntryRecord {
  id: number
  runId: string
  timestampUtc: string
  level: LogSeverity
  message: string
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
}

export interface BulkCreateProgress {
  done: number
  total: number
  name: string
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

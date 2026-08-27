// Mirrors the DataSync.Api DTOs exactly (see src/DataSync.Core/Config/*.cs and
// src/DataSync.Api/Controllers/*.cs). Hand-written rather than generated: the author of both sides
// already knows the exact contract, and a codegen step would add a moving part (fetching a live
// OpenAPI doc) without reducing risk here.

export type DriverType = 'MsSql'
export type AuthMode = 'SqlAuth' | 'IntegratedAuth'

export interface ConnectionConfig {
  name: string
  driverType: DriverType
  host: string
  port: number | null
  database: string | null
  authMode: AuthMode
  userId: string | null
  credentialSecretRef: string | null
  properties: Record<string, string>
}

export interface ConnectionInput {
  name: string
  driverType: DriverType
  host: string
  port?: number | null
  database?: string | null
  authMode: AuthMode
  userId?: string | null
  password?: string | null
  properties?: Record<string, string>
}

export type ScheduleMode = 'Continuous' | 'Periodic'

export interface SchedulingConfig {
  mode: ScheduleMode
  frequencySeconds: number | null
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
}

export interface TableMappingConfig {
  name: string
  sources: SourceTableSpec[]
  targets: TableSpec[]
  columnMappings: ColumnMapping[]
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

// What the registered driver behind a connection actually supports. Queried live rather than
// hardcoded per engine — see GET /api/connections/{name}/capabilities. Readers come from the
// *source* connection's driver, staging providers and writers from the *target*'s.
export interface ReaderCapability {
  kind: string
  supportsSegmentation: boolean
}

export interface StagingCapability {
  kind: string
}

export interface WriterCapability {
  kind: string
  /** Removes target rows that are absent from the change set, within the scope it was given. False
   * for upsert-only writers, which can add and update but never notice an absence. */
  supportsReconciliation: boolean
}

export interface DriverCapabilities {
  driverType: DriverType
  readers: ReaderCapability[]
  stagingProviders: StagingCapability[]
  writers: WriterCapability[]
}

// Which slice of a source table one reload covers. The discriminator property is "mode", matching
// DataSync.Drivers.Abstractions.BatchReloadSegment's JsonPolymorphic configuration exactly.
export type SegmentMode = 'full' | 'list' | 'range' | 'auto'

export type BatchReloadSegment =
  | { mode: 'full' }
  | { mode: 'list'; column: string; values: string[] }
  /** Half-open: rangeMin inclusive, rangeMax exclusive. */
  | { mode: 'range'; column: string; rangeMin: string; rangeMax: string }
  /** Expanded server-side into bucketCount concrete range segments before anything is enqueued. */
  | { mode: 'auto'; column: string; bucketCount: number }

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

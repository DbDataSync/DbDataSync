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

export interface ReplicationTaskConfig {
  name: string
  enabled: boolean
  scheduling: SchedulingConfig
  changeProcessing: ChangeProcessingConfig
}

export interface TableRef {
  connectionName: string
  database: string
  schema: string
  table: string
}

export interface SourceTableRef extends TableRef {
  filter: string | null
}

export interface ColumnMapping {
  sourceColumn: string
  targetColumn: string
  transform: string | null
}

export interface TableMappingConfig {
  name: string
  sources: SourceTableRef[]
  targets: TableRef[]
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
// whole replication — see architecture/implementation/phase-9-work-queue-schema.md.
export interface TriggerResponse {
  runIds: string[]
}

export interface ApiErrorBody {
  error?: string
  title?: string
}

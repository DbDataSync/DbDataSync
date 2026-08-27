import type {
  BackfillRequest,
  ColumnMetadata,
  CommitInfo,
  ConnectionConfig,
  ConnectionInput,
  ConnectionTestReport,
  CredentialSource,
  DriverCapabilities,
  LogEntryRecord,
  ReplicationTaskConfig,
  TableMappingConfig,
  TableMetadata,
  TaskRunRecord,
  TriggerResponse,
} from './types'

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })

  if (!response.ok) {
    let message = response.statusText
    try {
      const body = await response.json()
      message = body.error ?? body.title ?? message
    } catch {
      // No JSON body — fall back to the status text already captured.
    }
    throw new ApiError(response.status, message)
  }

  if (response.status === 204) return undefined as T
  const text = await response.text()
  return text ? (JSON.parse(text) as T) : (undefined as T)
}

const put = <T>(path: string, body: unknown) =>
  request<T>(path, { method: 'PUT', body: JSON.stringify(body) })

export const api = {
  connections: {
    list: () => request<ConnectionConfig[]>('/api/connections'),
    get: (name: string) => request<ConnectionConfig>(`/api/connections/${encodeURIComponent(name)}`),
    upsert: (name: string, input: ConnectionInput) =>
      put<ConnectionConfig>(`/api/connections/${encodeURIComponent(name)}`, input),
    delete: (name: string) => request<void>(`/api/connections/${encodeURIComponent(name)}`, { method: 'DELETE' }),
    capabilities: (name: string) =>
      request<DriverCapabilities>(`/api/connections/${encodeURIComponent(name)}/capabilities`),
    test: (name: string) =>
      request<ConnectionTestReport>(`/api/connections/${encodeURIComponent(name)}/test`, { method: 'POST' }),
    credentialSource: (name: string) =>
      request<CredentialSource>(`/api/connections/${encodeURIComponent(name)}/credential-source`),
  },
  metadata: {
    databases: (connectionName: string) =>
      request<string[]>(`/api/connections/${encodeURIComponent(connectionName)}/metadata/databases`),
    tables: (connectionName: string, database: string) =>
      request<TableMetadata[]>(
        `/api/connections/${encodeURIComponent(connectionName)}/metadata/databases/${encodeURIComponent(database)}/tables`,
      ),
    columns: (connectionName: string, database: string, schema: string, table: string) =>
      request<ColumnMetadata[]>(
        `/api/connections/${encodeURIComponent(connectionName)}/metadata/databases/${encodeURIComponent(database)}` +
          `/schemas/${encodeURIComponent(schema)}/tables/${encodeURIComponent(table)}/columns`,
      ),
  },
  replications: {
    list: () => request<string[]>('/api/replications'),
    get: (name: string) => request<ReplicationTaskConfig>(`/api/replications/${encodeURIComponent(name)}`),
    upsert: (name: string, task: ReplicationTaskConfig) =>
      put<ReplicationTaskConfig>(`/api/replications/${encodeURIComponent(name)}`, task),
    delete: (name: string) => request<void>(`/api/replications/${encodeURIComponent(name)}`, { method: 'DELETE' }),
    history: (name: string) => request<CommitInfo[]>(`/api/replications/${encodeURIComponent(name)}/history`),
  },
  tableMappings: {
    list: (replicationName: string) =>
      request<string[]>(`/api/replications/${encodeURIComponent(replicationName)}/table-mappings`),
    get: (replicationName: string, mappingName: string) =>
      request<TableMappingConfig>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}`,
      ),
    upsert: (replicationName: string, mappingName: string, mapping: TableMappingConfig) =>
      put<TableMappingConfig>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}`,
        mapping,
      ),
    delete: (replicationName: string, mappingName: string) =>
      request<void>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}`,
        { method: 'DELETE' },
      ),
  },
  runs: {
    trigger: (replicationName: string) =>
      request<TriggerResponse>(`/api/replications/${encodeURIComponent(replicationName)}/runs`, { method: 'POST' }),
    history: (replicationName: string, limit = 50) =>
      request<TaskRunRecord[]>(
        `/api/replications/${encodeURIComponent(replicationName)}/runs?limit=${limit}`,
      ),
    get: (runId: string) => request<TaskRunRecord>(`/api/runs/${runId}`),
    logs: (runId: string, sinceId?: number) =>
      request<LogEntryRecord[]>(`/api/runs/${runId}/logs${sinceId ? `?sinceId=${sinceId}` : ''}`),
    cancel: (runId: string) => request<void>(`/api/runs/${runId}/cancel`, { method: 'POST' }),
    backfill: (replicationName: string, mappingName: string, body: BackfillRequest) =>
      request<TriggerResponse>(
        `/api/replications/${encodeURIComponent(replicationName)}/mappings/${encodeURIComponent(mappingName)}/backfill`,
        { method: 'POST', body: JSON.stringify(body) },
      ),
  },
}

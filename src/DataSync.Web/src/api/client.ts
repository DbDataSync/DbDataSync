import type {
  ApplyResult,
  BackfillRequest,
  ColumnMetadata,
  CommitInfo,
  ConnectionConfig,
  ConnectionInput,
  ConnectionTestReport,
  CredentialSource,
  DriverCapabilities,
  BulkCreateRequest,
  BulkCreateResult,
  InferredColumnType,
  DriverType,
  ParameterDescriptor,
  ProvisioningPlanReport,
  ScriptCompileResult,
  ScriptDefinition,
  MetricsWindow,
  PreviewReport,
  ReplicationStatus,
  RunMetrics,
  VerificationResultPage,
  VerificationResultRecord,
  ScriptListItem,
  ScriptTestRequest,
  ScriptTestResult,
  ScriptSlotInfo,
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
  scripts: {
    list: () => request<ScriptListItem[]>('/api/scripts'),
    slots: () => request<ScriptSlotInfo[]>('/api/scripts/slots'),
    get: (name: string) => request<ScriptDefinition>(`/api/scripts/${encodeURIComponent(name)}`),
    upsert: (name: string, script: ScriptDefinition) =>
      put<ScriptCompileResult>(`/api/scripts/${encodeURIComponent(name)}`, script),
    /** Runs the script against sample input. Takes the definition in the body, so what is tested is
     * what is in the editor rather than what was last saved. */
    test: (name: string, body: ScriptTestRequest) =>
      request<ScriptTestResult>(`/api/scripts/${encodeURIComponent(name)}/test`, {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    /** Checks without saving, so an operator finds out before committing. */
    compile: (name: string, script: ScriptDefinition) =>
      request<ScriptCompileResult>(`/api/scripts/${encodeURIComponent(name)}/compile`, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(script),
      }),
    delete: (name: string) => request<void>(`/api/scripts/${encodeURIComponent(name)}`, { method: 'DELETE' }),
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
    bulkCreate: (replicationName: string, body: BulkCreateRequest) =>
      request<BulkCreateResult>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/bulk`,
        { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) },
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
  provisioning: {
    get: (replicationName: string, mappingName: string) =>
      request<ProvisioningPlanReport>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}/provisioning`,
      ),
    inferredColumnTypes: (replicationName: string, mappingName: string) =>
      request<InferredColumnType[]>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}` +
          `/provisioning/inferred-column-types`,
      ),
    apply: (replicationName: string, mappingName: string, action: string) =>
      request<ApplyResult>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}` +
          `/provisioning/${encodeURIComponent(action)}/apply`,
        { method: 'POST' },
      ),
  },
  drivers: {
    /** What a connection of this driver takes, given what it has been given so far. A POST because
     * the answer depends on the values — Host is not a setting in connection-string mode — and those
     * are an arbitrary operator-typed bag that does not belong in a query string. */
    connectionParameters: (driverType: DriverType, values: Record<string, string>) =>
      request<ParameterDescriptor[]>(
        `/api/drivers/${encodeURIComponent(driverType)}/connection-parameters`,
        { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(values) },
      ),
  },
  preview: {
    get: (replicationName: string, mappingName: string) =>
      request<PreviewReport>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}/preview`,
      ),
  },
  replicationStatus: {
    get: (name: string) =>
      request<ReplicationStatus>(`/api/replications/${encodeURIComponent(name)}/status`),
    setEnabled: (name: string, enabled: boolean) =>
      put<ReplicationTaskConfig>(`/api/replications/${encodeURIComponent(name)}/enabled`, { enabled }),
  },
  metrics: {
    get: (replicationName: string, window: MetricsWindow) =>
      request<RunMetrics>(
        `/api/replications/${encodeURIComponent(replicationName)}/metrics?window=${window}`,
      ),
  },
  verification: {
    run: (replicationName: string, mappingName: string) =>
      request<{ runIds: string[] }>(
        `/api/replications/${encodeURIComponent(replicationName)}/mappings/${encodeURIComponent(mappingName)}/verify`,
        { method: 'POST' },
      ),
    results: (replicationName: string, mappingName: string) =>
      request<VerificationResultRecord[]>(
        `/api/replications/${encodeURIComponent(replicationName)}/verification-results` +
          `?mappingName=${encodeURIComponent(mappingName)}`,
      ),
    result: (replicationName: string, id: number, offset: number, limit: number, differingOnly: boolean) =>
      request<VerificationResultPage>(
        `/api/replications/${encodeURIComponent(replicationName)}/verification-results/${id}` +
          `?offset=${offset}&limit=${limit}&differingOnly=${differingOnly}`,
      ),
    deleteResult: (replicationName: string, id: number) =>
      request<void>(
        `/api/replications/${encodeURIComponent(replicationName)}/verification-results/${id}`,
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

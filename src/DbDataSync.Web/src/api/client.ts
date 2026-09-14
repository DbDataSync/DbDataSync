import type {
  AdminCertificateStatus,
  AdminConfigEntry,
  ApplyResult,
  BackfillBatchProgress,
  BackfillRequest,
  ReconcileDeletesRequest,
  SegmentCandidate,
  SegmentingStrategyConfig,
  ColumnMetadata,
  CommitInfo,
  ConnectionConfig,
  ConnectionInput,
  ConnectionTestReport,
  CredentialSource,
  DriverCapabilities,
  DriverSummary,
  FromCatalogResult,
  KnownDriverSummary,
  KnownLibrarySummary,
  LibraryValidationReport,
  LibraryManifest,
  LibrarySearchResponse,
  LibrarySummary,
  RestartRequiredStatus,
  BulkCreateRequest,
  BulkCreateResult,
  MappingLag,
  MappingReadState,
  MetadataRefreshResult,
  PauseEvent,
  ReplicationLag,
  CertificateActionResult,
  CertificateCandidate,
  InferredColumnType,
  InferredNaturalKey,
  AuthStatus,
  UserSummary,
  DriverType,
  ParameterDescriptor,
  ProvisioningPlanReport,
  ReplicationProvisioningPlan,
  ReplicationProvisioningApplyResult,
  QueryPreviewResult,
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
  SetMappingReadStateRequest,
  ScriptSlotInfo,
  LogEntryRecord,
  NotificationFeed,
  ReplicationTaskConfig,
  TableMappingConfig,
  TableMetadata,
  TaskRunRecord,
  RunHistoryFilters,
  RunHistoryPage,
  RunWatermarkTimes,
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
    // The session is a cookie, and a cross-origin dev setup (Vite on 5173 proxying to the API) does
    // not send one unless asked. Without this, signing in appears to do nothing.
    credentials: 'same-origin',
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

/**
 * The query string `runs` and `runs/watermark-times` both build from — one function rather than two
 * copies of the same four `if`s, because the two calls have to ask about exactly the same page (see
 * phase 104) and a query string assembled twice is a query string that can drift.
 */
function runHistoryQuery(filters: RunHistoryFilters, limit: number): string {
  const params = new URLSearchParams()
  if (filters.kind) params.set('kind', filters.kind)
  if (filters.mappingName) params.set('mappingName', filters.mappingName)
  if (filters.status) params.set('status', filters.status)
  if (filters.cursor) params.set('cursor', filters.cursor)
  params.set('limit', String(limit))
  return params.toString()
}

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
    /** Phase 109j item 4: the deep, connection-scoped check — spawns a real child process, creates a
     * real scratch table, stages and writes a few synthetic rows through the driver's own real
     * pipeline, and drops it again. Slower than `test` and needs DDL rights; a deliberately separate
     * action rather than folded into it. */
    validateLibrary: (name: string) =>
      request<LibraryValidationReport>(`/api/connections/${encodeURIComponent(name)}/validate-library`, {
        method: 'POST',
      }),
    credentialSource: (name: string) =>
      request<CredentialSource>(`/api/connections/${encodeURIComponent(name)}/credential-source`),
    /** Runs a query and returns its columns and first few rows. Takes the text in the body, so what
     * runs is what is in the editor rather than what was last saved to the mapping. */
    queryPreview: (name: string, query: string, sampleRows = 20) =>
      request<QueryPreviewResult>(`/api/connections/${encodeURIComponent(name)}/query-preview`, {
        method: 'POST',
        body: JSON.stringify({ query, sampleRows }),
      }),
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
    /** Every pause/resume this replication has recorded, over both grains — see phase 131. */
    pauseHistory: (name: string, limit?: number) =>
      request<PauseEvent[]>(
        `/api/replications/${encodeURIComponent(name)}/pause-history${limit ? `?limit=${limit}` : ''}`,
      ),
    // Every mapping's lag and the range across them, in one call — see phase 86. The per-mapping
    // endpoint below still exists for a caller that wants exactly one.
    lag: (name: string) => request<ReplicationLag>(`/api/replications/${encodeURIComponent(name)}/lag`),
    backfills: (name: string, limit?: number) =>
      request<BackfillBatchProgress[]>(
        `/api/replications/${encodeURIComponent(name)}/backfills${limit ? `?limit=${limit}` : ''}`,
      ),
  },
  tableMappings: {
    list: (replicationName: string) =>
      request<string[]>(`/api/replications/${encodeURIComponent(replicationName)}/table-mappings`),
    get: (replicationName: string, mappingName: string) =>
      request<TableMappingConfig>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}`,
      ),
    lag: (replicationName: string, mappingName: string) =>
      request<MappingLag>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}/lag`,
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
    refreshMetadata: (replicationName: string, mappingName: string) =>
      request<MetadataRefreshResult>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}/refresh-metadata`,
        { method: 'POST' },
      ),
    delete: (replicationName: string, mappingName: string) =>
      request<void>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}`,
        { method: 'DELETE' },
      ),
    /** This mapping's resolved intent and hold — see phase 100. */
    readState: (replicationName: string, mappingName: string) =>
      request<MappingReadState>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}/read-state`,
      ),
    /** Sets the intent and clears or sets the hold, in one call — see phase 100. */
    setReadState: (replicationName: string, mappingName: string, body: SetMappingReadStateRequest) =>
      request<MappingReadState>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}/read-state`,
        { method: 'POST', body: JSON.stringify(body) },
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
    inferredNaturalKey: (replicationName: string, mappingName: string) =>
      request<InferredNaturalKey>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}` +
          `/provisioning/inferred-natural-key`,
      ),
    apply: (replicationName: string, mappingName: string, action: string) =>
      request<ApplyResult>(
        `/api/replications/${encodeURIComponent(replicationName)}/table-mappings/${encodeURIComponent(mappingName)}` +
          `/provisioning/${encodeURIComponent(action)}/apply`,
        { method: 'POST' },
      ),
    /** Every table mapping's provisioning, aggregated — phase 105's replication-wide Provisioning tab.
     * Distinct from `get` above, which is one mapping's Setup card. */
    getReplicationPlan: (replicationName: string) =>
      request<ReplicationProvisioningPlan>(
        `/api/replications/${encodeURIComponent(replicationName)}/provisioning`,
      ),
    applyReplicationPlan: (replicationName: string, stepIds: string[]) =>
      request<ReplicationProvisioningApplyResult>(
        `/api/replications/${encodeURIComponent(replicationName)}/provisioning/apply`,
        { method: 'POST', body: JSON.stringify({ stepIds }) },
      ),
  },
  auth: {
    status: () => request<AuthStatus>('/api/auth/status'),
    /** Negotiates a Windows identity and mints a session. Windows deployments only — the endpoint
     * says so where it is not one. */
    signInWithWindows: () => request<AuthStatus>('/api/auth/windows', { method: 'POST' }),
    signOut: () => request<void>('/api/auth/sign-out', { method: 'POST' }),
    beginPasskey: () => request<unknown>('/api/auth/passkey/begin', { method: 'POST' }),
    completePasskey: (assertion: unknown) =>
      request<AuthStatus>('/api/auth/passkey/complete', { method: 'POST', body: JSON.stringify(assertion) }),
  },
  invites: {
    create: (role: string, forUserId?: string) =>
      request<{ url: string; expiresAtUtc: string; role: string }>('/api/invites', {
        method: 'POST',
        body: JSON.stringify({ role, forUserId: forUserId ?? null }),
      }),
    check: (code: string) => request<{ valid: boolean }>(`/api/invites/check?code=${encodeURIComponent(code)}`),
    beginRegistration: (code: string, displayName: string, email: string | null) =>
      request<unknown>('/api/invites/begin-registration', {
        method: 'POST',
        body: JSON.stringify({ code, displayName, email }),
      }),
    completeRegistration: (code: string, displayName: string, email: string | null, attestation: unknown) =>
      request<AuthStatus>(
        `/api/invites/complete-registration?code=${encodeURIComponent(code)}` +
          `&displayName=${encodeURIComponent(displayName)}&email=${encodeURIComponent(email ?? '')}`,
        { method: 'POST', body: JSON.stringify(attestation) },
      ),
  },
  admin: {
    config: {
      list: () => request<AdminConfigEntry[]>('/api/admin/config'),
      /** Writes one key into dbdatasync.config.yaml — the same call for a direct edit of a file-sourced
       * key and an "adopt" of one that is not; the caller supplies the current effective value either
       * way. Does not take effect in the running process until it restarts. */
      set: (key: string, value: string) =>
        put<AdminConfigEntry>(`/api/admin/config/${encodeURIComponent(key)}`, { value }),
      /** StateConnectionString's password only, through the secret store — never through the file. */
      setSecret: (key: string, value: string) =>
        request<void>(`/api/admin/config/${encodeURIComponent(key)}/secret`, {
          method: 'PUT', body: JSON.stringify({ value }),
        }),
    },
    /** The Certificates section (phase 83) — a second door onto `dbdatasync cert …`. None of these ever
     * return or accept a private key. */
    certificate: {
      get: () => request<AdminCertificateStatus>('/api/admin/certificate'),
      candidates: () => request<CertificateCandidate[]>('/api/admin/certificate/candidates'),
      createSelfSigned: (dnsNames: string[], validityDays: number | null) =>
        request<CertificateActionResult>('/api/admin/certificate/self-signed', {
          method: 'POST', body: JSON.stringify({ dnsNames, validityDays }),
        }),
      enroll: (dnsNames: string[], template: string, caConfig: string | null) =>
        request<CertificateActionResult>('/api/admin/certificate/enroll', {
          method: 'POST', body: JSON.stringify({ dnsNames, template, caConfig }),
        }),
      retrieve: (requestId: string) =>
        request<CertificateActionResult>('/api/admin/certificate/retrieve', {
          method: 'POST', body: JSON.stringify({ requestId }),
        }),
      bind: (thumbprint: string, allowInvalid: boolean | null) =>
        request<CertificateActionResult>('/api/admin/certificate/bind', {
          method: 'POST', body: JSON.stringify({ thumbprint, allowInvalid }),
        }),
    },
    /** Whether this process has a config value, library, or driver change on disk it hasn't picked up
     * yet (phase 120) — a server-side flag, not just this tab's own session state, so a different
     * admin's tab (or this one after a reload) still learns about a change another request made. */
    restartRequired: {
      get: () => request<RestartRequiredStatus>('/api/admin/restart-required'),
    },
  },
  users: {
    list: () => request<UserSummary[]>('/api/users'),
    update: (id: string, changes: { role?: string; enabled?: boolean }) =>
      put<UserSummary>(`/api/users/${encodeURIComponent(id)}`, changes),
    removeCredential: (id: string, credentialId: string) =>
      request<void>(
        `/api/users/${encodeURIComponent(id)}/credentials/${encodeURIComponent(credentialId)}`,
        { method: 'DELETE' },
      ),
  },
  libraries: {
    /** Every installed library (phase 118's admin Libraries screen). Admin-only, unlike `drivers.list`
     * below — this reveals what's on disk, not just what a connection editor picker offers. */
    list: () => request<LibrarySummary[]>('/api/libraries'),
    knownLibraries: () => request<KnownLibrarySummary[]>('/api/known-libraries'),
    knownDrivers: () => request<KnownDriverSummary[]>('/api/known-drivers'),
    /** A read-only proxy onto the public NuGet index (phase 119). A disabled deployment answers 503,
     * which `request` surfaces as a thrown `ApiError` — the caller treats that the same as a
     * `status: "unavailable"` body (a 200 the call still resolved with), both meaning "fall back to
     * manual entry". */
    search: (q: string) => request<LibrarySearchResponse>(`/api/libraries/search?q=${encodeURIComponent(q)}`),
    /** Installs synchronously — the request holds until `dotnet publish` finishes restoring the
     * package (phase 120). `factoryType` is required only when `packageId` isn't a curated one. */
    create: (body: { packageId: string; version: string; factoryType?: string; source?: string }) =>
      request<LibraryManifest>('/api/libraries', { method: 'POST', body: JSON.stringify(body) }),
    /** Refused (409) while a driver still names this library, unless `force`. */
    remove: (id: string, force?: boolean) =>
      request<void>(`/api/libraries/${encodeURIComponent(id)}${force ? '?force=true' : ''}`, { method: 'DELETE' }),
  },
  drivers: {
    /** Every registered driver — the three built-ins plus any `driver.yaml` descriptor an operator has
     * added (phase 109d). What the connection editor's engine picker reads instead of a hardcoded list. */
    list: () => request<DriverSummary[]>('/api/drivers'),
    /** What a connection of this driver takes, given what it has been given so far. A POST because
     * the answer depends on the values — Host is not a setting in connection-string mode — and those
     * are an arbitrary operator-typed bag that does not belong in a query string. */
    connectionParameters: (driverType: DriverType, values: Record<string, string>) =>
      request<ParameterDescriptor[]>(
        `/api/drivers/${encodeURIComponent(driverType)}/connection-parameters`,
        { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(values) },
      ),
    /** The one-click "add" from a bundled `KnownDrivers` catalog entry (phase 120) — installs its
     * bound library (reusing it if already installed) and writes the descriptor. */
    installFromCatalog: (knownDriverId: string, version: string) =>
      request<FromCatalogResult>('/api/drivers/from-catalog', {
        method: 'POST', body: JSON.stringify({ knownDriverId, version }),
      }),
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
    // State, not config — this never commits. The note travels with every action, in both
    // directions, because the popup asks every time.
    setPaused: (name: string, paused: boolean, note: string | null) =>
      put<ReplicationStatus>(`/api/replications/${encodeURIComponent(name)}/paused`, { paused, note }),
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
  notifications: {
    /** The whole feed by default. `sinceId` exists for a future notification centre that pages
     * through it; the bell wants the latest and asks for all of it, which retention bounds. */
    list: (sinceId?: number) =>
      request<NotificationFeed>(`/api/notifications${sinceId ? `?sinceId=${sinceId}` : ''}`),
    markSeen: (lastSeenNotificationId: number) =>
      request<NotificationFeed>('/api/notifications/seen', {
        method: 'POST',
        body: JSON.stringify({ lastSeenNotificationId }),
      }),
  },
  runs: {
    trigger: (replicationName: string) =>
      request<TriggerResponse>(`/api/replications/${encodeURIComponent(replicationName)}/runs`, { method: 'POST' }),
    /**
     * A page of run history — filtered and paged server-side since phase 104. `filters` and `limit`
     * are shared with `watermarkTimes` below through {@link runHistoryQuery}, one function building
     * both query strings, so the two cannot quietly stop asking about the same page.
     */
    history: (replicationName: string, filters: RunHistoryFilters = {}, limit = 50) =>
      request<RunHistoryPage>(
        `/api/replications/${encodeURIComponent(replicationName)}/runs?${runHistoryQuery(filters, limit)}`,
      ),
    /**
     * When those same runs' watermarks were the source's position, keyed by run id — see phase 88.
     *
     * The same filters, cursor and limit as `history`, so the two answers cover the same page — a
     * filtered or paged run that has a watermark must not show a blank one just because this call
     * asked a different question than the list beside it did. A run with no timestamp at all is
     * absent rather than present with two nulls.
     */
    watermarkTimes: (replicationName: string, filters: RunHistoryFilters = {}, limit = 50) =>
      request<Record<string, RunWatermarkTimes>>(
        `/api/replications/${encodeURIComponent(replicationName)}/runs/watermark-times?${runHistoryQuery(filters, limit)}`,
      ),
    get: (runId: string) => request<TaskRunRecord>(`/api/runs/${runId}`),
    /** Reloads the table and clears the stored watermark, for a run whose source position expired. */
    resync: (runId: string) =>
      request<TriggerResponse>(`/api/runs/${runId}/resync`, { method: 'POST' }),
    logs: (runId: string, sinceId?: number) =>
      request<LogEntryRecord[]>(`/api/runs/${runId}/logs${sinceId ? `?sinceId=${sinceId}` : ''}`),
    cancel: (runId: string) => request<void>(`/api/runs/${runId}/cancel`, { method: 'POST' }),
    /**
     * What a segmenting strategy proposes for this mapping right now. Every candidate, selected or
     * not — the checklist is a proposal to disagree with, not an announcement.
     */
    previewSegmenting: (replicationName: string, mappingName: string, strategyName: string) =>
      request<{ candidates: SegmentCandidate[] }>(
        `/api/replications/${encodeURIComponent(replicationName)}/mappings/${encodeURIComponent(mappingName)}` +
          `/segmenting/${encodeURIComponent(strategyName)}/preview`,
      ),
    /**
     * The same preview, for a strategy that has not been saved — the editor's Test button. A POST
     * only because the strategy travels in the body; it writes nothing.
     */
    previewUnsavedSegmenting: (
      replicationName: string, mappingName: string, strategy: SegmentingStrategyConfig,
    ) =>
      request<{ candidates: SegmentCandidate[] }>(
        `/api/replications/${encodeURIComponent(replicationName)}/mappings/${encodeURIComponent(mappingName)}` +
          `/segmenting/preview`,
        { method: 'POST', body: JSON.stringify(strategy) },
      ),
    backfill: (replicationName: string, mappingName: string, body: BackfillRequest) =>
      request<TriggerResponse>(
        `/api/replications/${encodeURIComponent(replicationName)}/mappings/${encodeURIComponent(mappingName)}/backfill`,
        { method: 'POST', body: JSON.stringify(body) },
      ),
    /** Phase 124's delete-diff sweep trigger — same shape as `backfill` above (one RunId per segment),
     * always through KeyReconcile/KeyReconcileDelete. */
    reconcileDeletes: (replicationName: string, mappingName: string, body: ReconcileDeletesRequest) =>
      request<TriggerResponse>(
        `/api/replications/${encodeURIComponent(replicationName)}/mappings/${encodeURIComponent(mappingName)}/reconcile-deletes`,
        { method: 'POST', body: JSON.stringify(body) },
      ),
  },
}

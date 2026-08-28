import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback } from 'react'
import { api } from './client'
import type {
  ScriptDefinition, ScriptTestRequest, MetricsWindow, BackfillRequest, ConnectionInput, ReplicationTaskConfig, TableMappingConfig } from './types'

// Query keys are centralized here so mutations know exactly what to invalidate.
const keys = {
  connections: ['connections'] as const,
  connection: (name: string) => ['connections', name] as const,
  capabilities: (name: string) => ['connections', name, 'capabilities'] as const,
  scripts: ['scripts'] as const,
  script: (name: string) => ['scripts', name] as const,
  scriptSlots: ['scripts', 'slots'] as const,
  credentialSource: (name: string) => ['connections', name, 'credential-source'] as const,
  databases: (connectionName: string) => ['metadata', connectionName, 'databases'] as const,
  tables: (connectionName: string, database: string) => ['metadata', connectionName, database, 'tables'] as const,
  columns: (connectionName: string, database: string, schema: string, table: string) =>
    ['metadata', connectionName, database, schema, table, 'columns'] as const,
  replications: ['replications'] as const,
  replication: (name: string) => ['replications', name] as const,
  replicationHistory: (name: string) => ['replications', name, 'history'] as const,
  tableMappings: (replicationName: string) => ['replications', replicationName, 'table-mappings'] as const,
  tableMapping: (replicationName: string, mappingName: string) =>
    ['replications', replicationName, 'table-mappings', mappingName] as const,
  runHistory: (replicationName: string) => ['replications', replicationName, 'runs'] as const,
  run: (runId: string) => ['runs', runId] as const,
  provisioning: (replicationName: string, mappingName: string) =>
    ['replications', replicationName, 'table-mappings', mappingName, 'provisioning'] as const,
  preview: (replicationName: string, mappingName: string) =>
    ['replications', replicationName, 'table-mappings', mappingName, 'preview'] as const,
  metrics: (replicationName: string, window: string) =>
    ['replications', replicationName, 'metrics', window] as const,
}

export function useConnections() {
  return useQuery({ queryKey: keys.connections, queryFn: api.connections.list })
}

export function useConnection(name: string | undefined) {
  return useQuery({
    queryKey: keys.connection(name ?? ''),
    queryFn: () => api.connections.get(name!),
    enabled: !!name,
  })
}

export function useUpsertConnection() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ name, input }: { name: string; input: ConnectionInput }) => api.connections.upsert(name, input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.connections }),
  })
}

export function useDeleteConnection() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => api.connections.delete(name),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.connections }),
  })
}

/**
 * What the driver behind one connection supports. Cached hard: capabilities are fixed for the
 * lifetime of the API process (they're derived from which drivers are registered in it), so
 * refetching them on window focus would be pure noise.
 */
export function useCapabilities(connectionName: string | undefined) {
  return useQuery({
    queryKey: keys.capabilities(connectionName ?? ''),
    queryFn: () => api.connections.capabilities(connectionName!),
    enabled: !!connectionName,
    staleTime: Infinity,
  })
}

/**
 * The reader/staging/writer Kinds available to one replication. Readers are resolved against its
 * source connection's driver and staging/writers against its target's, since that is where each
 * actually runs — taken from the replication's first table mapping.
 *
 * A replication with no mappings yet has no connections to ask, so it falls back to the first
 * configured connection: with a single registered driver that is the same answer, and it keeps a
 * brand-new replication's settings editable instead of showing empty pickers until a mapping exists.
 */
export function useReplicationCapabilities(replicationName: string | undefined) {
  const { data: mappingNames } = useTableMappings(replicationName)
  const { data: mapping } = useTableMapping(replicationName, mappingNames?.[0])
  const { data: connections } = useConnections()

  const fallbackConnection = connections?.[0]?.name
  const source = useCapabilities(mapping?.sources[0]?.connectionName ?? fallbackConnection)
  const target = useCapabilities(mapping?.targets[0]?.connectionName ?? fallbackConnection)

  return {
    readers: source.data?.readers ?? [],
    stagingProviders: target.data?.stagingProviders ?? [],
    writers: target.data?.writers ?? [],
    isLoading: source.isLoading || target.isLoading,
    error: source.error ?? target.error,
  }
}

/**
 * Capabilities to offer before a replication exists to resolve them against — creating one, where
 * there are no table mappings and so no connections of its own yet. Uses the first configured
 * connection: with one registered driver that is the same answer, and it is a far better default than
 * a list of Kind strings compiled into this app, which would be a guess about the server's drivers.
 */
/** Capabilities for a driver type — what a connection being created can ask, having no name yet. */
export function useDriverCapabilities(driverType: string | undefined) {
  return useQuery({
    queryKey: ['drivers', driverType ?? '', 'capabilities'] as const,
    queryFn: () => api.connections.capabilitiesForDriver(driverType!),
    enabled: !!driverType,
  })
}

export function useDefaultCapabilities() {
  const { data: connections } = useConnections()
  return useCapabilities(connections?.[0]?.name)
}

export function useDatabases(connectionName: string | undefined) {
  return useQuery({
    queryKey: keys.databases(connectionName ?? ''),
    queryFn: () => api.metadata.databases(connectionName!),
    enabled: !!connectionName,
  })
}

export function useTables(connectionName: string | undefined, database: string | undefined) {
  return useQuery({
    queryKey: keys.tables(connectionName ?? '', database ?? ''),
    queryFn: () => api.metadata.tables(connectionName!, database!),
    enabled: !!connectionName && !!database,
  })
}

export function useColumns(
  connectionName: string | undefined,
  database: string | undefined,
  schema: string | undefined,
  table: string | undefined,
) {
  return useQuery({
    queryKey: keys.columns(connectionName ?? '', database ?? '', schema ?? '', table ?? ''),
    queryFn: () => api.metadata.columns(connectionName!, database!, schema!, table!),
    enabled: !!connectionName && !!database && !!schema && !!table,
  })
}

export function useReplications() {
  return useQuery({ queryKey: keys.replications, queryFn: api.replications.list })
}

export function useReplication(name: string | undefined) {
  return useQuery({
    queryKey: keys.replication(name ?? ''),
    queryFn: () => api.replications.get(name!),
    enabled: !!name,
  })
}

export function useUpsertReplication() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ name, task }: { name: string; task: ReplicationTaskConfig }) => api.replications.upsert(name, task),
    onSuccess: (_data, { name }) => {
      queryClient.invalidateQueries({ queryKey: keys.replications })
      queryClient.invalidateQueries({ queryKey: keys.replication(name) })
      queryClient.invalidateQueries({ queryKey: keys.replicationHistory(name) })
    },
  })
}

export function useDeleteReplication() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => api.replications.delete(name),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.replications }),
  })
}

export function useReplicationHistory(name: string | undefined) {
  return useQuery({
    queryKey: keys.replicationHistory(name ?? ''),
    queryFn: () => api.replications.history(name!),
    enabled: !!name,
  })
}

export function useTableMappings(replicationName: string | undefined) {
  return useQuery({
    queryKey: keys.tableMappings(replicationName ?? ''),
    queryFn: () => api.tableMappings.list(replicationName!),
    enabled: !!replicationName,
  })
}

export function useTableMapping(replicationName: string | undefined, mappingName: string | undefined) {
  return useQuery({
    queryKey: keys.tableMapping(replicationName ?? '', mappingName ?? ''),
    queryFn: () => api.tableMappings.get(replicationName!, mappingName!),
    enabled: !!replicationName && !!mappingName,
  })
}

export function useUpsertTableMapping(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ mappingName, mapping }: { mappingName: string; mapping: TableMappingConfig }) =>
      api.tableMappings.upsert(replicationName, mappingName, mapping),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: keys.tableMappings(replicationName) })
      queryClient.invalidateQueries({ queryKey: keys.replicationHistory(replicationName) })
    },
  })
}

export function useDeleteTableMapping(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (mappingName: string) => api.tableMappings.delete(replicationName, mappingName),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.tableMappings(replicationName) }),
  })
}

export function useRunHistory(replicationName: string | undefined, refetchInterval?: number) {
  return useQuery({
    queryKey: keys.runHistory(replicationName ?? ''),
    queryFn: () => api.runs.history(replicationName!),
    enabled: !!replicationName,
    refetchInterval,
  })
}

/**
 * A run's completion is observed by useRunHub (SignalR + a tight REST poll), independently of
 * useRunHistory's own interval-based polling — so by the time a run is known to be complete, the
 * history list's periodic poll may not have landed on a fetch taken after the final TaskRuns row was
 * written, leaving RowsRead/RowsWritten showing a stale (e.g. placeholder 0/0) snapshot until the
 * next scheduled poll happens to fire. Call the returned function right when completion is observed
 * to force an authoritative refetch instead of waiting on that timing.
 */
export function useInvalidateRunHistory(replicationName: string) {
  const queryClient = useQueryClient()
  return useCallback(
    () => queryClient.invalidateQueries({ queryKey: keys.runHistory(replicationName) }),
    [queryClient, replicationName],
  )
}

export function useTriggerRun(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api.runs.trigger(replicationName),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.runHistory(replicationName) }),
  })
}

/**
 * Queues a reload of one table mapping. Returns one RunId per segment — an Auto segment is expanded
 * server-side, so a single submission can produce many independently-scheduled runs.
 */
export function useBackfill(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ mappingName, request }: { mappingName: string; request: BackfillRequest }) =>
      api.runs.backfill(replicationName, mappingName, request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.runHistory(replicationName) }),
  })
}

export function useCancelRun(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (runId: string) => api.runs.cancel(runId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.runHistory(replicationName) }),
  })
}

/**
 * Tests one connection, on demand.
 *
 * A mutation rather than a query on purpose: opening every configured database because a list
 * rendered is a surprising thing for a console to do, and the result is a reading taken at a moment,
 * not state to keep fresh. Nothing here polls.
 */
export function useTestConnection() {
  return useMutation({ mutationFn: (name: string) => api.connections.test(name) })
}

/** Which secret a connection resolves through. Fixed for a given name, so cached like capabilities. */
export function useCredentialSource(connectionName: string | undefined) {
  return useQuery({
    queryKey: keys.credentialSource(connectionName ?? ''),
    queryFn: () => api.connections.credentialSource(connectionName!),
    enabled: !!connectionName,
    staleTime: Infinity,
  })
}

/** The global script registry. */
export function useScripts() {
  return useQuery({ queryKey: keys.scripts, queryFn: () => api.scripts.list() })
}

/** Which slots this build supports — from the API, not hardcoded here, the same way driver
 * capabilities are. A slot added server-side appears without an SPA change. */
export function useScriptSlots() {
  return useQuery({ queryKey: keys.scriptSlots, queryFn: () => api.scripts.slots(), staleTime: Infinity })
}

export function useScript(name: string | undefined) {
  return useQuery({
    queryKey: keys.script(name ?? ''),
    queryFn: () => api.scripts.get(name!),
    enabled: !!name,
  })
}

export function useUpsertScript() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ name, script }: { name: string; script: ScriptDefinition }) => api.scripts.upsert(name, script),
    onSuccess: (_, { name }) => {
      queryClient.invalidateQueries({ queryKey: keys.scripts })
      queryClient.invalidateQueries({ queryKey: keys.script(name) })
    },
  })
}

/** Compiles without saving. A mutation because it is an action, and deliberately not cached — the
 * answer is about the code in the editor right now. */
export function useCompileScript() {
  return useMutation({
    mutationFn: ({ name, script }: { name: string; script: ScriptDefinition }) => api.scripts.compile(name, script),
  })
}

export function useDeleteScript() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => api.scripts.delete(name),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.scripts }),
  })
}

/** Both sides' provisioning plans for one table mapping — the Setup card's state badges and preview SQL. */
/** What a pass would run. Not cached across mounts for long: it reflects the mapping as saved *and*
 * the watermark as stored, both of which move. */
export function useMappingPreview(replicationName: string | undefined, mappingName: string | undefined) {
  return useQuery({
    queryKey: keys.preview(replicationName ?? '', mappingName ?? ''),
    queryFn: () => api.preview.get(replicationName!, mappingName!),
    enabled: !!replicationName && !!mappingName,
  })
}

/** Not a query: a test is an explicit action against sample — or real — data, and must never happen
 * as a side effect of a refetch. Same shape as phase 19's connection test. */
export function useTestScript() {
  return useMutation({
    mutationFn: ({ name, request }: { name: string; request: ScriptTestRequest }) =>
      api.scripts.test(name, request),
  })
}

/** Polled rather than pushed: a 24-hour aggregate is not something anyone watches change, and the
 * case where someone is watching is a live run, which the run hub already covers. */
export function useRunMetrics(replicationName: string | undefined, window: MetricsWindow) {
  return useQuery({
    queryKey: keys.metrics(replicationName ?? '', window),
    queryFn: () => api.metrics.get(replicationName!, window),
    enabled: !!replicationName,
    refetchInterval: 30_000,
  })
}

export function useProvisioning(replicationName: string | undefined, mappingName: string | undefined) {
  return useQuery({
    queryKey: keys.provisioning(replicationName ?? '', mappingName ?? ''),
    queryFn: () => api.provisioning.get(replicationName!, mappingName!),
    enabled: !!replicationName && !!mappingName,
  })
}

/** Re-plans and applies one provisioning action. A mutation, not folded into the query above: Apply
 * is a deliberate, confirmed action against a live database, not something that should ever happen as
 * a side effect of a refetch. */
export function useApplyProvisioning(replicationName: string, mappingName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (action: string) => api.provisioning.apply(replicationName, mappingName, action),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.provisioning(replicationName, mappingName) }),
  })
}

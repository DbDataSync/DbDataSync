import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback } from 'react'
import { api } from './client'
import type {
  BulkCreateRequest,
  DriverType,
  ScriptDefinition, ScriptTestRequest, MetricsWindow, BackfillRequest, ConnectionInput, ReplicationTaskConfig, SegmentingStrategyConfig, TableMappingConfig } from './types'

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
  runWatermarkTimes: (replicationName: string) =>
    ['replications', replicationName, 'runs', 'watermark-times'] as const,
  run: (runId: string) => ['runs', runId] as const,
  provisioning: (replicationName: string, mappingName: string) =>
    ['replications', replicationName, 'table-mappings', mappingName, 'provisioning'] as const,
  inferredColumnTypes: (replicationName: string, mappingName: string) =>
    ['replications', replicationName, 'table-mappings', mappingName, 'inferred-column-types'] as const,
  inferredNaturalKey: (replicationName: string, mappingName: string) =>
    ['replications', replicationName, 'table-mappings', mappingName, 'inferred-natural-key'] as const,
  preview: (replicationName: string, mappingName: string) =>
    ['replications', replicationName, 'table-mappings', mappingName, 'preview'] as const,
  replicationStatus: (replicationName: string) =>
    ['replications', replicationName, 'status'] as const,
  metrics: (replicationName: string, window: string) =>
    ['replications', replicationName, 'metrics', window] as const,
  replicationLag: (replicationName: string) => ['replications', replicationName, 'lag'] as const,
  verificationResults: (replicationName: string, mappingName: string) =>
    ['replications', replicationName, 'verification-results', mappingName] as const,
  verificationResult: (replicationName: string, id: number) =>
    ['replications', replicationName, 'verification-results', id] as const,
  notifications: ['notifications'] as const,
  adminConfig: ['admin', 'config'] as const,
  adminCertificate: ['admin', 'certificate'] as const,
  adminCertificateCandidates: ['admin', 'certificate', 'candidates'] as const,
}

/**
 * The notification feed, polled — see phase 77.
 *
 * Polled rather than pushed: the run hub exists for somebody watching one run live, and a bell is the
 * opposite case, a person who is not looking. Fifteen seconds is well inside the interval at which
 * anybody notices, and one small query at that rate is nothing next to the scheduler's own tick.
 */
export function useNotifications() {
  return useQuery({
    queryKey: keys.notifications,
    queryFn: () => api.notifications.list(),
    refetchInterval: 15_000,
    refetchOnWindowFocus: true,
  })
}

/** Advances the read cursor. Only ever moves forward, which the server enforces. */
export function useMarkNotificationsSeen() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (lastSeenNotificationId: number) => api.notifications.markSeen(lastSeenNotificationId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.notifications }),
  })
}

/**
 * Who the caller is. Refetched on window focus, so a session that expired while a tab was in the
 * background is noticed when somebody comes back to it rather than at their next click.
 */
export function useAuthStatus() {
  return useQuery({
    queryKey: ['auth', 'status'] as const,
    queryFn: () => api.auth.status(),
    refetchOnWindowFocus: true,
    retry: false,
  })
}

export function useSignInWithWindows() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api.auth.signInWithWindows(),
    // Everything on screen was fetched as nobody; none of it is right any more.
    onSuccess: () => queryClient.invalidateQueries(),
  })
}

export function useSignOut() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api.auth.signOut(),
    onSuccess: () => queryClient.invalidateQueries(),
  })
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
// A by-driver-type capabilities hook lived here from phase 42 until phase 50, for the connection
// form's declared settings. Those now come from POST /api/drivers/{type}/connection-parameters, which
// is the same question asked with the values that decide the answer — so nothing in this app asks the
// valueless version any more. The endpoint stays; a hook with no caller does not.

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

/**
 * Every one of a replication's mappings, loaded in full.
 *
 * N requests, and deliberately so rather than a new endpoint: the mappings sidebar is mounted
 * alongside every screen that wants this and already loads each mapping by name, so these come back
 * from react-query's cache without a second request being made. An endpoint would be a new surface
 * to keep correct in exchange for saving nothing.
 */
export function useTableMappingDetails(replicationName: string, names: string[] | undefined) {
  return useQueries({
    queries: (names ?? []).map((name) => ({
      queryKey: keys.tableMapping(replicationName, name),
      queryFn: () => api.tableMappings.get(replicationName, name),
    })),
  })
}

/**
 * Creates one mapping per selected table in a single request, reporting progress over the run hub's
 * push channel under a batch id the caller owns.
 */
export function useBulkCreateMappings(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: BulkCreateRequest) => api.tableMappings.bulkCreate(replicationName, request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.tableMappings(replicationName) }),
  })
}

/**
 * What a connection of this driver takes, given the values it has so far.
 *
 * Keyed on the driver and on the values of the parameters the *server* marked `recalc`, so typing in
 * a host field costs nothing and switching address mode asks again. The request body carries the live
 * values rather than the key, because the key is only the subset that changes the answer.
 *
 * The first render has no descriptors yet and so an empty recalc key; the body still carries the real
 * draft, so that first answer is correct and the one refetch that follows changes nothing on screen.
 */
export function useConnectionParameters(
  driverType: DriverType | undefined,
  values: Record<string, string>,
  recalcKey: Record<string, string>,
) {
  return useQuery({
    queryKey: ['drivers', driverType ?? '', 'connection-parameters', recalcKey] as const,
    queryFn: () => api.drivers.connectionParameters(driverType!, values),
    enabled: !!driverType,
    placeholderData: (previous) => previous,
  })
}

/**
 * The recovery for a run whose source position expired: reload the table, and clear the watermark so
 * the incremental pass can start again.
 *
 * A mutation and never a refetch side effect — a full reload of a table that fell behind can be hours
 * of work, and it happens because somebody pressed the button.
 */
export function useResyncRun(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (runId: string) => api.runs.resync(runId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.replicationHistory(replicationName) }),
  })
}

export function useReplication(name: string | undefined) {
  return useQuery({
    queryKey: keys.replication(name ?? ''),
    queryFn: () => api.replications.get(name!),
    enabled: !!name,
  })
}

/**
 * What the worker is doing, polled.
 *
 * Pulled rather than pushed, on run-metrics' call — and at five seconds rather than thirty, because a
 * worker's whole life is measured in seconds: a card that refreshed twice a minute would mostly show
 * a process that has already exited.
 */
export function useReplicationStatus(replicationName: string | undefined) {
  return useQuery({
    queryKey: keys.replicationStatus(replicationName ?? ''),
    queryFn: () => api.replicationStatus.get(replicationName!),
    enabled: !!replicationName,
    refetchInterval: 5_000,
  })
}

/**
 * Turns a replication on or off, on its own.
 *
 * Deliberately not the upsert: this commits immediately from any tab, and it must not carry along
 * whatever unsaved edit is sitting in the Overview's draft. The replication query is invalidated so
 * the accent that reflects enabled/disabled follows.
 */
export function useSetReplicationEnabled(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (enabled: boolean) => api.replicationStatus.setEnabled(replicationName, enabled),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: keys.replication(replicationName) })
      queryClient.invalidateQueries({ queryKey: keys.replications })
    },
  })
}

/**
 * Holds a replication, or releases it, with whatever note the operator entered.
 *
 * Invalidates the status query and nothing else: pausing writes no config, so the replication query
 * has not changed and refetching it would be a request for an answer nobody asked a new question
 * about.
 */
export function useSetReplicationPaused(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ paused, note }: { paused: boolean; note: string | null }) =>
      api.replicationStatus.setPaused(replicationName, paused, note),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: keys.replicationStatus(replicationName) })
    },
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

/**
 * The one cadence the three monitoring panels share — the run list, the metrics card and the lag
 * tab (phase 88).
 *
 * Ten seconds, down from thirty on the two that polled and up from never on the one that only
 * listened. It is a single constant rather than three literals because the number is now a promise
 * the UI makes out loud: each of those panels renders a countdown to its own next refresh, and a
 * panel whose interval drifted from the number beside it would be worse than one with no countdown
 * at all.
 *
 * Deliberately not applied to `useReplicationStatus` (5s), `useNotifications` (15s) or
 * `useVerificationResults` (5s). Those cadences answer different questions — two of them are
 * screens somebody is watching immediately after pressing a button — and none of them was asked
 * about.
 */
export const MONITORING_REFRESH_MS = 10_000

export function useRunHistory(replicationName: string | undefined, refetchInterval?: number) {
  return useQuery({
    queryKey: keys.runHistory(replicationName ?? ''),
    queryFn: () => api.runs.history(replicationName!),
    enabled: !!replicationName,
    refetchInterval,
  })
}

/**
 * When each run in that history moved its mapping's watermark, dated out of polling history — see
 * phase 88.
 *
 * A second query rather than fields on the run record, because the answer is derived on read and
 * changes as `ChangeCheckHistory`'s retention window moves; the run itself is durable and does not.
 * It shares the run history's cadence so the two halves of a row never disagree by a poll.
 *
 * Its key is nested under `runHistory`'s, which means every existing invalidation of the run list —
 * a trigger, a cancel, a run completing on the hub — refreshes these timestamps too. That is what
 * should happen: a pass that just ended is exactly the one whose new watermark is worth dating.
 */
export function useRunWatermarkTimes(
  replicationName: string | undefined, refetchInterval?: number,
) {
  return useQuery({
    queryKey: keys.runWatermarkTimes(replicationName ?? ''),
    queryFn: () => api.runs.watermarkTimes(replicationName!),
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

/**
 * Runs a segmenting strategy and returns everything it proposes.
 *
 * Enabled only once a strategy is actually picked. Safe to run on selection for a DuckDB strategy,
 * which opens no connection at all; the other three reach a real database, which is why the form
 * says so beside the picker before offering them.
 */
/**
 * Runs a strategy that is still being written and hands back what it proposes — the editor's Test
 * button (phase 61).
 *
 * A mutation rather than a query, even though it reads: it runs when somebody presses Test, and a
 * query keyed on a half-typed SQL string would run against a real connection on every keystroke for
 * the two connection-bound kinds.
 */
/**
 * Runs the query currently in the source tab's editor and hands back what it returned.
 *
 * A mutation rather than a query, for the reason `useTestSegmentingStrategy` is one: it runs when
 * somebody presses Preview. Keyed as a query on a half-typed SQL string, it would execute against a
 * real system on every keystroke — which is the one thing a preview of arbitrary SQL must not do.
 */
export function usePreviewQuery() {
  return useMutation({
    mutationFn: ({ connectionName, query }: { connectionName: string; query: string }) =>
      api.connections.queryPreview(connectionName, query),
  })
}

export function useTestSegmentingStrategy(replicationName: string) {
  return useMutation({
    mutationFn: ({ mappingName, strategy }: { mappingName: string; strategy: SegmentingStrategyConfig }) =>
      api.runs.previewUnsavedSegmenting(replicationName, mappingName, strategy),
  })
}

export function useSegmentingPreview(
  replicationName: string,
  mappingName: string,
  strategyName: string | null,
) {
  return useQuery({
    queryKey: ['replications', replicationName, 'table-mappings', mappingName, 'segmenting', strategyName] as const,
    queryFn: () => api.runs.previewSegmenting(replicationName, mappingName, strategyName!),
    enabled: Boolean(replicationName && mappingName && strategyName),
    // A strategy that computes from "today" has to be re-run rather than served from cache — the
    // whole reason its default is a reference and not a frozen list.
    staleTime: 0,
    gcTime: 0,
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
    refetchInterval: MONITORING_REFRESH_MS,
  })
}

/**
 * Every mapping's lag, and the range across them — see phase 86.
 *
 * `MONITORING_REFRESH_MS`, the same cadence `useRunMetrics` polls on — ten seconds since phase 88,
 * thirty before it. Lag is still a figure somebody reads when they go looking rather than one they
 * watch move; what changed is that the tab now shows when the next reading lands, and a wait of
 * half a minute for a number somebody is standing in front of is longer than it needs to be.
 *
 * One query per replication, not one per mapping. The Monitoring tab and each row of the
 * replications list share this key, so a list of ten replications is ten requests rather than ten
 * times however many mappings each has.
 */
export function useReplicationLag(replicationName: string | undefined) {
  return useQuery({
    queryKey: keys.replicationLag(replicationName ?? ''),
    queryFn: () => api.replications.lag(replicationName!),
    enabled: !!replicationName,
    refetchInterval: MONITORING_REFRESH_MS,
  })
}

/**
 * Past results for one mapping, most recent first.
 *
 * Polled, because "Run checks" queues a run rather than performing one: the mutation succeeds the
 * moment the work is enqueued, and the result arrives seconds later when a TaskRunner has done it.
 * Invalidating on the mutation refetches too early and then never again, which leaves an operator
 * looking at an unchanged screen wondering whether the button worked.
 *
 * Five seconds, not the metrics card's thirty: this is a screen somebody is watching immediately
 * after pressing a button, which is exactly the case a 24-hour aggregate is not.
 */
export function useVerificationResults(replicationName: string, mappingName: string | undefined) {
  return useQuery({
    queryKey: keys.verificationResults(replicationName, mappingName ?? ''),
    queryFn: () => api.verification.results(replicationName, mappingName!),
    enabled: !!mappingName,
    refetchInterval: 5_000,
  })
}

/**
 * One page of a result, read back out of the parquet the runner wrote.
 *
 * `placeholderData` keeps the previous page on screen while the next one loads, so paging through a
 * large result does not blink the table away and back on every click.
 */
export function useVerificationResult(
  replicationName: string,
  id: number | undefined,
  offset: number,
  limit: number,
  differingOnly: boolean,
) {
  return useQuery({
    queryKey: [...keys.verificationResult(replicationName, id ?? 0), offset, limit, differingOnly] as const,
    queryFn: () => api.verification.result(replicationName, id!, offset, limit, differingOnly),
    enabled: !!id,
    placeholderData: (previous) => previous,
  })
}

/** Throws away one result — its index row and its file. */
export function useDeleteVerificationResult(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api.verification.deleteResult(replicationName, id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['replications', replicationName, 'verification-results'] }),
  })
}

/** Running checks is an explicit action, like phase 41's script test and phase 19's connection test —
 * never something that happens as a side effect of a refetch. */
export function useRunVerification(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (mappingName: string) => api.verification.run(replicationName, mappingName),
    onSuccess: () => queryClient.invalidateQueries({
      queryKey: ['replications', replicationName, 'verification-results'],
    }),
  })
}

export function useProvisioning(replicationName: string | undefined, mappingName: string | undefined) {
  return useQuery({
    queryKey: keys.provisioning(replicationName ?? '', mappingName ?? ''),
    queryFn: () => api.provisioning.get(replicationName!, mappingName!),
    enabled: !!replicationName && !!mappingName,
  })
}

/**
 * What each source column would become on the target — the type the column mapping editor shows on
 * every row before anyone overrides it.
 *
 * A separate query from `useProvisioning` rather than a field on the plan: the editor needs it while
 * the operator is still choosing columns, which is exactly when there is no plan worth showing.
 * Failures are silent by design (`retry: false`), since a source table that is not reachable yet is
 * normal during setup and the editor degrades to showing no inferred type.
 */
export function useInferredColumnTypes(replicationName: string | undefined, mappingName: string | undefined) {
  return useQuery({
    queryKey: keys.inferredColumnTypes(replicationName ?? '', mappingName ?? ''),
    queryFn: () => api.provisioning.inferredColumnTypes(replicationName!, mappingName!),
    enabled: !!replicationName && !!mappingName,
    retry: false,
  })
}

/**
 * What this mapping's natural key would be derived as — the mapping Pipeline tab's read path for the
 * SCD Type 2 writer (phase 68).
 *
 * Same shape as `useInferredColumnTypes` beside it, and `retry: false` for the same reason: a source
 * table that is not there yet 404s, which is normal during setup and not worth three attempts.
 */
export function useInferredNaturalKey(
  replicationName: string | undefined, mappingName: string | undefined, enabled = true) {
  return useQuery({
    queryKey: keys.inferredNaturalKey(replicationName ?? '', mappingName ?? ''),
    queryFn: () => api.provisioning.inferredNaturalKey(replicationName!, mappingName!),
    enabled: enabled && !!replicationName && !!mappingName,
    retry: false,
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

/** Every DataSync:* key CONFIG.md documents — the admin config screen (phase 81). Not polled: this is
 * process configuration, not something that changes underneath an open tab. */
export function useAdminConfig() {
  return useQuery({ queryKey: keys.adminConfig, queryFn: api.admin.config.list })
}

/** Writes one key into datasync.config.yaml — a direct edit of a file-sourced row, or "adopt" of one
 * that is not. Same mutation either way; the page decides which value to send. */
export function useSetAdminConfig() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => api.admin.config.set(key, value),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.adminConfig }),
  })
}

/** StateConnectionString's password, through the secret store. A separate mutation from the value
 * above because it never touches datasync.config.yaml and the page never sees what it sets. */
export function useSetAdminConfigSecret() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => api.admin.config.setSecret(key, value),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.adminConfig }),
  })
}

/** The bound certificate, its expiry, binding state and key access (phase 83). Not polled, the same
 * reasoning as useAdminConfig: this is process/host state, not something that changes underneath an
 * open tab on its own — an action below invalidates it explicitly instead. */
export function useAdminCertificate() {
  return useQuery({ queryKey: keys.adminCertificate, queryFn: api.admin.certificate.get })
}

/** Server-auth certificates already in LocalMachine\My — what the Bind dialog picks from. Fetched only
 * while that dialog is open (`enabled`), since listing a certificate store is not free and nothing else
 * on this screen needs it. */
export function useAdminCertificateCandidates(enabled: boolean) {
  return useQuery({
    queryKey: keys.adminCertificateCandidates,
    queryFn: api.admin.certificate.candidates,
    enabled,
  })
}

/** Every certificate action below invalidates the same query: each one changes what GET reports (a new
 * certificate installed, a pending enrollment recorded or resolved, a binding written), and the API's
 * response already carries the refreshed status — invalidating still triggers a real refetch rather than
 * quietly trusting a POST body forever, the same caution useSetAdminConfig already takes. */
function invalidateAdminCertificate(queryClient: ReturnType<typeof useQueryClient>) {
  queryClient.invalidateQueries({ queryKey: keys.adminCertificate })
}

export function useCreateSelfSignedCertificate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ dnsNames, validityDays }: { dnsNames: string[]; validityDays: number | null }) =>
      api.admin.certificate.createSelfSigned(dnsNames, validityDays),
    onSuccess: () => invalidateAdminCertificate(queryClient),
  })
}

export function useEnrollCertificate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ dnsNames, template, caConfig }: { dnsNames: string[]; template: string; caConfig: string | null }) =>
      api.admin.certificate.enroll(dnsNames, template, caConfig),
    onSuccess: () => invalidateAdminCertificate(queryClient),
  })
}

export function useRetrieveCertificate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (requestId: string) => api.admin.certificate.retrieve(requestId),
    onSuccess: () => invalidateAdminCertificate(queryClient),
  })
}

export function useBindCertificate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ thumbprint, allowInvalid }: { thumbprint: string; allowInvalid: boolean | null }) =>
      api.admin.certificate.bind(thumbprint, allowInvalid),
    onSuccess: () => {
      invalidateAdminCertificate(queryClient)
      queryClient.invalidateQueries({ queryKey: keys.adminCertificateCandidates })
    },
  })
}

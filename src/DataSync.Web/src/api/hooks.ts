import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback } from 'react'
import { api } from './client'
import type { ConnectionInput, ReplicationTaskConfig, TableMappingConfig } from './types'

// Query keys are centralized here so mutations know exactly what to invalidate.
const keys = {
  connections: ['connections'] as const,
  connection: (name: string) => ['connections', name] as const,
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

export function useCancelRun(replicationName: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (runId: string) => api.runs.cancel(runId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.runHistory(replicationName) }),
  })
}

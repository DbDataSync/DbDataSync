// v1 only registers the MSSQL driver (src/DataSync.Drivers.MsSql/MsSqlDriverKinds.cs), and there's no
// "list supported reader/cache/writer kinds" metadata endpoint yet — these mirror that file directly.
// A driver-capability endpoint would replace this once a second driver exists (architecture backlog).
export const READER_KINDS = ['MsSqlChangeTracking', 'Watermark'] as const
export const CACHE_KINDS = ['MsSqlStagingTable'] as const
export const WRITER_KINDS = ['MsSqlMerge'] as const

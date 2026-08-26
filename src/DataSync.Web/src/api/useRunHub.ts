import * as signalR from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'
import { ApiError, api } from './client'
import type { LogSeverity, RunStatus } from './types'

export interface LiveLogLine {
  id: number
  timestampUtc: string
  level: LogSeverity
  message: string
}

export interface RunCompletedPayload {
  runId: string
  status: RunStatus
  rowsRead: number
  rowsWritten: number
  errorSummary: string | null
}

/**
 * Joins the RunHub group for one run and accumulates logLine/runCompleted events as they arrive
 * (architecture/detailed-design.md §3.1). Live for the lifetime of the component; a new runId tears
 * down the old connection and starts fresh.
 *
 * Also polls the REST endpoints directly, not just SignalR: a run can finish (and RunMonitorService
 * can broadcast + stop tracking it) before the browser's SignalR handshake completes — a fast run
 * against a small table can complete in under 200ms, comparable to or faster than a WebSocket/
 * long-polling negotiation. Without this fallback, a fast run's live log/completion would be silently
 * missed entirely rather than just arriving late. The poll stops once a terminal status is seen from
 * either source.
 */
export function useRunHub(runId: string | undefined) {
  const [logLines, setLogLines] = useState<LiveLogLine[]>([])
  const [completed, setCompleted] = useState<RunCompletedPayload | null>(null)
  const connectionRef = useRef<signalR.HubConnection | null>(null);

  useEffect(() => {
    setLogLines([])
    setCompleted(null)
    if (!runId) return

    let cancelled = false;
    let isCompleted = false;
    const seenLogIds = new Set<number>();
    let maxLogId: number | undefined;

    const mergeLogs = (entries: LiveLogLine[]) => {
      const fresh = entries.filter((e) => !seenLogIds.has(e.id));
      if (fresh.length === 0) return;
      for (const entry of fresh) {
        seenLogIds.add(entry.id);
        maxLogId = maxLogId === undefined ? entry.id : Math.max(maxLogId, entry.id);
      }
      setLogLines((prev) => [...prev, ...fresh].sort((a, b) => a.id - b.id));
    };

    const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/run').withAutomaticReconnect().build();
    connectionRef.current = connection;

    connection.on('logLine', (entry: LiveLogLine) => mergeLogs([entry]));
    connection.on('runCompleted', (payload: RunCompletedPayload) => {
      if (cancelled) return;
      isCompleted = true;
      setCompleted(payload);
    });

    connection.start().then(() => connection.invoke('JoinRun', runId)).catch((err) => console.error('RunHub connection failed', err));

    const pollOnce = async () => {
      try {
        const [logs, run] = await Promise.all([api.runs.logs(runId, maxLogId), api.runs.get(runId)]);
        if (cancelled) return;
        mergeLogs(logs);
        if (run.status !== 'Pending' && run.status !== 'Running') {
          isCompleted = true;
          setCompleted({
            runId: run.runId,
            status: run.status,
            rowsRead: run.rowsRead,
            rowsWritten: run.rowsWritten,
            errorSummary: run.errorSummary,
          });
        }
      } catch (err) {
        // A 404 is expected and transient here: this can poll before the spawned TaskRunner process
        // has written its first TaskRuns row. Anything else is unexpected and worth surfacing.
        if (!(err instanceof ApiError && err.status === 404))
          console.error('Run status poll failed', err);
      }
    };

    void pollOnce();
    const pollInterval = setInterval(() => {
      if (!cancelled && !isCompleted) void pollOnce();
    }, 750);

    return () => {
      cancelled = true;
      clearInterval(pollInterval);
      connection.stop().catch(() => {
        /* best-effort teardown */
      });
      connectionRef.current = null;
    };
  }, [runId]);

  return { logLines, completed };
}

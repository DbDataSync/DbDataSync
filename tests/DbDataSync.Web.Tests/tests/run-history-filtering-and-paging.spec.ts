import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'run-history-paging-demo'
const MAPPING_NAME = 'orders'

/**
 * Server-side filtering and paging for the run history list — phase 104.
 *
 * **Stubbed at the network boundary**, following `runs-watermarks-refresh.spec.ts` and
 * `monitoring-restructure.spec.ts` — but unlike those, the stub here actually implements the
 * filtering and paging semantics the real endpoint promises (kind/mappingName/status predicates, a
 * keyset cursor, newest-first order) rather than returning one canned page. What is under test is the
 * client: does it send the filters and cursor it claims to, and does it render whatever page comes
 * back. `TaskRunStoreTests` and `CrossEngineStateTests` cover the server's own half of the same
 * contract against a real store.
 *
 * **The fixture is 55 runs deep on purpose.** The one failure is the *oldest* of the 55 — outside the
 * newest fifty an unfiltered first page returns. A shallower fixture would put that failure on page
 * one regardless of whether the status filter is even applied, and every assertion below would pass
 * against the pre-phase-104 client-side filter too, proving nothing about the fix.
 */

interface RunFixture {
  runId: string
  taskName: string
  status: string
  runKind: string
  mappingName: string
  segmentLabel: string | null
  enqueuedAtUtc: string
  claimedAtUtc: string | null
  startedAtUtc: string | null
  endedAtUtc: string | null
  rowsRead: number
  rowsWritten: number
  errorSummary: string | null
  errorDetail: string | null
  failureKind: string | null
  timing: null
  previousWatermark: string | null
  newWatermark: string | null
  pid: number | null
}

const TOTAL_RUNS = 55
const OLD_FAILURE_INDEX = 0 // the very oldest run — rank 55th from the newest, well past a 50-row page
const ORIGIN = Date.parse('2026-01-01T00:00:00Z')

function makeRun(index: number): RunFixture {
  const isOldFailure = index === OLD_FAILURE_INDEX
  const at = new Date(ORIGIN + index * 60_000).toISOString()
  return {
    runId: `run-${String(index).padStart(3, '0')}`,
    taskName: REPLICATION_NAME,
    status: isOldFailure ? 'Failed' : 'Succeeded',
    runKind: 'Primary',
    mappingName: MAPPING_NAME,
    segmentLabel: null,
    enqueuedAtUtc: at,
    claimedAtUtc: at,
    startedAtUtc: at,
    endedAtUtc: at,
    rowsRead: 10,
    rowsWritten: 10,
    errorSummary: isOldFailure ? 'ancient failure, well before the newest fifty' : null,
    errorDetail: null,
    failureKind: null,
    timing: null,
    previousWatermark: null,
    newWatermark: null,
    pid: 4242,
  }
}

const ALL_RUNS = Array.from({ length: TOTAL_RUNS }, (_, i) => makeRun(i))
const OLD_FAILURE_ID = ALL_RUNS[OLD_FAILURE_INDEX].runId
const NEWEST_ID = ALL_RUNS[ALL_RUNS.length - 1].runId

/**
 * The fake server's own keyset paging — the same shape `TaskRunStore.GetRunHistory` implements for
 * real: filter, order newest first, skip past the cursor's row, take `limit`, and hand back the last
 * kept row's id as the next cursor (or null at the end of the history). Runs' own ids stand in for
 * the real `(EnqueuedAtUtc, RunId)` cursor here — this fixture's rows are already uniquely ordered,
 * so an id alone is enough to resume after; the point under test is that the *client* treats
 * whatever cursor comes back as opaque, not how this fixture happens to encode one.
 */
function serverPage(url: URL): { runs: RunFixture[]; nextCursor: string | null } {
  const kind = url.searchParams.get('kind')
  const mappingName = url.searchParams.get('mappingName')
  const status = url.searchParams.get('status')
  const cursor = url.searchParams.get('cursor')
  const limit = Number(url.searchParams.get('limit') ?? '50')

  let matching = ALL_RUNS
    .filter((r) => !kind || r.runKind === kind)
    .filter((r) => !mappingName || r.mappingName === mappingName)
    .filter((r) => !status || r.status === status)
    .slice()
    .sort((a, b) => (a.enqueuedAtUtc < b.enqueuedAtUtc ? 1 : a.enqueuedAtUtc > b.enqueuedAtUtc ? -1 : 0))

  if (cursor) {
    const at = matching.findIndex((r) => r.runId === cursor)
    matching = at === -1 ? [] : matching.slice(at + 1)
  }

  const runs = matching.slice(0, limit)
  const nextCursor = matching.length > limit ? runs[runs.length - 1].runId : null
  return { runs, nextCursor }
}

const TASK = {
  name: REPLICATION_NAME,
  enabled: true,
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: {
    reader: { kind: 'MsSqlChangeTracking', options: {} },
    cache: { kind: 'MsSqlStagingTable', options: {} },
    writer: { kind: 'MsSqlMerge', options: {} },
  },
  endpoints: {
    source: { connectionName: 'src', database: 'AppDb' },
    target: { connectionName: 'tgt', database: 'Warehouse' },
  },
}

const json = (body: unknown) => ({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify(body),
})

/** Counted so the polling test asserts a cadence rather than trusting one. */
interface Counts { history: number }

async function stub(page: Page): Promise<Counts> {
  const base = `/api/replications/${REPLICATION_NAME}`
  const counts: Counts = { history: 0 }

  // By RegExp and before the generic catch-all, as in the other Runs specs: both carry a query
  // string, and `?` is a wildcard in Playwright's glob syntax.
  await page.route(/\/runs\/watermark-times/, (route) => route.fulfill(json({})))
  await page.route(/\/runs\?/, (route) => {
    counts.history += 1
    return route.fulfill(json(serverPage(new URL(route.request().url()))))
  })
  await page.route(`**${base}/lag`, (route) => route.fulfill(json({
    mappings: {}, lowestLagMs: null, highestLagMs: null, rangeIncludesEstimates: false,
  })))
  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json([MAPPING_NAME])))
  await page.route(`**${base}/table-mappings/*`, (route) => route.fulfill(json({
    name: MAPPING_NAME,
    sources: [{ connectionName: null, database: null, schema: 'dbo', table: MAPPING_NAME, filter: null }],
    targets: [{ connectionName: null, database: null, schema: 'dbo', table: MAPPING_NAME }],
    columnMappings: [],
  })))
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true,
  })))
  await page.route(`**${base}/metrics*`, (route) => route.fulfill(json({
    runs: TOTAL_RUNS, failures: 1, rowsWritten: TOTAL_RUNS * 10, rowsRead: TOTAL_RUNS * 10, buckets: [],
    processingP50Ms: 1000, processingP95Ms: 1000, processingMaxMs: 1000,
    lastCompletedPassUtc: ALL_RUNS[ALL_RUNS.length - 1].endedAtUtc,
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))

  return counts
}

test.describe('run history: server-side filtering and paging', () => {
  test('01 - filtering by status finds a failure the newest fifty runs do not include', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await expect(page.getByTestId('run-history-table')).toBeVisible()

    // The fixture's whole point: the unfiltered first page is the newest fifty, and the one failure —
    // enqueued before all fifty-four other runs — is not among them.
    await expect(page.getByTestId(`run-status-${NEWEST_ID}`)).toBeVisible()
    await expect(page.getByTestId(`run-status-${OLD_FAILURE_ID}`)).toHaveCount(0)

    await page.getByTestId('run-filter-status').selectOption('Failed')

    // The server-side filter searches the whole history, not merely whatever page happened to be on
    // screen when the filter was applied.
    await expect(page.getByTestId(`run-status-${OLD_FAILURE_ID}`)).toBeVisible()
    await expect(page.getByTestId(`run-status-${NEWEST_ID}`)).toHaveCount(0)

    await page.screenshot({
      path: path.join(screenshotsDir, '104-status-filter-finds-old-failure.png'), fullPage: true,
    })
  })

  test('02 - paging older reaches the rest of the history, and back restores page one', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await expect(page.getByTestId('run-history-table')).toBeVisible()
    await expect(page.getByTestId(`run-status-${NEWEST_ID}`)).toBeVisible()

    await expect(page.getByTestId('run-page-older')).toBeEnabled()
    await page.getByTestId('run-page-older').click()

    // Page two carries the rest of the history, including the failure page one could not show — no
    // filter needed this time, since paging alone reaches it.
    await expect(page.getByTestId(`run-status-${OLD_FAILURE_ID}`)).toBeVisible()
    await expect(page.getByTestId(`run-status-${NEWEST_ID}`)).toHaveCount(0)
    await expect(page.getByTestId('runs-static-note')).toBeVisible()

    // "Newer" steps back exactly one page...
    await page.getByTestId('run-page-newer').click()
    await expect(page.getByTestId(`run-status-${NEWEST_ID}`)).toBeVisible()
    await expect(page.getByTestId(`run-status-${OLD_FAILURE_ID}`)).toHaveCount(0)
    await expect(page.getByTestId('runs-static-note')).toHaveCount(0)

    // ...and "Back to top" is the other way home, from however many pages deep.
    await page.getByTestId('run-page-older').click()
    await expect(page.getByTestId('run-page-hometop')).toBeVisible()
    await page.getByTestId('run-page-hometop').click()
    await expect(page.getByTestId(`run-status-${NEWEST_ID}`)).toBeVisible()
    await expect(page.getByTestId('runs-static-note')).toHaveCount(0)

    await page.screenshot({
      path: path.join(screenshotsDir, '104-paging-older-and-back.png'), fullPage: true,
    })
  })

  test('03 - polling stops off page one and resumes on returning to it', async ({ page }) => {
    const counts = await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await expect(page.getByTestId('runs-countdown')).toBeVisible()

    await page.getByTestId('run-page-older').click()
    await expect(page.getByTestId('runs-static-note')).toBeVisible()

    const afterPaging = counts.history
    // Longer than the ten-second cadence page one polls on — long enough that a live poll still
    // running off page one would have fired at least once by now.
    await page.waitForTimeout(12_000)
    expect(counts.history).toBe(afterPaging)

    await page.getByTestId('run-page-hometop').click()
    await expect(page.getByTestId('runs-countdown')).toBeVisible()

    await expect
      .poll(() => counts.history, { timeout: 15_000, intervals: [500] })
      .toBeGreaterThan(afterPaging)
  })
})

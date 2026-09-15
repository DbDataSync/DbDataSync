import { test, expect, type Page } from '@playwright/test'
import path from 'node:path'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('bulk-load-history')

const REPLICATION_NAME = 'bulk-load-history-demo'
const MAPPING_ORDERS = 'orders'
const MAPPING_CUSTOMERS = 'customers'

/**
 * Monitoring's fourth sub-tab — every past bulk load, across every mapping — see phase 139.
 * `BulkLoadBatchStore.GetRecentBulkLoads`'s own doc comment named this screen as a future consumer
 * since phase 107; this is the first spec to exercise it.
 *
 * **Stubbed at the network boundary**, following `run-history-filtering-and-paging.spec.ts`: the
 * fixture's own `serverPage` actually implements `mappingName`-filtered, keyset-paged, newest-first
 * semantics rather than returning one canned page, so what is under test is the client — does it send
 * the filter and cursor it claims to, and does it render whatever page comes back. The server's own
 * half of the same contract is `BulkLoadBatchStoreTests.GetHistory` and
 * `BulkLoadHistoryCursorCodecTests`, against a real store.
 *
 * **The fixture crosses a real page boundary on purpose.** 19 `orders` batches plus 3 `customers`
 * batches (22 total, `customers` the newest three) — more than the endpoint's own default page size
 * of 20 — so "Older" has somewhere real to go, and the `orders`-only filter (19, all on one page)
 * proves the filter narrows the *whole* history rather than just whatever page was already on screen.
 */

interface BatchFixture {
  batchId: string
  mappingName: string
  createdAtUtc: string
  segmentCount: number
  segmentsSucceeded: number
  segmentsFailed: number
  segmentsRunning: number
  rowsRead: number
  rowsCopied: number
  estimatedRows: number | null
  estimateCaveat: string | null
  startedAtUtc: string | null
  lastActivityUtc: string | null
  state: string
}

const ORIGIN = Date.parse('2026-01-01T00:00:00Z')

/** 19 plain `orders` batches, oldest first — index 18 (the newest of them) carries distinguishing,
 * assertable figures; the rest are filler so the fixture is deep enough to cross a page boundary once
 * the 3 `customers` batches (newer still) are added on top. */
const ORDERS_BATCHES: BatchFixture[] = Array.from({ length: 19 }, (_, i) => {
  const at = new Date(ORIGIN + i * 60_000).toISOString()
  const isNewestOrders = i === 18
  return {
    batchId: `orders-${String(i).padStart(3, '0')}`,
    mappingName: MAPPING_ORDERS,
    createdAtUtc: at,
    segmentCount: isNewestOrders ? 10 : 1,
    segmentsSucceeded: isNewestOrders ? 10 : 1,
    segmentsFailed: 0,
    segmentsRunning: 0,
    rowsRead: isNewestOrders ? 123_456 : 10,
    rowsCopied: isNewestOrders ? 123_456 : 10,
    estimatedRows: isNewestOrders ? 200_000 : null,
    estimateCaveat: null,
    startedAtUtc: at,
    lastActivityUtc: at,
    state: 'Completed',
  }
})

/** 3 `customers` batches, all newer than every `orders` batch above — the newest of them (index 2)
 * is still `Running`, with no failed segments; the point is only that the mapping filter can find
 * these three and nothing else. */
const CUSTOMERS_BATCHES: BatchFixture[] = Array.from({ length: 3 }, (_, i) => {
  const at = new Date(ORIGIN + (19 + i) * 60_000).toISOString()
  const isNewest = i === 2
  return {
    batchId: `customers-${String(i).padStart(3, '0')}`,
    mappingName: MAPPING_CUSTOMERS,
    createdAtUtc: at,
    segmentCount: 4,
    segmentsSucceeded: isNewest ? 2 : 4,
    segmentsFailed: 0,
    segmentsRunning: isNewest ? 2 : 0,
    rowsRead: 5_000,
    rowsCopied: 5_000,
    estimatedRows: null,
    estimateCaveat: null,
    startedAtUtc: at,
    lastActivityUtc: at,
    state: isNewest ? 'Running' : 'Completed',
  }
})

const ALL_BATCHES = [...ORDERS_BATCHES, ...CUSTOMERS_BATCHES]
const NEWEST_ORDERS_ID = 'orders-018'
const OLDEST_ORDERS_ID = 'orders-000'
const NEWEST_CUSTOMERS_ID = 'customers-002'

/**
 * The fake server's own keyset paging — the same shape `BulkLoadBatchStore.GetHistory` implements for
 * real: filter by mapping, order newest first, skip past the cursor's row, take `limit`, and hand back
 * the last kept row's id as the next cursor (or null at the end of the history). A batch id alone
 * stands in for the real `(CreatedAtUtc, BatchId)` cursor here — this fixture's rows are already
 * uniquely ordered, so an id is enough to resume after; the point under test is that the *client*
 * treats whatever cursor comes back as opaque, not how this fixture happens to encode one.
 */
function serverPage(url: URL): { batches: BatchFixture[]; nextCursor: string | null } {
  const mappingName = url.searchParams.get('mappingName')
  const cursor = url.searchParams.get('cursor')
  const limit = Number(url.searchParams.get('limit') ?? '20')

  let matching = ALL_BATCHES
    .filter((b) => !mappingName || b.mappingName === mappingName)
    .slice()
    .sort((a, b) => (a.createdAtUtc < b.createdAtUtc ? 1 : a.createdAtUtc > b.createdAtUtc ? -1 : 0))

  if (cursor) {
    const at = matching.findIndex((b) => b.batchId === cursor)
    matching = at === -1 ? [] : matching.slice(at + 1)
  }

  const batches = matching.slice(0, limit)
  const nextCursor = matching.length > limit ? batches[batches.length - 1].batchId : null
  return { batches, nextCursor }
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

/** Counted so the paging test asserts a real request per click, not just an already-cached page. */
interface Counts { history: number }

async function stub(page: Page): Promise<Counts> {
  const base = `/api/replications/${REPLICATION_NAME}`
  const counts: Counts = { history: 0 }

  // By RegExp and before the bare-endpoint catch-all, as in the Run History spec: this carries a
  // query string, and `?` is a wildcard in Playwright's glob syntax.
  await page.route(/\/bulk-loads\/history\?/, (route) => {
    counts.history += 1
    return route.fulfill(json(serverPage(new URL(route.request().url()))))
  })
  await page.route(/\/runs\/watermark-times/, (route) => route.fulfill(json({})))
  await page.route(/\/runs\?limit=/, (route) => route.fulfill(json({ runs: [], nextCursor: null })))
  await page.route(`**${base}/bulk-loads`, (route) => route.fulfill(json([])))
  await page.route(`**${base}/lag`, (route) => route.fulfill(json({
    mappings: {}, lowestLagMs: null, highestLagMs: null, rangeIncludesEstimates: false,
  })))
  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json([MAPPING_ORDERS, MAPPING_CUSTOMERS])))
  await page.route(`**${base}/table-mappings/*`, (route) => route.fulfill(json({
    name: MAPPING_ORDERS,
    sources: [{ connectionName: null, database: null, schema: 'dbo', table: MAPPING_ORDERS, filter: null }],
    targets: [{ connectionName: null, database: null, schema: 'dbo', table: MAPPING_ORDERS }],
    columnMappings: [],
  })))
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true,
  })))
  await page.route(`**${base}/metrics*`, (route) => route.fulfill(json({
    runs: 0, failures: 0, rowsWritten: 0, rowsRead: 0, buckets: [],
    processingP50Ms: null, processingP95Ms: null, processingMaxMs: null, lastCompletedPassUtc: null,
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))

  return counts
}

test.describe('bulk load history (phase 139)', () => {
  test('01 - the sub-tab renders, and a seeded batch shows the right columns', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring/bulk-load-history`)

    await expect(page.getByTestId('monitoring-tab-bulk-load-history')).toHaveClass(/active/)
    await expect(page.getByTestId('bulk-load-history-table')).toBeVisible()

    const row = page.locator(`[data-testid="bulk-load-history-row"][data-batch-id="${NEWEST_ORDERS_ID}"]`)
    await expect(row).toBeVisible()
    await expect(row).toHaveAttribute('data-bulk-load-state', 'Completed')
    await expect(page.getByTestId(`bulk-load-history-segments-${NEWEST_ORDERS_ID}`)).toContainText('10 / 10')
    await expect(page.getByTestId(`bulk-load-history-rows-copied-${NEWEST_ORDERS_ID}`)).toContainText('123,456')
    await expect(page.getByTestId(`bulk-load-history-rows-copied-${NEWEST_ORDERS_ID}`)).toContainText('200,000')
    await expect(page.getByTestId(`bulk-load-history-state-${NEWEST_ORDERS_ID}`)).toContainText('completed')

    // A still-running batch reads as running, not as some stalled variant of completed.
    const runningRow = page.locator(`[data-testid="bulk-load-history-row"][data-batch-id="${NEWEST_CUSTOMERS_ID}"]`)
    await expect(runningRow).toHaveAttribute('data-bulk-load-state', 'Running')

    await page.screenshot({ path: path.join(screenshotsDir, '139-history-table.png'), fullPage: true })
  })

  test('02 - the mapping filter narrows the list to one mapping\'s own batches', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring/bulk-load-history`)
    await expect(page.getByTestId('bulk-load-history-table')).toBeVisible()

    // Unfiltered, the newest page mixes both mappings — customers is newer than every orders batch.
    await expect(page.locator('[data-batch-id^="customers-"]').first()).toBeVisible()

    await page.getByTestId('bulk-load-history-filter-mapping').selectOption(MAPPING_ORDERS)

    // Every remaining row is now an orders batch, including one page one alone would not have shown
    // when unfiltered (orders-000 — page two of the unfiltered list).
    await expect(page.locator('[data-batch-id^="customers-"]')).toHaveCount(0)
    await expect(page.locator(`[data-batch-id="${OLDEST_ORDERS_ID}"]`)).toBeVisible()
    // Filtering resets to page one — no "Older" needed to reach it — and 19 orders batches fit on one
    // page, so there is nothing older to page into.
    await expect(page.getByTestId('bulk-load-history-page-older')).toBeDisabled()

    await page.screenshot({ path: path.join(screenshotsDir, '139-mapping-filter.png'), fullPage: true })
  })

  test('03 - Older/Newer moves between pages of the unfiltered history', async ({ page }) => {
    const counts = await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring/bulk-load-history`)
    await expect(page.getByTestId('bulk-load-history-table')).toBeVisible()

    // Page one is the newest 20 of 22 — the oldest orders batch is not on it yet.
    await expect(page.locator(`[data-batch-id="${NEWEST_CUSTOMERS_ID}"]`)).toBeVisible()
    await expect(page.locator(`[data-batch-id="${OLDEST_ORDERS_ID}"]`)).toHaveCount(0)

    const afterFirstLoad = counts.history
    await expect(page.getByTestId('bulk-load-history-page-older')).toBeEnabled()
    await page.getByTestId('bulk-load-history-page-older').click()

    await expect(page.locator(`[data-batch-id="${OLDEST_ORDERS_ID}"]`)).toBeVisible()
    await expect(page.locator(`[data-batch-id="${NEWEST_CUSTOMERS_ID}"]`)).toHaveCount(0)
    await expect(page.getByTestId('bulk-load-history-page-older')).toBeDisabled()
    expect(counts.history).toBeGreaterThan(afterFirstLoad)

    // "Newer" steps back exactly one page, to the live-looking first page.
    await page.getByTestId('bulk-load-history-page-newer').click()
    await expect(page.locator(`[data-batch-id="${NEWEST_CUSTOMERS_ID}"]`)).toBeVisible()
    await expect(page.locator(`[data-batch-id="${OLDEST_ORDERS_ID}"]`)).toHaveCount(0)

    await page.screenshot({ path: path.join(screenshotsDir, '139-older-newer-paging.png'), fullPage: true })
  })
})

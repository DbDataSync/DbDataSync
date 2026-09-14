import { test, expect, type Page } from '@playwright/test'
import path from 'node:path'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('bulk-load-progress')

const REPLICATION_NAME = 'bulk-load-progress-demo'
const MAPPING_NAME = 'orders'

/**
 * The "Batch reload" card on Monitoring → Current Status (phase 107). Stubbed at the network boundary
 * like `monitoring-restructure.spec.ts` — what is under test is the card reading `/bulk-loads` and
 * rendering rows-copied / estimated-total / segments, not any real reload.
 */

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

const RUNNING_BATCH = {
  batchId: 'batch-1',
  mappingName: MAPPING_NAME,
  createdAtUtc: '2026-09-05T10:00:00Z',
  segmentCount: 10,
  segmentsSucceeded: 3,
  segmentsFailed: 0,
  segmentsRunning: 1,
  rowsRead: 240_000,
  rowsCopied: 240_000,
  estimatedRows: 2_400_000,
  estimateCaveat: null as string | null,
  startedAtUtc: '2026-09-05T10:00:01Z',
  lastActivityUtc: '2026-09-05T10:03:00Z',
  state: 'Running',
}

const json = (body: unknown) => ({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify(body),
})

async function stub(page: Page, bulkLoads: unknown[]) {
  const base = `/api/replications/${REPLICATION_NAME}`

  await page.route(/\/runs\/watermark-times/, (route) => route.fulfill(json({})))
  await page.route(/\/runs\?limit=/, (route) => route.fulfill(json({ runs: [], nextCursor: null })))
  await page.route(`**${base}/bulk-loads`, (route) => route.fulfill(json(bulkLoads)))
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
    runs: 0, failures: 0, rowsWritten: 0, rowsRead: 0, buckets: [],
    processingP50Ms: null, processingP95Ms: null, processingMaxMs: null, lastCompletedPassUtc: null,
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))
}

test.describe('replication detail: batch reload progress (phase 107)', () => {
  test('01 - a running bulk load shows rows copied, an estimated total and segment progress', async ({ page }) => {
    await stub(page, [RUNNING_BATCH])
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    const card = page.getByTestId('bulk-load-progress-card')
    await expect(card).toBeVisible()
    await expect(card).toHaveAttribute('data-bulk-load-state', 'Running')

    await expect(page.getByTestId('bulk-load-rows-copied')).toContainText('240,000')
    await expect(page.getByTestId('bulk-load-estimated-total')).toContainText('2,400,000')
    await expect(page.getByTestId('bulk-load-segments')).toContainText('3 / 10')

    // It sits above the reader-lag card it shares the tab with.
    const order = await page
      .locator('[data-testid="bulk-load-progress-card"], [data-testid="monitoring-range"]')
      .evaluateAll((els) => els.map((el) => el.getAttribute('data-testid')))
    expect(order).toEqual(['bulk-load-progress-card', 'monitoring-range'])

    await page.screenshot({ path: path.join(screenshotsDir, '110-bulk-load-progress.png'), fullPage: true })
  })

  test('02 - the filter caveat shows when the estimate ignored the mapping row filter', async ({ page }) => {
    await stub(page, [{ ...RUNNING_BATCH, estimateCaveat: 'ignores row filter' }])
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    await expect(page.getByTestId('bulk-load-estimate-caveat')).toBeVisible()
  })

  test('03 - an unknown estimate is not shown as zero', async ({ page }) => {
    await stub(page, [{ ...RUNNING_BATCH, estimatedRows: null }])
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    await expect(page.getByTestId('bulk-load-estimated-total')).toContainText('Unknown')
  })

  test('04 - no card when there is no recent bulk load', async ({ page }) => {
    await stub(page, [])
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    await expect(page.getByTestId('monitoring-range')).toBeVisible()
    await expect(page.getByTestId('bulk-load-progress-card')).toHaveCount(0)
  })

  test('05 - a long-finished bulk load has cleared from the card', async ({ page }) => {
    await stub(page, [{
      ...RUNNING_BATCH,
      state: 'Completed',
      segmentsSucceeded: 10,
      segmentsRunning: 0,
      rowsCopied: 2_390_000,
      lastActivityUtc: '2020-01-01T00:00:00Z',
    }])
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    await expect(page.getByTestId('monitoring-range')).toBeVisible()
    await expect(page.getByTestId('bulk-load-progress-card')).toHaveCount(0)
  })
})

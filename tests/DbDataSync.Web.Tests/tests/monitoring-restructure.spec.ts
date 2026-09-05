import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'monitoring-restructure-demo'
const MAPPING_NAME = 'orders'
const RUN_ID = '44444444-4444-4444-4444-444444444444'

/**
 * Phase 103's information-architecture move: Runs folds under Monitoring as a **Run History**
 * sub-tab beside **Current Status**, Schedule moves from the detail rail onto Overview, and each
 * `RefreshCountdown` moves out of the shared shell chrome (`ShellActions`, now deleted) into the
 * header of the card or pane it describes.
 *
 * **Stubbed at the network boundary**, following `lag-monitoring.spec.ts` and
 * `run-details-dialog.spec.ts`. What is under test is layout and routing — which sub-tab a URL
 * lands on, where a card sits relative to another, whether a portal that no longer exists still
 * shows up anywhere — not any particular reading a real replication would produce.
 */

const TASK = {
  name: REPLICATION_NAME,
  enabled: true,
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: {
    reader: { kind: 'MsSqlCdc', parallelism: 1, options: {} },
    cache: { kind: 'MsSqlStagingTable', options: {} },
    writer: { kind: 'MsSqlMerge', parallelism: 1, options: {} },
  },
  endpoints: {
    source: { connectionName: 'src', database: 'AppDb' },
    target: { connectionName: 'tgt', database: 'Warehouse' },
  },
}

const LAG_PAYLOAD = {
  mappings: {
    [MAPPING_NAME]: { readerKind: 'MsSqlCdc', supported: true, exactLagMs: 60_000, versionsBehind: null, estimatedLagMs: null },
  },
  lowestLagMs: 60_000,
  highestLagMs: 60_000,
  rangeIncludesEstimates: false,
}

const RUNS = [
  {
    runId: RUN_ID, taskName: REPLICATION_NAME, status: 'Succeeded', runKind: 'Primary',
    mappingName: MAPPING_NAME, segmentLabel: null,
    enqueuedAtUtc: '2026-09-01T09:20:00Z', claimedAtUtc: '2026-09-01T09:20:00Z',
    startedAtUtc: '2026-09-01T09:20:01Z', endedAtUtc: '2026-09-01T09:20:04Z',
    rowsRead: 2, rowsWritten: 2, errorSummary: null, errorDetail: null, failureKind: null,
    timing: null, previousWatermark: null, newWatermark: null, pid: 4242,
  },
]

const json = (body: unknown) => ({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify(body),
})

async function stub(page: Page) {
  const base = `/api/replications/${REPLICATION_NAME}`

  // By RegExp and before the catch-alls, as in run-details-dialog.spec.ts: both carry a query
  // string, and `?` is a wildcard in Playwright's glob syntax.
  await page.route(/\/runs\/watermark-times/, (route) => route.fulfill(json({})))
  // { runs, nextCursor } since phase 104 added server-side filtering and paging — a bare array
  // before that.
  await page.route(/\/runs\?limit=/, (route) => route.fulfill(json({ runs: RUNS, nextCursor: null })))
  // The bare endpoint, no query string, is the trigger — Run Now posts here and nowhere else.
  await page.route(`**${base}/runs`, (route) =>
    route.request().method() === 'POST'
      ? route.fulfill({ ...json({ runIds: [RUN_ID] }), status: 202 })
      : route.fallback())
  await page.route(`**${base}/lag`, (route) => route.fulfill(json(LAG_PAYLOAD)))
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
    runs: 3, failures: 0, rowsWritten: 24, rowsRead: 24, buckets: [],
    processingP50Ms: 3000, processingP95Ms: 3000, processingMaxMs: 3000,
    lastCompletedPassUtc: '2026-09-01T09:20:04Z',
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))
}

test.describe('replication detail: Monitoring restructure (phase 103)', () => {
  test('01 - /runs redirects to Run History rather than 404ing, with the URL rewritten', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)

    await expect(page).toHaveURL(new RegExp(`/replications/${REPLICATION_NAME}/monitoring/history$`))
    await expect(page.getByTestId('run-history-table')).toBeVisible()
    await expect(page.getByTestId('monitoring-tab-history')).toHaveClass(/active/)
    // The top-level tab bar reads Monitoring, not a fifth Runs tab of its own.
    await expect(page.getByTestId('tab-monitoring')).toHaveClass(/active/)
    await expect(page.getByTestId('tab-runs')).toHaveCount(0)

    await page.screenshot({ path: path.join(screenshotsDir, '80-runs-redirect.png'), fullPage: true })
  })

  test('02 - Current Status is the index and Run History a sub-tab, each with its own content', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    // No segment needed: the bare section URL opens what it is for.
    await expect(page).toHaveURL(new RegExp(`/replications/${REPLICATION_NAME}/monitoring$`))
    await expect(page.getByTestId('monitoring-tab-current')).toHaveClass(/active/)
    await expect(page.getByTestId('monitoring-range')).toBeVisible()
    await expect(page.getByTestId('run-history-table')).toHaveCount(0)

    await page.getByTestId('monitoring-tab-history').click()
    await expect(page).toHaveURL(new RegExp(`/monitoring/history$`))
    await expect(page.getByTestId('monitoring-tab-history')).toHaveClass(/active/)
    await expect(page.getByTestId('run-history-table')).toBeVisible()
    await expect(page.getByTestId('monitoring-range')).toHaveCount(0)

    await page.screenshot({ path: path.join(screenshotsDir, '81-monitoring-subtabs.png'), fullPage: true })
  })

  test('03 - Run Now still triggers from Overview, a tab that is not Monitoring', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)
    await expect(page.getByTestId('trigger-run-button')).toBeVisible({ timeout: 15_000 })

    await page.getByTestId('trigger-run-button').click()

    // The command lands on Run History, two levels down the outlet now — the chrome survived the
    // move without needing to change.
    await expect(page).toHaveURL(new RegExp(`/monitoring/history$`))
    await expect(page.getByTestId('live-run-panel')).toBeVisible()
  })

  test('04 - Schedule sits on Overview between Endpoints and the sub-tabs, and an edit survives a look at another tab', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)

    await expect(page.getByTestId('replication-schedule-card')).toBeVisible({ timeout: 15_000 })

    // The order asked for: endpoints, then schedule, then the sub-tab bar underneath both.
    const order = await page
      .locator('[data-testid="endpoints-card"], [data-testid="replication-schedule-card"], [data-testid="overview-subtabs"]')
      .evaluateAll((els) => els.map((el) => el.getAttribute('data-testid')))
    expect(order).toEqual(['endpoints-card', 'replication-schedule-card', 'overview-subtabs'])

    // Not on the other tabs — that visibility is what was given up in exchange.
    await page.getByTestId('tab-monitoring').click()
    await expect(page.getByTestId('replication-schedule-card')).toHaveCount(0)
    await page.getByTestId('tab-overview').click()

    await page.screenshot({ path: path.join(screenshotsDir, '82-overview-schedule.png'), fullPage: true })

    // An edit, then away and back — proving the draft survives rather than assuming it does, since
    // the card unmounts and remounts between the two (phase 46 moved draft ownership to the page).
    await page.getByTestId('schedule-frequency-input').fill('45')
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId('tab-overview').click()
    await expect(page.getByTestId('schedule-frequency-input')).toHaveValue('45')
  })

  test('05 - each countdown lives in the header of what it describes, and the shell chrome carries none of them', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    // Metrics is in the rail on every tab; Lag lives in the Reader lag card's own head — there is no
    // pane-level heading on Current Status at all, since a bare heading whose only content was the
    // countdown said nothing the tab title above it did not already say.
    await expect(page.getByTestId('metrics-card').getByTestId('metrics-countdown')).toBeVisible()
    await expect(page.getByTestId('monitoring-range').getByTestId('lag-countdown')).toBeVisible()

    await page.getByTestId('monitoring-tab-history').click()
    await expect(page.getByTestId('run-history-table').getByTestId('runs-countdown')).toBeVisible()
    // Metrics stays visible from every tab, Run History included — the rail did not move.
    await expect(page.getByTestId('metrics-card').getByTestId('metrics-countdown')).toBeVisible()

    // Both halves matter: the portal (`ShellActions`) that used to carry all three is deleted
    // entirely, not merely left empty, so neither the class nor any countdown shows up inside it.
    await expect(page.locator('.shell-actions')).toHaveCount(0)
    await expect(page.locator('.topbar').getByTestId('metrics-countdown')).toHaveCount(0)
    await expect(page.locator('.topbar').getByTestId('lag-countdown')).toHaveCount(0)
    await expect(page.locator('.topbar').getByTestId('runs-countdown')).toHaveCount(0)

    await page.screenshot({ path: path.join(screenshotsDir, '83-countdowns-in-own-cards.png'), fullPage: true })
  })
})

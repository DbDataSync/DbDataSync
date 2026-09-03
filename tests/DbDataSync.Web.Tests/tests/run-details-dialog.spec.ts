import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'run-details-demo'

/**
 * The run history's status column — where its badges sit, and what opens behind them. Phase 96.
 *
 * **Stubbed at the network boundary**, following `lag-monitoring.spec.ts` and
 * `runs-watermarks-refresh.spec.ts`. What is under test is rendering: where a badge sits in its
 * cell, and which fields a popup carries for a run that worked versus one that did not. Producing a
 * failed run and a succeeded one side by side against real databases means making a pass fail on
 * purpose, and the assertion would still be about the payload's shape.
 *
 * Two of the three tests here are the first coverage this dialog has ever had — it shipped without
 * any — and the alignment one is a real geometric assertion rather than a look at a screenshot,
 * because a layout defect confirmed only by looking is one that comes back.
 */

const RUN_OK = '11111111-1111-1111-1111-111111111111'
const RUN_FAILED = '22222222-2222-2222-2222-222222222222'
const RUN_TRACED = '33333333-3333-3333-3333-333333333333'

const ERROR_DETAIL =
  'Microsoft.Data.SqlClient.SqlException (0x80131904): Invalid object name \'dbo.Orders_Staging\'.\n' +
  '   at Microsoft.Data.SqlClient.SqlCommand.ExecuteNonQueryAsync()'

const run = (over: Record<string, unknown>) => ({
  taskName: REPLICATION_NAME,
  status: 'Succeeded',
  runKind: 'Primary',
  mappingName: 'orders',
  segmentLabel: null,
  enqueuedAtUtc: '2026-05-01T09:20:00Z',
  claimedAtUtc: '2026-05-01T09:20:00Z',
  startedAtUtc: '2026-05-01T09:20:05Z',
  endedAtUtc: '2026-05-01T09:20:12Z',
  rowsRead: 0,
  rowsWritten: 0,
  errorSummary: null,
  errorDetail: null,
  failureKind: null,
  timing: null,
  previousWatermark: null,
  newWatermark: null,
  pid: null,
  ...over,
})

const RUNS = [
  // Succeeded, with counts and a five-second queue wait — the run whose details had nowhere to be
  // read before this phase.
  run({ runId: RUN_OK, pid: 4242, rowsRead: 1240, rowsWritten: 1240, previousWatermark: '20', newWatermark: '60' }),
  // Failed, which is the only status that used to be a button, and so the only badge that sat
  // centred in its cell while every other one was left-aligned.
  run({
    runId: RUN_FAILED,
    status: 'Failed',
    pid: 4243,
    errorSummary: "Invalid object name 'dbo.Orders_Staging'.",
    errorDetail: ERROR_DETAIL,
  }),
  // Traced, so the dialog has a stage breakdown to show as well as the row's own chevron.
  run({
    runId: RUN_TRACED,
    pid: 4244,
    rowsRead: 8,
    rowsWritten: 8,
    timing: {
      readerKind: 'MsSqlChangeTracking',
      readerLifetimeMs: 900,
      readerTimeToFirstRowMs: 120,
      stagingKind: 'MsSqlStagingTable',
      stagingDurationMs: 1000,
      writerKind: 'MsSqlMerge',
      writerDurationMs: 300,
    },
  }),
]

const TASK = {
  name: REPLICATION_NAME,
  enabled: true,
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: {
    reader: { kind: 'MsSqlChangeTracking', parallelism: 1, options: {} },
    cache: { kind: 'MsSqlStagingTable', options: {} },
    writer: { kind: 'MsSqlMerge', parallelism: 1, options: {} },
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

async function stub(page: Page) {
  const base = `/api/replications/${REPLICATION_NAME}`

  // By RegExp and before the catch-all, as in runs-watermarks-refresh.spec.ts: both carry a query
  // string, and `?` is a wildcard in Playwright's glob syntax.
  await page.route(/\/runs\/watermark-times/, (route) => route.fulfill(json({
    [RUN_OK]: {
      previousWatermarkTimeUtc: '2026-05-01T09:09:00Z',
      newWatermarkTimeUtc: '2026-05-01T09:20:00Z',
    },
  })))
  await page.route(/\/runs\?limit=/, (route) => route.fulfill(json(RUNS)))
  await page.route(`**${base}/lag`, (route) => route.fulfill(json({
    mappings: {}, lowestLagMs: null, highestLagMs: null, rangeIncludesEstimates: false,
  })))
  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json(['orders'])))
  await page.route(`**${base}/table-mappings/*`, (route) => route.fulfill(json({
    name: 'orders',
    sources: [{ connectionName: null, database: null, schema: 'dbo', table: 'orders', filter: null }],
    targets: [{ connectionName: null, database: null, schema: 'dbo', table: 'orders' }],
    columnMappings: [],
  })))
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true,
  })))
  await page.route(`**${base}/metrics*`, (route) => route.fulfill(json({
    runs: 3, failures: 1, rowsWritten: 1248, rowsRead: 1248, buckets: [],
    processingP50Ms: 7000, processingP95Ms: 7000, processingMaxMs: 7000,
    lastCompletedPassUtc: '2026-05-01T09:20:12Z',
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))
}

/**
 * The left edge of the **badge**, not of the cell that holds it.
 *
 * This distinction is the whole test. `text-align: center` centres a badge *inside* its wrapper and
 * leaves the wrapper itself spanning the grid column, so measuring the wrapper returns the column's
 * own left edge for a centred badge and a left-aligned one alike — an assertion that passes against
 * the defect it was written for. `.status` is the badge `StatusBadge` renders.
 */
const badgeEdgeOf = async (page: Page, testId: string) => {
  const box = await page.getByTestId(testId).locator('.status').boundingBox()
  expect(box, `${testId} has no badge bounding box`).not.toBeNull()
  return box!.x
}

const leftEdgeOf = async (page: Page, testId: string) => {
  const box = await page.getByTestId(testId).boundingBox()
  expect(box, `${testId} has no bounding box`).not.toBeNull()
  return box!.x
}

test.describe('run details dialog and the status column', () => {
  /**
   * The reported defect, as geometry.
   *
   * **Both halves matter.** A failed run's status used to be a `<button>` and every other status a
   * `<span>`, and a button carries the UA stylesheet's `text-align: center` where a span inherits
   * the row's `left` — so the failed badge sat centred in its cell and the rest did not. Making
   * every status a button agrees the badges with *each other*, and would leave the whole column
   * centred under a left-aligned header: the first assertion alone passes against that, which is
   * why the second one is here.
   */
  test('01 - every status badge starts at the same x, and at the header\'s', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await expect(page.getByTestId('run-history-table')).toBeVisible()

    const succeeded = await badgeEdgeOf(page, `run-status-${RUN_OK}`)
    const failed = await badgeEdgeOf(page, `run-status-${RUN_FAILED}`)
    const header = await leftEdgeOf(page, 'run-status-header')

    // Sub-pixel layout rounding is real; a centred badge is out by tens of pixels.
    expect(Math.abs(failed - succeeded)).toBeLessThan(1)
    expect(Math.abs(succeeded - header)).toBeLessThan(1)

    await page.screenshot({ path: path.join(screenshotsDir, '70-run-status-column.png'), fullPage: true })
  })

  /**
   * The new function: a run that worked has somewhere to go. "How long did this take, how many rows,
   * where did the watermark land" is an ordinary question about a successful pass, and the row
   * answers it only in truncated cells and a tooltip.
   */
  test('02 - a succeeded run opens its details, with rows, timing and watermark', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)

    await page.getByTestId(`run-status-${RUN_OK}`).click()
    const dialog = page.getByTestId('run-details-dialog')
    await expect(dialog).toBeVisible()

    // Not "Run failed" — the heading follows the run, and this one did not fail.
    await expect(dialog).toContainText('Run details')

    const figures = page.getByTestId('run-details-figures')
    await expect(figures).toContainText('1,240')
    await expect(figures).toContainText('Processing time')
    // 09:20:05 → 09:20:12. Formatted by the same helper the row's cell uses.
    await expect(figures).toContainText('7.0s')
    // Queued five seconds before it started, which the row only ever carried as a tooltip on
    // another cell.
    await expect(figures).toContainText('Queue time')

    await expect(dialog).toContainText('Watermark')

    // The half that is conditional, and the only half: there is no error block on a run that worked.
    await expect(page.getByTestId('run-details-dialog-message')).toHaveCount(0)

    await page.screenshot({ path: path.join(screenshotsDir, '71-run-details-succeeded.png'), fullPage: true })

    await page.getByTestId('run-details-dialog-close').click()
    await expect(dialog).toHaveCount(0)
  })

  /** The dialog's original job, which had no coverage at all until now. */
  test('03 - a failed run opens the same dialog, and it carries the error', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)

    await page.getByTestId(`run-status-${RUN_FAILED}`).click()
    const dialog = page.getByTestId('run-details-dialog')
    await expect(dialog).toBeVisible()
    await expect(dialog).toContainText('Run failed')

    // Every field the succeeded run got, *plus* the error — one adaptive dialog, not two.
    await expect(page.getByTestId('run-details-figures')).toContainText('Processing time')

    // The summary shows by default — the one line that says what went wrong, not a stack trace
    // nobody asked for yet.
    await expect(page.getByTestId('run-details-dialog-message')).toContainText('Invalid object name')
    await expect(page.getByTestId('run-details-dialog-message')).not.toContainText('ExecuteNonQueryAsync')
    await expect(page.getByTestId('run-details-dialog-error-detail')).toHaveCount(0)

    // The full exception is behind an explicit expand, for whoever needs the stack trace.
    await page.getByTestId('run-details-dialog-expand-error').click()
    await expect(page.getByTestId('run-details-dialog-error-detail')).toContainText('ExecuteNonQueryAsync')

    await page.screenshot({ path: path.join(screenshotsDir, '72-run-details-failed.png'), fullPage: true })
  })

  /**
   * The row's own chevron is unchanged — a traced pass still expands in place. The dialog carries the
   * same breakdown because somebody who opened it to ask how long the run took is already asking the
   * question the trace answers, but it did not replace the inline one.
   */
  test('04 - a traced run shows its stage timings in the dialog as well as inline', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)

    await expect(page.getByTestId(`run-timing-toggle-${RUN_TRACED}`)).toBeVisible()

    await page.getByTestId(`run-status-${RUN_TRACED}`).click()
    const dialog = page.getByTestId('run-details-dialog')
    await expect(dialog).toContainText('Stage timings')
    await expect(dialog.getByTestId(`run-timing-${RUN_TRACED}`)).toContainText('MsSqlChangeTracking')
  })
})

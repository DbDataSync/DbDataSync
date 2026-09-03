import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'runs-demo'

/**
 * The runs page's PID and watermark timestamps, and the ten-second refresh the three monitoring
 * panels now share — see phase 88.
 *
 * **Stubbed at the network boundary**, following `lag-monitoring.spec.ts`. What is under test here
 * is rendering and cadence: which cell a value lands in, which dash means which absence, and how
 * three independently-mounted hooks count down. Producing a run whose previous watermark has aged
 * out of `ChangeCheckHistory`'s retention window against a real database means either waiting seven
 * days or reaching in and deleting rows, and the resulting assertion would still be about the shape
 * the server sends. `RunWatermarkTimeTests` covers the server's half against real config, real runs
 * and real polling history; this covers the client's half, and the two meet at the payload below.
 */

const RUN_ADVANCED = '11111111-1111-1111-1111-111111111111'
const RUN_QUEUED = '22222222-2222-2222-2222-222222222222'
const RUN_HALF_DATED = '33333333-3333-3333-3333-333333333333'

const run = (over: Record<string, unknown>) => ({
  taskName: REPLICATION_NAME,
  status: 'Succeeded',
  runKind: 'Primary',
  mappingName: 'orders',
  segmentLabel: null,
  enqueuedAtUtc: '2026-05-01T09:20:00Z',
  claimedAtUtc: '2026-05-01T09:20:00Z',
  startedAtUtc: '2026-05-01T09:20:01Z',
  endedAtUtc: '2026-05-01T09:20:04Z',
  rowsRead: 12,
  rowsWritten: 12,
  errorSummary: null,
  failureKind: null,
  timing: null,
  previousWatermark: null,
  newWatermark: null,
  pid: null,
  ...over,
})

const RUNS = [
  // A completed pass with a worker process and both ends of its watermark inside the window.
  run({ runId: RUN_ADVANCED, pid: 4242, previousWatermark: '20', newWatermark: '60' }),
  // Queued and never claimed: no PID, and no position made durable.
  run({ runId: RUN_QUEUED, status: 'Queued', startedAtUtc: null, endedAtUtc: null }),
  // Started outside the retention window and ended inside it — one half datable, one half not.
  run({ runId: RUN_HALF_DATED, pid: 909, previousWatermark: '7', newWatermark: '550' }),
]

const WATERMARK_TIMES = {
  [RUN_ADVANCED]: {
    previousWatermarkTimeUtc: '2026-05-01T09:09:00Z',
    newWatermarkTimeUtc: '2026-05-01T09:20:00Z',
  },
  // Present, with only the half the history can still place. The other half must not be invented.
  [RUN_HALF_DATED]: {
    previousWatermarkTimeUtc: null,
    newWatermarkTimeUtc: '2026-05-01T09:30:00Z',
  },
  // RUN_QUEUED is absent entirely: it made no position durable, so there is nothing to date.
}

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

const LAG_PAYLOAD = {
  mappings: {
    orders: {
      readerKind: 'MsSqlChangeTracking',
      supported: true,
      exactLagMs: 300_000,
      versionsBehind: 60,
      estimatedLagMs: null,
      asOfUtc: '2026-05-01T09:30:00Z',
    },
  },
  lowestLagMs: 300_000,
  highestLagMs: 300_000,
  rangeIncludesEstimates: false,
}

const json = (body: unknown) => ({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify(body),
})

/** Counted so a test can assert a cadence rather than trust one. */
interface Counts { history: number }

async function stub(page: Page): Promise<Counts> {
  const base = `/api/replications/${REPLICATION_NAME}`
  const counts: Counts = { history: 0 }

  // Registered before the generic `**${base}` catch-all below, and matched by RegExp rather than by
  // glob: both of these carry a query string, and `?` is a wildcard in Playwright's glob syntax.
  await page.route(/\/runs\/watermark-times/, (route) => route.fulfill(json(WATERMARK_TIMES)))
  await page.route(/\/runs\?limit=/, (route) => {
    counts.history += 1
    return route.fulfill(json(RUNS))
  })
  await page.route(`**${base}/lag`, (route) => route.fulfill(json(LAG_PAYLOAD)))
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
    runs: 3, failures: 0, rowsWritten: 24, rowsRead: 24, buckets: [],
    processingP50Ms: 3000, processingP95Ms: 3000, processingMaxMs: 3000,
    lastCompletedPassUtc: '2026-05-01T09:20:04Z',
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))

  return counts
}

const seconds = async (page: Page, testId: string) =>
  Number(await page.getByTestId(testId).getAttribute('data-seconds'))

test.describe('runs page watermarks and the shared refresh', () => {
  test('01 - the run list shows the PID and dates both ends of the watermark', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)

    await expect(page.getByTestId('run-history-table')).toBeVisible()

    // On the wire since the work queue existed, and on screen since phase 88. Null is a fact about
    // a run nobody ever claimed, not a missing reading — so it is a dash rather than a blank.
    await expect(page.getByTestId(`run-pid-${RUN_ADVANCED}`)).toHaveText('4242')
    await expect(page.getByTestId(`run-pid-${RUN_QUEUED}`)).toHaveText('—')

    // A pass that advanced its position shows when it started from and when it got to. The times
    // are locale-formatted, so what is asserted is that both ends resolved to something with digits
    // in it rather than to the dash that means "not datable".
    const advanced = page.getByTestId(`run-watermark-${RUN_ADVANCED}`)
    await expect(advanced).toContainText('→')
    expect(await advanced.innerText()).toMatch(/\d.*→.*\d/s)

    // The raw position stays reachable, which is the whole reason the cell shows a time instead:
    // one of the two is readable at a glance and the other is what you paste into a query.
    const ends = advanced.locator('span[title]')
    await expect(ends.first()).toHaveAttribute('title', /Started from source position 20\./)
    await expect(ends.last()).toHaveAttribute('title', /Advanced to source position 60\./)

    // Three dashes, three different meanings. A run that made no position durable has one dash for
    // the whole cell...
    await expect(page.getByTestId(`run-watermark-${RUN_QUEUED}`)).toHaveText('—')

    // ...and a position the retained history cannot place has a dash of its own, still carrying its
    // raw value and saying why there is no time rather than showing a plausible one.
    const halfDated = page.getByTestId(`run-watermark-${RUN_HALF_DATED}`)
    const from = halfDated.locator('span[title]').first()
    await expect(from).toHaveText('—')
    await expect(from).toHaveAttribute('title', /aged out of the retention window/)
    expect(await halfDated.innerText()).toMatch(/\d/)

    await page.screenshot({
      path: path.join(screenshotsDir, '62-runs-pid-and-watermarks.png'), fullPage: true,
    })
  })

  test('02 - each panel counts down on its own cycle, not a shared clock', async ({ page }) => {
    await stub(page)

    // The lag tab and the metrics card mount together and fetch together.
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)
    await expect(page.getByTestId('lag-countdown')).toBeVisible()
    await expect(page.getByTestId('metrics-countdown')).toBeVisible()

    // The reading the lag figure is measured against, beside the figure — phase 88's fourth gap.
    // It is what tells a five-minute lag from a stalled poller: the same number means two different
    // things depending on whether the source was last seen a minute ago or an hour ago.
    const asOf = page.getByTestId('monitoring-asof-orders')
    await expect(asOf).toContainText('as of')
    await expect(asOf).toHaveAttribute('title', /Not the current time/)

    // Four seconds later, the run list mounts and fetches for the first time. Navigated by clicking
    // the tab rather than by `page.goto`, which would reload the document and reset all three:
    // what is under test is three live queries on one page, not three fresh ones.
    await page.waitForTimeout(4000)
    await page.getByTestId('tab-runs').click()
    await expect(page.getByTestId('runs-countdown')).toBeVisible()

    const runs = await seconds(page, 'runs-countdown')
    const metrics = await seconds(page, 'metrics-countdown')

    // The property: three hooks with three mount times give three numbers. A single shared timer in
    // the shell would have made these equal, and a countdown keyed to mounting rather than to the
    // query's own `dataUpdatedAt` would have reset the metrics card on the navigation.
    expect(runs).toBeGreaterThan(metrics)
    expect(runs - metrics).toBeGreaterThanOrEqual(2)

    // And it is a countdown, not a static label.
    await page.waitForTimeout(2000)
    expect(await seconds(page, 'metrics-countdown')).toBeLessThan(metrics)

    await page.screenshot({
      path: path.join(screenshotsDir, '63-refresh-countdowns.png'), fullPage: true,
    })
  })

  test('03 - the run list polls on the ten-second cadence and resets when it lands', async ({ page }) => {
    const counts = await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await expect(page.getByTestId('runs-countdown')).toBeVisible()

    // One fetch on mount, and nothing like a storm: this panel used to have no fixed poll at all,
    // and the risk of layering one under a push-driven panel is that the two chase each other. An
    // upper bound rather than an equality, because a window-focus refetch is a legitimate extra one
    // and is not what "storm" means.
    expect(counts.history).toBeLessThanOrEqual(2)

    // Down to the end of the cycle, and then back up. Both halves matter: the climb alone is true
    // the instant the page loads, and it is the descent before it that makes the climb a refetch
    // rather than a first render. The reset is driven by the query's own `dataUpdatedAt`, so it
    // cannot report a refresh that did not happen.
    await expect
      .poll(() => seconds(page, 'runs-countdown'), { timeout: 20_000, intervals: [250] })
      .toBeLessThanOrEqual(2)

    await expect
      .poll(() => seconds(page, 'runs-countdown'), { timeout: 20_000, intervals: [250] })
      .toBeGreaterThan(8)

    // A handful of requests over twenty seconds, not dozens. The number this panel would produce if
    // the poll and the push-driven invalidation were re-triggering each other has no upper bound at
    // all, which is the failure this bounds.
    expect(counts.history).toBeGreaterThanOrEqual(2)
    expect(counts.history).toBeLessThanOrEqual(4)
  })

  test('04 - watching a live run keeps the tighter backstop rather than the shared cadence', async ({ page }) => {
    await stub(page)
    // The trigger only. The history GET carries a query string and is matched by the RegExp route
    // above, but stating the method keeps this from quietly swallowing it if that ever changes.
    await page.route(`**/api/replications/${REPLICATION_NAME}/runs`, (route) =>
      route.request().method() === 'POST'
        ? route.fulfill({ ...json({ runIds: [RUN_ADVANCED] }), status: 202 })
        : route.fallback())

    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await expect(page.getByTestId('runs-countdown')).toHaveAttribute('title', /every 10 seconds/)

    // Run Now puts the panel into live-watch. The ten-second poll is layered onto the SignalR push
    // and the 1.5-second backstop, never in place of either — so while a run is being watched the
    // panel is still on the tighter cadence, and the countdown says which one it is on rather than
    // counting down from a number the panel is not using.
    await page.getByTestId('trigger-run-button').click()
    await expect(page.getByTestId('live-run-panel')).toBeVisible()
    await expect(page.getByTestId('runs-countdown')).toHaveAttribute('title', /every 2 seconds/)
  })
})

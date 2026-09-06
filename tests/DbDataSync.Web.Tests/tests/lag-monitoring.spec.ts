import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'lag-demo'

/**
 * The four states a mapping's lag can be in, on screen — see phase 86.
 *
 * **Stubbed at the network boundary, not driven through a real source.** The four states are
 * properties of the *payload*: a mapping is unsupported because of its reader kind, has no data
 * because it has never run, and is estimated rather than exact because its version has aged out of
 * `sys.dm_tran_commit_table`. Producing all four against real databases means four replications, a
 * reader that cannot report lag, a mapping deliberately left unrun, and a DMV window nothing can
 * make time pass in — hours of fixture to assert something about rendering. `ReplicationLagTests`
 * covers the server's half against the real service; this covers the client's half against the
 * shape the server sends, and the two meet at `ReplicationLag`.
 *
 * Every request this screen makes is stubbed, including the replication itself, so this file
 * depends on nothing the golden path leaves behind and can run in any order beside it.
 */

const MAPPINGS = ['orders', 'invoices', 'archive', 'fresh']

const LAG_PAYLOAD = {
  mappings: {
    // Exact: CDC, five minutes, stated by the engine at both ends.
    orders: { readerKind: 'MsSqlCdc', supported: true, exactLagMs: 300_000, versionsBehind: null, estimatedLagMs: null },
    // Estimated: Change Tracking past its DMV window, an hour, reconstructed from poll history.
    invoices: { readerKind: 'MsSqlChangeTracking', supported: true, exactLagMs: null, versionsBehind: 4200, estimatedLagMs: 3_600_000 },
    // Not applicable: a reader with no database-wide position to compare against.
    archive: { readerKind: 'Watermark', supported: false, exactLagMs: null, versionsBehind: null, estimatedLagMs: null },
    // No data yet: supported, but nothing has run.
    fresh: { readerKind: 'MsSqlCdc', supported: true, exactLagMs: null, versionsBehind: null, estimatedLagMs: null },
  },
  // The range the server computed: over the two mappings that have a figure, and no others.
  lowestLagMs: 300_000,
  highestLagMs: 3_600_000,
  rangeIncludesEstimates: true,
}

const TASK = {
  name: REPLICATION_NAME,
  enabled: true,
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: {
    reader: { kind: 'MsSqlCdc', options: {} },
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

async function stub(page: Page) {
  const base = `/api/replications/${REPLICATION_NAME}`

  await page.route(`**${base}/lag`, (route) => route.fulfill(json(LAG_PAYLOAD)))
  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json(MAPPINGS)))
  await page.route(`**${base}/table-mappings/*`, (route) => {
    const name = decodeURIComponent(route.request().url().split('/').pop()!)
    return route.fulfill(json({
      name,
      // A mapping that states neither endpoint, so the row has to resolve the replication's — the
      // same inheritance the server applies.
      sources: [{ connectionName: null, database: null, schema: 'dbo', table: name, filter: null }],
      targets: [{ connectionName: null, database: null, schema: 'dbo', table: name }],
      columnMappings: [],
    }))
  })
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true,
  })))
  await page.route(`**${base}/metrics*`, (route) => route.fulfill(json({
    runs: 0, failures: 0, rowsWritten: 0, rowsRead: 0, buckets: [],
    processingP50Ms: null, processingP95Ms: null, processingMaxMs: null, lastCompletedPassUtc: null,
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))
}

test.describe('lag monitoring', () => {
  test('01 - the Monitoring tab renders all four lag states distinctly', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    await expect(page.getByTestId('monitoring-mappings')).toBeVisible()

    // The states, as the rows themselves declare them. Four mappings, four different answers, and
    // no two rows claiming the same one.
    const states = await Promise.all(
      MAPPINGS.map((m) => page.getByTestId(`monitoring-row-${m}`).getAttribute('data-lag-state')),
    )
    expect(states).toEqual(['exact', 'estimated', 'not-applicable', 'no-data'])

    // And as somebody reading the screen sees them. The point of the phase is that these are four
    // readings and not one dash, so the assertion is that the four cells differ from each other —
    // not merely that each contains a word.
    const texts = await Promise.all(
      MAPPINGS.map((m) => page.getByTestId(`monitoring-lag-${m}`).innerText()),
    )
    expect(new Set(texts.map((t) => t.trim())).size).toBe(4)

    await expect(page.getByTestId('monitoring-lag-orders')).toContainText('5m')
    await expect(page.getByTestId('monitoring-lag-orders')).toContainText('exact')
    await expect(page.getByTestId('monitoring-lag-invoices')).toContainText('~1h')
    await expect(page.getByTestId('monitoring-lag-invoices')).toContainText('estimated')
    // The exact version count travels with the estimated time, and is not the same figure.
    await expect(page.getByTestId('monitoring-lag-invoices')).toContainText('4,200 versions behind')

    // Neither of these is a dash, and neither reads like zero.
    await expect(page.getByTestId('monitoring-lag-archive')).toContainText('not applicable')
    await expect(page.getByTestId('monitoring-lag-fresh')).toContainText('no data yet')

    // The source/target convention the mappings overview already uses, resolved through the
    // replication's endpoints because the mapping states none of its own.
    await expect(page.getByTestId('monitoring-row-orders')).toContainText('dbo.orders')
    await expect(page.getByTestId('monitoring-row-orders')).toContainText('src · AppDb')
    await expect(page.getByTestId('monitoring-row-orders')).toContainText('tgt · Warehouse')

    await page.screenshot({ path: path.join(screenshotsDir, '60-monitoring-tab.png'), fullPage: true })
  })

  test('02 - the range is the server\'s, over the mappings that have a figure', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    // Five minutes to an hour: the unsupported and the never-run mapping are not in it, and neither
    // pulled the low end to zero.
    await expect(page.getByTestId('monitoring-range-figure')).toContainText('lowest 5m · highest 1h')

    // And it says an estimate is in it, rather than showing the range as a fact.
    await expect(page.getByTestId('monitoring-range-estimated')).toBeVisible()
  })

  test('03 - the replications list shows the furthest-behind mapping', async ({ page }) => {
    await stub(page)
    await page.route('**/api/replications', (route) => route.fulfill(json([REPLICATION_NAME])))
    await page.goto('/replications')

    // The highest, not the range — a list answers "which of these needs looking at". The ~ carries
    // the same caveat the tab spells out.
    await expect(page.getByTestId(`replication-lag-${REPLICATION_NAME}`)).toHaveText('~1h')

    await page.screenshot({ path: path.join(screenshotsDir, '61-replications-lag-column.png'), fullPage: true })
  })

  test('04 - a replication where nothing can report lag says so rather than showing zero', async ({ page }) => {
    await stub(page)
    await page.route('**/api/replications', (route) => route.fulfill(json([REPLICATION_NAME])))
    await page.route(`**/api/replications/${REPLICATION_NAME}/lag`, (route) => route.fulfill(json({
      mappings: { archive: LAG_PAYLOAD.mappings.archive, fresh: LAG_PAYLOAD.mappings.fresh },
      lowestLagMs: null,
      highestLagMs: null,
      rangeIncludesEstimates: false,
    })))

    await page.goto('/replications')
    await expect(page.getByTestId(`replication-lag-${REPLICATION_NAME}`)).toHaveText('no lag data')

    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)
    await expect(page.getByTestId('monitoring-range-figure')).toContainText('no mapping reports lag yet')
  })

  /**
   * The row has to be tall enough for the cell it contains — phase 96.
   *
   * `.grid-row` sets a **fixed** height, not a floor, and `LagCell` in its fullest state stacks three
   * things: the figure with its badge, "N versions behind", and the "as of" line phase 88 added. That
   * is roughly 50px of content, and in the 40px `tall` box it used to sit in it spilled above *and*
   * below into the rows on either side.
   *
   * **The fixture has to be the fullest state, not the default one.** Every other payload in this
   * file omits `asOfUtc`, which is exactly why this survived: a test built on those rows passes
   * against the broken CSS.
   */
  test('05 - a lag cell in its fullest state stays inside its row', async ({ page }) => {
    await stub(page)
    await page.route(`**/api/replications/${REPLICATION_NAME}/lag`, (route) => route.fulfill(json({
      ...LAG_PAYLOAD,
      mappings: {
        ...LAG_PAYLOAD.mappings,
        // All three lines at once: a figure, a version count beside it, and the reading's own time.
        orders: {
          readerKind: 'MsSqlChangeTracking',
          supported: true,
          exactLagMs: 300_000,
          versionsBehind: 4200,
          estimatedLagMs: null,
          asOfUtc: '2026-05-01T09:30:00Z',
        },
      },
    })))

    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)
    await expect(page.getByTestId('monitoring-asof-orders')).toBeVisible()

    const row = await page.getByTestId('monitoring-row-orders').boundingBox()
    const cell = await page.getByTestId('monitoring-lag-orders').boundingBox()
    expect(row).not.toBeNull()
    expect(cell).not.toBeNull()

    // Inside its row at both ends. `align-items: center` in a too-short row spills equally in both
    // directions, so asserting only the bottom would pass with the top still overlapping.
    expect(cell!.y).toBeGreaterThanOrEqual(row!.y - 0.5)
    expect(cell!.y + cell!.height).toBeLessThanOrEqual(row!.y + row!.height + 0.5)

    // And it does not overlap the row beneath it, which is what the spill actually looked like.
    const next = await page.getByTestId('monitoring-row-invoices').boundingBox()
    expect(cell!.y + cell!.height).toBeLessThanOrEqual(next!.y + 0.5)
  })
})

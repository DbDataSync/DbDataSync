import { test, expect, type Page } from '@playwright/test'
import path from 'node:path'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('monitoring-intent-and-hold')

const REPLICATION_NAME = 'intent-hold-demo'

/**
 * Phase 102: the Monitoring tab shows and manages every mapping's intent and hold.
 *
 * **Stubbed at the network boundary**, following `lag-monitoring.spec.ts`,
 * `run-details-dialog.spec.ts` and `monitoring-restructure.spec.ts`. The read-state store is a plain
 * mutable object per test, updated by the `POST .../read-state` route and read back by the `GET` one —
 * the shape a real recovery or intent change actually has: an action that changes what the next GET
 * reports, not a page that free-floats its own belief about the row.
 *
 * Three mappings carry three different reader kinds on purpose:
 * - `orders` (`MsSqlCdc`) declares all three of `Changes`/`ChangesFromEarliest`/`ChangesFromLatest`.
 * - `legacy` (`Watermark`) declares `Changes` and `ChangesFromLatest` but **not**
 *   `ChangesFromEarliest` — the case a uniform picker gets wrong, per the phase doc.
 * - `reload` (`BatchReload`) declares none of the three at all — the reader with no incremental mode.
 *
 * `InitialLoad` is never in any reader's declared set (phase 101's retarget: it is universal, not
 * per-reader), so every one of the three still has to offer it.
 */

const json = (body: unknown) => ({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify(body),
})

const TASK = (paused: boolean, enabled = true) => ({
  name: REPLICATION_NAME,
  enabled,
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
})

const MAPPING = (name: string, readerKind: string, verification: unknown[] = []) => ({
  name,
  sources: [{ connectionName: 'src', database: 'AppDb', schema: 'dbo', table: name, filter: null }],
  targets: [{ connectionName: 'tgt', database: 'Warehouse', schema: 'dbo', table: name }],
  columnMappings: [],
  verification,
  __readerKind: readerKind, // consumed only by the fixture below, never sent to the SPA
})

const MAPPINGS: Record<string, ReturnType<typeof MAPPING>> = {
  orders: MAPPING('orders', 'MsSqlCdc'),
  legacy: MAPPING('legacy', 'Watermark'),
  reload: MAPPING('reload', 'BatchReload'),
}

const CAPABILITIES = {
  driverType: 'MsSql',
  readers: [
    { kind: 'MsSqlCdc', supportsSegmentation: false, detectsDeletes: true, parameters: [], supportedIntents: ['Changes', 'ChangesFromEarliest', 'ChangesFromLatest'] },
    // The watermark reader: no honest ChangesFromEarliest, per the reader's own doc comment.
    { kind: 'Watermark', supportsSegmentation: false, detectsDeletes: false, parameters: [], supportedIntents: ['Changes', 'ChangesFromLatest'] },
    // Batch reload: no incremental mode at all.
    { kind: 'BatchReload', supportsSegmentation: true, detectsDeletes: false, parameters: [], supportedIntents: [] },
  ],
  stagingProviders: [{ kind: 'MsSqlStagingTable', parameters: [] }],
  writers: [{ kind: 'MsSqlMerge', supportsReconciliation: true, parameters: [] }],
  supportsConnectionTest: false,
  supportedProvisioningActions: [],
}

interface ReadStateFixture {
  [mappingName: string]: { intent: string; hold: string; watermark: string | null; watermarkTimeUtc: string | null }
}

/** The read-state store this test's stubs share — a POST updates it, a GET reports it. */
function defaultReadStates(): ReadStateFixture {
  return {
    orders: { intent: 'Changes', hold: 'None', watermark: '2026-05-01', watermarkTimeUtc: null },
    legacy: { intent: 'Changes', hold: 'None', watermark: '2026-05-01', watermarkTimeUtc: null },
    reload: { intent: 'InitialLoad', hold: 'None', watermark: null, watermarkTimeUtc: null },
  }
}

async function stub(page: Page, options: {
  taskPaused?: boolean
  taskEnabled?: boolean
  readStates?: ReadStateFixture
} = {}) {
  const base = `/api/replications/${REPLICATION_NAME}`
  const names = Object.keys(MAPPINGS)
  const states = options.readStates ?? defaultReadStates()

  await page.route(`**${base}/lag`, (route) => route.fulfill(json({
    mappings: Object.fromEntries(names.map((name) => [
      name,
      { readerKind: MAPPINGS[name].__readerKind, supported: true, exactLagMs: 60_000, versionsBehind: null, estimatedLagMs: null },
    ])),
    lowestLagMs: 60_000,
    highestLagMs: 60_000,
    rangeIncludesEstimates: false,
  })))

  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json(names)))
  await page.route(`**${base}/table-mappings/*`, (route) => {
    const name = decodeURIComponent(route.request().url().split('/').pop()!)
    const { __readerKind, ...mapping } = MAPPINGS[name]
    return route.fulfill(json(mapping))
  })

  for (const name of names) {
    await page.route(`**${base}/table-mappings/${name}/read-state`, (route) => {
      if (route.request().method() === 'POST') {
        const body = JSON.parse(route.request().postData() ?? '{}')
        states[name] = { ...states[name], intent: body.intent, hold: body.hold }
      }
      return route.fulfill(json(states[name]))
    })
  }

  await page.route('**/api/connections/src/capabilities', (route) => route.fulfill(json(CAPABILITIES)))
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: options.taskEnabled ?? true, paused: options.taskPaused ?? false,
    pauseNote: options.taskPaused ? 'investigating a source outage' : null, shouldRun: !(options.taskPaused ?? false),
  })))
  await page.route(`**${base}/metrics*`, (route) => route.fulfill(json({
    runs: 0, failures: 0, rowsWritten: 0, rowsRead: 0, buckets: [],
    processingP50Ms: null, processingP95Ms: null, processingMaxMs: null, lastCompletedPassUtc: null,
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK(options.taskPaused ?? false, options.taskEnabled ?? true))))
}

test.describe('monitoring: intent and hold (phase 102)', () => {
  test('01 - a held mapping renders as held and does not read as idle', async ({ page }) => {
    await stub(page, {
      readStates: {
        ...defaultReadStates(),
        orders: { intent: 'Changes', hold: 'PositionExpired', watermark: null, watermarkTimeUtc: null },
      },
    })
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    const held = page.getByTestId('monitoring-row-orders')
    const idle = page.getByTestId('monitoring-row-legacy')

    await expect(held).toHaveAttribute('data-hold-state', 'position-expired')
    await expect(idle).toHaveAttribute('data-hold-state', 'none')

    await expect(page.getByTestId('monitoring-hold-orders')).toBeVisible()
    await expect(page.getByTestId('monitoring-hold-orders')).toContainText('position expired')
    // The unheld row states no hold at all — it does not carry the badge with an "off" spelling of it.
    await expect(page.getByTestId('monitoring-hold-legacy')).toHaveCount(0)

    await expect(page.getByTestId('monitoring-manage-orders')).toHaveText('Recover…')

    await page.screenshot({ path: path.join(screenshotsDir, '90-monitoring-held-mapping.png'), fullPage: true })
  })

  test('02 - only declared intents are offered, and InitialLoad is offered regardless of reader', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    await page.getByTestId('monitoring-manage-orders').click()
    let options = await page.getByTestId('read-state-intent-select').locator('option').allTextContents()
    expect(options).toEqual(['Initial load', 'Changes', 'Changes, from earliest', 'Changes, from latest'])
    await page.getByTestId('read-state-cancel').click()

    // The watermark reader: no honest ChangesFromEarliest, so it is not offered — the case a uniform
    // picker would get wrong.
    await page.getByTestId('monitoring-manage-legacy').click()
    options = await page.getByTestId('read-state-intent-select').locator('option').allTextContents()
    expect(options).toEqual(['Initial load', 'Changes', 'Changes, from latest'])
    expect(options).not.toContain('Changes, from earliest')
    await page.getByTestId('read-state-cancel').click()

    // Batch reload declares nothing at all, and still gets InitialLoad rather than an empty picker.
    await page.getByTestId('monitoring-manage-reload').click()
    options = await page.getByTestId('read-state-intent-select').locator('option').allTextContents()
    expect(options).toEqual(['Initial load'])
  })

  test('03 - recovering from a hold issues the expected call and the row stops being held', async ({ page }) => {
    const states = {
      ...defaultReadStates(),
      orders: { intent: 'Changes', hold: 'PositionExpired', watermark: null, watermarkTimeUtc: null },
    }
    await stub(page, { readStates: states })
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    await expect(page.getByTestId('monitoring-row-orders')).toHaveAttribute('data-hold-state', 'position-expired')

    await page.getByTestId('monitoring-manage-orders').click()
    await expect(page.getByTestId('read-state-dialog')).toContainText('discarded history')
    await expect(page.getByTestId('read-state-recover-earliest')).toBeVisible()
    await expect(page.getByTestId('read-state-recover-initial-load')).toBeVisible()

    await page.getByTestId('read-state-recover-earliest').click()

    // The dialog closes and the row's own hold clears — driven by the same GET the row already polls,
    // now answering with what the POST above just wrote into the fixture.
    await expect(page.getByTestId('read-state-dialog')).toHaveCount(0)
    await expect(page.getByTestId('monitoring-row-orders')).toHaveAttribute('data-hold-state', 'none')
    expect(states.orders).toMatchObject({ intent: 'ChangesFromEarliest', hold: 'None' })

    await page.screenshot({ path: path.join(screenshotsDir, '91-monitoring-recovered.png'), fullPage: true })
  })

  test('04 - ChangesFromLatest requires a confirmation that names what is skipped', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    await page.getByTestId('monitoring-manage-orders').click()
    await page.getByTestId('read-state-intent-select').selectOption('ChangesFromLatest')
    await page.getByTestId('read-state-confirm').click()

    // Not committed yet — the picker's Save became a "Continue…" into a second, explicit step.
    const warning = page.getByTestId('read-state-data-loss-warning')
    await expect(warning).toBeVisible()
    await expect(warning).toContainText('dbo.orders')
    await expect(warning).toContainText('never be replicated')

    await page.screenshot({ path: path.join(screenshotsDir, '92-monitoring-data-loss-confirm.png'), fullPage: true })

    // Back retreats to the picker rather than committing anything.
    await page.getByTestId('read-state-back').click()
    await expect(page.getByTestId('read-state-intent-select')).toBeVisible()
    await expect(page.getByTestId('read-state-data-loss-warning')).toHaveCount(0)
  })

  test('05 - a table resumed under a paused replication still reads as not running', async ({ page }) => {
    const states = {
      ...defaultReadStates(),
      orders: { intent: 'Changes', hold: 'Paused', watermark: null, watermarkTimeUtc: null },
    }
    await stub(page, { taskPaused: true, readStates: states })
    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)

    // Both grains are true at once, and the row states the coarser one — mirroring StatusCard's own
    // precedence (disabled, then paused, then running/idle) rather than two badges to reconcile.
    await expect(page.getByTestId('monitoring-row-orders')).toHaveAttribute('data-hold-state', 'replication-paused')
    await expect(page.getByTestId('monitoring-hold-orders')).toContainText('replication paused')
    // The mapping's own hold is not lost — it is still named, so clearing it is not mistaken for
    // enough on its own.
    await expect(page.getByTestId('monitoring-hold-own-orders')).toContainText('paused on its own')

    // Resuming the mapping's own hold (clearing ReadHold.Paused) does not flip the row to idle: the
    // replication is still paused, and the row has to keep saying so.
    await page.getByTestId('monitoring-pause-orders').click()
    await expect.poll(() => states.orders.hold).toBe('None')
    await expect(page.getByTestId('monitoring-row-orders')).toHaveAttribute('data-hold-state', 'replication-paused')
    await expect(page.getByTestId('monitoring-hold-orders')).toContainText('replication paused')

    await page.screenshot({ path: path.join(screenshotsDir, '93-monitoring-paused-under-replication-pause.png'), fullPage: true })
  })

  /**
   * The lag cell in its fullest state *plus* the new Intent & hold content — phase 96's original
   * assertion, extended, because this is the row phase 102 makes taller.
   */
  test('06 - a lag cell in its fullest state, beside a held mapping\'s content, stays inside the row', async ({ page }) => {
    const states = {
      ...defaultReadStates(),
      orders: { intent: 'ChangesFromEarliest', hold: 'PositionExpired', watermark: null, watermarkTimeUtc: null },
    }
    await stub(page, { readStates: states })
    await page.route(`**/api/replications/${REPLICATION_NAME}/lag`, (route) => route.fulfill(json({
      mappings: {
        orders: {
          readerKind: 'MsSqlCdc', supported: true, exactLagMs: 300_000, versionsBehind: 4200,
          estimatedLagMs: null, asOfUtc: '2026-05-01T09:30:00Z',
        },
        legacy: { readerKind: 'Watermark', supported: true, exactLagMs: 60_000, versionsBehind: null, estimatedLagMs: null },
        reload: { readerKind: 'BatchReload', supported: false, exactLagMs: null, versionsBehind: null, estimatedLagMs: null },
      },
      lowestLagMs: 60_000,
      highestLagMs: 300_000,
      rangeIncludesEstimates: false,
    })))

    await page.goto(`/replications/${REPLICATION_NAME}/monitoring`)
    await expect(page.getByTestId('monitoring-asof-orders')).toBeVisible()
    await expect(page.getByTestId('monitoring-hold-orders')).toBeVisible()

    const row = await page.getByTestId('monitoring-row-orders').boundingBox()
    const cell = await page.getByTestId('monitoring-lag-orders').boundingBox()
    const intentHold = await page.getByTestId('monitoring-intent-orders').boundingBox()
    expect(row).not.toBeNull()
    expect(cell).not.toBeNull()
    expect(intentHold).not.toBeNull()

    expect(cell!.y).toBeGreaterThanOrEqual(row!.y - 0.5)
    expect(cell!.y + cell!.height).toBeLessThanOrEqual(row!.y + row!.height + 0.5)
    expect(intentHold!.y).toBeGreaterThanOrEqual(row!.y - 0.5)
    expect(intentHold!.y + intentHold!.height).toBeLessThanOrEqual(row!.y + row!.height + 0.5)

    const next = await page.getByTestId('monitoring-row-legacy').boundingBox()
    expect(cell!.y + cell!.height).toBeLessThanOrEqual(next!.y + 0.5)
    expect(intentHold!.y + intentHold!.height).toBeLessThanOrEqual(next!.y + 0.5)
  })
})

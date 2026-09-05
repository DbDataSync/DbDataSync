import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'provisioning-wide-demo'
const MAPPING_NAME = 'orders'

/**
 * Phase 105 — the replication-wide Provisioning tab.
 *
 * **Stubbed at the network boundary**, following `lag-monitoring.spec.ts` and
 * `monitoring-restructure.spec.ts`: what is under test is the page's layout and interaction — one
 * group per server, the warn-never-block prerequisite rule, Copy carrying exactly the selected
 * statements, Run reporting per-step outcomes and refreshing the plan — not any particular DDL a real
 * driver would produce. `ReplicationProvisioningPlanTests` (unit) covers the server's dedup/group/order
 * logic against that real shape.
 */

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
    source: { connectionName: 'src', database: 'Sales' },
    target: { connectionName: 'tgt', database: 'DW' },
  },
  provisioning: { createTargetTableIfMissing: true, alterTargetTableColumnsIfMissingOrChanged: null },
}

const DB_STEP = {
  id: 'step-db-1',
  title: 'Enable Change Tracking on database [Sales]',
  commandText: 'ALTER DATABASE [Sales] SET CHANGE_TRACKING = ON;',
  rationale: null,
  scope: 'Database',
  side: 'Source',
  automatic: false,
  contributingMappings: ['orders', 'customers'],
}

const TABLE_STEP = {
  id: 'step-table-1',
  title: 'Enable Change Tracking on table [Orders]',
  commandText: 'ALTER TABLE [dbo].[Orders] ENABLE CHANGE_TRACKING;',
  rationale: null,
  scope: 'Table',
  side: 'Source',
  automatic: false,
  contributingMappings: ['orders'],
}

const CREATE_STEP = {
  id: 'step-create-1',
  title: 'Create table [dbo].[Orders]',
  commandText: 'CREATE TABLE [dbo].[Orders] (Id INT NOT NULL PRIMARY KEY);',
  rationale: null,
  scope: 'Table',
  side: 'Target',
  automatic: true,
  contributingMappings: ['orders'],
}

function planPayload() {
  return {
    groups: [
      { connectionName: 'src', database: 'Sales', side: 'Source', steps: [DB_STEP, TABLE_STEP] },
      { connectionName: 'tgt', database: 'DW', side: 'Target', steps: [CREATE_STEP] },
    ],
    excluded: [
      { mappingName: 'legacy', side: 'Target', reason: "'legacy' has no primary key to key an upsert on." },
    ],
  }
}

const json = (body: unknown) => ({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify(body),
})

/** Captures every `navigator.clipboard.writeText` call into `window.__copied` — Playwright's clipboard
 * permission model is heavier than this needs, and this is the same trick other specs in this suite
 * use for anything that reads back what was copied. */
async function captureClipboard(page: Page) {
  await page.addInitScript(() => {
    // @ts-expect-error -- test-only global
    window.__copied = []
    const write = (text: string) => {
      // @ts-expect-error -- test-only global
      window.__copied.push(text)
      return Promise.resolve()
    }
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: write }, configurable: true })
  })
}

async function lastCopiedText(page: Page): Promise<string> {
  // @ts-expect-error -- test-only global
  return page.evaluate(() => (window.__copied as string[]).at(-1) ?? '')
}

async function stub(page: Page, { planCallCount }: { planCallCount: { count: number } }) {
  const base = `/api/replications/${REPLICATION_NAME}`

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
    processingP50Ms: 0, processingP95Ms: 0, processingMaxMs: 0, lastCompletedPassUtc: null,
  })))
  await page.route(`**${base}/lag`, (route) => route.fulfill(json({
    mappings: {}, lowestLagMs: null, highestLagMs: null, rangeIncludesEstimates: false,
  })))
  await page.route(`**${base}/provisioning`, (route) => {
    planCallCount.count += 1
    return route.fulfill(json(planPayload()))
  })
  await page.route(`**${base}/provisioning/apply`, (route) => route.fulfill(json({
    steps: [
      { id: TABLE_STEP.id, title: TABLE_STEP.title, outcome: 'Applied', error: null, warning: null, elapsedMs: 12 },
    ],
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))
}

test.describe('replication detail: replication-wide provisioning (phase 105)', () => {
  test('01 - the page renders one group per server, and lists excluded mappings with their reason', async ({ page }) => {
    await stub(page, { planCallCount: { count: 0 } })
    await page.goto(`/replications/${REPLICATION_NAME}/overview/provisioning`)

    await expect(page.getByTestId('overview-tab-provisioning')).toHaveClass(/active/)
    await expect(page.getByTestId('replication-provisioning-panel')).toBeVisible()

    const groups = page.getByTestId('provisioning-group')
    await expect(groups).toHaveCount(2)
    await expect(groups.filter({ hasText: 'src' })).toHaveAttribute('data-database', 'Sales')
    await expect(groups.filter({ hasText: 'src' })).toHaveAttribute('data-side', 'Source')
    await expect(groups.filter({ hasText: 'tgt' })).toHaveAttribute('data-database', 'DW')
    await expect(groups.filter({ hasText: 'tgt' })).toHaveAttribute('data-side', 'Target')

    // The target step the auto-flag would run anyway is still shown and still ticked, just labelled.
    const createRow = page.getByTestId('provisioning-step').filter({ hasText: CREATE_STEP.title })
    await expect(createRow.getByTestId('provisioning-step-automatic')).toBeVisible()
    await expect(createRow.getByTestId('provisioning-step-checkbox')).toBeChecked()

    await expect(page.getByTestId('provisioning-excluded-row')).toHaveCount(1)
    await expect(page.getByTestId('provisioning-excluded-row')).toContainText('legacy')
    await expect(page.getByTestId('provisioning-excluded-row')).toContainText('no primary key')

    await page.screenshot({ path: path.join(screenshotsDir, '90-provisioning-groups.png'), fullPage: true })
  })

  test('02 - unticking a database-level step surfaces the warning on its dependent table-level step', async ({ page }) => {
    await stub(page, { planCallCount: { count: 0 } })
    await page.goto(`/replications/${REPLICATION_NAME}/overview/provisioning`)
    await expect(page.getByTestId('provisioning-group')).toHaveCount(2)

    const dbRow = page.getByTestId('provisioning-step').filter({ hasText: DB_STEP.title })
    const tableRow = page.getByTestId('provisioning-step').filter({ hasText: TABLE_STEP.title })

    await expect(tableRow.getByTestId('provisioning-step-warning')).toHaveCount(0)

    await dbRow.getByTestId('provisioning-step-checkbox').uncheck()

    await expect(tableRow.getByTestId('provisioning-step-warning')).toBeVisible()
    await expect(tableRow.getByTestId('provisioning-step-warning')).toContainText('database-level prerequisite')
    // Warned, never blocked — the table step stays selectable and ticked.
    await expect(tableRow.getByTestId('provisioning-step-checkbox')).toBeChecked()

    await page.screenshot({ path: path.join(screenshotsDir, '91-provisioning-prerequisite-warning.png'), fullPage: true })
  })

  test('03 - Copy yields only the selected statements for that group', async ({ page }) => {
    await captureClipboard(page)
    await stub(page, { planCallCount: { count: 0 } })
    await page.goto(`/replications/${REPLICATION_NAME}/overview/provisioning`)
    await expect(page.getByTestId('provisioning-group')).toHaveCount(2)

    const dbRow = page.getByTestId('provisioning-step').filter({ hasText: DB_STEP.title })
    await dbRow.getByTestId('provisioning-step-checkbox').uncheck()

    const sourceGroup = page.getByTestId('provisioning-group').filter({ hasText: 'src' })
    await sourceGroup.getByTestId('provisioning-group-copy').click()

    const copied = await lastCopiedText(page)
    expect(copied).toContain(TABLE_STEP.commandText)
    expect(copied).not.toContain(DB_STEP.commandText)
    // The header comment names the replication and the endpoint, for a DBA reading it out of context.
    expect(copied).toContain(REPLICATION_NAME)
    expect(copied).toContain('src')
    expect(copied).toContain('Sales')
  })

  test('04 - Run reports per-step outcomes and the plan refreshes afterward', async ({ page }) => {
    const planCallCount = { count: 0 }
    await stub(page, { planCallCount })
    page.on('dialog', (dialog) => dialog.accept())

    await page.goto(`/replications/${REPLICATION_NAME}/overview/provisioning`)
    await expect(page.getByTestId('provisioning-group')).toHaveCount(2)
    const callsBeforeRun = planCallCount.count

    await page.getByTestId('provisioning-run-selected').click()

    const tableRow = page.getByTestId('provisioning-step').filter({ hasText: TABLE_STEP.title })
    await expect(tableRow.getByTestId('provisioning-step-result')).toContainText('applied')

    // Apply's success invalidates the plan query, and the page re-fetches it — the same "re-plan after
    // Apply" rule the per-mapping Setup card follows, so selection is never left describing a plan that
    // is no longer current.
    await expect.poll(() => planCallCount.count).toBeGreaterThan(callsBeforeRun)

    await page.screenshot({ path: path.join(screenshotsDir, '92-provisioning-run-results.png'), fullPage: true })
  })
})

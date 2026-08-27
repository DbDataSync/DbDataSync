import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { DB_NAME, querySql, runSql, SA_PASSWORD, SOURCE_TABLE, SRC_CONNECTION_NAME, TARGET_TABLE, TGT_CONNECTION_NAME } from '../test-db'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'playwright-sync'
const MAPPING_NAME = 'items'

async function shot(page: Page, name: string) {
  await page.screenshot({ path: path.join(screenshotsDir, name), fullPage: true })
}

/** Waits for a <select data-testid=testId>'s options to include `value` (populated asynchronously by
 * a metadata-browsing API call) before selecting it — avoids racing react-query. */
async function selectWhenReady(page: Page, testId: string, value: string) {
  const select = page.getByTestId(testId)
  await expect(select.locator(`option[value="${value}"]`)).toBeAttached({ timeout: 15_000 })
  await select.selectOption(value)
}

test.describe.serial('golden path: define, configure, and run a replication end-to-end', () => {
  test('01 - app loads and redirects to the replications list', async ({ page }) => {
    await page.goto('/')
    await expect(page).toHaveURL(/\/replications$/)
    await expect(page.getByRole('heading', { name: 'Replications' })).toBeVisible()
    await shot(page, '01-replications-empty.png')
  })

  test('02 - create source and target connections', async ({ page }) => {
    await page.goto('/connections')

    for (const name of [SRC_CONNECTION_NAME, TGT_CONNECTION_NAME]) {
      await page.getByTestId('new-connection-button').click()
      await expect(page).toHaveURL(/\/connections\/new$/)
      await page.getByTestId('connection-name-input').fill(name)
      await page.getByTestId('connection-host-input').fill('localhost')
      await page.locator('#conn-port').fill('14330')
      await page.getByTestId('connection-database-input').fill(DB_NAME)
      await page.getByTestId('connection-userid-input').fill('sa')
      await page.getByTestId('connection-password-input').fill(SA_PASSWORD)
      if (name === SRC_CONNECTION_NAME) await shot(page, '02-connection-form.png')
      await page.getByTestId('save-connection-button').click()
      await expect(page.getByTestId('connections-table')).toContainText(name)
    }

    await shot(page, '03-connections-list.png')
  })

  test('03 - create the replication', async ({ page }) => {
    await page.goto('/replications')
    await page.getByTestId('new-replication-button').click()
    await page.getByTestId('replication-name-input').fill(REPLICATION_NAME)
    await page.getByTestId('create-replication-button').click()

    await expect(page).toHaveURL(new RegExp(`/replications/${REPLICATION_NAME}$`))
    await expect(page.getByRole('heading', { name: REPLICATION_NAME })).toBeVisible()
  })

  test('04 - set the replication\'s endpoints', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-overview').click()

    // Source and target belong to the replication: every mapping inherits them, and only a mapping
    // that genuinely reads or writes somewhere else has to say so.
    await selectWhenReady(page, 'task-source-connection-select', SRC_CONNECTION_NAME)
    await selectWhenReady(page, 'task-source-database-select', DB_NAME)
    await selectWhenReady(page, 'task-target-connection-select', TGT_CONNECTION_NAME)
    await selectWhenReady(page, 'task-target-database-select', DB_NAME)
    await shot(page, '04-replication-endpoints.png')

    await page.getByTestId('save-settings-button').click()

    await page.reload()
    await page.getByTestId('tab-overview').click()
    await expect(page.getByTestId('task-source-connection-select')).toHaveValue(SRC_CONNECTION_NAME, { timeout: 15_000 })
  })

  test('05 - add a table mapping that inherits the replication\'s endpoints', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId('new-mapping-button').click()

    await page.getByTestId('mapping-name-input').fill(MAPPING_NAME)

    // Nothing to pick but the tables — the connection and database arrive from the replication and
    // are shown read-only, and the table picker still cascades from them.
    await expect(page.getByTestId('source-side')).toContainText('INHERITED')
    await expect(page.getByTestId('source-connection-select')).toHaveCount(0)
    await selectWhenReady(page, 'source-table-select', `dbo.${SOURCE_TABLE}`)
    await selectWhenReady(page, 'target-table-select', `dbo.${TARGET_TABLE}`)

    // Column mappings auto-suggest once both tables' columns load (same-name match: Id, Name).
    // The design renders rows as CSS-grid divs rather than a <table>, so count the row class.
    await expect(page.getByTestId('column-mappings-table').locator('.grid-row')).toHaveCount(2, { timeout: 15_000 })
    await shot(page, '05-table-mapping-form.png')

    await page.getByTestId('save-mapping-button').click()
    // The mappings list is the sidebar now, not a table below the form.
    await expect(page.getByTestId(`mapping-item-${MAPPING_NAME}`)).toBeVisible({ timeout: 15_000 })
    await shot(page, '06-table-mappings-list.png')
  })

  test('06 - trigger a run and watch it complete live', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-runs').click()
    await page.getByTestId('trigger-run-button').click()

    await expect(page.getByTestId('live-run-panel')).toBeVisible()
    await expect(page.getByTestId('live-log-viewer')).toContainText('Run started', { timeout: 15_000 })
    await shot(page, '07-live-run-in-progress.png')

    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 30_000 })
    await expect(page.getByTestId('live-run-panel')).toContainText('2 row(s) read · 2 row(s) written')
    await shot(page, '08-live-run-completed.png')

    // The live panel auto-clears a few seconds after completion, leaving the persisted history row.
    await expect(page.getByTestId('run-history-table')).toContainText('succeeded', { timeout: 10_000 })
    await shot(page, '09-run-history.png')
  })

  test('07 - config history shows the auto-committed changes', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-history').click()
    await expect(page.getByTestId('history-table')).toContainText('replication task')
    await expect(page.getByTestId('history-table')).toContainText('table mapping')
    await shot(page, '10-config-history.png')
  })

  test('08 - data actually replicated to the target table', async () => {
    // The real end-to-end proof, independent of anything the UI claims.
    const output = querySql(`SET NOCOUNT ON; SELECT Id, Name FROM dbo.[${TARGET_TABLE}] ORDER BY Id;`, DB_NAME)
    expect(output).toContain('Widget')
    expect(output).toContain('Gadget')
    expect(output.trim().split('\n').filter((l) => l.trim())).toHaveLength(2)
  })

  test('09 - an ad-hoc backfill repairs the target through the UI', async ({ page }) => {
    // Diverge the target from the source behind the replication's back. An incremental pass can't fix
    // this — Change Tracking has nothing new to report, since nothing changed at the *source* — which
    // is exactly the situation a reload exists for.
    runSql(`DELETE FROM dbo.[${TARGET_TABLE}] WHERE Name = 'Widget';`, DB_NAME)
    expect(querySql(`SET NOCOUNT ON; SELECT Name FROM dbo.[${TARGET_TABLE}];`, DB_NAME)).not.toContain('Widget')

    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('backfill-button').click()
    await expect(page.getByTestId('backfill-form')).toBeVisible()

    // The Kind pickers are populated from the live capabilities endpoint, and default by capability:
    // a reader that can be scoped to a segment, and a writer that reconciles rather than only upserts.
    await expect(page.getByTestId('backfill-reader-select')).toHaveValue('MsSqlBatchReload')
    await expect(page.getByTestId('backfill-writer-select')).toHaveValue('MsSqlMergeReconcile')
    await expect(page.getByTestId('backfill-mapping-select')).toHaveValue(MAPPING_NAME)
    await shot(page, '11-backfill-form.png')

    await page.getByTestId('backfill-submit-button').click()

    await expect(page.getByTestId('live-run-panel')).toBeVisible()
    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 30_000 })
    await shot(page, '12-backfill-completed.png')

    // Distinguishable from the replication's own incremental passes in history.
    await expect(page.getByTestId('run-history-table')).toContainText('BACKFILL', { timeout: 10_000 })
    await expect(page.getByTestId('run-history-table')).toContainText('full')
    await shot(page, '13-run-history-with-backfill.png')

    const rows = querySql(`SET NOCOUNT ON; SELECT Id, Name FROM dbo.[${TARGET_TABLE}] ORDER BY Id;`, DB_NAME)
    expect(rows).toContain('Widget')
    expect(rows).toContain('Gadget')
  })

  test('10 - the backfill left the incremental sync\'s watermark alone', async ({ page }) => {
    // Nothing has changed at the source since test 05, so the next incremental pass must read nothing.
    // Had the backfill disturbed the watermark, this pass would re-read the whole table instead — the
    // single most important consequence of Backfill runs never calling SetWatermark.
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-runs').click()
    await page.getByTestId('trigger-run-button').click()

    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 30_000 })
    await expect(page.getByTestId('live-run-panel')).toContainText('0 row(s) read · 0 row(s) written')
  })

  test('11 - settings expose live driver capabilities and per-stage options', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-overview').click()

    // Offered because the registered driver advertises them, not because they are compiled into the
    // SPA — the reload reader and reconciling writers did not exist when this picker was written.
    const readerSelect = page.getByTestId('reader-kind-select')
    await expect(readerSelect.locator('option[value="MsSqlBatchReload"]')).toBeAttached({ timeout: 15_000 })
    await expect(readerSelect.locator('option[value="MsSqlBatchReload"]')).toContainText('segmentable')

    // The pipeline is three selectable stages; picking one swaps both the Kind picker and its options.
    await page.getByTestId('stage-writer').click()
    await expect(page.getByTestId('writer-kind-select').locator('option[value="MsSqlMerge"]')).toContainText('upsert-only')

    // A stage option is a key/value pair now, not a line of JSON in a textarea.
    await page.getByTestId('stage-reader').click()
    const options = page.getByTestId('reader-options')
    await options.getByPlaceholder('Setting name').fill('snapshotIsolation')
    await options.getByRole('button', { name: '+ Add' }).click()
    await options.getByLabel('snapshotIsolation value').fill('false')

    await page.getByTestId('save-settings-button').click()
    await shot(page, '14-pipeline-settings.png')

    // Survives a reload, which is the only proof it reached the config repo.
    await page.reload()
    await page.getByTestId('tab-overview').click()
    await expect(page.getByTestId('reader-options').getByLabel('snapshotIsolation value')).toHaveValue('false', { timeout: 15_000 })
  })

  test('12 - a mapping can override the replication\'s endpoint for itself', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId(`mapping-item-${MAPPING_NAME}`).click()

    // Turning the override on replaces the read-only inherited values with pickers, seeded with
    // what was in effect — so it opens as a starting point, not a blank form.
    await page.getByTestId('source-override-toggle').click()
    await expect(page.getByTestId('source-side')).not.toContainText('INHERITED')
    await expect(page.getByTestId('source-connection-select')).toHaveValue(SRC_CONNECTION_NAME)
    await selectWhenReady(page, 'source-database-select', DB_NAME)
    await selectWhenReady(page, 'source-table-select', `dbo.${SOURCE_TABLE}`)
    await shot(page, '15-mapping-endpoint-override.png')

    await page.getByTestId('save-mapping-button').click()
    await expect(page.getByTestId(`mapping-item-${MAPPING_NAME}`)).toBeVisible({ timeout: 15_000 })

    // Turning it back off returns the mapping to inheriting, which is what the saved config has to
    // record — an override that cannot be undone is a one-way door.
    await page.reload()
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId(`mapping-item-${MAPPING_NAME}`).click()
    await expect(page.getByTestId('source-connection-select')).toBeVisible({ timeout: 15_000 })
    await page.getByTestId('source-override-toggle').click()
    await expect(page.getByTestId('source-side')).toContainText('INHERITED')
    await page.getByTestId('save-mapping-button').click()

    await page.reload()
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId(`mapping-item-${MAPPING_NAME}`).click()
    await expect(page.getByTestId('source-side')).toContainText('INHERITED', { timeout: 15_000 })
  })
})

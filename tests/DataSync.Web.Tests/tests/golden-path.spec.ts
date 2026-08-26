import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { DB_NAME, querySql, SA_PASSWORD, SOURCE_TABLE, SRC_CONNECTION_NAME, TARGET_TABLE, TGT_CONNECTION_NAME } from '../test-db'

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

  test('04 - add a table mapping via the metadata pickers', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId('new-mapping-button').click()

    await page.getByTestId('mapping-name-input').fill(MAPPING_NAME)

    await selectWhenReady(page, 'source-connection-select', SRC_CONNECTION_NAME)
    await selectWhenReady(page, 'source-database-select', DB_NAME)
    await selectWhenReady(page, 'source-table-select', `dbo.${SOURCE_TABLE}`)

    await selectWhenReady(page, 'target-connection-select', TGT_CONNECTION_NAME)
    await selectWhenReady(page, 'target-database-select', DB_NAME)
    await selectWhenReady(page, 'target-table-select', `dbo.${TARGET_TABLE}`)

    // Column mappings auto-suggest once both tables' columns load (same-name match: Id, Name).
    await expect(page.getByTestId('column-mappings-table').locator('tbody tr')).toHaveCount(2, { timeout: 15_000 })
    await shot(page, '04-table-mapping-form.png')

    await page.getByTestId('save-mapping-button').click()
    await expect(page.getByTestId('table-mappings-table')).toContainText(MAPPING_NAME)
    await shot(page, '05-table-mappings-list.png')
  })

  test('05 - trigger a run and watch it complete live', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-runs').click()
    await page.getByTestId('trigger-run-button').click()

    await expect(page.getByTestId('live-run-panel')).toBeVisible()
    await expect(page.getByTestId('live-log-viewer')).toContainText('Run started', { timeout: 15_000 })
    await shot(page, '06-live-run-in-progress.png')

    await expect(page.getByTestId('live-run-panel')).toContainText('Succeeded', { timeout: 30_000 })
    await expect(page.getByTestId('live-run-panel')).toContainText('2 row(s) read, 2 row(s) written')
    await shot(page, '07-live-run-completed.png')

    // The live panel auto-clears a few seconds after completion, leaving the persisted history row.
    await expect(page.getByTestId('run-history-table')).toContainText('Succeeded', { timeout: 10_000 })
    await shot(page, '08-run-history.png')
  })

  test('06 - config history shows the auto-committed changes', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-history').click()
    await expect(page.getByTestId('history-table')).toContainText('replication task')
    await expect(page.getByTestId('history-table')).toContainText('table mapping')
    await shot(page, '09-config-history.png')
  })

  test('07 - data actually replicated to the target table', async () => {
    // The real end-to-end proof, independent of anything the UI claims.
    const output = querySql(`SET NOCOUNT ON; SELECT Id, Name FROM dbo.[${TARGET_TABLE}] ORDER BY Id;`, DB_NAME)
    expect(output).toContain('Widget')
    expect(output).toContain('Gadget')
    expect(output.trim().split('\n').filter((l) => l.trim())).toHaveLength(2)
  })
})

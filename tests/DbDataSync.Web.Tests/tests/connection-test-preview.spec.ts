import path from 'node:path'
import { test, expect } from '@playwright/test'
import { KNOWN_DRIVER_ID, KNOWN_DRIVER_LIBRARY } from '../playwright.config'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('connection-test-preview')

// The real MySQL container docker-compose.yml stands up (dbdatasync-mysql, port 13306) — a
// GenericDriver-backed connection (mysql.generic) is the only way to exercise phase 176M's
// PreviewConnection end to end through the real console: every hand-written built-in (MsSql/Postgres/
// MySql/Oracle) doesn't implement it, only a descriptor-driven driver does.
const MYSQL_ROOT_PASSWORD = process.env.DBDATASYNC_MYSQL_ROOT_PASSWORD ?? 'DbDataSync_Test_Pw1'
const CONNECTION_NAME = `preview-e2e-${Date.now()}`

/**
 * Phase 177M: `ConnectionTestCard`'s new "What was actually resolved and attempted" disclosure, showing
 * phase 176M's `resolvedConnectionString`/`jdbcUri`/`outsideProperties`. Installs `mysql.generic` itself
 * (self-contained — doesn't depend on `admin-drivers-libraries.spec.ts` having already run in this
 * invocation) rather than assuming another spec's fixture state.
 */
test.describe.serial('connection test preview (phase 177M)', () => {
  test('installing mysql.generic makes a preview-capable driver available', async ({ page }) => {
    await page.goto('/drivers')
    const catalogRow = page.getByTestId(`admin-known-driver-${KNOWN_DRIVER_ID}`)
    const driverRow = page.getByTestId(`admin-driver-row-${KNOWN_DRIVER_ID}`)

    // Which of the two branches applies depends on whether an earlier spec in this same run already
    // installed mysql.generic — but the known-drivers catalog list loads asynchronously, so checking
    // catalogRow.isVisible() right after navigation can read "not visible yet" (the fetch hasn't
    // resolved) as "already installed," which isn't what either element actually means. Wait for the
    // page to settle into one of its two real starting states first.
    await expect(catalogRow.or(driverRow)).toBeVisible({ timeout: 10_000 })

    if (await catalogRow.isVisible()) {
      await catalogRow.getByTestId(`admin-known-driver-version-${KNOWN_DRIVER_ID}`).fill('2.4.0')
      await catalogRow.getByTestId(`admin-known-driver-add-${KNOWN_DRIVER_ID}`).click()
      await expect(driverRow).toBeVisible({ timeout: 20_000 })
    } else {
      // Another spec in this same run already installed it (real full-suite ordering) — nothing to do.
      await expect(driverRow).toBeVisible()
    }
  })

  test('testing a mysql.generic connection shows the resolved connection string, redacted', async ({ page }) => {
    await page.goto('/connections')
    await page.getByTestId('new-connection-button').click()
    await expect(page).toHaveURL(/\/connections\/new$/)

    await page.getByTestId('connection-name-input').fill(CONNECTION_NAME)
    await page.getByTestId('connection-driver-select').selectOption(KNOWN_DRIVER_ID)
    await page.getByTestId('connection-parameters-host').fill('localhost')
    await page.getByTestId('connection-parameters-port').fill('13306')
    await page.getByTestId('connection-parameters-database').fill('dbdatasync')
    await page.getByTestId('connection-parameters-userId').fill('root')
    await page.getByTestId('connection-parameters-password').fill(MYSQL_ROOT_PASSWORD)
    await page.getByTestId('save-connection-button').click()
    await expect(page.getByTestId('connections-table')).toContainText(CONNECTION_NAME)

    await page.goto(`/connections/${CONNECTION_NAME}`)
    await page.getByTestId('test-connection-button').click()
    const result = page.getByTestId('connection-test-result')
    // Not .toContainText('reachable') — ConnectionTestCard's own failure text is "unreachable", which
    // contains that exact substring, so a loose text match would report a genuine connection failure
    // as a pass. The .dot-ok class is the actual succeeded/failed signal; asserting on that instead of
    // string-matching text is what actually distinguishes the two.
    await expect(result.locator('.dot-ok')).toBeVisible({ timeout: 20_000 })

    const details = page.getByTestId('connection-resolved-details')
    await expect(details).toBeVisible()
    await details.locator('summary').click()

    const resolved = page.getByTestId('connection-resolved-string')
    await expect(resolved).toContainText('localhost')
    await expect(resolved).toContainText('dbdatasync')
    await expect(resolved).toContainText('••••••')
    await expect(resolved).not.toContainText(MYSQL_ROOT_PASSWORD)

    // GenericDriver's own preview: no JDBC URI, no outside properties — nothing routes through either
    // for a plain ADO.NET-shaped driver.
    await expect(page.getByTestId('connection-resolved-jdbc-uri')).toHaveCount(0)
    await expect(page.getByTestId('connection-resolved-properties')).toHaveCount(0)

    await page.screenshot({ path: path.join(screenshotsDir, 'resolved-connection-details.png'), fullPage: true })
  })
})

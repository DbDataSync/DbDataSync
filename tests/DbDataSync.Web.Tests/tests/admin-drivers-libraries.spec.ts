import path from 'node:path'
import { test, expect } from '@playwright/test'
import { KNOWN_DRIVER_ID, KNOWN_DRIVER_LIBRARY } from '../playwright.config'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('admin-drivers-libraries')

/**
 * Phase 118's read-only Drivers and Libraries admin screens, plus (phase 120) installing the one
 * bundled catalog driver through the real "Add" button as this spec's own fixture setup — a better
 * one than a CLI shortcut now that installing is a real, tested feature. Runs first (alphabetically,
 * before every other admin- or library-prefixed spec in this suite), so mysql.generic and its bound
 * library (installed as MySqlConnector — its real package id, see KNOWN_DRIVER_LIBRARY) are in place
 * for anything later that assumes an already-installed descriptor driver.
 */
test.describe.serial('admin: Drivers and Libraries', () => {
  test('adding mysql.generic from the catalog list installs it with no trust dialog, and it appears listed', async ({ page }) => {
    await page.goto('/drivers')

    const catalogRow = page.getByTestId(`admin-known-driver-${KNOWN_DRIVER_ID}`)
    await expect(catalogRow).toBeVisible()
    await catalogRow.getByTestId(`admin-known-driver-version-${KNOWN_DRIVER_ID}`).fill('2.4.0')
    await catalogRow.getByTestId(`admin-known-driver-add-${KNOWN_DRIVER_ID}`).click()

    // A curated catalog pick never opens the trust dialog — that's only for a package the operator
    // named themselves (see library-trust-and-remove.spec.ts).
    await expect(page.getByTestId('admin-libraries-trust-dialog')).toHaveCount(0)

    const descriptorRow = page.getByTestId(`admin-driver-row-${KNOWN_DRIVER_ID}`)
    await expect(descriptorRow).toBeVisible({ timeout: 20_000 })
    await expect(descriptorRow.getByTestId(`admin-driver-source-${KNOWN_DRIVER_ID}`)).toHaveText('Descriptor')
    await expect(descriptorRow).toContainText(KNOWN_DRIVER_LIBRARY)
    await expect(page.getByTestId('restart-required-banner')).toBeVisible()

    // Already installed now, so the catalog panel stops offering to add a second copy.
    await expect(page.getByTestId(`admin-known-driver-${KNOWN_DRIVER_ID}`)).not.toBeVisible()
  })

  test('Drivers tab lists the three built-ins alongside the descriptor driver just installed', async ({ page }) => {
    await page.goto('/drivers')

    for (const id of ['MsSql', 'Postgres', 'DuckDb']) {
      const row = page.getByTestId(`admin-driver-row-${id}`)
      await expect(row).toBeVisible()
      await expect(row.getByTestId(`admin-driver-source-${id}`)).toHaveText('Built-in')
    }

    const descriptorRow = page.getByTestId(`admin-driver-row-${KNOWN_DRIVER_ID}`)
    await expect(descriptorRow).toBeVisible()
    await expect(descriptorRow.getByTestId(`admin-driver-source-${KNOWN_DRIVER_ID}`)).toHaveText('Descriptor')
    await expect(descriptorRow).toContainText(KNOWN_DRIVER_LIBRARY)

    await page.screenshot({ path: path.join(screenshotsDir, '118-drivers-tab.png'), fullPage: true })
  })

  test('Libraries tab lists the installed library as resolving, curated, and used by the descriptor', async ({ page }) => {
    await page.goto('/drivers/libraries')

    await expect(page.getByRole('heading', { name: 'Libraries' })).toBeVisible()

    const row = page.getByTestId(`admin-library-row-${KNOWN_DRIVER_LIBRARY}`)
    await expect(row).toBeVisible()
    await expect(row.getByTestId(`admin-library-resolves-${KNOWN_DRIVER_LIBRARY}`)).toContainText('resolves')
    await expect(row.getByTestId(`admin-library-curated-${KNOWN_DRIVER_LIBRARY}`)).toBeVisible()
    await expect(row).toContainText(KNOWN_DRIVER_ID)

    // Still in use by the descriptor driver — Remove is disabled, only Force is offered.
    await expect(page.getByTestId(`admin-library-remove-${KNOWN_DRIVER_LIBRARY}`)).toBeDisabled()
    await expect(page.getByTestId(`admin-library-force-remove-${KNOWN_DRIVER_LIBRARY}`)).toBeVisible()

    await page.screenshot({ path: path.join(screenshotsDir, '118-libraries-tab.png'), fullPage: true })
  })
})

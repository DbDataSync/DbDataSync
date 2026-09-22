import path from 'node:path'
import { test, expect } from '@playwright/test'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('driver-authoring')

/**
 * `driver-yaml-authoring-ui.md`, built: create a driver through the form (ADO.NET base, since the JDBC
 * path needs a real jar plus `ikvm` actually installed — neither set up in this test server, and
 * exercised at the API level instead, where `DriverAuthoringTests` proves it without either), see it
 * listed, edit it, see the change persisted. Run standalone (not alongside `admin-drivers-libraries.spec.ts`)
 * so the `mysql-connector` quick-add chip is guaranteed visible — nothing else in this run has installed
 * it first.
 */
test.describe.serial('admin: driver authoring', () => {
  const driverId = `authoring-e2e-${Date.now()}`

  test('creating a driver through the form lists it, with the fields it was given', async ({ page }) => {
    await page.goto('/drivers/new')
    await expect(page.getByRole('heading', { name: 'New driver' })).toBeVisible()

    await page.getByTestId('driver-edit-id').fill(driverId)
    await page.getByTestId('driver-edit-display-name').fill('Authoring E2E driver')

    // ADO.NET is the default; the mysql-connector quick-add chip installs+selects it inline. A library's
    // id is always its real NuGet package id ("MySqlConnector"), never the catalog shorthand
    // ("mysql-connector") the chip is labeled with — every install path agrees on this now (see
    // follow-up-library-install-paths-disagree-on-the-resulting-library-id.md).
    const chip = page.getByTestId('admin-libraries-chip-mysql-connector')
    await expect(chip).toBeVisible()
    await chip.click()
    await page.getByTestId('admin-libraries-command-version').fill('2.4.0')
    await page.getByTestId('admin-libraries-install-button').click()
    await expect(page.getByTestId('admin-libraries-install-command')).toContainText('Installed', { timeout: 15_000 })
    await page.getByTestId('driver-edit-library-select').selectOption('MySqlConnector')

    await page.getByTestId('driver-edit-kind-Watermark').check()
    await page.getByTestId('driver-edit-kind-StagingTable').check()
    await page.getByTestId('driver-edit-kind-DeleteInsert').check()

    await page.screenshot({ path: path.join(screenshotsDir, 'authoring-new-driver-filled.png'), fullPage: true })

    await page.getByTestId('driver-edit-save').click()

    await expect(page).toHaveURL(/\/drivers$/)
    const row = page.getByTestId(`admin-driver-row-${driverId}`)
    await expect(row).toBeVisible()
    await expect(row.getByTestId(`admin-driver-source-${driverId}`)).toHaveText('Descriptor')
    await expect(row).toContainText('MySqlConnector')
  })

  test('editing it loads the existing fields, and a change persists', async ({ page }) => {
    await page.goto('/drivers')
    await page.getByTestId(`admin-driver-edit-${driverId}`).click()

    await expect(page).toHaveURL(new RegExp(`/drivers/${driverId}/edit$`))
    await expect(page.getByTestId('driver-edit-id')).toBeDisabled()
    await expect(page.getByTestId('driver-edit-id')).toHaveValue(driverId)
    await expect(page.getByTestId('driver-edit-display-name')).toHaveValue('Authoring E2E driver')
    await expect(page.getByTestId('driver-edit-kind-Watermark')).toBeChecked()
    await expect(page.getByTestId('driver-edit-kind-DeleteInsert')).toBeChecked()

    await page.getByTestId('driver-edit-display-name').fill('Renamed via edit')
    await page.getByTestId('driver-edit-save').click()

    await expect(page).toHaveURL(/\/drivers$/)
    await expect(page.getByTestId(`admin-driver-row-${driverId}`)).toContainText('Renamed via edit')

    await page.screenshot({ path: path.join(screenshotsDir, 'authoring-after-edit.png'), fullPage: true })
  })
})

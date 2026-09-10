import path from 'node:path'
import { test, expect } from '@playwright/test'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('admin-library-install')

/**
 * Phase 120's install/remove flow for a package the operator found themselves, not one of the
 * bundled catalog entries — the trust-confirmation path a curated pick (admin-drivers-libraries.spec.ts's
 * `mysql.generic`) never opens. Dapper stands in for "any real, well-known NuGet package that isn't in
 * DbDataSync.Libraries.KnownLibraries" — it doesn't need to be an ADO.NET provider for this test, which
 * is about the install/trust/remove mechanics, not about a working DbProviderFactory.
 */
test.describe.serial('admin: install and remove a non-curated library', () => {
  test('installing it requires a factory type, opens the trust dialog, and installs on confirm', async ({ page }) => {
    await page.goto('/admin/libraries')

    const searchInput = page.getByTestId('admin-libraries-search-input')
    await expect(searchInput).toBeVisible({ timeout: 15_000 })
    await searchInput.fill('Dapper')
    await page.getByTestId('admin-libraries-search-submit').click()

    const result = page.getByTestId('admin-libraries-result-Dapper')
    await expect(result).toBeVisible({ timeout: 15_000 })
    await expect(result).not.toContainText('vetted')

    const versionSelect = page.getByTestId('admin-libraries-result-version-Dapper')
    const firstVersion = await versionSelect.locator('option').nth(1).getAttribute('value')
    await versionSelect.selectOption(firstVersion!)

    const command = page.getByTestId('admin-libraries-install-command')
    await expect(command).toContainText("isn't one of the bundled")

    const installButton = page.getByTestId('admin-libraries-install-button')
    await expect(installButton).toBeDisabled()
    await page.getByTestId('admin-libraries-command-factory-type').fill('System.Data.Odbc.OdbcFactory, System.Data.Odbc')
    await expect(installButton).toBeEnabled()

    await installButton.click()
    await expect(page.getByTestId('admin-libraries-trust-dialog')).toBeVisible()
    await page.screenshot({ path: path.join(screenshotsDir, '120-trust-dialog.png'), fullPage: true })
    await page.getByTestId('admin-libraries-trust-confirm').click()

    await expect(page.getByTestId('admin-library-row-Dapper')).toBeVisible({ timeout: 30_000 })
    await expect(page.getByTestId('restart-required-banner')).toBeVisible()
    await page.screenshot({ path: path.join(screenshotsDir, '120-install-restart-required.png'), fullPage: true })
  })

  test('removing it (nothing depends on it) works with a plain confirm', async ({ page }) => {
    await page.goto('/admin/libraries')

    const row = page.getByTestId('admin-library-row-Dapper')
    await expect(row).toBeVisible()

    page.once('dialog', (dialog) => dialog.accept())
    await row.getByTestId('admin-library-remove-Dapper').click()

    await expect(page.getByTestId('admin-library-row-Dapper')).toHaveCount(0)
  })
})

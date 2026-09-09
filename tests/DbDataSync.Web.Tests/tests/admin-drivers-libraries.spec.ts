import { test, expect } from '@playwright/test'
import { KNOWN_DRIVER_ID, KNOWN_DRIVER_LIBRARY } from '../playwright.config'

/**
 * Phase 118's read-only Drivers and Libraries admin screens. playwright.config.ts seeds the scratch
 * repo with a `mysql.generic` descriptor driver (bound to the `mysql-connector` library) before the API
 * host ever starts, so both screens have something beyond the three built-ins to show.
 */
test.describe('admin: Drivers and Libraries', () => {
  test('Drivers tab lists the three built-ins and the seeded descriptor driver', async ({ page }) => {
    await page.goto('/admin/drivers')

    await expect(page.getByRole('heading', { name: 'Drivers' })).toBeVisible()

    for (const id of ['MsSql', 'Postgres', 'DuckDb']) {
      const row = page.getByTestId(`admin-driver-row-${id}`)
      await expect(row).toBeVisible()
      await expect(row.getByTestId(`admin-driver-source-${id}`)).toHaveText('Built-in')
    }

    const descriptorRow = page.getByTestId(`admin-driver-row-${KNOWN_DRIVER_ID}`)
    await expect(descriptorRow).toBeVisible()
    await expect(descriptorRow.getByTestId(`admin-driver-source-${KNOWN_DRIVER_ID}`)).toHaveText('Descriptor')
    await expect(descriptorRow).toContainText(KNOWN_DRIVER_LIBRARY)

    // The catalog's "Add" affordance is present but disabled — install is phase 120.
    const addButton = page.getByTestId(`admin-known-driver-add-${KNOWN_DRIVER_ID}`)
    await expect(addButton).toBeVisible()
    await expect(addButton).toBeDisabled()
  })

  test('Libraries tab lists the seeded library as resolving, curated, and used by the descriptor', async ({ page }) => {
    await page.goto('/admin/libraries')

    await expect(page.getByRole('heading', { name: 'Libraries' })).toBeVisible()

    const row = page.getByTestId(`admin-library-row-${KNOWN_DRIVER_LIBRARY}`)
    await expect(row).toBeVisible()
    await expect(row.getByTestId(`admin-library-resolves-${KNOWN_DRIVER_LIBRARY}`)).toContainText('resolves')
    await expect(row.getByTestId(`admin-library-curated-${KNOWN_DRIVER_LIBRARY}`)).toBeVisible()
    await expect(row).toContainText(KNOWN_DRIVER_ID)

    // Install is phase 119/120 — the affordance is present but disabled today.
    const installButton = page.getByTestId('admin-libraries-install')
    await expect(installButton).toBeVisible()
    await expect(installButton).toBeDisabled()
  })
})

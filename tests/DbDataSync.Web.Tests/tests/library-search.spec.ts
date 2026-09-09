import { test, expect } from '@playwright/test'

/**
 * Phase 119's NuGet search box on the Libraries screen — against the real public index, since this
 * suite's single API instance doesn't have a way to flip `DbDataSync:NuGetSearchEnabled` per test (the
 * disabled/unavailable-degrades-to-manual-entry behavior is covered at the API layer by
 * `LibrarySearchTests`, and the client-side branch it drives is the same `status !== 'ok'` check this
 * test's "enabled" path already exercises the other side of).
 */
test.describe('admin: Libraries — NuGet search', () => {
  test('searching finds a real package, and a version is required before the install command appears', async ({ page }) => {
    await page.goto('/admin/libraries')

    const searchInput = page.getByTestId('admin-libraries-search-input')
    // The probe-on-mount call has to settle (proving search is reachable) before the box replaces the
    // "checking..." placeholder — a generous timeout since it's a real network round trip.
    await expect(searchInput).toBeVisible({ timeout: 15_000 })

    await searchInput.fill('MySqlConnector')
    await page.getByTestId('admin-libraries-search-submit').click()

    const result = page.getByTestId('admin-libraries-result-MySqlConnector')
    await expect(result).toBeVisible({ timeout: 15_000 })
    await expect(result).toContainText('downloads')

    // No command yet — a version hasn't been picked.
    await expect(page.getByTestId('admin-libraries-install-command')).not.toBeVisible()

    const versionSelect = page.getByTestId('admin-libraries-result-version-MySqlConnector')
    const firstRealVersion = await versionSelect.locator('option').nth(1).getAttribute('value')
    await versionSelect.selectOption(firstRealVersion!)

    const command = page.getByTestId('admin-libraries-install-command')
    await expect(command).toBeVisible()
    await expect(command).toContainText(`dbdatasync config library install MySqlConnector --version ${firstRealVersion}`)
    // MySqlConnector is a bundled, curated entry (phase 117) — no trust warning for it.
    await expect(command).not.toContainText("isn't one of the bundled")
  })

  test('a quick-add chip for a curated library not yet installed pre-fills its id', async ({ page }) => {
    await page.goto('/admin/libraries')

    // Npgsql is bundled (phase 117) and not seeded by global setup, so its chip should be offered.
    const chip = page.getByTestId('admin-libraries-chip-npgsql')
    await expect(chip).toBeVisible()
    await chip.click()

    await expect(page.getByTestId('admin-libraries-command-version')).toBeVisible()
    await page.getByTestId('admin-libraries-command-version').fill('9.0.0')

    const command = page.getByTestId('admin-libraries-install-command')
    await expect(command).toContainText('dbdatasync config library install Npgsql --version 9.0.0')
  })
})

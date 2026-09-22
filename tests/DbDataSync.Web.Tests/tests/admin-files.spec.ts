import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { test, expect } from '@playwright/test'

/**
 * Phase 173V's Admin Files screen — upload, list, and delete against the real `files/` store, through
 * the actual multipart upload control (not a mocked fetch), the same "exercise the real feature" posture
 * `admin-drivers-libraries.spec.ts` already takes for installing a library through the UI.
 */
test.describe.serial('admin: Files', () => {
  const jarPath = path.join(os.tmpdir(), `dbdatasync-web-e2e-upload-${Date.now()}.jar`)
  const jarName = path.basename(jarPath)

  test.beforeAll(() => {
    fs.writeFileSync(jarPath, 'not a real jar, just bytes for the upload control to send')
  })

  test.afterAll(() => {
    fs.rmSync(jarPath, { force: true })
  })

  test('uploading a .jar lists it, then it can be removed', async ({ page }) => {
    await page.goto('/drivers/files')
    await expect(page.getByRole('heading', { name: 'Files' })).toBeVisible()

    await page.getByTestId('files-upload-input').setInputFiles(jarPath)

    const result = page.getByTestId(`files-upload-result-${jarName}`)
    await expect(result).toBeVisible({ timeout: 10_000 })
    await expect(result).toContainText('uploaded')

    const row = page.getByTestId(`admin-file-row-${jarName}`)
    await expect(row).toBeVisible()
    await expect(row).toContainText('B') // formatBytes' own suffix for a file this small

    page.once('dialog', (dialog) => void dialog.accept())
    await row.getByTestId(`admin-file-remove-${jarName}`).click()
    await expect(row).not.toBeVisible()
  })

  test('a non-.jar file is refused with a clear reason', async ({ page }) => {
    const txtPath = path.join(os.tmpdir(), `dbdatasync-web-e2e-upload-${Date.now()}.txt`)
    fs.writeFileSync(txtPath, 'plain text')
    const txtName = path.basename(txtPath)

    try {
      await page.goto('/drivers/files')
      await page.getByTestId('files-upload-input').setInputFiles(txtPath)

      const result = page.getByTestId(`files-upload-result-${txtName}`)
      await expect(result).toBeVisible({ timeout: 10_000 })
      await expect(result).toContainText('not an accepted file type')
      await expect(page.getByTestId(`admin-file-row-${txtName}`)).toHaveCount(0)
    } finally {
      fs.rmSync(txtPath, { force: true })
    }
  })
})

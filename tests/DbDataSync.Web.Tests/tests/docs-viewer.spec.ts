import path from 'node:path'
import { test, expect } from '@playwright/test'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('docs-viewer')

/**
 * Phase 160's Docs viewer against the real pages of `docs/` — in dev the Vite server serves the repo's own folder at
 * `/docs/*.md`, the same address the packaged build serves them at — plus scripted responses for what a real page can
 * never be made to do: be hostile, or not be Markdown.
 *
 * The suite runs with authentication off, so there is no Viewer to sign in as here. That a Viewer may read the version
 * behind these pages is `AboutControllerTests`' subject; nothing on the Docs screens depends on the role.
 */

test('the rail leads to an index of every page, for the version that is running', async ({ page }) => {
  await page.goto('/replications')
  await page.getByTestId('rail-docs').click()

  await expect(page).toHaveURL(/\/docs$/)
  await expect(page.getByTestId('docs-index')).toBeVisible()
  await expect(page.getByTestId('docs-index').getByRole('link')).toHaveCount(7)
  // The version comes from /api/about — present and not the placeholder text alone.
  await expect(page.getByTestId('docs-version')).toContainText(/\d/)
  await page.screenshot({ path: path.join(screenshotsDir, '01-docs-index.png') })
})

test('a page of the docs renders as a document — tables included', async ({ page }) => {
  await page.goto('/docs/replication-concepts')

  const content = page.getByTestId('docs-content')
  await expect(content.getByRole('heading', { level: 1 })).toBeVisible()
  // The thing Markdown.tsx could not do, and the reason this renderer exists.
  await expect(content.locator('table').first()).toBeVisible()
  await expect(content.locator('table th').first()).not.toBeEmpty()
  await page.screenshot({ path: path.join(screenshotsDir, '02-docs-page-with-table.png') })
})

test('a link to another page — anchor and all — stays inside the app and lands on the heading', async ({ page }) => {
  await page.goto('/docs/replication-concepts')

  await page.getByTestId('docs-content').getByRole('link', { name: 'descriptor driver' }).first().click()

  await expect(page).toHaveURL(/\/docs\/drivers-and-libraries#descriptor-drivers$/)
  const heading = page.getByTestId('docs-content').locator('#descriptor-drivers')
  await expect(heading).toBeVisible()
  await expect(heading).toBeInViewport()
})

test('the README link every page opens with goes to the docs index', async ({ page }) => {
  await page.goto('/docs/install')

  await page.getByTestId('docs-content').getByRole('link', { name: 'DbDataSync', exact: true }).first().click()

  await expect(page).toHaveURL(/\/docs$/)
  await expect(page.getByTestId('docs-index')).toBeVisible()
})

test('a page that is not part of the build says so, and points at the list', async ({ page }) => {
  await page.goto('/docs/no-such-page')

  await expect(page.getByTestId('docs-not-found')).toContainText('no-such-page')
  await page.getByTestId('docs-not-found').getByRole('link').click()
  await expect(page.getByTestId('docs-index')).toBeVisible()
})

test('the admin screens that used to say "see docs/…" now link to the docs', async ({ page }) => {
  await page.goto('/admin/config')
  await page.getByTestId('config-docs-link').click()
  await expect(page).toHaveURL(/\/docs\/configuration$/)

  await page.goto('/admin/drivers')
  await page.getByTestId('drivers-docs-link').click()
  await expect(page).toHaveURL(/\/docs\/drivers-and-libraries#descriptor-drivers$/)
})

test('a hostile document is shown as text and does nothing', async ({ page }) => {
  const dialogs: string[] = []
  page.on('dialog', async dialog => { dialogs.push(dialog.message()); await dialog.dismiss() })

  await page.route('**/docs/install.md', route => route.fulfill({
    contentType: 'text/markdown; charset=utf-8',
    body: [
      '# Hostile',
      '<script>window.__pwned = 1; alert("script")</script>',
      '<img src=x onerror="window.__pwned = 1; alert(\'img\')">',
      '[click me](javascript:window.__pwned=1;alert("link"))',
      '![pic](javascript:alert("image"))',
      '<iframe src="https://example.invalid/"></iframe>',
    ].join('\n\n'),
  }))
  await page.goto('/docs/install')

  const content = page.getByTestId('docs-content')
  await expect(content.getByRole('heading', { name: 'Hostile' })).toBeVisible()
  // Rendered as the text it was written as, not as elements.
  await expect(content).toContainText('<script>')
  await expect(content).toContainText('[click me](javascript:')
  await expect(content.locator('script, iframe, img, a[href^="javascript:"]')).toHaveCount(0)

  await page.getByTestId('docs-content').click()
  await page.waitForTimeout(300)
  expect(dialogs).toEqual([])
  expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined()
})

test('an answer that is not a document is refused, not rendered', async ({ page }) => {
  await page.route('**/docs/install.md', route => route.fulfill({
    contentType: 'text/html',
    body: '<html><body><h1>A proxy error page</h1></body></html>',
  }))
  await page.goto('/docs/install')

  await expect(page.getByTestId('docs-content')).toHaveCount(0)
  await expect(page.getByText('did not answer with a document')).toBeVisible()
  await expect(page.getByText('A proxy error page')).toHaveCount(0)
})

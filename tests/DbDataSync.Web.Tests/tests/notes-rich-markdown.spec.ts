import path from 'node:path'
import { test, expect, type Page } from '@playwright/test'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('notes-rich-markdown')

const REPLICATION_NAME = 'notes-rich-demo'

/**
 * Phase 161: Notes render with the small renderer unless the deployment turned `DbDataSync:NotesRichMarkdown` on, and while
 * it is on every Notes panel says so.
 *
 * **Stubbed at the network boundary** (following `monitoring-restructure.spec.ts`): what is under test is which renderer the
 * page chooses from `/api/about`, what each one does with the same note, and the badge — not a real replication. The Admin
 * Configuration test at the end is the exception; it runs against the real API, which is where the catalog entry lives.
 */

const NOTE = [
  '# Owner',
  '',
  'The warehouse team. **Do not** reload during close.',
  '',
  '| Table | Owner |',
  '| ----- | ----- |',
  '| orders | warehouse |',
  '',
  '![the diagram](https://example.invalid/diagram.png)',
  '',
  '<script>window.__pwned = 1</script>',
  '',
  '[click me](javascript:window.__pwned=1)',
].join('\n')

const TASK = {
  name: REPLICATION_NAME,
  enabled: true,
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: {
    reader: { kind: 'MsSqlCdc', options: {} },
    cache: { kind: 'MsSqlStagingTable', options: {} },
    writer: { kind: 'MsSqlMerge', options: {} },
  },
  endpoints: {
    source: { connectionName: 'src', database: 'AppDb' },
    target: { connectionName: 'tgt', database: 'Warehouse' },
  },
  notes: NOTE,
}

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })

async function stub(page: Page, notesRichMarkdown: boolean) {
  const base = `/api/replications/${REPLICATION_NAME}`
  await page.route('**/api/about', (route) => route.fulfill(json({ version: '2026.9.20.1', notesRichMarkdown })))
  await page.route(/\/runs\/watermark-times/, (route) => route.fulfill(json({})))
  await page.route(/\/runs\?limit=/, (route) => route.fulfill(json({ runs: [], nextCursor: null })))
  await page.route(`**${base}/lag`, (route) => route.fulfill(json({
    mappings: {}, lowestLagMs: null, highestLagMs: null, rangeIncludesEstimates: false,
  })))
  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json([])))
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true,
  })))
  await page.route(`**${base}/metrics*`, (route) => route.fulfill(json({
    runs: 0, failures: 0, rowsWritten: 0, rowsRead: 0, buckets: [],
    processingP50Ms: 0, processingP95Ms: 0, processingMaxMs: 0, lastCompletedPassUtc: null,
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))
}

test.describe('Notes: the rich Markdown opt-in (phase 161)', () => {
  test('01 - off: the small renderer, no table, and no badge', async ({ page }) => {
    await stub(page, false)
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)

    const notes = page.getByTestId('replication-notes-rendered')
    await expect(notes).toContainText('warehouse team')
    // Exactly what Notes have always done: a table is text, not a table.
    await expect(notes.locator('table')).toHaveCount(0)
    await expect(notes).toContainText('| orders | warehouse |')
    await expect(page.getByTestId('replication-notes-rich-badge')).toHaveCount(0)
    await page.screenshot({ path: path.join(screenshotsDir, '01-notes-default-renderer.png') })
  })

  test('02 - on: the table renders, and the panel says the richer renderer is on', async ({ page }) => {
    await stub(page, true)
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)

    const notes = page.getByTestId('replication-notes-rendered')
    await expect(notes.locator('table')).toBeVisible()
    await expect(notes.locator('td', { hasText: 'warehouse' }).first()).toBeVisible()
    const badge = page.getByTestId('replication-notes-rich-badge')
    await expect(badge).toBeVisible()
    await expect(badge).toHaveText('Rich Markdown on')
    await page.screenshot({ path: path.join(screenshotsDir, '02-notes-rich-renderer-with-badge.png') })
  })

  test('03 - on: a hostile note is inert, and an image is a link rather than a fetch', async ({ page }) => {
    const dialogs: string[] = []
    page.on('dialog', async (dialog) => { dialogs.push(dialog.message()); await dialog.dismiss() })
    const requested: string[] = []
    page.on('request', (request) => requested.push(request.url()))

    await stub(page, true)
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)

    const notes = page.getByTestId('replication-notes-rendered')
    await expect(notes.locator('table')).toBeVisible()
    await expect(notes.locator('script, iframe, img, a[href^="javascript:"]')).toHaveCount(0)
    // The script tag is text; the javascript: link is text too.
    await expect(notes).toContainText('<script>')
    await expect(notes).toContainText('[click me](javascript:')
    // The image is offered as a link — the browser did not go and fetch it.
    await expect(notes.getByRole('link', { name: 'the diagram' })).toHaveAttribute('href', 'https://example.invalid/diagram.png')
    expect(requested.some((url) => url.includes('example.invalid'))).toBe(false)

    await page.waitForTimeout(300)
    expect(dialogs).toEqual([])
    expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined()
  })

  test('04 - if the deployment cannot be asked, the safe renderer is what shows', async ({ page }) => {
    await stub(page, true)
    await page.route('**/api/about', (route) => route.fulfill({ status: 500, body: 'no' }))
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)

    const notes = page.getByTestId('replication-notes-rendered')
    await expect(notes).toContainText('warehouse team')
    await expect(notes.locator('table')).toHaveCount(0)
    await expect(page.getByTestId('replication-notes-rich-badge')).toHaveCount(0)
  })

  test('05 - Admin Configuration lists the setting with its warning beside it, off by default', async ({ page }) => {
    await page.goto('/admin/config')

    const row = page.getByTestId('admin-config-row-NotesRichMarkdown')
    await expect(row).toBeVisible()
    // Beside the control, always — not a confirmation after the fact.
    await expect(page.getByTestId('admin-config-caution-NotesRichMarkdown')).toContainText("other people's sessions")
    await expect(page.getByTestId('admin-config-running-NotesRichMarkdown')).toBeVisible()
    await row.scrollIntoViewIfNeeded()
    await page.screenshot({ path: path.join(screenshotsDir, '03-admin-config-caution.png') })

    // No other setting carries one.
    await expect(page.locator('[data-testid^="admin-config-caution-"]')).toHaveCount(1)
  })
})

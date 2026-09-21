import path from 'node:path'
import { test, expect, type Page, type Route } from '@playwright/test'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('admin-updates')

/**
 * Phase 159's Updates screen. The suite's single API instance is a development build with updating off, so what
 * the *server* decides — who may ask, what it accepts, the restart itself — is covered by the API tests
 * (`AdminUpdateControllerTests`) and against real tooling by phase 159's own verification; replacing the code of
 * the process under test is not something a browser suite can drive. What is here is the page: every state it can
 * be in, and above all what it does while the service disappears and comes back, scripted through the update
 * endpoints and nothing else.
 */

type Phase = 'idle' | 'staging' | 'draining' | 'applying' | 'restarting' | 'succeeded' | 'rolledback' | 'failed'

const statusOf = (overrides: Record<string, unknown> = {}) => ({
  enabled: true,
  runningVersion: '2026.9.16.1005',
  installKind: 'ToolPath',
  channels: ['stable', 'beta', 'snapshot'],
  canApply: true,
  cannotApplyReason: null,
  phase: 'idle' as Phase,
  message: null,
  atUtc: null,
  fromVersion: null,
  toVersion: null,
  requestedBy: null,
  pending: false,
  onTrial: false,
  logPath: '/var/lib/dbdatasync-update/update.log',
  history: [],
  ...overrides,
})

const nugetUrl = (version: string) => `https://www.nuget.org/packages/DbDataSync/${version}`

const RELEASES = {
  stable: [
    { version: '2026.9.18.1918', channel: 'stable', builtUtc: '2026-09-18T19:18:00Z', installed: false, newer: true, url: nugetUrl('2026.9.18.1918') },
    { version: '2026.9.16.1005', channel: 'stable', builtUtc: '2026-09-16T10:05:00Z', installed: true, newer: false, url: nugetUrl('2026.9.16.1005') },
    { version: '2026.9.11.532', channel: 'stable', builtUtc: '2026-09-11T05:32:00Z', installed: false, newer: false, url: nugetUrl('2026.9.11.532') },
  ],
  beta: [
    { version: '2026.9.12.721-beta', channel: 'beta', builtUtc: '2026-09-12T07:21:00Z', installed: false, newer: false, url: nugetUrl('2026.9.12.721-beta') },
  ],
  snapshot: [
    { version: '2026.9.19.1432-snapshot.g65615e7', channel: 'snapshot', builtUtc: '2026-09-19T14:32:00Z', installed: false, newer: true, url: 'https://github.com/DbDataSync/DbDataSync/releases/tag/snapshot-2026.9.19.1432-snapshot.g65615e7' },
  ],
} as const

/** Serves a fixed status, and releases per channel. Returns what was asked to be applied. */
async function serve(page: Page, status: () => Route | unknown) {
  const applied: string[] = []

  await page.route('**/api/admin/update/releases**', async (route) => {
    const channel = new URL(route.request().url()).searchParams.get('channel') as keyof typeof RELEASES
    await route.fulfill({ json: { releases: RELEASES[channel] ?? [], warnings: [] } })
  })
  await page.route('**/api/admin/update/apply', async (route) => {
    applied.push(JSON.parse(route.request().postData() ?? '{}').version)
    await route.fulfill({ status: 202, json: { message: 'Updating. The service will restart.' } })
  })
  await page.route('**/api/admin/update/status', async (route) => {
    const next = status()
    if (next === 'unreachable') await route.abort('connectionrefused')
    else await route.fulfill({ json: next })
  })

  return applied
}

test.describe('admin: Updates', () => {
  test('is off by default, and says how to turn it on instead of offering buttons that would refuse', async ({ page }) => {
    await serve(page, () => statusOf({ enabled: false, canApply: false }))

    await page.goto('/admin/updates')

    await expect(page.getByTestId('updates-disabled')).toContainText('DbDataSync:Updates:Mode')
    await expect(page.getByTestId('updates-releases')).toHaveCount(0)
    await expect(page.getByTestId('updates-running-version')).toHaveText('2026.9.16.1005')
    await page.screenshot({ path: path.join(screenshotsDir, '159-updates-disabled.png'), fullPage: true })
  })

  test('an installation that cannot apply still lists releases, gives the reason, and disables every button', async ({ page }) => {
    await serve(page, () => statusOf({
      canApply: false,
      cannotApplyReason: 'This is a container image. Update it by pulling a newer image tag and recreating the container.',
    }))

    await page.goto('/admin/updates')

    await expect(page.getByTestId('updates-cannot-apply')).toContainText('pulling a newer image tag')
    await expect(page.getByTestId('updates-cannot-apply')).toContainText('dbdatasync update')
    await expect(page.getByTestId('updates-release-2026.9.18.1918')).toBeVisible()
    await expect(page.getByTestId('updates-apply-2026.9.18.1918')).toBeDisabled()
  })

  test('lists a channel newest first, marking what is running, and switches channel', async ({ page }) => {
    await serve(page, () => statusOf())

    await page.goto('/admin/updates')

    const rows = page.locator('[data-testid^="updates-release-"]')
    await expect(rows).toHaveCount(3)
    await expect(rows.nth(0)).toContainText('2026.9.18.1918')
    await expect(rows.nth(0)).toContainText('newer')
    await expect(rows.nth(1)).toContainText('running')
    await expect(page.getByTestId('updates-apply-2026.9.16.1005')).toBeDisabled()
    await expect(rows.nth(2)).toContainText('older')

    await page.getByTestId('updates-channel-snapshot').click()
    await expect(page.getByTestId('updates-release-2026.9.19.1432-snapshot.g65615e7')).toBeVisible()
    await expect(page.getByTestId('updates-release-2026.9.18.1918')).toHaveCount(0)
    await page.screenshot({ path: path.join(screenshotsDir, '159-updates-snapshot-channel.png'), fullPage: true })
  })

  test('the confirmation says what will happen — and warns about a snapshot, and about going back to an older version', async ({ page }) => {
    const applied = await serve(page, () => statusOf())
    await page.goto('/admin/updates')

    await page.getByTestId('updates-apply-2026.9.18.1918').click()
    const dialog = page.getByTestId('updates-confirm-dialog')
    await expect(dialog).toContainText('Update to 2026.9.18.1918?')
    await expect(dialog).toContainText('restarts')
    await expect(dialog).toContainText('puts the current one back')
    await expect(page.getByTestId('updates-confirm-snapshot')).toHaveCount(0)
    await page.screenshot({ path: path.join(screenshotsDir, '159-updates-confirm.png'), fullPage: true })
    await page.getByTestId('updates-confirm-cancel').click()
    await expect(dialog).toHaveCount(0)

    await page.getByTestId('updates-apply-2026.9.11.532').click()
    await expect(page.getByTestId('updates-confirm-older')).toContainText('older')
    await page.keyboard.press('Escape')
    await expect(dialog).toHaveCount(0)

    await page.getByTestId('updates-channel-snapshot').click()
    await page.getByTestId('updates-apply-2026.9.19.1432-snapshot.g65615e7').click()
    await expect(page.getByTestId('updates-confirm-snapshot')).toContainText('development build')
    await expect(page.getByTestId('updates-confirm-snapshot')).toContainText('not a tampered one')
    await page.getByTestId('updates-confirm-cancel').click()

    expect(applied).toEqual([])
  })

  test('applying an update carries on through the service going away and coming back', async ({ page }) => {
    // What the page sees, in order: winding down, about to restart, then nothing at all (the service is down),
    // then the new version answering and proving itself, then confirmed. The page has to keep asking through the
    // gap and never show it as an error.
    // The server records `draining` before it answers 202, so that is the first thing seen after pressing Update.
    const script: unknown[] = [
      statusOf({ phase: 'draining', message: 'Waiting for running work to finish.', toVersion: '2026.9.18.1918', pending: true }),
      statusOf({ phase: 'applying', message: 'Restarting to install 2026.9.18.1918.', toVersion: '2026.9.18.1918', pending: true }),
      'unreachable',
      'unreachable',
      statusOf({ runningVersion: '2026.9.18.1918', phase: 'restarting', message: '2026.9.18.1918 is installed; waiting for it to start.', toVersion: '2026.9.18.1918', onTrial: true }),
    ]
    let served = 0
    const final = statusOf({
      runningVersion: '2026.9.18.1918', phase: 'succeeded', message: 'Updated to 2026.9.18.1918.', toVersion: '2026.9.18.1918',
      history: [{ phase: 'succeeded', message: 'Updated to 2026.9.18.1918.', fromVersion: '2026.9.16.1005', toVersion: '2026.9.18.1918', atUtc: '2026-09-19T15:00:00Z', requestedBy: 'dan' }],
    })

    const applied = await serve(page, () => {
      // Nothing is scripted until the admin has pressed the button.
      if (applied.length === 0) return statusOf()
      const next = served < script.length ? script[served] : final
      served++
      return next
    })

    await page.goto('/admin/updates')
    await page.getByTestId('updates-apply-2026.9.18.1918').click()
    await page.getByTestId('updates-confirm').click()

    expect(applied).toEqual(['2026.9.18.1918'])
    await expect(page.getByTestId('updates-phase')).toHaveText('Waiting for running work to finish', { timeout: 15_000 })
    await page.screenshot({ path: path.join(screenshotsDir, '159-updates-draining.png'), fullPage: true })

    await expect(page.getByTestId('updates-phase')).toHaveText('The service is restarting…', { timeout: 20_000 })
    // The gap is not an error.
    await expect(page.getByTestId('error-banner')).toHaveCount(0)
    await page.screenshot({ path: path.join(screenshotsDir, '159-updates-restarting.png'), fullPage: true })

    await expect(page.getByTestId('updates-running-version')).toHaveText('2026.9.18.1918', { timeout: 20_000 })
    await expect(page.getByTestId('updates-last-result')).toContainText('Updated', { timeout: 20_000 })
    await expect(page.getByTestId('updates-progress')).toHaveCount(0)
    await expect(page.getByTestId('updates-history')).toContainText('2026.9.16.1005 → 2026.9.18.1918')
    await page.screenshot({ path: path.join(screenshotsDir, '159-updates-done.png'), fullPage: true })
  })

  test('a rolled-back update is reported as a problem, with where to look', async ({ page }) => {
    await serve(page, () => statusOf({
      phase: 'rolledback',
      message: '2026.9.18.1918 did not become healthy, so 2026.9.16.1005 was put back.',
      fromVersion: '2026.9.16.1005', toVersion: '2026.9.18.1918',
      history: [{ phase: 'rolledback', message: 'x', fromVersion: '2026.9.16.1005', toVersion: '2026.9.18.1918', atUtc: '2026-09-19T15:00:00Z', requestedBy: 'dan' }],
    }))

    await page.goto('/admin/updates')

    const result = page.getByTestId('updates-last-result')
    await expect(result).toContainText('Rolled back')
    await expect(result).toContainText('so 2026.9.16.1005 was put back')
    await expect(result).toContainText('/var/lib/dbdatasync-update/update.log')
    await expect(page.getByTestId('updates-progress')).toHaveCount(0)
    // Nothing is in flight, so the buttons work again.
    await expect(page.getByTestId('updates-apply-2026.9.18.1918')).toBeEnabled()
    await page.screenshot({ path: path.join(screenshotsDir, '159-updates-rolled-back.png'), fullPage: true })
  })

  test('while an update is in flight no other update can be started', async ({ page }) => {
    await serve(page, () => statusOf({ phase: 'draining', message: 'Waiting.', toVersion: '2026.9.18.1918', pending: true }))

    await page.goto('/admin/updates')

    await expect(page.getByTestId('updates-progress')).toBeVisible()
    await expect(page.getByTestId('updates-apply-2026.9.11.532')).toBeDisabled()
  })

  test('a service that never comes back is called out, with where to look', async ({ page }) => {
    await page.clock.install()
    // The last thing it said before it went away, then silence.
    let calls = 0
    await serve(page, () =>
      calls++ === 0
        ? statusOf({ phase: 'applying', message: 'Restarting.', toVersion: '2026.9.18.1918', pending: true })
        : 'unreachable')

    await page.goto('/admin/updates')
    await expect(page.getByTestId('updates-phase')).toBeVisible()

    await page.clock.fastForward(30_000)
    await expect(page.getByTestId('updates-phase')).toHaveText('The service is restarting…')
    await expect(page.getByTestId('updates-overdue')).toHaveCount(0)

    await page.clock.fastForward(130_000)
    await expect(page.getByTestId('updates-overdue')).toContainText('journalctl -u dbdatasync')
    await expect(page.getByTestId('updates-overdue')).toContainText('/var/lib/dbdatasync-update/update.log')
  })
})

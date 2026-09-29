import path from 'node:path'
import { test, expect, type Page, type Route } from '@playwright/test'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('admin-updates')

/**
 * The Updates screen (phase 159; read-only since phase 196L). The console no longer applies an update — it gives the
 * admin the commands that do, built by the server — so what is here is the page: every state it can be in, the
 * commands it shows and copies, and how it follows an update run from a shell through the service going away and
 * coming back. The suite's single API instance is a development build, so everything is scripted through the update
 * endpoints; what the server puts in them is `AdminUpdateControllerTests`' and `UpdateServiceTests`' subject.
 */

type Phase = 'idle' | 'staging' | 'draining' | 'applying' | 'restarting' | 'succeeded' | 'rolledback' | 'failed'

const ROOT = '/var/lib/dbdatasync'

const commandsOf = (overrides: Record<string, unknown> = {}) => ({
  where: 'In a terminal on the server. Applying and rolling back need root, because they stop and start the service and write the install directory.',
  list: 'dbdatasync update --list',
  status: `dbdatasync update --status --repo ${ROOT}`,
  apply: `sudo dbdatasync update --to {version} --apply --repo ${ROOT}`,
  rollback: `sudo dbdatasync update --rollback --repo ${ROOT}`,
  versionPlaceholder: '{version}',
  convertsFirst: false,
  printsOnly: false,
  ...overrides,
})

const slotsOf = (other: string | null = '2026.9.11.532') => ({
  root: '/opt/dbdatasync',
  current: 'a',
  slots: [
    { name: 'a', version: '2026.9.16.1005', current: true, ambiguous: false },
    { name: 'b', version: other, current: false, ambiguous: false },
  ],
  checks: [],
})

const statusOf = (overrides: Record<string, unknown> = {}) => ({
  enabled: true,
  runningVersion: '2026.9.16.1005',
  installKind: 'ToolPath',
  channels: ['stable', 'beta', 'snapshot'],
  commands: commandsOf(),
  commandsUnavailableReason: null,
  slots: slotsOf(),
  phase: 'idle' as Phase,
  message: null,
  atUtc: null,
  fromVersion: null,
  toVersion: null,
  requestedBy: null,
  logPath: `${ROOT}/updates/update.log`,
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

/** Serves a fixed status, and releases per channel. Returns every request the page made that was not a GET — there
 * should be none: nothing on this page changes anything. */
async function serve(page: Page, status: () => Route | unknown) {
  const writes: string[] = []

  await page.route('**/api/admin/update/**', async (route) => {
    const request = route.request()
    const url = new URL(request.url())
    if (request.method() !== 'GET') {
      writes.push(`${request.method()} ${url.pathname}`)
      await route.fulfill({ status: 404 })
    } else if (url.pathname.endsWith('/releases')) {
      const channel = url.searchParams.get('channel') as keyof typeof RELEASES
      await route.fulfill({ json: { releases: RELEASES[channel] ?? [], warnings: [] } })
    } else {
      const next = status()
      if (next === 'unreachable') await route.abort('connectionrefused')
      else await route.fulfill({ json: next })
    }
  })

  return writes
}

test.describe('admin: Updates', () => {
  test('shows both slots and the commands, each with a copy button', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    const writes = await serve(page, () => statusOf())

    await page.goto('/admin/updates')

    await expect(page.getByTestId('updates-slot-a')).toContainText('2026.9.16.1005')
    await expect(page.getByTestId('updates-slot-a')).toContainText('running')
    await expect(page.getByTestId('updates-slot-b')).toContainText('2026.9.11.532')
    await expect(page.getByTestId('updates-where')).toContainText('need root')
    await expect(page.getByTestId('updates-command-list')).toHaveText('dbdatasync update --list')
    await expect(page.getByTestId('updates-command-apply')).toHaveText(`sudo dbdatasync update --to <version> --apply --repo ${ROOT}`)
    await expect(page.getByTestId('updates-command-rollback')).toHaveText(`sudo dbdatasync update --rollback --repo ${ROOT}`)

    await page.getByTestId('updates-command-rollback-copy').click()
    await expect(page.getByTestId('updates-command-rollback-copy')).toHaveText('Copied')
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(`sudo dbdatasync update --rollback --repo ${ROOT}`)
    await page.screenshot({ path: path.join(screenshotsDir, '196-updates-commands.png'), fullPage: true })

    expect(writes).toEqual([])
  })

  test('each release gives its own exact command, and says what running it will do', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    const writes = await serve(page, () => statusOf())
    await page.goto('/admin/updates')

    await page.getByTestId('updates-commands-2026.9.18.1918').click()
    const dialog = page.getByTestId('updates-commands-dialog')
    await expect(dialog).toContainText('Update to 2026.9.18.1918')
    await expect(page.getByTestId('updates-dialog-command')).toHaveText(`sudo dbdatasync update --to 2026.9.18.1918 --apply --repo ${ROOT}`)
    await expect(page.getByTestId('updates-dialog-explanation')).toContainText('switches back on its own')
    await expect(page.getByTestId('updates-dialog-explanation')).toContainText('--rollback')
    await page.getByTestId('updates-dialog-command-copy').click()
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(`sudo dbdatasync update --to 2026.9.18.1918 --apply --repo ${ROOT}`)
    await page.screenshot({ path: path.join(screenshotsDir, '196-updates-release-command.png'), fullPage: true })
    await page.getByTestId('updates-dialog-close').click()
    await expect(dialog).toHaveCount(0)

    await page.getByTestId('updates-commands-2026.9.11.532').click()
    await expect(page.getByTestId('updates-dialog-older')).toContainText('older')
    await page.keyboard.press('Escape')
    await expect(dialog).toHaveCount(0)

    await page.getByTestId('updates-channel-snapshot').click()
    await page.getByTestId('updates-commands-2026.9.19.1432-snapshot.g65615e7').click()
    await expect(page.getByTestId('updates-dialog-snapshot')).toContainText('not a tampered one')

    expect(writes).toEqual([])
  })

  test('lists a channel newest first, marking what is running, and switches channel', async ({ page }) => {
    await serve(page, () => statusOf())

    await page.goto('/admin/updates')

    const rows = page.locator('[data-testid^="updates-release-"]')
    await expect(rows).toHaveCount(3)
    await expect(rows.nth(0)).toContainText('newer')
    await expect(rows.nth(1)).toContainText('running')
    await expect(page.getByTestId('updates-commands-2026.9.16.1005')).toBeDisabled()
    await expect(rows.nth(2)).toContainText('older')

    await page.getByTestId('updates-channel-snapshot').click()
    await expect(page.getByTestId('updates-release-2026.9.19.1432-snapshot.g65615e7')).toBeVisible()
    await expect(page.getByTestId('updates-release-2026.9.18.1918')).toHaveCount(0)
  })

  test('with release lookup off, it still gives the commands and says how to turn the list on', async ({ page }) => {
    await serve(page, () => statusOf({ enabled: false }))

    await page.goto('/admin/updates')

    await expect(page.getByTestId('updates-disabled')).toContainText('DbDataSync:Updates:Mode')
    await expect(page.getByTestId('updates-releases')).toHaveCount(0)
    await expect(page.getByTestId('updates-command-list')).toBeVisible()
    await page.screenshot({ path: path.join(screenshotsDir, '196-updates-lookup-off.png'), fullPage: true })
  })

  test('an install from before versioned slots says the first update converts it, and offers no rollback', async ({ page }) => {
    await serve(page, () => statusOf({ slots: null, commands: commandsOf({ rollback: null, convertsFirst: true }) }))

    await page.goto('/admin/updates')

    await expect(page.getByTestId('updates-converts-first')).toContainText('converts it, once')
    await expect(page.getByTestId('updates-command-rollback')).toHaveCount(0)
    await expect(page.getByTestId('updates-slots')).toHaveCount(0)
  })

  test('a container has no commands, only the reason', async ({ page }) => {
    await serve(page, () => statusOf({
      installKind: 'Container', slots: null, commands: null,
      commandsUnavailableReason: 'This is a container image. Update it by pulling a newer image tag and recreating the container.',
    }))

    await page.goto('/admin/updates')

    await expect(page.getByTestId('updates-no-commands')).toContainText('pulling a newer image tag')
    await expect(page.getByTestId('updates-how')).toHaveCount(0)
    await expect(page.getByTestId('updates-commands-2026.9.18.1918')).toBeDisabled()
  })

  test('a note about the slots is shown as it came', async ({ page }) => {
    await serve(page, () => statusOf({
      slots: {
        ...slotsOf('2026.9.18.1918'),
        checks: [{ level: 'note', message: 'Running 2026.9.16.1005, the older of the two installed; slot b holds 2026.9.18.1918.' }],
      },
    }))

    await page.goto('/admin/updates')

    await expect(page.getByTestId('updates-slot-check')).toContainText('Note: Running 2026.9.16.1005, the older of the two')
  })

  test('an update run from a shell is followed through the service going away and coming back', async ({ page }) => {
    // What the CLI records, in order: installing into the other slot, switched and starting, then nothing at all (the
    // service is down while it switches), then the new version answering and the update succeeded.
    const script: unknown[] = [
      statusOf({ phase: 'applying', message: 'Installing 2026.9.18.1918 into slot b.', toVersion: '2026.9.18.1918', requestedBy: 'root' }),
      statusOf({ phase: 'restarting', message: 'Switched to slot b; starting 2026.9.18.1918.', toVersion: '2026.9.18.1918', requestedBy: 'root' }),
      'unreachable',
      'unreachable',
    ]
    let served = 0
    const final = statusOf({
      runningVersion: '2026.9.18.1918', phase: 'succeeded', message: 'Updated to 2026.9.18.1918.', toVersion: '2026.9.18.1918',
      slots: { ...slotsOf(), current: 'b', slots: [
        { name: 'a', version: '2026.9.16.1005', current: false, ambiguous: false },
        { name: 'b', version: '2026.9.18.1918', current: true, ambiguous: false },
      ] },
      history: [{ phase: 'succeeded', message: 'Updated to 2026.9.18.1918.', fromVersion: '2026.9.16.1005', toVersion: '2026.9.18.1918', atUtc: '2026-09-19T15:00:00Z', requestedBy: 'root' }],
    })

    await serve(page, () => {
      const next = served < script.length ? script[served] : final
      served++
      return next
    })

    await page.goto('/admin/updates')
    await expect(page.getByTestId('updates-phase')).toHaveText('Installing beside the running version')
    await expect(page.getByTestId('updates-progress')).toContainText('started by root from a shell')
    await page.screenshot({ path: path.join(screenshotsDir, '196-updates-in-progress.png'), fullPage: true })

    await expect(page.getByTestId('updates-phase')).toHaveText('The service is restarting…', { timeout: 20_000 })
    // The gap is not an error.
    await expect(page.getByTestId('error-banner')).toHaveCount(0)

    await expect(page.getByTestId('updates-running-version')).toHaveText('2026.9.18.1918', { timeout: 20_000 })
    await expect(page.getByTestId('updates-last-result')).toContainText('Updated', { timeout: 20_000 })
    await expect(page.getByTestId('updates-slot-a')).toContainText('kept, to switch back to')
    await expect(page.getByTestId('updates-history')).toContainText('2026.9.16.1005 → 2026.9.18.1918')
  })

  test('a switched-back update is reported as a problem, with where to look', async ({ page }) => {
    await serve(page, () => statusOf({
      phase: 'rolledback',
      message: '2026.9.18.1918 did not answer after the update, so 2026.9.16.1005 was switched back in.',
      fromVersion: '2026.9.16.1005', toVersion: '2026.9.18.1918',
      history: [{ phase: 'rolledback', message: 'x', fromVersion: '2026.9.16.1005', toVersion: '2026.9.18.1918', atUtc: '2026-09-19T15:00:00Z', requestedBy: 'root' }],
    }))

    await page.goto('/admin/updates')

    const result = page.getByTestId('updates-last-result')
    await expect(result).toContainText('Switched back')
    await expect(result).toContainText('was switched back in')
    await expect(result).toContainText(`${ROOT}/updates/update.log`)
    await expect(page.getByTestId('updates-progress')).toHaveCount(0)
  })

  test('a service that never comes back is called out, with where to look', async ({ page }) => {
    await page.clock.install()
    // The last thing it said before it went away, then silence.
    let calls = 0
    await serve(page, () =>
      calls++ === 0
        ? statusOf({ phase: 'restarting', message: 'Switched to slot b.', toVersion: '2026.9.18.1918' })
        : 'unreachable')

    await page.goto('/admin/updates')
    await expect(page.getByTestId('updates-phase')).toBeVisible()

    await page.clock.fastForward(30_000)
    await expect(page.getByTestId('updates-phase')).toHaveText('The service is restarting…')
    await expect(page.getByTestId('updates-overdue')).toHaveCount(0)

    await page.clock.fastForward(130_000)
    await expect(page.getByTestId('updates-overdue')).toContainText('the terminal the update was run from')
    await expect(page.getByTestId('updates-overdue')).toContainText(`${ROOT}/updates/update.log`)
  })
})

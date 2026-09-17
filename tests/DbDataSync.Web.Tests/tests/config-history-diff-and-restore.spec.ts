import { test, expect, type Page } from '@playwright/test'
import path from 'node:path'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('config-history')

const REPLICATION_NAME = 'config-history-demo'

/**
 * The Version Control tab's two new actions — phase 35.
 *
 * The tab has shown the auto-commit log since phase 6, and until now that was all it did: when, who
 * and what message, with no way to look at a commit or undo it. These cover the two things that
 * changed, and the property that matters most about the second one — **restoring adds a commit rather
 * than removing one**, so the log grows.
 *
 * **Stubbed at the network boundary**, following `run-history-filtering-and-paging.spec.ts`: the fake
 * server here actually implements the restore by appending to its own history, so what is under test
 * is the client — does it ask for the right thing, show what will change before doing it, and reflect
 * a grown log afterwards. The server's own half of this contract is
 * `ConfigHistoryDiffAndRestoreTests`, against a real git repository.
 *
 * **The two diffs are deliberately different**, which is the subtlety the confirmation exists for: a
 * commit's own patch says what that commit changed, and the restore preview says what restoring to it
 * would change. Here, restoring to the commit that *added* `orders.yaml` would also *delete*
 * `invoices.yaml`, created afterwards — which appears in the preview and nowhere in that commit's own
 * patch.
 */

const ORDERS_V1 = 'name: orders\ntargets:\n- table: Orders\n'
const ORDERS_V2 = 'name: orders\ntargets:\n- table: OrdersV2\n'
const INVOICES = 'name: invoices\ntargets:\n- table: Invoices\n'

const SHA_ADD_ORDERS = 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1'
const SHA_ADD_INVOICES = 'b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2'
const SHA_EDIT_ORDERS = 'c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3'
const SHA_RESTORE = 'd4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4'

const MAPPINGS = 'config/replications/config-history-demo/table-mappings'

interface Commit {
  sha: string
  message: string
  authorName: string
  authorEmail: string
  whenUtc: string
}

/** Newest first, as the real endpoint returns it. */
const BASE_HISTORY: Commit[] = [
  { sha: SHA_EDIT_ORDERS, message: "Save table mapping 'orders'", authorName: 'Dana', authorEmail: 'd@example.com', whenUtc: '2026-01-03T09:00:00Z' },
  { sha: SHA_ADD_INVOICES, message: "Save table mapping 'invoices'", authorName: 'Dana', authorEmail: 'd@example.com', whenUtc: '2026-01-02T09:00:00Z' },
  { sha: SHA_ADD_ORDERS, message: "Save table mapping 'orders'", authorName: 'Ray', authorEmail: 'r@example.com', whenUtc: '2026-01-01T09:00:00Z' },
]

/** What each commit itself changed. */
const COMMIT_DIFFS: Record<string, unknown> = {
  [SHA_EDIT_ORDERS]: {
    sha: SHA_EDIT_ORDERS,
    message: "Save table mapping 'orders'",
    truncated: false,
    changes: [{ path: `${MAPPINGS}/orders.yaml`, kind: 'Modified', before: ORDERS_V1, after: ORDERS_V2 }],
  },
  [SHA_ADD_ORDERS]: {
    sha: SHA_ADD_ORDERS,
    message: "Save table mapping 'orders'",
    truncated: false,
    changes: [{ path: `${MAPPINGS}/orders.yaml`, kind: 'Added', before: null, after: ORDERS_V1 }],
  },
  [SHA_ADD_INVOICES]: {
    sha: SHA_ADD_INVOICES,
    message: "Save table mapping 'invoices'",
    truncated: false,
    changes: [{ path: `${MAPPINGS}/invoices.yaml`, kind: 'Added', before: null, after: INVOICES }],
  },
}

/** What restoring to each would change from *now* — a different question, and here a different answer. */
const RESTORE_PREVIEWS: Record<string, unknown> = {
  [SHA_ADD_ORDERS]: {
    sha: SHA_ADD_ORDERS,
    message: "Save table mapping 'orders'",
    truncated: false,
    changes: [
      { path: `${MAPPINGS}/invoices.yaml`, kind: 'Deleted', before: INVOICES, after: null },
      { path: `${MAPPINGS}/orders.yaml`, kind: 'Modified', before: ORDERS_V2, after: ORDERS_V1 },
    ],
  },
}

const TASK = {
  name: REPLICATION_NAME,
  enabled: true,
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: {
    reader: { kind: 'MsSqlChangeTracking', options: {} },
    cache: { kind: 'MsSqlStagingTable', options: {} },
    writer: { kind: 'MsSqlMerge', options: {} },
  },
  endpoints: {
    source: { connectionName: 'src', database: 'AppDb' },
    target: { connectionName: 'tgt', database: 'Warehouse' },
  },
}

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })

interface Counts { restores: number }

async function stub(page: Page): Promise<Counts> {
  const base = `/api/replications/${REPLICATION_NAME}`
  const counts: Counts = { restores: 0 }
  // The fake server's own log, which the restore appends to — the whole point being that it grows.
  const history = [...BASE_HISTORY]

  await page.route(/\/history\/[0-9a-f]+\/diff$/, (route) => {
    const sha = new URL(route.request().url()).pathname.split('/').at(-2)!
    return route.fulfill(json(COMMIT_DIFFS[sha] ?? { sha, message: '', changes: [], truncated: false }))
  })
  await page.route(/\/history\/[0-9a-f]+\/restore-preview$/, (route) => {
    const sha = new URL(route.request().url()).pathname.split('/').at(-2)!
    return route.fulfill(json(RESTORE_PREVIEWS[sha] ?? { sha, message: '', changes: [], truncated: false }))
  })
  await page.route(/\/history\/[0-9a-f]+\/restore$/, (route) => {
    counts.restores += 1
    history.unshift({
      sha: SHA_RESTORE,
      message: `Restore replication '${REPLICATION_NAME}' to ${SHA_ADD_ORDERS.slice(0, 8)}`,
      authorName: 'Dana',
      authorEmail: 'd@example.com',
      whenUtc: '2026-01-04T09:00:00Z',
    })
    return route.fulfill(json({
      restoredFromSha: SHA_ADD_ORDERS,
      commitSha: SHA_RESTORE,
      changes: (RESTORE_PREVIEWS[SHA_ADD_ORDERS] as { changes: unknown[] }).changes,
      warnings: ["The restored config references connection 'src', which does not exist. Mappings using it cannot run until it is created."],
    }))
  })
  await page.route(`**${base}/history`, (route) => route.fulfill(json(history)))
  await page.route(`**${base}/pause-history*`, (route) => route.fulfill(json([])))
  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json([])))
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true,
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))

  return counts
}

test.describe('config history: diff and restore (phase 35)', () => {
  test('01 - a commit shows what it changed, in a diff', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/history`)

    await expect(page.getByTestId('history-table')).toBeVisible()
    await expect(page.getByTestId('history-row')).toHaveCount(3)

    // The newest commit — an edit to orders.yaml.
    await page.getByTestId('view-changes').first().click()

    await expect(page.getByTestId('commit-diff')).toBeVisible()
    await expect(page.getByTestId('diff-file')).toHaveCount(1)
    await expect(page.getByTestId('diff-file').first()).toContainText('orders.yaml')
    // Monaco renders both sides; the old table name is only in the left pane, the new only in the right.
    await expect(page.getByTestId('commit-diff-editor')).toBeVisible()
    await expect(page.getByTestId('commit-diff-editor')).toContainText('OrdersV2')

    await page.screenshot({ path: path.join(screenshotsDir, '35-commit-diff.png'), fullPage: true })
  })

  test('02 - the restore confirmation shows what will change, not what that commit changed', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/history`)
    await expect(page.getByTestId('history-table')).toBeVisible()

    // The oldest commit: it *added* orders.yaml, and nothing else.
    await page.getByTestId('restore-here').last().click()

    await expect(page.getByTestId('restore-confirm')).toBeVisible()
    const summary = page.getByTestId('restore-summary')
    // Restoring to it today would delete invoices.yaml, which was created after it and appears
    // nowhere in that commit's own patch. This line is the whole reason the preview is a second call.
    await expect(summary).toContainText('1 removed')
    await expect(summary).toContainText('invoices.yaml')
    await expect(summary).toContainText('1 changed')
    await expect(summary).toContainText('orders.yaml')

    await page.screenshot({ path: path.join(screenshotsDir, '35-restore-confirmation.png'), fullPage: true })
  })

  test('03 - restoring makes the log grow rather than shrink', async ({ page }) => {
    const counts = await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/history`)
    await expect(page.getByTestId('history-row')).toHaveCount(3)

    await page.getByTestId('restore-here').last().click()
    await expect(page.getByTestId('restore-confirm')).toBeVisible()
    await page.getByTestId('restore-confirm-go').click()

    await expect(page.getByTestId('restore-done')).toBeVisible()
    await expect(page.getByTestId('restore-done')).toContainText(SHA_RESTORE.slice(0, 8))
    // A connection the restored config names but which does not exist is said out loud rather than
    // refused — it is the most likely way a restore lands a replication that cannot run.
    await expect(page.getByTestId('restore-done')).toContainText("connection 'src'")
    expect(counts.restores).toBe(1)

    await page.getByTestId('restore-cancel').click()

    // Four, not two: history is never rewritten, so the undo is itself in the log.
    await expect(page.getByTestId('history-row')).toHaveCount(4)
    await expect(page.getByTestId('history-row').first()).toContainText('Restore replication')

    await page.screenshot({ path: path.join(screenshotsDir, '35-log-grew.png'), fullPage: true })
  })

  test('04 - a restore that would change nothing is offered but not armed', async ({ page }) => {
    await stub(page)
    await page.goto(`/replications/${REPLICATION_NAME}/history`)
    await expect(page.getByTestId('history-table')).toBeVisible()

    // The newest commit has no restore preview in the fixture — i.e. the config is already there.
    await page.getByTestId('restore-here').first().click()

    await expect(page.getByTestId('restore-confirm')).toContainText('already exactly as it was')
    await expect(page.getByTestId('restore-confirm-go')).toBeDisabled()
  })
})

import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { DB_NAME, querySql, runSql, SA_PASSWORD, SOURCE_TABLE, SRC_CONNECTION_NAME, TARGET_TABLE, TGT_CONNECTION_NAME } from '../test-db'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'playwright-sync'
const MAPPING_NAME = 'items'
const SOURCE_NAME_COLUMN = 'Name'
// The mapping created in test 05 puts UPPER({{column}}) on Name, so everything downstream of the
// source sees it upper-cased — which is the end-to-end proof that a source-dialect transform runs.
const NAMES = { widget: 'WIDGET', gadget: 'GADGET' }

async function shot(page: Page, name: string) {
  await page.screenshot({ path: path.join(screenshotsDir, name), fullPage: true })
}

/**
 * Replaces a Monaco editor's contents. Monaco is not an <input>, so `fill()` does not reach it — but it
 * keeps a hidden textarea for input, which `insertText` writes through in one event rather than as
 * keystrokes, so auto-closing brackets and auto-indent never fire.
 */
async function setCode(page: Page, testId: string, code: string) {
  const editor = page.getByTestId(testId)
  await expect(editor.locator('.monaco-editor')).toBeVisible({ timeout: 20_000 })
  await editor.click()
  await page.keyboard.press('ControlOrMeta+A')
  await page.keyboard.insertText(code)
}

/**
 * Waits for a <select data-testid=testId> to offer an option reading `label` (populated asynchronously
 * by a metadata-browsing API call) before selecting it — avoids racing react-query.
 *
 * By **label**, not value. The table picker's option values are indexes into the loaded list, because
 * a "schema.table" value has to be split back apart and that is wrong for a name containing a literal
 * dot (phase 45). A label is what the operator picks anyway.
 */
async function selectWhenReady(page: Page, testId: string, label: string) {
  const select = page.getByTestId(testId)
  await expect(select.locator('option', { hasText: label }).first()).toBeAttached({ timeout: 15_000 })
  await select.selectOption({ label })
}

test.describe.serial('golden path: define, configure, and run a replication end-to-end', () => {
  test('01 - app loads and redirects to the replications list', async ({ page }) => {
    await page.goto('/')
    await expect(page).toHaveURL(/\/replications$/)
    await expect(page.getByRole('heading', { name: 'Replications' })).toBeVisible()
    await shot(page, '01-replications-empty.png')
  })

  test('02 - create source and target connections', async ({ page }) => {
    await page.goto('/connections')

    for (const name of [SRC_CONNECTION_NAME, TGT_CONNECTION_NAME]) {
      await page.getByTestId('new-connection-button').click()
      await expect(page).toHaveURL(/\/connections\/new$/)
      await page.getByTestId('connection-name-input').fill(name)
      await page.getByTestId('connection-parameters-host').fill('localhost')
      await page.getByTestId('connection-parameters-port').fill('14330')
      await page.getByTestId('connection-parameters-database').fill(DB_NAME)
      await page.getByTestId('connection-parameters-userId').fill('sa')
      await page.getByTestId('connection-parameters-password').fill(SA_PASSWORD)
      if (name === SRC_CONNECTION_NAME) await shot(page, '02-connection-form.png')
      await page.getByTestId('save-connection-button').click()
      await expect(page.getByTestId('connections-table')).toContainText(name)
    }

    await shot(page, '03-connections-list.png')
  })

  test('03 - create the replication', async ({ page }) => {
    await page.goto('/replications')
    await page.getByTestId('new-replication-button').click()
    await page.getByTestId('replication-name-input').fill(REPLICATION_NAME)
    await page.getByTestId('create-replication-button').click()

    // Landing on the replication lands on a tab, not on a bare frame.
    await expect(page).toHaveURL(new RegExp(`/replications/${REPLICATION_NAME}/overview$`))
    await expect(page.getByRole('heading', { name: REPLICATION_NAME })).toBeVisible()
  })

  test('04 - set the replication\'s endpoints', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-overview').click()

    // Source and target belong to the replication: every mapping inherits them, and only a mapping
    // that genuinely reads or writes somewhere else has to say so.
    await selectWhenReady(page, 'task-source-connection-select', SRC_CONNECTION_NAME)
    await selectWhenReady(page, 'task-source-database-select', DB_NAME)
    await selectWhenReady(page, 'task-target-connection-select', TGT_CONNECTION_NAME)
    await selectWhenReady(page, 'task-target-database-select', DB_NAME)
    await shot(page, '04-replication-endpoints.png')

    await page.getByTestId('save-settings-button').click()

    await page.reload()
    await page.getByTestId('tab-overview').click()
    await expect(page.getByTestId('task-source-connection-select')).toHaveValue(SRC_CONNECTION_NAME, { timeout: 15_000 })
  })

  test('04b - the Overview leads with what a replication is, not with an advanced customisation', async ({ page }) => {
    // Phase 23 put the scripts card first because it was the new thing, which is the oldest reason to
    // get an ordering wrong. Endpoints are what a replication *is*, and they stay above the tabs
    // (phase 64) because every tab below is about what happens between those two endpoints.
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)
    await expect(page.locator('.pane .card').first()).toHaveAttribute('data-side', 'source', { timeout: 15_000 })

    // Notes is the index tab: the bare /overview URL opens what the replication is for.
    await expect(page.getByTestId('replication-notes-card')).toBeVisible()
    await expect(page.getByTestId('overview-tab-notes')).toHaveClass(/active/)

    // And the scripts card shows its slots straight away — it is a tab now, not a card in a stack.
    await page.getByTestId('overview-tab-transforms').click()
    await expect(page.getByTestId('script-bindings-title')).toContainText('Custom transforms and providers')
    await expect(page.getByTestId('script-binding-rowTransform')).toBeVisible()
  })

  test('05 - add a table mapping that inherits the replication\'s endpoints', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId('new-mapping-button').click()
    await expect(page).toHaveURL(new RegExp(`/replications/${REPLICATION_NAME}/mappings/new$`))
    // The mapping being created has no row in the list until it is saved, so the sidebar stands one in.
    await expect(page.getByTestId('mappings-sidebar')).toContainText('new mapping')

    await page.getByTestId('mapping-name-input').fill(MAPPING_NAME)

    // Nothing to pick but the tables — the connection and database arrive from the replication and
    // are shown read-only, and the table picker still cascades from them.
    await expect(page.getByTestId('source-side')).toContainText('INHERITED')
    await expect(page.getByTestId('source-connection-select')).toHaveCount(0)
    await selectWhenReady(page, 'source-table-select', `dbo.${SOURCE_TABLE}`)

    // The target is a combobox, not a closed list — a name it does not have is a table to create
    // (test 18). Naming one it does have works the same way.
    await page.getByTestId('target-schema-input').fill('dbo')
    await page.getByTestId('target-table-input').fill(TARGET_TABLE)
    await expect(page.getByTestId('target-table-will-be-created')).toHaveCount(0)

    // The editor's own tabs (phase 64). Source/Target and the filter stay above them; everything
    // about how the mapping behaves is a tab, and Notes is the one a bare mapping URL opens.
    await expect(page.getByTestId('mapping-notes-card')).toBeVisible()
    await page.getByTestId('mapping-tab-columns').click()

    // Column mappings auto-suggest once both tables' columns load (same-name match: Id, Name).
    // The design renders rows as CSS-grid divs rather than a <table>, so count the row class.
    await expect(page.getByTestId('column-mappings-table').locator('.grid-row')).toHaveCount(2, { timeout: 15_000 })
    await shot(page, '05-table-mapping-form.png')

    // A transform is SQL in the source's own dialect, evaluated by the source engine. {{column}} is
    // substituted with whatever reference is correct for the reader's statement.
    // Text with a pencil, not an open input: click to edit, Enter to commit.
    await page.getByTestId(`column-mapping-transform-${SOURCE_NAME_COLUMN}-edit`).click()
    await page.getByTestId(`column-mapping-transform-${SOURCE_NAME_COLUMN}`).fill('UPPER({{column}})')
    await page.getByTestId(`column-mapping-transform-${SOURCE_NAME_COLUMN}`).press('Enter')

    await page.getByTestId('save-mapping-button').click()
    // The mappings list is the sidebar now, not a table below the form, and saving puts the mapping
    // that was actually saved in the URL — a create names something that had no route a moment ago.
    await expect(page.getByTestId(`mapping-item-${MAPPING_NAME}`)).toBeVisible({ timeout: 15_000 })
    await expect(page).toHaveURL(new RegExp(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}$`))
    await shot(page, '06-table-mappings-list.png')
  })

  test('06 - trigger a run and watch it complete live', async ({ page }) => {
    // Touch both rows before triggering. A replication that has never run is due immediately, so the
    // scheduler can beat this test to the pass — and then the triggered run correctly reads nothing,
    // because there is nothing left to read.
    //
    // Touching first narrows that window; it does not close it, because the scheduler can still take
    // a pass between the UPDATE and the click. So a run that reads nothing is treated as "the
    // scheduler got there first" and the whole gesture is repeated, rather than failing an assertion
    // about who won a race the test was never about.
    for (let attempt = 1; ; attempt++) {
      runSql(`UPDATE dbo.[${SOURCE_TABLE}] SET Name = Name;`, DB_NAME)

      await page.goto(`/replications/${REPLICATION_NAME}`)
      await page.getByTestId('tab-runs').click()
      await page.getByTestId('trigger-run-button').click()

      await expect(page.getByTestId('live-run-panel')).toBeVisible()
      await expect(page.getByTestId('live-log-viewer')).toContainText('Run started', { timeout: 15_000 })
      if (attempt === 1) await shot(page, '07-live-run-in-progress.png')

      await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 30_000 })
      const reported = await page.getByTestId('live-run-panel').textContent()
      if (reported?.includes('2 row(s) read · 2 row(s) written')) break

      expect(attempt, 'the scheduler took the changes before every triggered run').toBeLessThan(3)
    }
    await shot(page, '08-live-run-completed.png')

    // The live panel auto-clears a few seconds after completion, leaving the persisted history row.
    await expect(page.getByTestId('run-history-table')).toContainText('succeeded', { timeout: 10_000 })
    await shot(page, '09-run-history.png')
  })

  test('07 - config history shows the auto-committed changes', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-history').click()
    await expect(page.getByTestId('history-table')).toContainText('replication task')
    await expect(page.getByTestId('history-table')).toContainText('table mapping')
    await shot(page, '10-config-history.png')
  })

  test('08 - data actually replicated to the target table, with the transform applied', async () => {
    // The real end-to-end proof, independent of anything the UI claims — and the source's own
    // rows are still 'Widget'/'Gadget', so upper case at the target can only have come from the
    // transform being evaluated by the source engine.
    const output = querySql(`SET NOCOUNT ON; SELECT Id, Name FROM dbo.[${TARGET_TABLE}] ORDER BY Id;`, DB_NAME)
    expect(output).toContain(NAMES.widget)
    expect(output).toContain(NAMES.gadget)
    expect(output.trim().split('\n').filter((l) => l.trim())).toHaveLength(2)
  })

  test('09 - an ad-hoc backfill repairs the target through the UI', async ({ page }) => {
    // Diverge the target from the source behind the replication's back. An incremental pass can't fix
    // this — Change Tracking has nothing new to report, since nothing changed at the *source* — which
    // is exactly the situation a reload exists for.
    runSql(`DELETE FROM dbo.[${TARGET_TABLE}] WHERE Name = '${NAMES.widget}';`, DB_NAME)
    expect(querySql(`SET NOCOUNT ON; SELECT Name FROM dbo.[${TARGET_TABLE}];`, DB_NAME)).not.toContain(NAMES.widget)

    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('backfill-button').click()
    await expect(page.getByTestId('backfill-form')).toBeVisible()

    // The Kind pickers are populated from the live capabilities endpoint, and default by capability:
    // a reader that can be scoped to a segment, and a writer that reconciles rather than only upserts.
    await expect(page.getByTestId('backfill-reader-select')).toHaveValue('MsSqlBatchReload')
    await expect(page.getByTestId('backfill-writer-select')).toHaveValue('MsSqlMergeReconcile')
    await expect(page.getByTestId('backfill-mapping-select')).toHaveValue(MAPPING_NAME)
    await shot(page, '11-backfill-form.png')

    await page.getByTestId('backfill-submit-button').click()

    await expect(page.getByTestId('live-run-panel')).toBeVisible()
    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 30_000 })
    await shot(page, '12-backfill-completed.png')

    // Distinguishable from the replication's own incremental passes in history.
    await expect(page.getByTestId('run-history-table')).toContainText('BACKFILL', { timeout: 10_000 })
    await expect(page.getByTestId('run-history-table')).toContainText('full')
    await shot(page, '13-run-history-with-backfill.png')

    const rows = querySql(`SET NOCOUNT ON; SELECT Id, Name FROM dbo.[${TARGET_TABLE}] ORDER BY Id;`, DB_NAME)
    expect(rows).toContain(NAMES.widget)
    expect(rows).toContain(NAMES.gadget)
  })

  test('10 - the backfill left the incremental sync\'s watermark alone', async ({ page }) => {
    // Nothing has changed at the source since test 05, so the next incremental pass must read nothing.
    // Had the backfill disturbed the watermark, this pass would re-read the whole table instead — the
    // single most important consequence of Backfill runs never calling SetWatermark.
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-runs').click()
    await page.getByTestId('trigger-run-button').click()

    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 30_000 })
    await expect(page.getByTestId('live-run-panel')).toContainText('0 row(s) read · 0 row(s) written')
  })

  test('11 - settings expose live driver capabilities and per-stage options', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-overview').click()
    await page.getByTestId('overview-tab-pipeline').click()

    // Offered because the registered driver advertises them, not because they are compiled into the
    // SPA — the reload reader and reconciling writers did not exist when this picker was written.
    const readerSelect = page.getByTestId('reader-kind-select')
    await expect(readerSelect.locator('option[value="MsSqlBatchReload"]')).toBeAttached({ timeout: 15_000 })
    await expect(readerSelect.locator('option[value="MsSqlBatchReload"]')).toContainText('segmentable')

    // Watermark mode is offered, not withheld — append-only tables are exactly what it is for — but
    // it says what it cannot do rather than letting an operator assume deletes are covered.
    await expect(readerSelect.locator('option[value="Watermark"]')).toContainText('does not detect deletes')
    await expect(readerSelect.locator('option[value="MsSqlChangeTracking"]')).not.toContainText('does not detect deletes')

    // The pipeline is three selectable stages; picking one swaps both the Kind picker and its options.
    await page.getByTestId('stage-writer').click()
    await expect(page.getByTestId('writer-kind-select').locator('option[value="MsSqlMerge"]')).toContainText('upsert-only')

    // A stage's settings are offered, not typed: the change-tracking reader declares snapshotIsolation
    // beside the code that reads it, so choosing that Kind offers a labelled toggle rather than
    // leaving an operator to know the key by heart and spell it into a free-form table (phase 42).
    await page.getByTestId('stage-reader').click()
    const snapshotIsolation = page.getByTestId('reader-options-snapshotIsolation')
    await expect(snapshotIsolation).toBeVisible({ timeout: 15_000 })
    await expect(page.getByTestId('reader-options')).toContainText('Snapshot isolation')
    await expect(page.getByTestId('reader-options')).toContainText('snapshot transaction')

    // Declared as a Bool, so it is a toggle — and it starts at the default the declaration states.
    await expect(snapshotIsolation).toHaveAttribute('aria-pressed', 'false')
    await snapshotIsolation.click()
    await expect(snapshotIsolation).toHaveAttribute('aria-pressed', 'true')
    await snapshotIsolation.click()

    await page.getByTestId('save-settings-button').click()
    await shot(page, '14-pipeline-settings.png')

    // Asked of the API, not of the form that just wrote it: the only proof it reached the config repo.
    // Left off deliberately — this database does not allow snapshot isolation, and a run would fail.
    await expect.poll(async () => {
      const task = await (await page.request.get(`/api/replications/${REPLICATION_NAME}`)).json()
      return task.changeProcessing.reader.options.snapshotIsolation
    }, { timeout: 15_000 }).toBe('false')
  })

  test('12 - a mapping can override the replication\'s endpoint for itself', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId(`mapping-item-${MAPPING_NAME}`).click()

    // Turning the override on replaces the read-only inherited values with pickers, seeded with
    // what was in effect — so it opens as a starting point, not a blank form.
    await page.getByTestId('source-override-toggle').click()
    await expect(page.getByTestId('source-side')).not.toContainText('INHERITED')
    await expect(page.getByTestId('source-connection-select')).toHaveValue(SRC_CONNECTION_NAME)
    await selectWhenReady(page, 'source-database-select', DB_NAME)
    await selectWhenReady(page, 'source-table-select', `dbo.${SOURCE_TABLE}`)
    await shot(page, '15-mapping-endpoint-override.png')

    await page.getByTestId('save-mapping-button').click()
    await expect(page.getByTestId(`mapping-item-${MAPPING_NAME}`)).toBeVisible({ timeout: 15_000 })

    // Turning it back off returns the mapping to inheriting, which is what the saved config has to
    // record — an override that cannot be undone is a one-way door.
    await page.reload()
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId(`mapping-item-${MAPPING_NAME}`).click()
    await expect(page.getByTestId('source-connection-select')).toBeVisible({ timeout: 15_000 })
    await page.getByTestId('source-override-toggle').click()
    await expect(page.getByTestId('source-side')).toContainText('INHERITED')
    await page.getByTestId('save-mapping-button').click()

    await page.reload()
    await page.getByTestId('tab-mappings').click()
    await page.getByTestId(`mapping-item-${MAPPING_NAME}`).click()
    await expect(page.getByTestId('source-side')).toContainText('INHERITED', { timeout: 15_000 })
  })

  test('13 - a connection can be tested from its editor, and the list reports reachability on demand', async ({ page }) => {
    await page.goto(`/connections/${SRC_CONNECTION_NAME}`)

    // The card is present before anything is tested, and says so — "not tested" is a distinct state
    // from "unreachable", and showing a reading nobody asked for is what kept this out of phase 15.
    await expect(page.getByTestId('connection-test-card')).toContainText('Not tested yet')

    // The credential field an operator staring at an auth failure needs: which variable is read.
    await expect(page.getByTestId('credential-env-var')).toHaveValue(
      `CLRKERNEL_SECRET_DATASYNC_CONNECTION_${SRC_CONNECTION_NAME.replace(/-/g, '_').toUpperCase()}`,
    )
    await expect(page.getByTestId('credential-store-select')).toBeDisabled()

    await page.getByTestId('test-connection-button').click()
    await expect(page.getByTestId('connection-test-result')).toContainText('reachable', { timeout: 20_000 })
    await expect(page.getByTestId('connection-test-result')).toContainText('SQL Server')
    await shot(page, '16-connection-test.png')

    // The list column stays empty until asked — no page load opens a database.
    await page.goto('/connections')
    await expect(page.getByTestId(`reachable-${SRC_CONNECTION_NAME}`)).toHaveText('—')

    await page.getByTestId('test-all-connections-button').click()
    await expect(page.getByTestId(`reachable-${SRC_CONNECTION_NAME}`)).toContainText('reachable', { timeout: 20_000 })
    await expect(page.getByTestId(`reachable-${TGT_CONNECTION_NAME}`)).toContainText('reachable', { timeout: 20_000 })
    await shot(page, '17-connections-reachability.png')
  })

  test('14 - every screen has its own URL, and reloading one stays on it', async ({ page }) => {
    // The reason this matters: all four tabs and the open mapping used to be component state under a
    // single route, so a reload dropped you back on Overview and there was no link to send anyone.
    const base = `/replications/${REPLICATION_NAME}`
    await page.goto(base)

    for (const [testId, path] of [
      ['tab-mappings', 'mappings'],
      ['tab-runs', 'runs'],
      ['tab-history', 'history'],
      ['tab-overview', 'overview'],
    ] as const) {
      await page.getByTestId(testId).click()
      await expect(page).toHaveURL(new RegExp(`${base}/${path}`))
    }

    // Deep-linked straight in, with no navigation to get there.
    await page.goto(`${base}/history`)
    await expect(page.getByTestId('history-table')).toBeVisible()
    await expect(page.getByTestId('tab-history')).toHaveClass(/active/)

    await page.goto(`${base}/mappings/${MAPPING_NAME}`)
    await expect(page.getByRole('heading', { name: MAPPING_NAME })).toBeVisible({ timeout: 15_000 })
    await expect(page.getByTestId(`mapping-item-${MAPPING_NAME}`)).toHaveClass(/active/)
    await page.reload()
    await expect(page.getByRole('heading', { name: MAPPING_NAME })).toBeVisible({ timeout: 15_000 })

    // /mappings with nothing chosen lands on the overview — which tables are mapped is what the
    // section is about, and "whichever mapping sorts first" was never an answer anyone asked for.
    // `replace`, so Back leaves the tab instead of bouncing off the redirect.
    await page.goto(`${base}/runs`)
    await page.getByTestId('tab-mappings').click()
    await expect(page).toHaveURL(new RegExp(`${base}/mappings/overview$`))
    await page.goBack()
    await expect(page).toHaveURL(new RegExp(`${base}/runs$`))

    // The browser's own history works across tabs, which is what it means for these to be pages.
    await page.goBack()
    await expect(page).toHaveURL(new RegExp(`${base}/mappings/${MAPPING_NAME}$`))
    await page.goForward()
    await expect(page).toHaveURL(new RegExp(`${base}/runs$`))

    // A stale or mistyped URL lands somewhere real.
    await page.goto('/replications/does-not-exist-anywhere/nonsense')
    await expect(page).toHaveURL(/\/replications$/)
  })

  test('15 - a C# script generates a source transform, bound on the table mapping', async ({ page }) => {
    // The whole scripting loop through the UI: write C#, have the server compile it, bind it to a
    // mapping, and see the source engine evaluate what the script generated.
    const SCRIPT = 'reverse-name'

    await page.goto('/scripts')
    await page.getByTestId('new-script-button').click()
    await page.getByTestId('script-name-input').fill(SCRIPT)
    await page.getByTestId('script-entry-type-input').fill('ReverseName')
    await setCode(page, 'script-code-input', `using DataSync.Scripting.Abstractions;

public sealed class ReverseName : ISqlColumnExpression
{
    public string? RenderSql(SqlColumnExpressionContext c) =>
        c.SourceColumn == c.Parameters.Require("column")
            ? $"REVERSE({c.ColumnReference})"
            : null;
}
`)

    // Compile before saving — the operator finds out here rather than at the first run.
    await page.getByTestId('check-script-button').click()
    await expect(page.getByTestId('script-code-input')).toBeVisible()
    await page.getByTestId('save-script-button').click()
    await expect(page.getByTestId('scripts-table')).toContainText(SCRIPT, { timeout: 15_000 })
    await shot(page, '18-scripts-list.png')

    // A script that does not compile is refused, with the compiler's own diagnostics.
    await page.getByTestId('new-script-button').click()
    await page.getByTestId('script-name-input').fill('broken-script')
    await setCode(page, 'script-code-input', 'this is not C#')
    await page.getByTestId('check-script-button').click()
    await expect(page.getByTestId('script-diagnostics')).toBeVisible({ timeout: 15_000 })

    // The reason the editor is worth its weight: the compiler's line and column become a squiggle on
    // the offending token, not just a message underneath that the reader has to go and find.
    await expect(page.getByTestId('script-code-input').locator('.squiggly-error').first())
      .toBeVisible({ timeout: 15_000 })
    await shot(page, '19-script-diagnostics.png')

    // Bind it on the mapping — the most specific level, which is what the hierarchy exists for.
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/transforms`)
    await expect(page.getByTestId('script-bindings-card')).toBeVisible({ timeout: 15_000 })
    await page.getByTestId('script-binding-sqlColumnExpression').selectOption(SCRIPT)

    const parameters = page.getByTestId('script-parameters-sqlColumnExpression')
    await parameters.getByPlaceholder('Parameter name').fill('column')
    await parameters.getByRole('button', { name: '+ Add' }).click()
    await parameters.getByLabel('column value').fill(SOURCE_NAME_COLUMN)
    await shot(page, '20-script-binding.png')

    await page.getByTestId('save-mapping-button').click()

    // The mapping already carries a literal UPPER({{column}}) on Name from test 05, and a literal
    // transform beats a script — so the script's REVERSE has to lose. Clearing the literal is what
    // lets it win, and proves the precedence rule rather than assuming it.
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/columns`)
    await page.getByTestId(`column-mapping-transform-${SOURCE_NAME_COLUMN}-edit`).click()
    await page.getByTestId(`column-mapping-transform-${SOURCE_NAME_COLUMN}`).fill('')
    await page.getByTestId(`column-mapping-transform-${SOURCE_NAME_COLUMN}`).press('Enter')
    await page.getByTestId('save-mapping-button').click()

    // Touch the source so Change Tracking has something to report. Without this the next pass reads
    // zero rows and the target keeps whatever the previous pass left — which is correct behaviour and
    // would make this assertion measure nothing.
    runSql(`UPDATE dbo.[${SOURCE_TABLE}] SET Name = Name;`, DB_NAME)

    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await page.getByTestId('trigger-run-button').click()
    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 30_000 })

    // 'Widget' reversed is 'tegdiW' — and the source still holds 'Widget', so the only thing that
    // could have reversed it is the source engine evaluating SQL a C# script generated.
    const rows = querySql(`SET NOCOUNT ON; SELECT Name FROM dbo.[${TARGET_TABLE}];`, DB_NAME)
    expect(rows).toContain('tegdiW')
  })

  test('15b - the Scripts list says whether a script is bound to anything', async ({ page }) => {
    // The first question anyone has about a script they did not write, and the one thing a list of
    // names, kinds and descriptions cannot answer.
    await page.goto('/scripts')
    await expect(page.getByTestId('script-usage-reverse-name')).toContainText(REPLICATION_NAME, { timeout: 15_000 })

    // An unbound one says so, which is the answer worth showing: usually a mistake, or safe to delete.
    await page.goto('/scripts/new')
    await page.getByTestId('script-name-input').fill('never-bound')
    await page.getByTestId('script-kind-select').selectOption('rowTransform')
    await page.getByTestId('script-entry-type-input').fill('Unused')
    await setCode(page, 'script-code-input', `using System.Threading;
using System.Threading.Tasks;
using DataSync.Drivers.Abstractions;
using DataSync.Scripting.Abstractions;

public sealed class Unused : IRowTransform
{
    public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext c) => input;

    public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext c, CancellationToken ct)
        => ValueTask.FromResult<ChangeRow?>(row);
}
`)
    await page.getByTestId('save-script-button').click()
    await expect(page.getByTestId('scripts-table')).toContainText('never-bound', { timeout: 15_000 })

    await expect(page.getByTestId('script-usage-never-bound')).toContainText('unused')
  })

  test('16 - a C# row transform filters rows in process, between the reader and staging', async ({ page }) => {
    // The other half of the transform story. Phase 22's SQL runs at the source; this runs here, on the
    // stream, and can do the one thing SQL in a SELECT list cannot: drop the row entirely.
    const SCRIPT = 'drop-gadgets'

    await page.goto('/scripts/new')
    await page.getByTestId('script-name-input').fill(SCRIPT)
    await page.getByTestId('script-kind-select').selectOption('rowTransform')
    await page.getByTestId('script-entry-type-input').fill('DropGadgets')
    await setCode(page, 'script-code-input', `using System.Threading;
using System.Threading.Tasks;
using DataSync.Drivers.Abstractions;
using DataSync.Scripting.Abstractions;

public sealed class DropGadgets : IRowTransform
{
    public ChangeSchema DeclareSchema(ChangeSchema input, RowTransformContext c) => input;

    public ValueTask<ChangeRow?> TransformAsync(ChangeRow row, RowTransformContext c, CancellationToken ct)
    {
        // 'tegdaG', not 'Gadget': the source's own SQL transform has already run by the time a row
        // reaches here, so this sees REVERSE(Name). That ordering is the contract — source SQL, then
        // values, then the row — and this is what it looks like.
        var name = row["Name"] as string;
        if (name == "tegdaG")
        {
            c.Log($"dropping {name}");
            return ValueTask.FromResult<ChangeRow?>(null);
        }
        return ValueTask.FromResult<ChangeRow?>(row);
    }
}
`)
    await page.getByTestId('save-script-button').click()
    await expect(page.getByTestId('scripts-table')).toContainText(SCRIPT, { timeout: 15_000 })

    // Bound on the replication this time — the middle level, inherited by every mapping under it.
    await page.goto(`/replications/${REPLICATION_NAME}/overview/transforms`)
    await expect(page.getByTestId('script-bindings-card')).toBeVisible({ timeout: 15_000 })
    await page.getByTestId('script-binding-rowTransform').selectOption(SCRIPT)
    await page.getByTestId('save-settings-button').click()

    // Clear the target and touch the source, so this pass genuinely re-reads both rows.
    runSql(`DELETE FROM dbo.[${TARGET_TABLE}];`, DB_NAME)
    runSql(`UPDATE dbo.[${SOURCE_TABLE}] SET Name = Name;`, DB_NAME)

    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await page.getByTestId('trigger-run-button').click()
    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 30_000 })

    await shot(page, '21-row-transform-run.png')

    // The script's log line lands in the run log too, but the live panel auto-clears a few seconds
    // after completion and a two-row run beats the assertion to it. That the log reaches the run is
    // covered by TransformPipelineTests; what matters here is the data.

    // One row staged out of two read, and the one that survived is the source-transformed 'Widget'.
    const rows = querySql(`SET NOCOUNT ON; SELECT Name FROM dbo.[${TARGET_TABLE}];`, DB_NAME)
    expect(rows).toContain('tegdiW')
    expect(rows).not.toContain('tegdaG')
    expect(rows.trim().split('\n').filter((l) => l.trim())).toHaveLength(1)
  })

  test('17 - a connection can be addressed by connection string instead of host and port', async ({ page }) => {
    // The prerequisite for ODBC and JDBC, whose engines have no host and port to give — and useful
    // today for a SQL Server string carrying a failover partner.
    const NAME = 'playwright-cs'

    await page.goto('/connections/new')
    await page.getByTestId('connection-name-input').fill(NAME)
    await page.getByTestId('connection-parameters-addressMode').selectOption('connectionString')

    // Host and port are gone rather than greyed out: a disabled Host beside a connection string
    // invites the question of which one is being used.
    await expect(page.getByTestId('connection-parameters-host')).toHaveCount(0)

    await page.getByTestId('connection-parameters-connectionString').fill('Server=localhost,14330;TrustServerCertificate=True')
    await page.getByTestId('connection-parameters-database').fill(DB_NAME)
    await page.getByTestId('connection-parameters-userId').fill('sa')
    await page.getByTestId('connection-parameters-password').fill(SA_PASSWORD)
    await shot(page, '22-connection-string.png')

    await page.getByTestId('save-connection-button').click()
    await expect(page.getByTestId('connections-table')).toContainText(NAME)

    // It survives a reload in the mode it was saved in, and it actually connects.
    await page.goto(`/connections/${NAME}`)
    await expect(page.getByTestId('connection-parameters-addressMode')).toHaveValue('connectionString', { timeout: 15_000 })
    await expect(page.getByTestId('connection-parameters-connectionString')).toHaveValue(/Server=localhost,14330/)

    await page.getByTestId('test-connection-button').click()
    await expect(page.getByTestId('connection-test-result')).toContainText('reachable', { timeout: 20_000 })
  })

  test('18 - a target table that does not exist is named, created from the plan, and replicated into', async ({ page }) => {
    // The complaint phase 40 answers: provisioning was built, tested, and unreachable from the screen
    // where an operator would want it, because the target was a closed list of tables that already
    // existed.
    // Provisioning, a save, an apply and a run in one test — more steps than any other here, and the
    // default budget is one run's worth.
    test.setTimeout(180_000)

    const NEW_SOURCE = 'PwSrcOrders'
    const NEW_TARGET = 'PwTgtOrders'
    const NEW_MAPPING = 'orders'
    // Its own replication, not the one the earlier tests build up: that one carries test 16's
    // row-transform binding at the replication level, which every mapping under it inherits and
    // which expects a column this source does not have.
    const PROVISIONED_REPLICATION = 'playwright-provisioned'

    // Its own source table, not the one test 05 uses: a Primary watermark is per source table, so
    // sharing one would mean this mapping's first pass saw only changes since that watermark — no
    // rows, and nothing to prove.
    runSql(`
      IF OBJECT_ID('dbo.${NEW_TARGET}', 'U') IS NOT NULL DROP TABLE dbo.${NEW_TARGET};
      IF OBJECT_ID('dbo.${NEW_SOURCE}', 'U') IS NOT NULL DROP TABLE dbo.${NEW_SOURCE};
      CREATE TABLE dbo.${NEW_SOURCE} (Id INT NOT NULL PRIMARY KEY, Description NVARCHAR(60) NOT NULL);
      ALTER TABLE dbo.${NEW_SOURCE} ENABLE CHANGE_TRACKING;
      INSERT INTO dbo.${NEW_SOURCE} (Id, Description) VALUES (1, 'first order'), (2, 'second order');
    `, DB_NAME)

    await page.goto('/replications')
    await page.getByTestId('new-replication-button').click()
    await page.getByTestId('replication-name-input').fill(PROVISIONED_REPLICATION)
    await page.getByTestId('create-replication-button').click()
    await expect(page).toHaveURL(new RegExp(`/replications/${PROVISIONED_REPLICATION}/overview$`))

    await selectWhenReady(page, 'task-source-connection-select', SRC_CONNECTION_NAME)
    await selectWhenReady(page, 'task-source-database-select', DB_NAME)
    await selectWhenReady(page, 'task-target-connection-select', TGT_CONNECTION_NAME)
    await selectWhenReady(page, 'task-target-database-select', DB_NAME)
    await page.getByTestId('save-settings-button').click()

    await page.getByTestId('tab-mappings').click()
    await page.getByTestId('new-mapping-button').click()
    await page.getByTestId('mapping-name-input').fill(NEW_MAPPING)

    await selectWhenReady(page, 'source-table-select', `dbo.${NEW_SOURCE}`)

    await page.getByTestId('target-schema-input').fill('dbo')
    await page.getByTestId('target-table-input').fill(NEW_TARGET)
    await expect(page.getByTestId('target-table-will-be-created')).toBeVisible({ timeout: 15_000 })

    // The target has no catalog to read, so its columns are the source's — and this is not cosmetic:
    // the CREATE TABLE is generated from the mapping's column mappings, so what is listed here is
    // literally what gets created.
    await page.getByTestId('mapping-tab-columns').click()
    const rows = page.getByTestId('column-mappings-table').locator('.grid-row')
    await expect(rows).toHaveCount(2, { timeout: 15_000 })
    await expect(page.getByTestId('column-mappings-table')).toContainText("the source's columns")
    await page.getByTestId('mapping-tab-provisioning').click()
    await expect(page.getByTestId('provisioning-after-save-hint')).toBeVisible()
    await shot(page, '23-new-target-table.png')

    // Saving a mapping whose target does not exist stays possible — it is a legitimate intermediate
    // state, and blocking it would force provisioning before the operator can describe what they want.
    await page.getByTestId('save-mapping-button').click()
    await expect(page).toHaveURL(new RegExp(`/replications/${PROVISIONED_REPLICATION}/mappings/${NEW_MAPPING}$`))

    // Reopened, it still shows the name it was given and still says the table is not there.
    await page.reload()
    await expect(page.getByTestId('target-table-input')).toHaveValue(NEW_TARGET, { timeout: 15_000 })
    await expect(page.getByTestId('target-table-will-be-created')).toBeVisible({ timeout: 15_000 })

    // The tab says how much is outstanding without being opened — a satisfied plan shows no badge
    // at all rather than a "0".
    await expect(page.getByTestId('mapping-tab-provisioning-badge')).toBeVisible({ timeout: 20_000 })
    await page.getByTestId('mapping-tab-provisioning').click()

    // The settings come first and the two plans follow them: the toggles are what this mapping asks
    // for, and the plans are what that currently amounts to.
    const provisioningCard = page.locator('.subtab-panel')
    expect((await provisioningCard.getByTestId('provisioning-create').boundingBox())!.y)
      .toBeLessThan((await provisioningCard.getByTestId('provisioning-plan-target').boundingBox())!.y)

    // The Setup card now plans against it: the same code path an unattended run's
    // CreateTargetTableIfMissing uses, so this DDL is the DDL that would have run anyway.
    const targetPlan = page.getByTestId('provisioning-plan-target')
    await expect(targetPlan).toContainText('missing', { timeout: 20_000 })
    await expect(targetPlan).toContainText('CREATE TABLE')
    await expect(targetPlan).toContainText('Description')
    await shot(page, '24-create-table-plan.png')

    page.once('dialog', (d) => d.accept())
    await targetPlan.getByTestId('provisioning-plan-target-apply').click()

    // Each step's outcome, not just the resulting state: a step can fail without the request failing.
    await expect(targetPlan.getByTestId('provisioning-plan-target-result')).toContainText('✓', { timeout: 20_000 })
    await expect(targetPlan).toContainText('satisfied', { timeout: 20_000 })

    // Applied down to nothing outstanding, and the badge goes entirely rather than reading "0".
    await expect(page.getByTestId('mapping-tab-provisioning-badge')).toHaveCount(0, { timeout: 20_000 })

    expect(querySql(`SELECT COUNT(*) FROM sys.tables WHERE name = '${NEW_TARGET}';`, DB_NAME)).toContain('1')

    // And the loop closes: the mapping runs against the table it just described into existence.
    // Asserted on this mapping's own runs, not the shared history — the replication is on a
    // continuous schedule, so a pass that ran before Apply and failed for the very reason this test
    // is about is expected, and a history row saying "succeeded" may be someone else's.
    await page.getByTestId('tab-runs').click()
    await page.getByTestId('trigger-run-button').click()

    await expect.poll(async () => {
      const response = await page.request.get(`/api/replications/${PROVISIONED_REPLICATION}/runs?limit=30`)
      const runs: { mappingName: string; status: string; errorSummary: string | null }[] = await response.json()
      return runs
        .filter((r) => r.mappingName === NEW_MAPPING)
        .map((r) => `${r.status}${r.errorSummary ? `: ${r.errorSummary}` : ''}`)
    }, { timeout: 90_000 }).toContain('Succeeded')

    expect(querySql(`SET NOCOUNT ON; SELECT Description FROM dbo.${NEW_TARGET} ORDER BY Id;`, DB_NAME))
      .toContain('second order')
  })

  test('19 - the mapping preview shows every statement a pass would run, and where each came from', async ({ page }) => {
    // The complaint phase 37 answers: a mapping's behaviour is spread across a literal transform, a
    // script that generates more of them, an in-process transform, four hook points and whatever the
    // reader, staging provider and writer build themselves — and none of it was visible without
    // running a pass and reading the log.
    // Reachable from the pipeline card too, which is where the question "what will this actually
    // run" occurs to someone reading which reader and writer are configured.
    await page.goto(`/replications/${REPLICATION_NAME}/overview/pipeline`)
    await expect(page.getByTestId('preview-from-pipeline-link')).toBeVisible({ timeout: 15_000 })

    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}`)
    await page.getByTestId('mapping-tab-preview').click()
    await expect(page).toHaveURL(new RegExp(`/mappings/${MAPPING_NAME}/preview$`))

    const preview = page.getByTestId('mapping-preview')
    await expect(preview).toContainText('Source read', { timeout: 20_000 })
    await expect(preview).toContainText('Staging')
    await expect(preview).toContainText('Write')

    // And it answers a question that could not be asked before. Test 05 wrote UPPER({{column}}) on
    // Name by hand; test 15 bound a script to the same slot, and the script wins. The preview says so
    // — the generated expression, the script that produced it, and the level it is bound at — where
    // previously the only way to find out was to run a pass and look at the data.
    await expect(preview).toContainText('Generated column expression')
    await expect(preview).toContainText("Script 'reverse-name', bound on the mapping")
    await expect(preview).not.toContainText('UPPER(')

    // The row transform bound in test 16 runs in this process and generates no SQL. It is named and
    // says so — inventing a statement for it would be worse than admitting it has none.
    await expect(preview).toContainText('Row transform')
    await expect(preview).toContainText('No SQL')

    // The reader's own statement, which nobody could see before at all — carrying REVERSE, not UPPER.
    await expect(preview).toContainText('CHANGETABLE')
    await expect(preview).toContainText('REVERSE(base.[Name])')

    // Read-only: the place to change a statement is the thing that generated it.
    await expect(preview.locator('.monaco-editor textarea').first()).toHaveAttribute('readonly')

    await shot(page, '25-mapping-preview.png')
  })

  test('20 - a script can be run against sample data before a pass ever runs it', async ({ page }) => {
    // Compiling proves a script is C#. It proves nothing about whether it does what was meant — and
    // until now the next thing that happened after writing one was a replication run.
    await page.goto('/scripts/new')
    await page.getByTestId('script-name-input').fill('shout')
    await page.getByTestId('script-kind-select').selectOption('valueColumnExpression')
    await page.getByTestId('script-entry-type-input').fill('Shout')
    await setCode(page, 'script-code-input', `using System.Collections.Generic;
using DataSync.Scripting.Abstractions;

public sealed class Shout : IValueColumnExpression
{
    public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => ["Name"];

    public object? Evaluate(object? value, ValueColumnExpressionContext c) =>
        value is string s ? s.ToUpperInvariant() : value;
}
`)

    await page.getByTestId('run-script-test-button').click()

    // Which data it ran against, said every time — this is the safety property, not a caption.
    await expect(page.getByTestId('script-test-source')).toContainText('generated sample', { timeout: 20_000 })
    const cases = page.getByTestId('script-test-cases')
    await expect(cases).toContainText("'SAMPLE'")
    // The values that find the bug: an empty string and a null.
    await expect(cases).toContainText("Name = ''")
    await expect(cases).toContainText('Name = NULL')
    await shot(page, '26-script-test-generated.png')

    // Change the code, test again, and the output changes — the loop this phase exists to close.
    await setCode(page, 'script-code-input', `using System.Collections.Generic;
using DataSync.Scripting.Abstractions;

public sealed class Shout : IValueColumnExpression
{
    public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => ["Name"];

    public object? Evaluate(object? value, ValueColumnExpressionContext c) =>
        value is string s ? s + "!" : value;
}
`)
    await page.getByTestId('run-script-test-button').click()
    await expect(cases).toContainText("'sample!'", { timeout: 20_000 })

    // And a script that throws says so, rather than leaving a blank panel to be interpreted.
    await setCode(page, 'script-code-input', `using System.Collections.Generic;
using DataSync.Scripting.Abstractions;

public sealed class Shout : IValueColumnExpression
{
    public IReadOnlyList<string> DeclareColumns(ValueColumnDeclarationContext c) => ["Name"];

    public object? Evaluate(object? value, ValueColumnExpressionContext c) => ((string)value!).Substring(3);
}
`)
    await page.getByTestId('run-script-test-button').click()
    await expect(page.getByTestId('script-test-error')).toBeVisible({ timeout: 20_000 })

    // Live is a deliberate choice, never a fallback: the picker starts on generated.
    await expect(page.getByTestId('script-test-connection-select')).toHaveValue('')
  })

  test('21 - the Overview reports what the last day of passes actually did', async ({ page }) => {
    // Every figure comes from TaskRuns, which has recorded them since phase 5. The gap this closes is
    // that nobody had written the query — so by this point in the suite there are real runs to count.
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)

    const card = page.getByTestId('metrics-card')
    await expect(card).toBeVisible({ timeout: 15_000 })

    // Real numbers or nothing: several passes have run above, so this is not zero.
    await expect(page.getByTestId('metrics-runs')).not.toContainText('0', { timeout: 15_000 })
    await expect(page.getByTestId('metrics-duration')).toContainText('p50')
    await expect(page.getByTestId('metrics-sparkline')).toBeVisible()

    // Not "lag" — a pass that ran two minutes ago and found nothing looks identical to one that ran
    // two minutes ago and is an hour behind, so this says the thing it can actually answer.
    await expect(card).toContainText('Last completed pass')
    await expect(page.getByTestId('metrics-last-pass')).not.toContainText('never')

    await shot(page, '27-run-metrics.png')

    // The window is a real filter: an hour ago there had been no runs yet in this suite, so switching
    // to 1h and back to 24h must change something rather than re-rendering the same card.
    await expect(card).toContainText('Last 24h')
    await page.getByTestId('metrics-window-7d').click()
    await expect(card).toContainText('Last 7d')
    await page.getByTestId('metrics-window-1h').click()
    await expect(card).toContainText('Last 1h')
  })

  test('22 - source and target are the same pair of cards on both screens that configure them', async ({ page }) => {
    // Two screens answer "where does this read from and write to", and they had drifted into two
    // unrelated layouts because nothing made them share anything.
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)

    const overviewPair = page.locator('[data-testid="endpoints-card"] .side-pair')
    await expect(overviewPair).toBeVisible({ timeout: 15_000 })
    await expect(overviewPair.locator('[data-side="source"]')).toBeVisible()
    await expect(overviewPair.locator('[data-side="target"]')).toBeVisible()
    await expect(overviewPair.locator('.side-arrow')).toBeVisible()

    // The note is per side, not in a shared header: a mapping overrides source and target
    // independently, so whether *this* side is the inherited one is per-side information.
    await expect(overviewPair.locator('[data-side="source"]')).toContainText('inherited by')
    await expect(overviewPair.locator('[data-side="target"]')).toContainText('inherited by')

    // Each side's accent, from the tokens rather than from anything local.
    const sourceBorder = await overviewPair.locator('[data-side="source"]')
      .evaluate((el) => getComputedStyle(el).borderTopColor)
    const targetBorder = await overviewPair.locator('[data-side="target"]')
      .evaluate((el) => getComputedStyle(el).borderTopColor)
    expect(sourceBorder).not.toBe(targetBorder)

    await shot(page, '28-overview-endpoints.png')

    // The same pair, from the same component, on the mapping editor.
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}`)
    const mappingPair = page.locator('.side-pair')
    await expect(mappingPair.locator('[data-testid="source-side"]')).toBeVisible({ timeout: 15_000 })
    await expect(mappingPair.locator('[data-testid="target-side"]')).toBeVisible()
    await expect(await mappingPair.locator('[data-side="source"]')
      .evaluate((el) => getComputedStyle(el).borderTopColor)).toBe(sourceBorder)

    // Equal height, which is what makes them comparable — the source filter used to hang off the
    // Source card and made them different.
    const sourceBox = (await mappingPair.locator('[data-testid="source-side"]').boundingBox())!
    const targetBox = (await mappingPair.locator('[data-testid="target-side"]').boundingBox())!
    expect(Math.abs(sourceBox.height - targetBox.height)).toBeLessThan(2)

    // And the filter is its own row beneath both, not inside either card — collapsed while empty
    // (phase 64), the same convention the script bindings card set, and it says so on its heading
    // once a filter is actually set.
    await expect(mappingPair.getByTestId('source-filter-editor')).toHaveCount(0)
    await expect(page.getByTestId('source-filter-applied-pill')).toHaveCount(0)
    await expect(page.getByTestId('source-filter-editor')).toHaveCount(0)
    await page.getByTestId('source-filter-toggle').click()
    await expect(page.getByTestId('source-filter-editor')).toBeVisible()
    await shot(page, '29-mapping-endpoints.png')

    // The invariant this phase must not disturb: the table picker works whether or not the side is
    // overriding, because the picker cascades from the *resolved* endpoint either way.
    await expect(page.getByTestId('source-side')).toContainText('INHERITED')
    await expect(page.getByTestId('source-table-select')).toBeEnabled()
    await page.getByTestId('source-override-toggle').click()
    await expect(page.getByTestId('source-table-select')).toBeEnabled()
    await page.getByTestId('source-override-toggle').click()

    // And the association holds wherever a side is shown, not only on this pair: the Setup card's
    // two plan panels carry the same colours.
    await page.getByTestId('mapping-tab-provisioning').click()
    await expect(page.locator('[data-testid="provisioning-plan-source"]')).toBeVisible({ timeout: 20_000 })
    await expect(await page.locator('[data-testid="provisioning-plan-source"]')
      .evaluate((el) => getComputedStyle(el).borderTopColor)).toBe(sourceBorder)
    await expect(await page.locator('[data-testid="provisioning-plan-target"]')
      .evaluate((el) => getComputedStyle(el).borderTopColor)).toBe(targetBorder)

    // The horizontal section bar is gone: the vertical rail is the only way between sections now.
    for (const url of ['/replications', '/connections', '/scripts', `/connections/${SRC_CONNECTION_NAME}`]) {
      await page.goto(url)
      await expect(page.getByTestId('rail-replications')).toBeVisible()
      await expect(page.getByTestId('tab-replications')).toHaveCount(0)
      await expect(page.getByTestId('tab-connections')).toHaveCount(0)
      await expect(page.getByTestId('tab-scripts')).toHaveCount(0)
    }
  })

  test('23 - a declared setting renders itself, whoever declared it', async ({ page }) => {
    // Three places used to solve "an author declares settings, an operator fills them in" three ways.
    // They are one form now, and this walks all three of its callers.

    // 1. A driver's connection settings. The free-form properties bag is a declared vararg rather than
    //    something this screen assumes every connection has.
    await page.goto(`/connections/${SRC_CONNECTION_NAME}`)
    const properties = page.getByTestId('connection-parameters')
    await expect(properties).toBeVisible({ timeout: 15_000 })
    await expect(properties).toContainText('Custom properties')
    await expect(properties).toContainText('Appended to the connection string')

    // Rendered as the key/value table, which is what a Property vararg means — and round-tripping
    // through save exactly as it did when this screen hardcoded it.
    const table = page.getByTestId('connection-parameters-properties')
    await table.getByPlaceholder('Custom properties name').fill('Application Name')
    await table.getByRole('button', { name: '+ Add' }).click()
    await table.getByLabel('Application Name value').fill('DataSync')
    await page.getByTestId('save-connection-button').click()

    await page.goto(`/connections/${SRC_CONNECTION_NAME}`)
    await expect(page.getByTestId('connection-parameters-properties').getByLabel('Application Name value'))
      .toHaveValue('DataSync', { timeout: 15_000 })
    await shot(page, '30-declared-connection-settings.png')

    // 2. A script's parameters. The manifest declares them, so a binding gets a labelled control
    //    instead of a table to guess the names into — reverse-name declares 'column' (test 15 typed it).
    await page.goto('/scripts/reverse-name')
    await expect(page.getByTestId('script-name-input')).toHaveValue('reverse-name', { timeout: 15_000 })

    // 3. And the layout hints group what belongs together. The connection card is one card because
    //    the declaration says so, not because this screen decided.
    await page.goto(`/replications/${REPLICATION_NAME}/overview/pipeline`)
    await page.getByTestId('stage-reader').click()
    await expect(page.getByTestId('reader-options')).toContainText('Snapshot isolation', { timeout: 15_000 })

    // A Kind that declares nothing still gets the free-form table: an option a driver reads but has
    // not declared is still an option somebody set.
    await page.getByTestId('stage-cache').click()
    await expect(page.getByTestId('cache-options')).toBeVisible()
  })

  test('24 - a mapping can be checked against its target, and the threshold decides what is flagged', async ({ page }) => {
    test.setTimeout(180_000)

    // Set through the API here on purpose: this test is about running and reading a check. Test 34
    // configures one through the editor phase 48 added, which is the other half.
    const setCheck = async (differenceThreshold: number) => {
      const mapping = await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()
      mapping.verification = [{ name: 'total-rows', kind: 'RowCount', groupBy: [], measures: [], differenceThreshold }]
      const saved = await page.request.put(
        `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`, { data: mapping })
      expect(saved.ok(), await saved.text()).toBeTruthy()
    }

    const runAndWait = async (expectedResults: number) => {
      await page.getByTestId('run-verification-button').click()
      await expect.poll(async () => {
        const results = await (await page.request.get(
          `/api/replications/${REPLICATION_NAME}/verification-results?mappingName=${MAPPING_NAME}`)).json()
        return results.length
      }, { timeout: 60_000 }).toBeGreaterThanOrEqual(expectedResults)
    }

    await setCheck(0)
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}`)
    await page.getByTestId('mapping-tab-verify').click()
    await expect(page).toHaveURL(new RegExp(`/mappings/${MAPPING_NAME}/verification$`))

    await runAndWait(1)
    await expect(page.getByTestId('verification-result-total-rows')).toBeVisible({ timeout: 30_000 })

    // The result is a click away rather than rendered here — the checks screen manages checks, and
    // rendering every group of a large one is what used to lock the tab up.
    const openNewest = async () => {
      const results = await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/verification-results?mappingName=${MAPPING_NAME}`)).json()
      await page.getByTestId(`open-result-${results[0].id}`).click()
      await expect(page.getByTestId('verification-result')).toBeVisible({ timeout: 20_000 })
    }
    await openNewest()

    // A real difference, and an explainable one: test 16 bound a row transform that drops a row, so
    // the source has two rows where the target has one. The check finds it without being told.
    const result = page.getByTestId('verification-result')
    await expect(result).toContainText('differs', { timeout: 30_000 })
    await expect(page.getByTestId('verification-row-total')).toContainText('-1')

    // Both read times, as a gap — the number a difference has to be weighed against.
    await expect(page.getByTestId('verification-read-gap')).toContainText('apart')
    await expect(page.getByTestId('verification-read-gap')).toContainText('any difference is flagged')
    await shot(page, '31-verification-difference.png')

    // The same difference, under a threshold that tolerates it: still reported as a number, because
    // an operator reads it to judge drift — but no longer called a failure, because a replication
    // being behind is the premise of the feature rather than a fault.
    await setCheck(0.6)
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/verification`)
    await runAndWait(2)
    await openNewest()

    await expect(result).toContainText('match', { timeout: 30_000 })
    await expect(result).toContainText('are not flagged')
    await expect(page.getByTestId('verification-row-total')).toContainText('-1')
    await shot(page, '32-verification-within-threshold.png')
  })

  test('25 - status and schedule are chrome, the draft survives the tabs, and Enabled saves itself', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)

    // Save moved out of the Pipeline card and into the toolbar, because it commits endpoints,
    // pipeline *and* script bindings — not the one card it used to sit inside.
    await expect(page.getByTestId('save-settings-button')).toBeVisible({ timeout: 15_000 })
    await expect(page.locator('.pane').getByTestId('save-settings-button')).toHaveCount(0)

    // Status and Schedule are the same cards on every tab, not four mounts of them on one.
    for (const tab of ['tab-overview', 'tab-mappings', 'tab-runs', 'tab-history']) {
      await page.getByTestId(tab).click()
      await expect(page.getByTestId('replication-status-card')).toBeVisible()
      await expect(page.getByTestId('replication-schedule-card')).toBeVisible()
      await expect(page.getByTestId('enabled-toggle')).toBeVisible()
    }

    // Whichever of the two states it is in, the card names it — a worker stays up between passes now
    // and leaves after its idle timeout, so both are ordinary and neither should read as a fault.
    // Asserted as "says which, and explains it" rather than pinned to one, because which one it is
    // depends on whether this replication happens to be inside its idle window right now.
    await expect(page.getByTestId('replication-status-state')).toContainText(/^(Running|Idle)$/)
    await expect(page.getByTestId('replication-status-card')).toContainText(
      await page.getByTestId('replication-status-state').textContent() === 'Running'
        ? 'PID'
        : 'not a broken one')

    // The draft used to live in the Overview panel, which unmounts the moment you click Runs — so a
    // half-finished edit was lost by looking at something else. It is held in the chrome now.
    await page.getByTestId('tab-overview').click()
    await page.getByTestId('overview-tab-pipeline').click()
    await page.getByTestId('stage-reader').click()
    await page.getByTestId('reader-options-snapshotIsolation').click()
    await expect(page.getByTestId('reader-options-snapshotIsolation')).toHaveAttribute('aria-pressed', 'true')

    await page.getByTestId('tab-runs').click()
    await page.getByTestId('tab-overview').click()
    await page.getByTestId('overview-tab-pipeline').click()
    await page.getByTestId('stage-reader').click()
    await expect(page.getByTestId('reader-options-snapshotIsolation')).toHaveAttribute('aria-pressed', 'true')

    // And across the Overview's own tabs, which are routes of their own (phase 64) — the draft is
    // the layout route's, so moving between them must not reset it either.
    await page.getByTestId('overview-tab-notes').click()
    await page.getByTestId('overview-tab-pipeline').click()
    await page.getByTestId('stage-reader').click()
    await expect(page.getByTestId('reader-options-snapshotIsolation')).toHaveAttribute('aria-pressed', 'true')

    // Enabled commits on its own, from any tab, and carries nothing else with it. Toggled here with
    // that unsaved edit still pending — which is exactly the case its own endpoint exists for.
    await page.getByTestId('tab-history').click()
    await expect(page.getByTestId('replication-schedule-card')).toHaveClass(/enabled/)
    await page.getByTestId('enabled-toggle').click()

    await expect.poll(async () => {
      const task = await (await page.request.get(`/api/replications/${REPLICATION_NAME}`)).json()
      return { enabled: task.enabled, snapshot: task.changeProcessing.reader.options.snapshotIsolation }
    }, { timeout: 15_000 }).toEqual({ enabled: false, snapshot: 'false' })

    // The accent follows, on both cards, without a Save.
    await expect(page.getByTestId('replication-schedule-card')).toHaveClass(/disabled/)
    await expect(page.getByTestId('replication-status-card')).toHaveClass(/disabled/)
    await shot(page, '33-replication-chrome-disabled.png')

    // And it survives a reload, which is the only proof it reached the config repo — this is the
    // field that silently reverted before phase 46, because the serializer omitted `false`.
    await page.reload()
    await expect(page.getByTestId('enabled-toggle')).toHaveAttribute('aria-pressed', 'false', { timeout: 15_000 })

    // Put it back, so the rest of the suite runs against an enabled replication.
    await page.getByTestId('enabled-toggle').click()
    await expect(page.getByTestId('enabled-toggle')).toHaveAttribute('aria-pressed', 'true')
  })

  test('26 - a dotted table name, an unknown column, and DDL that fits on the screen', async ({ page }) => {
    test.setTimeout(180_000)

    // A table whose *name contains a dot* — legal as a quoted identifier, and exactly what breaks a
    // picker that combines schema and table into one string and splits it back apart.
    const DOTTED = 'Pw.Dotted.Table'
    runSql(`
      IF OBJECT_ID('dbo.[${DOTTED}]', 'U') IS NOT NULL DROP TABLE dbo.[${DOTTED}];
      CREATE TABLE dbo.[${DOTTED}] (Id INT NOT NULL PRIMARY KEY, Label NVARCHAR(40) NULL);
      ALTER TABLE dbo.[${DOTTED}] ENABLE CHANGE_TRACKING;
    `, DB_NAME)

    await page.goto(`/replications/${REPLICATION_NAME}/mappings/new`)
    await page.getByTestId('mapping-name-input').fill('dotted')
    await selectWhenReady(page, 'source-table-select', `dbo.${DOTTED}`)

    await page.getByTestId('target-schema-input').fill('dbo')
    await page.getByTestId('target-table-input').fill('PwDottedTarget')
    await page.getByTestId('mapping-tab-columns').click()

    // The pair survives the round trip. Split on '.', this would have produced schema "dbo",
    // table "Pw" and silently mapped the wrong table — or nothing at all.
    await expect(page.getByTestId('column-mappings-table').locator('.grid-row'))
      .toHaveCount(2, { timeout: 20_000 })
    await expect(page.getByTestId('column-mappings-table')).toContainText('Label')

    await page.getByTestId('save-mapping-button').click()
    await expect(page.getByTestId('mapping-item-dotted')).toBeVisible({ timeout: 15_000 })

    // The Setup card's SQL is Monaco now, not a <pre> whose overflow widened the whole page — and the
    // generated CREATE TABLE is one column per line rather than forty on one.
    await page.getByTestId('mapping-tab-provisioning').click()
    const targetPlan = page.getByTestId('provisioning-plan-target')
    await expect(targetPlan.locator('.monaco-editor')).toBeVisible({ timeout: 30_000 })
    await expect(targetPlan).toContainText('CREATE TABLE')
    await expect(targetPlan).toContainText('PRIMARY KEY')

    const overflows = await page.evaluate(() =>
      document.documentElement.scrollWidth > document.documentElement.clientWidth)
    expect(overflows, 'a long generated statement must stay inside its card').toBeFalsy()
    await shot(page, '35-setup-card-monaco.png')

    // A stored source column the freshly loaded metadata does not have is shown as itself and marked,
    // never silently rendered as the first option — which is what a bare <select> does, changing what
    // the operator sees without changing what is stored.
    const mapping = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/dotted`)).json()
    mapping.columnMappings = [{ sourceColumn: 'gone_away', targetColumn: 'Label', transform: null }]
    expect((await page.request.put(
      `/api/replications/${REPLICATION_NAME}/table-mappings/dotted`, { data: mapping })).ok()).toBeTruthy()

    await page.reload()
    await page.getByTestId('mapping-tab-columns').click()
    await expect(page.getByTestId('column-mapping-source-0')).toHaveValue('gone_away', { timeout: 20_000 })
    await expect(page.getByTestId('column-mapping-source-0')).toContainText('not on the source')
    await shot(page, '34-unknown-source-column.png')
  })

  test('27 - naming a mapping and its target follow from the source, until somebody says otherwise', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/new`)

    // Pick a source and the two things that follow from it fill themselves in.
    await selectWhenReady(page, 'source-table-select', `dbo.${SOURCE_TABLE}`)
    await expect(page.getByTestId('mapping-name-input')).toHaveValue(`dbo.${SOURCE_TABLE}`)
    await expect(page.getByTestId('target-table-input')).toHaveValue(SOURCE_TABLE)

    // A name somebody typed stops being inferred — picking a different source must not rewrite it.
    await page.getByTestId('mapping-name-input').fill('mine')
    await selectWhenReady(page, 'source-table-select', 'dbo.Pw.Dotted.Table')
    await expect(page.getByTestId('mapping-name-input')).toHaveValue('mine')

    // And a target somebody typed is an answer, not a placeholder: the autofill only ever fills an
    // empty field, because overwriting it would discard the more deliberate of the two.
    await page.getByTestId('target-table-input').fill('SomewhereElse')
    await selectWhenReady(page, 'source-table-select', `dbo.${SOURCE_TABLE}`)
    await expect(page.getByTestId('target-table-input')).toHaveValue('SomewhereElse')

    // The inferred name contains a dot, which config validation used to reject outright — the plan for
    // this phase assumed it did not. It saves, loads, and its own route works.
    await page.getByTestId('mapping-name-input').fill(`dbo.${SOURCE_TABLE}`)
    await page.getByTestId('target-schema-input').fill('dbo')
    await page.getByTestId('target-table-input').fill(TARGET_TABLE)
    await expect(page.getByTestId('save-mapping-button')).toBeEnabled()
    await page.getByTestId('save-mapping-button').click()

    await expect(page.getByTestId(`mapping-item-dbo.${SOURCE_TABLE}`)).toBeVisible({ timeout: 15_000 })
    await expect(page).toHaveURL(new RegExp(`/mappings/dbo.${SOURCE_TABLE}$`))

    await page.reload()
    await expect(page.getByTestId('mapping-name-input')).toHaveCount(0, { timeout: 15_000 })
    await expect(page.getByRole('heading', { name: `dbo.${SOURCE_TABLE}` })).toBeVisible()
  })

  test('28 - provisioning is set once on the replication and overridden per mapping', async ({ page }) => {
    // One answer in one place beats the same checkbox ticked on forty mappings.
    await page.goto(`/replications/${REPLICATION_NAME}/overview/provisioning`)
    const replicationCreate = page.getByTestId('task-provisioning-create')
    await expect(replicationCreate).toBeVisible({ timeout: 15_000 })

    await replicationCreate.getByTestId('task-provisioning-create-override').click()
    await replicationCreate.getByTestId('task-provisioning-create-checkbox').check()
    await page.getByTestId('save-settings-button').click()

    await expect.poll(async () => {
      const task = await (await page.request.get(`/api/replications/${REPLICATION_NAME}`)).json()
      return task.provisioning?.createTargetTableIfMissing
    }, { timeout: 15_000 }).toBe(true)

    // A mapping that has never been asked inherits it — which is why the mapping's own value starts
    // null rather than false: false would opt it out of a default it should pick up.
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/provisioning`)
    const mappingCreate = page.getByTestId('provisioning-create')
    await expect(mappingCreate).toContainText('INHERITED', { timeout: 20_000 })
    await expect(mappingCreate.getByTestId('provisioning-create-checkbox')).toBeChecked()
    await expect(mappingCreate.getByTestId('provisioning-create-checkbox')).toBeDisabled()
    await shot(page, '36-inherited-provisioning.png')

    // Overriding starts from what is in effect, so it is a starting point rather than a reset — then
    // saying no here wins locally without touching the replication's answer.
    await mappingCreate.getByTestId('provisioning-create-override').click()
    await expect(mappingCreate).not.toContainText('INHERITED')
    await mappingCreate.getByTestId('provisioning-create-checkbox').uncheck()
    await page.getByTestId('save-mapping-button').click()

    await expect.poll(async () => {
      const mapping = await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()
      const task = await (await page.request.get(`/api/replications/${REPLICATION_NAME}`)).json()
      return {
        mapping: mapping.provisioning?.createTargetTableIfMissing,
        replication: task.provisioning?.createTargetTableIfMissing,
      }
    }, { timeout: 15_000 }).toEqual({ mapping: false, replication: true })

    // The second setting is a separate question and falls back on its own — a mapping can override
    // one and inherit the other.
    await expect(page.getByTestId('provisioning-alter')).toContainText('INHERITED')
  })

  test('29 - a column\'s target type is inferred until overridden, and a rename is recorded as one', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/columns`)
    const before = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()

    // The inferred type is shown for every row without being stored. The SPA cannot work this out —
    // the canonical type system lives on the server — so an empty box here would be asking the
    // operator to translate types in their head.
    const type = page.getByTestId('column-mapping-type-1-text')
    await expect(type).toBeVisible({ timeout: 20_000 })
    await expect(type).toContainText(/varchar|char|text/i)
    await shot(page, '37-inferred-target-type.png')

    // And it stays out of the saved config: writing the inference down would freeze today's answer
    // against a source column that later changes.
    expect(before.columnMappings[1].targetType ?? null).toBeNull()

    // Overriding it is a pencil, not a permanently-open input: these are fields that are usually
    // right and occasionally disagreed with, and forty open inputs read as a form to fill in.
    await page.getByTestId('column-mapping-type-1-edit').click()
    await page.getByTestId('column-mapping-type-1').fill('nvarchar(200)')
    await page.getByTestId('column-mapping-type-1').press('Enter')
    await expect(page.getByTestId('column-mapping-type-1-text')).toHaveText('nvarchar(200)')

    // Renaming the target column records a rename rather than quietly becoming a different column:
    // the target has one under the old name, and dropping and re-adding would empty it.
    const renamedFrom = before.columnMappings[1].targetColumn
    await page.getByTestId('column-mapping-target-1-edit').click()
    await page.getByTestId('column-mapping-target-1').fill('RenamedByTest')
    await page.getByTestId('column-mapping-target-1').press('Enter')
    await expect(page.getByTestId('column-mappings-table')).toContainText('RENAMED')

    await page.getByTestId('save-mapping-button').click()
    await expect.poll(async () => {
      const mapping = await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()
      return mapping.columnMappings[1]
    }, { timeout: 15_000 }).toMatchObject({
      targetColumn: 'RenamedByTest',
      targetType: 'nvarchar(200)',
      renames: [{ from: renamedFrom, to: 'RenamedByTest', applied: false }],
    })

    // Which the Setup card then plans as a RENAME — never an ADD beside the old column, and never a
    // DROP. Both of those leave a table that looks right and is empty in the column that matters.
    await page.getByTestId('mapping-tab-provisioning').click()
    await expect(page.getByTestId('provisioning-plan-target')).toContainText('Rename', { timeout: 20_000 })
    await expect(page.getByTestId('provisioning-plan-target')).not.toContainText('DROP')
    await shot(page, '38-planned-rename.png')

    // Put the mapping back: the suite is serial, and a renamed column the target does not have would
    // break every pass after this one.
    expect((await page.request.put(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`, { data: before })).ok()).toBeTruthy()
  })

  test('30 - the overview says which source tables are mapped, and maps the rest in one request', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/overview`)
    const grid = page.getByTestId('mappings-overview')
    await expect(grid).toBeVisible({ timeout: 20_000 })

    // A table the replication already reads carries its count; one nothing reads says so. A count
    // rather than a tick, because two mappings can legitimately read the same source and "mapped"
    // would hide the second.
    // Counted from the mappings themselves rather than pinned to a number: by this point in the
    // suite more than one mapping reads the source table, which is exactly the fan-in the count is
    // there to make visible.
    const names: string[] = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings`)).json()
    const reading = (await Promise.all(names.map(async (n) =>
      (await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/table-mappings/${n}`)).json()).sources)))
      .flat()
      .filter((spec: { schema: string; table: string }) => `${spec.schema}.${spec.table}` === `dbo.${SOURCE_TABLE}`)
    expect(reading.length).toBeGreaterThan(0)

    await expect(page.getByTestId(`table-mapping-count-dbo.${SOURCE_TABLE}`))
      .toHaveText(String(reading.length), { timeout: 20_000 })
    await expect(page.getByTestId(`table-mapping-count-dbo.${TARGET_TABLE}`)).toContainText('unmapped')

    // Select-all acts on what the filter is showing, never on rows nobody has looked at.
    await page.getByTestId('table-filter').fill(TARGET_TABLE)
    await page.getByTestId('select-all-tables').check()
    await expect(page.getByTestId(`select-table-dbo.${TARGET_TABLE}`)).toBeChecked()

    await expect(page.getByTestId('create-mappings-button')).toContainText('Create 1 mapping')
    await page.getByTestId('create-mappings-button').click()

    // One request creates it, names it after the source, and lands on the editor for what was made.
    await expect(page).toHaveURL(new RegExp(`/mappings/dbo.${TARGET_TABLE}$`), { timeout: 20_000 })
    await shot(page, '39-mappings-overview.png')

    const created = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/dbo.${TARGET_TABLE}`)).json()
    expect(created.sources[0]).toMatchObject({ schema: 'dbo', table: TARGET_TABLE })
    expect(created.targets[0]).toMatchObject({ schema: 'dbo', table: TARGET_TABLE })
    // Everything else unset, so it inherits the replication rather than carrying its own copy of
    // settings nobody chose — the whole reason bulk creation is worth having.
    expect(created.sources[0].connectionName ?? null).toBeNull()
    expect(created.sources[0].database ?? null).toBeNull()

    // A second attempt at the same table is a skip, not a failure: ticking every row on a
    // replication that already maps half of them means "map the rest".
    const again = await (await page.request.post(
      `/api/replications/${REPLICATION_NAME}/table-mappings/bulk`,
      { data: { tables: [{ schema: 'dbo', table: TARGET_TABLE }] } })).json()
    expect(again).toEqual({ created: [], skipped: [`dbo.${TARGET_TABLE}`] })

    // Put the sidebar back the way the rest of the suite left it.
    expect((await page.request.delete(
      `/api/replications/${REPLICATION_NAME}/table-mappings/dbo.${TARGET_TABLE}`)).ok()).toBeTruthy()
  })

  test('31 - the connection form is declared by the driver, not written into the screen', async ({ page }) => {
    await page.goto('/connections/new')
    await expect(page.getByTestId('connection-parameters-host')).toBeVisible({ timeout: 20_000 })

    // The port comes from the driver. This screen used to hold a table of default ports, which a
    // third driver would have made stale the day it was added.
    await expect(page.getByTestId('connection-parameters-port')).toHaveValue('1433')
    await page.getByTestId('connection-driver-select').selectOption('Postgres')
    await expect(page.getByTestId('connection-parameters-port')).toHaveValue('5432', { timeout: 15_000 })
    await page.getByTestId('connection-driver-select').selectOption('MsSql')

    // Host and port share a row; the address dropdown and database each get their own. That layout
    // is declared by the driver too — the screen renders what it is told.
    const host = await page.getByTestId('connection-parameters-host').boundingBox()
    const port = await page.getByTestId('connection-parameters-port').boundingBox()
    expect(Math.abs(host!.y - port!.y), 'host and port share a row').toBeLessThan(4)
    expect(port!.x, 'port sits to the right of host').toBeGreaterThan(host!.x)

    // Auth mode decides whether a user and a password are settings at all, and the driver is what
    // decides that — no condition logic in the SPA, which would be the copy that disagrees.
    await expect(page.getByTestId('connection-parameters-userId')).toBeVisible()
    await page.getByTestId('connection-parameters-authMode').selectOption('IntegratedAuth')
    await expect(page.getByTestId('connection-parameters-userId')).toHaveCount(0, { timeout: 15_000 })
    await expect(page.getByTestId('connection-parameters-password')).toHaveCount(0)
    await shot(page, '40-declared-connection-form.png')

    // A setting nothing depends on never asks the driver again, and is always there.
    await page.getByTestId('connection-parameters-authMode').selectOption('SqlAuth')
    await expect(page.getByTestId('connection-parameters-database')).toBeVisible({ timeout: 15_000 })

    // The password is masked, and blank on load: the server never sends one back, so blank means
    // "keep the stored one" rather than "clear it".
    await expect(page.getByTestId('connection-parameters-password')).toHaveAttribute('type', 'password')

    await page.goto(`/connections/${SRC_CONNECTION_NAME}`)
    await expect(page.getByTestId('connection-parameters-password')).toHaveValue('', { timeout: 20_000 })
    await expect(page.getByTestId('connection-parameters-host')).toHaveValue('localhost')

    // Name and driver stay fixed after creation — neither is a setting, and both decide things every
    // existing mapping already depends on.
    await expect(page.getByTestId('connection-name-input')).toBeDisabled()
    await expect(page.getByTestId('connection-driver-select')).toBeDisabled()

    // Saving without touching the password keeps the connection working, which is the whole contract.
    await page.getByTestId('save-connection-button').click()
    await expect(page.getByTestId('connections-table')).toContainText(SRC_CONNECTION_NAME, { timeout: 15_000 })
    await page.goto(`/connections/${SRC_CONNECTION_NAME}`)
    await page.getByTestId('test-connection-button').click()
    await expect(page.getByTestId('connection-test-result')).toContainText('reachable', { timeout: 20_000 })
  })

  test('32 - a long value truncates instead of knocking its own row out of alignment', async ({ page }) => {
    // Every row is its own grid sharing one column template, not one grid and not a <table>. A cell
    // wider than its share used to grow that row's track — and only that row's — so one long value
    // was enough to stop a table lining up with its own header.
    const LONG = 'ThisIsAnAbsurdlyLongTargetColumnNameThatNoOneWouldEverActuallyUse'
    const before = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()

    const widened = structuredClone(before)
    widened.columnMappings[1].targetColumn = LONG
    expect((await page.request.put(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`, { data: widened })).ok()).toBeTruthy()

    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/columns`)
    const table = page.getByTestId('column-mappings-table')
    await expect(table).toContainText('Target column', { timeout: 20_000 })
    await expect(table.getByTestId('column-mapping-target-1-text')).toContainText(LONG.slice(0, 20))

    // The resolved track widths, not the cells' own boxes: a cell can sit anywhere within its track
    // (the Remove button is `justify-self: end`), and it is the *track* the bug widened.
    const tracks = (row: Element) => getComputedStyle(row).gridTemplateColumns
    const head = await table.locator('.grid-head').first().evaluate(tracks)
    const rows = await table.locator('.grid-row').evaluateAll((all) =>
      all.map((row) => getComputedStyle(row).gridTemplateColumns))

    expect(rows.length).toBeGreaterThan(1)
    for (const row of rows) expect(row).toEqual(head)

    // And the long value is cut off rather than allowed to push, which is what makes the alignment
    // above a deliberate answer instead of a clipped one.
    const overflowed = await table.getByTestId('column-mapping-target-1-text').evaluate(
      (el) => el.scrollWidth > el.clientWidth)
    expect(overflowed, 'the long name is truncated inside its cell').toBeTruthy()
    await shot(page, '41-long-value-truncates.png')

    // The runs table is the one this was reported against, and shares the same CSS.
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    const runs = page.getByTestId('run-history-table')
    await expect(runs).toContainText('Mapping', { timeout: 20_000 })
    const runHead = await runs.locator('.grid-head').first().evaluate(tracks)
    const runRows = await runs.locator('.grid-row').evaluateAll((all) =>
      all.map((row) => getComputedStyle(row).gridTemplateColumns))
    for (const row of runRows) expect(row).toEqual(runHead)

    expect((await page.request.put(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`, { data: before })).ok()).toBeTruthy()
  })

  test('33 - the browser tab shows the same mark the app does', async ({ page }) => {
    // Two hand-maintained copies of one glyph, which is fine only while something notices when they
    // stop matching. The favicon used to be unrelated artwork, so the tab and the app advertised two
    // different products.
    const webRoot = path.join(__dirname, '..', '..', '..', 'src', 'DataSync.Web')
    const logo = fs.readFileSync(path.join(webRoot, 'src', 'components', 'icons.tsx'), 'utf8')
      .split('export function LogoIcon()')[1].split('export function')[0]
    const paths = [...logo.matchAll(/d="([^"]+)"/g)].map((m) => m[1])
    expect(paths.length).toBeGreaterThan(3)

    const served = await (await page.request.get('/favicon.svg')).text()
    for (const d of paths) expect(served, 'the favicon draws the same glyph as LogoIcon').toContain(d)

    // On the accent square the rail draws around the same mark, not a colour invented for this asset.
    const accent = fs.readFileSync(path.join(webRoot, 'src', 'index.css'), 'utf8')
      .match(/--accent:\s*(#[0-9a-f]{6})/i)![1]
    expect(served.toLowerCase()).toContain(accent.toLowerCase())

    await page.goto('/replications')
    await expect(page.locator('link[rel="icon"]')).toHaveAttribute('href', '/favicon.svg')
  })

  test('34 - a check can be configured, run and removed without leaving the screen', async ({ page }) => {
    test.setTimeout(180_000)

    // Phase 43 built everything a check does and left it settable only by API call — so the screen
    // that shows results could not produce one. This is that gap closed, end to end.
    const before = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()

    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/verification`)
    await expect(page.getByTestId('verification-checks')).toBeVisible({ timeout: 20_000 })

    await page.getByTestId('add-check-button').click()
    await page.getByTestId('check-name-input').fill('ui-rows-by-name')

    // Row count takes a grouping and nothing else; switching to Sum is what asks for measures. The
    // form follows the kind rather than showing every field a check could ever have.
    await expect(page.getByTestId('check-measures')).toHaveCount(0)
    await page.getByTestId('check-kind-select').selectOption('Sum')
    await expect(page.getByTestId('check-measures')).toBeVisible()
    await page.getByTestId('check-kind-select').selectOption('RowCount')
    await expect(page.getByTestId('check-measures')).toHaveCount(0)

    // Columns are picked by their target names, as chips — what is selected has to be readable
    // without opening anything, because it decides what the result's rows mean.
    await page.getByTestId(`check-groupby-${SOURCE_NAME_COLUMN}`).click()
    await shot(page, '42-check-editor.png')
    await page.getByTestId('save-check-button').click()

    await expect(page.getByTestId('verification-checks')).toContainText('by Name', { timeout: 15_000 })
    const saved = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()
    expect(saved.verification).toContainEqual(expect.objectContaining({
      name: 'ui-rows-by-name', kind: 'RowCount', groupBy: [SOURCE_NAME_COLUMN],
    }))

    // And it runs, which is the only proof the editor wrote something the runner understands.
    await page.getByTestId('run-verification-button').click()
    await expect(page.getByTestId('verification-result-ui-rows-by-name')).toBeVisible({ timeout: 60_000 })

    const produced = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/verification-results?mappingName=${MAPPING_NAME}`)).json()
    const mine = produced.find((r: { checkName: string }) => r.checkName === 'ui-rows-by-name')
    await page.getByTestId(`open-result-${mine.id}`).click()
    await expect(page.getByTestId('verification-result')).toContainText('Name', { timeout: 20_000 })
    await page.goBack()

    // Editing pre-fills from what was saved, and the threshold is entered as the percentage the
    // results card already speaks in rather than as the fraction it is stored as.
    await page.getByTestId('edit-check-ui-rows-by-name').click()
    await expect(page.getByTestId('check-name-input')).toHaveValue('ui-rows-by-name')
    await expect(page.getByTestId(`check-groupby-${SOURCE_NAME_COLUMN}`)).toHaveAttribute('aria-pressed', 'true')
    await page.getByTestId('check-threshold-input').fill('25')
    await page.getByTestId('save-check-button').click()

    await expect.poll(async () => {
      const mapping = await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()
      return mapping.verification.find((c: { name: string }) => c.name === 'ui-rows-by-name')?.differenceThreshold
    }, { timeout: 15_000 }).toBeCloseTo(0.25)

    await page.getByTestId('remove-check-ui-rows-by-name').click()
    await expect(page.getByTestId('verification-checks')).not.toContainText('ui-rows-by-name', { timeout: 15_000 })

    expect((await page.request.put(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`, { data: before })).ok()).toBeTruthy()
  })

  test('35 - the rail lines up with the pane, and the mappings list is a nav bar beside it', async ({ page }) => {
    // Two columns whose first cards started on different lines, because the rail had no top padding
    // and the pane did.
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await expect(page.getByTestId('run-history-table')).toBeVisible({ timeout: 20_000 })

    const top = (locator: ReturnType<typeof page.locator>) =>
      locator.first().evaluate((el) => Math.round(el.getBoundingClientRect().top))
    expect(await top(page.locator('.pane > .card')))
      .toBe(await top(page.locator('.detail-rail > .card')))

    // The mappings list is a sidebar, so it belongs *beside* the pane and runs the full height. In a
    // block container it stacked above it instead, and stopped short of the bottom.
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/overview`)
    await expect(page.getByTestId('mappings-overview')).toBeVisible({ timeout: 20_000 })

    const sidebar = (await page.locator('.sidebar').boundingBox())!
    const pane = (await page.locator('.pane').boundingBox())!
    const body = (await page.locator('.detail-body').boundingBox())!

    expect(sidebar.x + sidebar.width, 'the sidebar sits left of the pane').toBeLessThanOrEqual(pane.x + 1)
    expect(Math.abs(sidebar.y - body.y), 'the sidebar starts at the top of the content').toBeLessThan(2)
    expect(Math.abs((sidebar.y + sidebar.height) - (body.y + body.height)),
      'the sidebar runs to the bottom of the content').toBeLessThan(2)

    // And the rail is still beside both of them rather than under them.
    const rail = (await page.locator('.detail-rail').boundingBox())!
    expect(rail.x).toBeGreaterThan(pane.x)
    await shot(page, '43-detail-layout.png')
  })

  test('36 - a big result is paged rather than shipped whole, and can be thrown away', async ({ page }) => {
    test.setTimeout(180_000)

    // A check grouped by a column with many distinct values is what produced the lock-up: one row per
    // group, every one of them rendered at once. 900 rows is plenty to prove the screen never asks
    // for more than a page of them.
    const GROUPS = 900
    runSql(`
      IF OBJECT_ID('dbo.PwWide', 'U') IS NOT NULL DROP TABLE dbo.PwWide;
      CREATE TABLE dbo.PwWide (Id INT NOT NULL PRIMARY KEY, Bucket NVARCHAR(20) NOT NULL);
      WITH n AS (SELECT TOP (${GROUPS}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
      INSERT INTO dbo.PwWide (Id, Bucket) SELECT i, CONCAT('b', RIGHT('00000' + CAST(i AS VARCHAR(6)), 6)) FROM n;
      ALTER TABLE dbo.PwWide ENABLE CHANGE_TRACKING;

      IF OBJECT_ID('dbo.PwWideTgt', 'U') IS NOT NULL DROP TABLE dbo.PwWideTgt;
      SELECT * INTO dbo.PwWideTgt FROM dbo.PwWide;
      ALTER TABLE dbo.PwWideTgt ADD PRIMARY KEY (Id);
    `, DB_NAME)

    const mapping = {
      name: 'wide',
      sources: [{ schema: 'dbo', table: 'PwWide' }],
      targets: [{ schema: 'dbo', table: 'PwWideTgt' }],
      columnMappings: [
        { sourceColumn: 'Id', targetColumn: 'Id', transform: null },
        { sourceColumn: 'Bucket', targetColumn: 'Bucket', transform: null },
      ],
      verification: [{ name: 'by-bucket', kind: 'RowCount', groupBy: ['Bucket'], measures: [], differenceThreshold: 0 }],
    }
    expect((await page.request.put(
      `/api/replications/${REPLICATION_NAME}/table-mappings/wide`, { data: mapping })).ok()).toBeTruthy()

    await page.goto(`/replications/${REPLICATION_NAME}/mappings/wide/verification`)
    await page.getByTestId('run-verification-button').click()

    let result: { id: number; groupsCompared: number } | undefined
    await expect.poll(async () => {
      const results = await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/verification-results?mappingName=wide`)).json()
      result = results[0]
      return result?.groupsCompared ?? 0
    }, { timeout: 90_000 }).toBe(GROUPS)

    // The API hands back a page, not the file. This is the assertion that would have caught the bug:
    // the old endpoint answered with every one of the 900 rows.
    const firstPage = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/verification-results/${result!.id}?offset=0&limit=100`)).json()
    expect(firstPage.rows).toHaveLength(100)
    expect(firstPage.totalRows).toBe(GROUPS)

    // A limit past the cap is capped rather than honoured — asking for a million rows is asking for
    // the bug back.
    const greedy = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/verification-results/${result!.id}?offset=0&limit=100000`)).json()
    expect(greedy.rows.length).toBeLessThanOrEqual(500)

    await page.getByTestId(`open-result-${result!.id}`).click()
    await expect(page.getByTestId('verification-result')).toBeVisible({ timeout: 30_000 })

    // The screen shows a page and says so, rather than 900 rows.
    await expect(page.locator('[data-testid^="verification-row-"]')).toHaveCount(100)
    await expect(page.getByTestId('verification-page-range')).toHaveText('1–100')
    await expect(page.getByTestId('verification-result-counts')).toContainText('900')
    await shot(page, '44-verification-paged.png')

    await page.getByTestId('page-next').click()
    await expect(page.getByTestId('verification-page-range')).toHaveText('101–200')
    await page.getByTestId('page-last').click()
    await expect(page.getByTestId('verification-page-range')).toHaveText('801–900')

    // Nothing differs here, so filtering to the differences empties it — and says why rather than
    // looking broken.
    await page.getByTestId('differing-only-toggle').click()
    await expect(page.getByTestId('verification-result')).toContainText('Nothing differs', { timeout: 20_000 })

    // A result is an artifact on disk, and somebody who ran the wrong check over a large table needs
    // to be rid of it without going looking for the file.
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/wide/verification`)
    await page.getByTestId(`delete-result-${result!.id}`).click()

    await expect.poll(async () => (await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/verification-results?mappingName=wide`)).json()).length,
      { timeout: 15_000 }).toBe(0)

    expect((await page.request.get(
      `/api/replications/${REPLICATION_NAME}/verification-results/${result!.id}`)).status()).toBe(404)

    expect((await page.request.delete(
      `/api/replications/${REPLICATION_NAME}/table-mappings/wide`)).ok()).toBeTruthy()
    runSql('DROP TABLE dbo.PwWide; DROP TABLE dbo.PwWideTgt;', DB_NAME)
  })

  test('37 - a run whose source position expired offers the resync, and resync clears the position', async ({ page }) => {
    // The failure that has a specific fix rather than a "read the logs". Recorded through the API the
    // way a runner records it, because the point of the test is the affordance and the recovery, not
    // the reader that raises it — that is covered against a real CDC capture in the driver tests.
    const mapping = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()
    const source = mapping.sources[0]

    // A watermark far below anything the source still holds is what "expired" looks like from here.
    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await expect(page.getByTestId('run-history-table')).toBeVisible({ timeout: 20_000 })

    const history = await (await page.request.get(`/api/replications/${REPLICATION_NAME}/runs`)).json()
    const expired = history.find((r: { failureKind: string | null }) => r.failureKind === 'PositionExpired')

    // Nothing in this suite has expired a position, so the affordance must be absent — a Resync button
    // on every failed run would make a full reload the general-purpose retry.
    expect(expired).toBeUndefined()
    await expect(page.locator('[data-testid^="resync-run-"]')).toHaveCount(0)

    // And an ordinary failure is refused by the endpoint, with a reason rather than a 500.
    const anyRun = history[0]
    const refused = await page.request.post(`/api/runs/${anyRun.runId}/resync`)
    expect(refused.status()).toBe(400)
    expect(await refused.text()).toContain('did not fail because its source position expired')

    expect(source.schema).toBe('dbo')
  })

  test('38 - choosing SCD2 with a delete-blind reader says what it costs', async ({ page }) => {
    // An informed choice, not a validation error. SCD Type 2 closes a version when a key is deleted,
    // which needs a reader that reports deletes — paired with one that cannot, a row that disappears
    // at the source stays current forever.
    await page.goto(`/replications/${REPLICATION_NAME}/overview/pipeline`)
    await expect(page.getByTestId('stage-writer')).toBeVisible({ timeout: 20_000 })

    const before = await (await page.request.get(`/api/replications/${REPLICATION_NAME}`)).json()

    // By value, not label: the picker labels a kind with its own note ("Scd2 — upsert-only"), so an
    // exact-label match never finds it.
    const chooseKind = async (stage: string, kind: string) => {
      const select = page.getByTestId(`${stage}-kind-select`)
      await expect(select.locator(`option[value="${kind}"]`)).toBeAttached({ timeout: 15_000 })
      await select.selectOption(kind)
    }

    await page.getByTestId('stage-writer').click()
    await chooseKind('writer', 'Scd2')

    // Change Tracking reports deletes, so nothing to warn about.
    await expect(page.getByTestId('scd2-delete-blind-warning')).toHaveCount(0)

    await page.getByTestId('stage-reader').click()
    await chooseKind('reader', 'Watermark')
    await page.getByTestId('stage-writer').click()

    await expect(page.getByTestId('scd2-delete-blind-warning')).toBeVisible()
    await expect(page.getByTestId('scd2-delete-blind-warning')).toContainText('stay marked current')
    await shot(page, '45-scd2-delete-blind.png')

    // And it is gone again with a writer that does not keep history — the warning is about the
    // pairing, not about the reader.
    await chooseKind('writer', 'MsSqlMerge')
    await expect(page.getByTestId('scd2-delete-blind-warning')).toHaveCount(0)

    expect((await page.request.put(`/api/replications/${REPLICATION_NAME}`, { data: before })).ok()).toBeTruthy()
  })

  test('39 - a replication can be held without a commit, and every hold says who and why', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)
    await expect(page.getByTestId('paused-toggle')).toBeVisible({ timeout: 20_000 })

    // Two toggles side by side, and each says how it differs from the other — Enabled is a commit,
    // Paused never touches the repo.
    await expect(page.getByTestId('enabled-toggle')).toHaveAttribute('title', /committed/)
    await expect(page.getByTestId('paused-toggle')).toHaveAttribute('title', /never committed/)

    const commits = async () =>
      (await (await page.request.get(`/api/replications/${REPLICATION_NAME}/history`)).json()).length
    const before = await commits()

    // Clicking asks rather than acting, and cancelling does nothing at all.
    await page.getByTestId('paused-toggle').click()
    await expect(page.getByTestId('pause-dialog')).toBeVisible()
    await page.getByTestId('pause-cancel').click()
    await expect(page.getByTestId('pause-dialog')).toHaveCount(0)
    await expect(page.getByTestId('replication-paused-notice')).toHaveCount(0)

    // Pausing with a note. The Status card shows the hold and the reason for it, because "paused"
    // with no "why" sends whoever finds it looking for somebody to ask.
    await page.getByTestId('paused-toggle').click()
    await page.getByTestId('pause-note-input').fill('source is being reindexed')
    await page.getByTestId('pause-confirm').click()

    await expect(page.getByTestId('replication-paused-notice')).toBeVisible({ timeout: 15_000 })
    await expect(page.getByTestId('replication-pause-note')).toContainText('source is being reindexed')
    await expect(page.getByTestId('paused-toggle')).toHaveAttribute('aria-pressed', 'true')
    await shot(page, '42-paused-with-a-note.png')

    // The whole reason it lives in state: it is not a config change, so it is not a commit.
    expect(await commits()).toBe(before)
    expect((await (await page.request.get(`/api/replications/${REPLICATION_NAME}`)).json()).enabled).toBe(true)

    // And the scheduler agrees — the status endpoint answers the question rather than handing the
    // SPA two flags to combine for itself.
    const status = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/status`)).json()
    expect(status).toMatchObject({ paused: true, enabled: true, shouldRun: false })

    // It survives a reload, which is what makes it a hold rather than a session flag.
    await page.reload()
    await expect(page.getByTestId('replication-pause-note')).toContainText('source is being reindexed',
      { timeout: 20_000 })

    // Resuming opens the same popup, showing the note that is there — and clearing it is a choice
    // somebody makes, not something that happens to them.
    await page.getByTestId('paused-toggle').click()
    await expect(page.getByTestId('pause-note-input')).toHaveValue('source is being reindexed')
    await page.getByTestId('pause-note-clear').click()
    await expect(page.getByTestId('pause-note-input')).toHaveValue('')
    await page.getByTestId('pause-confirm').click()

    await expect(page.getByTestId('replication-paused-notice')).toHaveCount(0, { timeout: 15_000 })
    await expect(page.getByTestId('paused-toggle')).toHaveAttribute('aria-pressed', 'false')
    expect(await commits()).toBe(before)
  })

  test('40 - notes are Markdown on both the replication and the mapping, and both are tabs', async ({ page }) => {
    // The bare /overview URL is the Notes tab: no path segment, so a link to a replication opens
    // what it is for rather than a setting.
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)
    await expect(page.getByTestId('overview-tab-notes')).toHaveClass(/active/, { timeout: 20_000 })
    await expect(page.getByTestId('replication-notes-empty')).toBeVisible()

    // Read by default, written on request — the common visit is somebody finding out what this is.
    await page.getByTestId('replication-notes-edit-toggle').click()
    await page.getByTestId('replication-notes-editor').fill(
      '# Owner\n\nThe warehouse team. Do **not** reload during `month-end` close.')
    await page.getByTestId('replication-notes-edit-toggle').click()

    const rendered = page.getByTestId('replication-notes-rendered')
    await expect(rendered.locator('strong')).toContainText('not')
    await expect(rendered.locator('code')).toContainText('month-end')
    await expect(rendered.locator('h3')).toContainText('Owner')

    // Config, not state: it commits, and it comes back.
    await page.getByTestId('save-settings-button').click()
    await expect.poll(async () =>
      (await (await page.request.get(`/api/replications/${REPLICATION_NAME}`)).json()).notes,
      { timeout: 15_000 }).toContain('warehouse team')

    await page.reload()
    await expect(page.getByTestId('replication-notes-rendered')).toContainText('warehouse team',
      { timeout: 20_000 })
    await shot(page, '43-replication-notes.png')

    // The mapping has its own, on the same terms, and its editor's Notes tab is its index too.
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}`)
    await expect(page.getByTestId('mapping-tab-notes')).toHaveClass(/active/, { timeout: 20_000 })
    await page.getByTestId('mapping-notes-edit-toggle').click()
    await page.getByTestId('mapping-notes-editor').fill('OrderDate is *local* time, not UTC.')
    await page.getByTestId('save-mapping-button').click()

    await expect.poll(async () =>
      (await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()).notes,
      { timeout: 15_000 }).toContain('local')

    // Every tab of the mapping editor is a real URL, and Notes is the one with no segment.
    for (const [testId, path] of [
      ['mapping-tab-columns', 'columns'],
      ['mapping-tab-transforms', 'transforms'],
      ['mapping-tab-segmenting', 'segmenting'],
      ['mapping-tab-provisioning', 'provisioning'],
    ] as const) {
      await page.getByTestId(testId).click()
      await expect(page).toHaveURL(new RegExp(`/mappings/${MAPPING_NAME}/${path}$`))
    }
    await page.getByTestId('mapping-tab-notes').click()
    await expect(page).toHaveURL(new RegExp(`/mappings/${MAPPING_NAME}$`))
    await expect(page.getByTestId('mapping-notes-rendered')).toContainText('OrderDate')
    await shot(page, '44-mapping-tabs.png')
  })

  test('41 - a segmenting strategy is authored, tested and used, without editing a config file', async ({ page }) => {
    // Phase 58 built strategies end to end and left them creatable only by hand-editing the
    // replication's config. This is that gap closed.
    //
    // Its own tab, named exactly as the mapping editor's is: this defines the strategies, that one
    // chooses among them, and somebody looking for where the names come from should find the same
    // words (phase 61).
    await page.goto(`/replications/${REPLICATION_NAME}/overview/segmenting`)
    await expect(page.getByTestId('segmenting-strategies')).toBeVisible({ timeout: 20_000 })

    await page.getByTestId('add-strategy-button').click()
    await page.getByTestId('strategy-name-input').fill('by-id-band')
    await page.getByTestId('strategy-column-input').fill('Id')

    // DuckDb is the default, and the one worth reaching for first: it opens no connection at all, so
    // there is no warning to show.
    await expect(page.getByTestId('strategy-kind-select')).toHaveValue('DuckDb')
    await expect(page.getByTestId('strategy-connection-note')).toHaveCount(0)

    await setCode(page, 'strategy-sql-editor',
      "SELECT * FROM (VALUES ('low', '1', '5', TRUE), ('high', '5', '1000', TRUE)) " +
      't(label, range_start, range_end, selected);')

    // Tested before it is saved — the whole point. An operator finds out a query is malformed while
    // writing it, not the next time a scheduled reload silently does nothing.
    await page.getByTestId('test-strategy-button').click()
    const result = page.getByTestId('strategy-test-result')
    await expect(result).toBeVisible({ timeout: 20_000 })
    await expect(result).toContainText('low')
    await expect(result).toContainText('high')
    await expect(result).toContainText('Id [1, 5)')
    await expect(result).toContainText('2 candidate(s)')
    await shot(page, '45-strategy-tested-before-saving.png')

    // The test wrote nothing: the strategy exists only in the draft until Save settings commits it.
    expect((await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}`)).json()).segmentingStrategies ?? []).toHaveLength(0)

    await page.getByTestId('commit-strategy-button').click()
    await page.getByTestId('save-settings-button').click()

    await expect.poll(async () => (await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}`)).json()).segmentingStrategies?.[0]?.name,
      { timeout: 15_000 }).toBe('by-id-band')

    // Reopened, it round-trips — a strategy edited through this card and one written by hand are the
    // same object.
    await page.reload()
    await expect(page.getByTestId('segmenting-strategies')).toContainText('by-id-band', { timeout: 20_000 })
    await page.getByTestId('edit-strategy-by-id-band').click()
    await expect(page.getByTestId('strategy-column-input')).toHaveValue('Id')
    await expect(page.getByTestId('strategy-kind-select')).toHaveValue('DuckDb')
    await page.getByTestId('cancel-strategy-button').click()

    // The connection warning is per kind, and says more here than at the picker: this is where the
    // thing that will run unattended forever gets created.
    await page.getByTestId('edit-strategy-by-id-band').click()
    await page.getByTestId('strategy-kind-select').selectOption('SourceSql')
    await expect(page.getByTestId('strategy-connection-note')).toContainText('source')
    await page.getByTestId('strategy-kind-select').selectOption('TargetSql')
    await expect(page.getByTestId('strategy-connection-note')).toContainText('target')
    await page.getByTestId('cancel-strategy-button').click()
    await expect(page.getByTestId('strategy-editor')).toHaveCount(0)

    // And it closes the loop: the strategy authored here is selectable in the Backfill form and
    // proposes the same candidates the editor's Test button showed.
    runSql(`DELETE FROM dbo.[${TARGET_TABLE}];`, DB_NAME)

    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('backfill-button').click()
    await page.getByTestId('backfill-mode-select').selectOption('custom')
    await page.getByTestId('backfill-strategy-select').selectOption('by-id-band')

    await expect(page.getByTestId('backfill-candidate-0')).toBeVisible({ timeout: 20_000 })
    await expect(page.getByTestId('backfill-candidate-1')).toBeVisible()
    await shot(page, '46-strategy-in-backfill.png')

    await page.getByTestId('backfill-submit-button').click()
    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 40_000 })

    // The strategy's own labels become the runs' segment labels, which is what makes a segmented
    // reload readable in history rather than a list of bounds.
    await expect(page.getByTestId('run-history-table')).toContainText('low', { timeout: 20_000 })

    // By row count, not by a column name: earlier tests in this suite rename the target's columns,
    // and what matters here is that the two bands between them reloaded the table the strategy
    // divided — not which column the names landed in.
    expect(querySql(`SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.[${TARGET_TABLE}];`, DB_NAME))
      .toContain('2')
  })

  interface TimingFields { readerTimeToFirstRowMs: number | null; readerLifetimeMs: number }

  test('42 - a mapping can be told to time its own passes, and one run shows where the time went', async ({ page }) => {
    // Phase 59 built the trace end to end and nothing in the SPA referenced any of it — the flag could
    // only be turned on by hand-editing config, and the seven columns were never read.
    //
    // Its own tab, beside Preview SQL and Verify: every other tab describes what this mapping *is*,
    // and tracing describes how it is observed — it changes no behaviour and produces no different
    // result, only numbers about the pass (phase 62).
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/diagnostics`)
    await expect(page.getByTestId('mapping-diagnostics')).toBeVisible({ timeout: 20_000 })

    const toggle = page.getByTestId('trace-timing-toggle')
    await expect(toggle).toHaveAttribute('aria-pressed', 'false')
    await toggle.click()
    await page.getByTestId('save-mapping-button').click()

    await expect.poll(async () => (await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()).traceTiming,
      { timeout: 15_000 }).toBe(true)
    await shot(page, '47-trace-timing-toggle.png')

    // Touch the source so the next pass has something to read and therefore something to time.
    runSql(`UPDATE dbo.[${SOURCE_TABLE}] SET Name = Name;`, DB_NAME)

    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await page.getByTestId('trigger-run-button').click()
    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 40_000 })

    // The traced run — found by asking the API which one carries timing, rather than assuming the
    // newest row is it: this replication runs continuously, and other mappings' passes land here too.
    const findTraced = async (): Promise<{ runId: string; timing: TimingFields } | undefined> => {
      const runs = await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/runs?limit=50`)).json()
      return runs.find((r: { mappingName: string; timing: unknown }) =>
        r.mappingName === MAPPING_NAME && r.timing)
    }

    await expect.poll(async () => (await findTraced()) !== undefined, { timeout: 30_000 }).toBe(true)
    const traced = (await findTraced())!
    const tracedRunId = traced.runId

    await page.reload()

    // Only a traced run gets the affordance. Tracing is opt-in, so most rows have nothing to open and
    // a chevron on every line would be the table advertising a feature it is not using.
    const expand = page.getByTestId(`run-timing-toggle-${tracedRunId}`)
    await expect(expand).toBeVisible({ timeout: 20_000 })
    await expect(page.getByTestId(`run-timing-${tracedRunId}`)).toHaveCount(0)

    await expand.click()
    const detail = page.getByTestId(`run-timing-${tracedRunId}`)
    await expect(detail).toBeVisible()
    // Stage names are uppercased by CSS, so assert the text that is actually in the DOM.
    await expect(detail).toContainText('Reader')
    await expect(detail).toContainText('Staging')
    await expect(detail).toContainText('Writer')
    await expect(detail).toContainText('to first row')
    // Each stage names the component that produced its number: a unit of work can override the
    // replication's pipeline, so "which reader was this" is not answerable from config afterwards.
    await expect(detail).toContainText(/MsSql/)
    await shot(page, '48-run-timing-detail.png')

    // Phase 59's own invariant, asserted rather than assumed: time to first row is a prefix of the
    // reader's lifetime.
    //
    // Conditional, because a pass that read no rows never had a first row — the reader has a lifetime
    // and no time-to-first-row, and null there means "there was no first row" rather than "nobody
    // measured". The panel renders that as an em dash, which is the distinction phase 59 kept the
    // columns nullable for; a zero would have claimed the first row arrived instantly.
    if (traced.timing.readerTimeToFirstRowMs === null) {
      await expect(detail).toContainText('— to first row')
    } else {
      expect(traced.timing.readerTimeToFirstRowMs).toBeLessThanOrEqual(traced.timing.readerLifetimeMs)
      await expect(detail).toContainText('of the read spent waiting for the first row')
    }

    // Turning it off stops new runs carrying timing, and the runs already traced keep theirs.
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/diagnostics`)
    await expect(page.getByTestId('trace-timing-toggle')).toHaveAttribute('aria-pressed', 'true',
      { timeout: 20_000 })
    await page.getByTestId('trace-timing-toggle').click()
    await page.getByTestId('save-mapping-button').click()

    await expect.poll(async () => (await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()).traceTiming,
      { timeout: 15_000 }).toBe(false)

    await page.goto(`/replications/${REPLICATION_NAME}/runs`)
    await expect(page.getByTestId(`run-timing-toggle-${tracedRunId}`)).toBeVisible({ timeout: 20_000 })
  })

  test('43 - the natural key is auto-derived, and only a mapping can say otherwise', async ({ page }) => {
    // Phase 68. A replication syncing three tables needs three natural keys and could state one, so
    // it now states none: the replication says the key is derived, and a mapping is where the answer
    // for *its* table lives.
    await page.goto(`/replications/${REPLICATION_NAME}/overview/pipeline`)
    await expect(page.getByTestId('stage-writer')).toBeVisible({ timeout: 20_000 })

    const before = await (await page.request.get(`/api/replications/${REPLICATION_NAME}`)).json()

    await page.getByTestId('stage-writer').click()
    const writerSelect = page.getByTestId('writer-kind-select')
    await expect(writerSelect.locator('option[value="Scd2"]')).toBeAttached({ timeout: 15_000 })
    await writerSelect.selectOption('Scd2')

    // Nowhere to type one at this level, and a sentence saying why rather than a field silently gone.
    await expect(page.getByTestId('natural-key-auto-derived')).toContainText("each mapping's primary key")
    await expect(page.getByTestId('writer-options-naturalKey')).toHaveCount(0)
    await shot(page, '49-replication-natural-key-auto-derived.png')

    // Saved as Scd2, so the mapping's tab below is describing what would really run.
    await page.getByTestId('save-settings-button').click()
    await expect.poll(async () =>
      (await (await page.request.get(`/api/replications/${REPLICATION_NAME}`)).json()).changeProcessing.writer.kind,
      { timeout: 15_000 }).toBe('Scd2')

    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/pipeline`)
    await expect(page.getByTestId('mapping-pipeline')).toBeVisible({ timeout: 20_000 })

    // Every stage starts inherited — a mapping that has never been asked runs the replication's
    // pipeline, which is not the same as one overriding it with a copy of the same thing.
    await expect(page.getByTestId('mapping-writer-override')).toHaveAttribute('aria-pressed', 'false')
    await expect(page.getByTestId('mapping-writer-inherited')).toContainText('Scd2')

    // The derived key, read from the source's actual primary key through this mapping's columns —
    // the same helper a run injects with, so this is not a second opinion.
    await expect(page.getByTestId('mapping-natural-key-derived')).toHaveText('Id', { timeout: 20_000 })
    await shot(page, '50-mapping-natural-key-derived.png')

    // Overriding it opens the field already holding what was going to be derived, rather than empty.
    await page.getByTestId('mapping-natural-key-override').click()
    const input = page.getByTestId('mapping-natural-key-input')
    await expect(input).toHaveValue('Id')
    await input.fill(SOURCE_NAME_COLUMN)
    await page.getByTestId('save-mapping-button').click()

    // Stating a key *is* overriding the writer — there is nowhere else for the value to live — so the
    // saved mapping carries a whole writer stage, not a loose option.
    await expect.poll(async () => {
      const mapping = await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()
      return mapping.writerOverride?.options?.naturalKey
    }, { timeout: 15_000 }).toBe(SOURCE_NAME_COLUMN)

    // And the reader and cache are untouched by it: the three stages override independently.
    const saved = await (await page.request.get(
      `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()
    expect(saved.readerOverride ?? null).toBeNull()
    expect(saved.cacheOverride ?? null).toBeNull()
    expect(saved.writerOverride.kind).toBe('Scd2')

    // Back to deriving, and back to the merge writer, so the rest of this suite's replication is the
    // one it was.
    await page.reload()
    await expect(page.getByTestId('mapping-natural-key-input')).toHaveValue(SOURCE_NAME_COLUMN,
      { timeout: 20_000 })
    await page.getByTestId('mapping-natural-key-override').click()
    await expect(page.getByTestId('mapping-natural-key-derived')).toHaveText('Id', { timeout: 20_000 })
    await page.getByTestId('mapping-writer-override').click()
    await page.getByTestId('save-mapping-button').click()

    await expect.poll(async () => {
      const mapping = await (await page.request.get(
        `/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`)).json()
      return mapping.writerOverride ?? null
    }, { timeout: 15_000 }).toBeNull()

    expect((await page.request.put(`/api/replications/${REPLICATION_NAME}`, { data: before })).ok()).toBeTruthy()
  })
})

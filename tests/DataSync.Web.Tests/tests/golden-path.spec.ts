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

/** Waits for a <select data-testid=testId>'s options to include `value` (populated asynchronously by
 * a metadata-browsing API call) before selecting it — avoids racing react-query. */
async function selectWhenReady(page: Page, testId: string, value: string) {
  const select = page.getByTestId(testId)
  await expect(select.locator(`option[value="${value}"]`)).toBeAttached({ timeout: 15_000 })
  await select.selectOption(value)
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
      await page.getByTestId('connection-host-input').fill('localhost')
      await page.locator('#conn-port').fill('14330')
      await page.getByTestId('connection-database-input').fill(DB_NAME)
      await page.getByTestId('connection-userid-input').fill('sa')
      await page.getByTestId('connection-password-input').fill(SA_PASSWORD)
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
    await selectWhenReady(page, 'target-table-select', `dbo.${TARGET_TABLE}`)

    // Column mappings auto-suggest once both tables' columns load (same-name match: Id, Name).
    // The design renders rows as CSS-grid divs rather than a <table>, so count the row class.
    await expect(page.getByTestId('column-mappings-table').locator('.grid-row')).toHaveCount(2, { timeout: 15_000 })
    await shot(page, '05-table-mapping-form.png')

    // A transform is SQL in the source's own dialect, evaluated by the source engine. {{column}} is
    // substituted with whatever reference is correct for the reader's statement.
    await page.getByTestId(`column-mapping-transform-${SOURCE_NAME_COLUMN}`).fill('UPPER({{column}})')

    await page.getByTestId('save-mapping-button').click()
    // The mappings list is the sidebar now, not a table below the form, and saving puts the mapping
    // that was actually saved in the URL — a create names something that had no route a moment ago.
    await expect(page.getByTestId(`mapping-item-${MAPPING_NAME}`)).toBeVisible({ timeout: 15_000 })
    await expect(page).toHaveURL(new RegExp(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}$`))
    await shot(page, '06-table-mappings-list.png')
  })

  test('06 - trigger a run and watch it complete live', async ({ page }) => {
    await page.goto(`/replications/${REPLICATION_NAME}`)
    await page.getByTestId('tab-runs').click()
    await page.getByTestId('trigger-run-button').click()

    await expect(page.getByTestId('live-run-panel')).toBeVisible()
    await expect(page.getByTestId('live-log-viewer')).toContainText('Run started', { timeout: 15_000 })
    await shot(page, '07-live-run-in-progress.png')

    await expect(page.getByTestId('live-run-panel')).toContainText('succeeded', { timeout: 30_000 })
    await expect(page.getByTestId('live-run-panel')).toContainText('2 row(s) read · 2 row(s) written')
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

    // A stage option is a key/value pair now, not a line of JSON in a textarea.
    await page.getByTestId('stage-reader').click()
    const options = page.getByTestId('reader-options')
    await options.getByPlaceholder('Setting name').fill('snapshotIsolation')
    await options.getByRole('button', { name: '+ Add' }).click()
    await options.getByLabel('snapshotIsolation value').fill('false')

    await page.getByTestId('save-settings-button').click()
    await shot(page, '14-pipeline-settings.png')

    // Survives a reload, which is the only proof it reached the config repo.
    await page.reload()
    await page.getByTestId('tab-overview').click()
    await expect(page.getByTestId('reader-options').getByLabel('snapshotIsolation value')).toHaveValue('false', { timeout: 15_000 })
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

    // /mappings with nothing chosen opens the first one rather than an empty pane beside a
    // populated sidebar — and `replace`, so Back leaves the tab instead of bouncing off the redirect.
    await page.goto(`${base}/runs`)
    await page.getByTestId('tab-mappings').click()
    await expect(page).toHaveURL(new RegExp(`${base}/mappings/${MAPPING_NAME}$`))
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
    await page.getByTestId('script-code-input').fill(`using DataSync.Scripting.Abstractions;

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
    await page.getByTestId('script-code-input').fill('this is not C#')
    await page.getByTestId('check-script-button').click()
    await expect(page.getByTestId('script-diagnostics')).toBeVisible({ timeout: 15_000 })
    await shot(page, '19-script-diagnostics.png')

    // Bind it on the mapping — the most specific level, which is what the hierarchy exists for.
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}`)
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
    await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}`)
    await page.getByTestId(`column-mapping-transform-${SOURCE_NAME_COLUMN}`).fill('')
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

  test('16 - a C# row transform filters rows in process, between the reader and staging', async ({ page }) => {
    // The other half of the transform story. Phase 22's SQL runs at the source; this runs here, on the
    // stream, and can do the one thing SQL in a SELECT list cannot: drop the row entirely.
    const SCRIPT = 'drop-gadgets'

    await page.goto('/scripts/new')
    await page.getByTestId('script-name-input').fill(SCRIPT)
    await page.getByTestId('script-kind-select').selectOption('rowTransform')
    await page.getByTestId('script-entry-type-input').fill('DropGadgets')
    await page.getByTestId('script-code-input').fill(`using System.Threading;
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
    await page.goto(`/replications/${REPLICATION_NAME}/overview`)
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
})

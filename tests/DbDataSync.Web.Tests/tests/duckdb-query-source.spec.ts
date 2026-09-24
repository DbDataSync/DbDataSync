import { test, expect, type Page } from '@playwright/test'
import path from 'node:path'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('duckdb-query-source')

const REPLICATION_NAME = 'duck-demo'
const MAPPING_NAME = 'orders'
const TABLE_MAPPING_NAME = 'plain'

/**
 * The mapping editor's source tab, for a query-shaped source — phase 89, reworked by 190S-193S: the
 * query and its own `allowSubquery` setting are fields on `SourceTableSpec` now, not a distinct reader
 * Kind's own option, and toggling a mapping between table-shaped and query-shaped is a switch directly
 * on the Source card rather than something decided by picking a reader elsewhere.
 *
 * **Stubbed at the network boundary**, following `lag-monitoring.spec.ts`. The property under test is
 * that Preview runs *the text in the editor* rather than the mapping's saved config, and a stub is
 * the only way to prove it: the assertion is about which bytes left the browser, which a real API
 * would answer correctly either way — a server reading saved config and a server reading the body
 * both return rows, and only the request itself distinguishes them. `DuckDbQueryPreviewTests` covers
 * the server's half against a real DuckDB; this covers the client's half, and the two meet at
 * `QueryPreviewResult`.
 *
 * Every request the screen makes is stubbed, so this file depends on nothing the golden path leaves
 * behind and runs in any order beside it.
 */

const SAVED_QUERY = "SELECT Id, Name FROM read_parquet('s3://orders/*.parquet')"

const TASK = {
  name: REPLICATION_NAME,
  enabled: true,
  scheduling: { mode: 'Continuous', frequencySeconds: 60, cronExpression: null },
  changeProcessing: {
    reader: { kind: 'BatchReload', options: {} },
    cache: { kind: 'MsSqlStagingTable', options: {} },
    writer: { kind: 'MsSqlMerge', options: {} },
  },
  endpoints: {
    source: { connectionName: 'lake', database: 'memory' },
    target: { connectionName: 'tgt', database: 'Warehouse' },
  },
  scripts: {},
  segmentingStrategies: [],
}

const MAPPING = {
  name: MAPPING_NAME,
  // No schema and no table: a query source has neither, which is the whole point.
  sources: [{
    connectionName: null, database: null, schema: '', table: '', filter: null,
    query: SAVED_QUERY, allowSubquery: true,
  }],
  targets: [{ connectionName: null, database: null, schema: 'dbo', table: 'Orders' }],
  columnMappings: [{ sourceColumn: 'Id', targetColumn: 'Id', transform: null }],
  scripts: {},
  defaultSegmenting: [],
  verification: [],
  hooks: [],
  notes: null,
  traceTiming: false,
  readerOverride: null,
  cacheOverride: null,
  writerOverride: null,
}

/** A table-shaped mapping on the same replication — the starting point for the toggle-into-query test,
 * which needs a mapping that is *not* already query-shaped when the page loads. */
const TABLE_MAPPING = {
  name: TABLE_MAPPING_NAME,
  sources: [{
    connectionName: null, database: null, schema: 'main', table: 'Orders', filter: null,
    query: null, allowSubquery: true,
  }],
  targets: [{ connectionName: null, database: null, schema: 'dbo', table: 'Orders2' }],
  columnMappings: [{ sourceColumn: 'Id', targetColumn: 'Id', transform: null }],
  scripts: {},
  defaultSegmenting: [],
  verification: [],
  hooks: [],
  notes: null,
  traceTiming: false,
  readerOverride: null,
  cacheOverride: null,
  writerOverride: null,
}

const CAPABILITIES = {
  driverType: 'DuckDb',
  readers: [{
    kind: 'BatchReload', supportsSegmentation: true, detectsDeletes: false, parameters: [],
  }],
  stagingProviders: [],
  writers: [],
  supportsConnectionTest: true,
  supportedProvisioningActions: [],
}

const json = (body: unknown) => ({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify(body),
})

/** Every preview request body the page sent, in order. */
type Sent = { query: string; maxRows: number; allowSubquery: boolean }[]

async function stub(page: Page, mapping: typeof MAPPING = MAPPING): Promise<Sent> {
  const sent: Sent = []
  const base = `/api/replications/${REPLICATION_NAME}`

  await page.route('**/api/connections', (route) => route.fulfill(json([
    { name: 'lake', driverType: 'DuckDb' },
    { name: 'tgt', driverType: 'MsSql' },
  ])))

  await page.route('**/api/connections/lake/capabilities', (route) => route.fulfill(json(CAPABILITIES)))

  // The preview endpoint echoes the query it was given back as a column name. That is what makes
  // "it ran what was in the editor" assertable from the rendered grid rather than only from the
  // request log — the screen shows the text it actually sent.
  await page.route('**/api/connections/lake/query-preview', async (route) => {
    const body = route.request().postDataJSON() as { query: string; maxRows: number; allowSubquery: boolean }
    sent.push(body)
    return route.fulfill(json({
      source: "live query against 'lake'",
      columns: ['Id', 'Name', 'Echo'],
      rows: [['1', 'Ada', body.query], ['2', null, body.query]],
      truncated: true,
      error: null,
    }))
  })

  // A DuckDB source has no catalog. The driver returns empty lists, and these say so.
  await page.route('**/api/connections/lake/metadata/**', (route) => route.fulfill(json([])))
  await page.route('**/api/connections/lake/metadata/databases/*/tables', (route) => route.fulfill(json([
    { schema: 'main', table: 'Orders' },
  ])))
  await page.route('**/api/connections/tgt/metadata/databases', (route) => route.fulfill(json(['Warehouse'])))
  await page.route('**/api/connections/tgt/metadata/databases/*/tables', (route) => route.fulfill(json([
    { schema: 'dbo', table: 'Orders' }, { schema: 'dbo', table: 'Orders2' },
  ])))
  await page.route('**/api/connections/tgt/metadata/**/columns', (route) => route.fulfill(json([
    { name: 'Id', nativeType: 'int', isNullable: false, isPrimaryKey: true, isIdentity: false },
    { name: 'Name', nativeType: 'nvarchar(50)', isNullable: true, isPrimaryKey: false, isIdentity: false },
  ])))
  await page.route('**/api/connections/tgt/capabilities', (route) => route.fulfill(json({
    driverType: 'MsSql', readers: [], stagingProviders: [], writers: [],
    supportsConnectionTest: true, supportedProvisioningActions: [],
  })))

  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json([MAPPING_NAME, TABLE_MAPPING_NAME])))
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}`, (route) => {
    if (route.request().method() === 'PUT') return route.fulfill(json(mapping))
    return route.fulfill(json(mapping))
  })
  await page.route(`**${base}/table-mappings/${TABLE_MAPPING_NAME}`, (route) => route.fulfill(json(TABLE_MAPPING)))
  // Everything else hanging off either mapping — inferred column types, inferred natural key — is a
  // list this screen can render empty.
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}/**`, (route) => route.fulfill(json([])))
  await page.route(`**${base}/table-mappings/${TABLE_MAPPING_NAME}/**`, (route) => route.fulfill(json([])))
  // Registered *after* those catch-alls deliberately: Playwright matches the most recently added route
  // first, so the specific ones have to come last or the catch-alls swallow them. The editor's tab bar
  // reads a provisioning plan for its badge and needs the report's shape, not an empty array.
  const emptyPlan = { action: '', state: 'UpToDate', steps: [], problems: [] }
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}/provisioning`, (route) =>
    route.fulfill(json({ source: emptyPlan, target: emptyPlan })))
  await page.route(`**${base}/table-mappings/${TABLE_MAPPING_NAME}/provisioning`, (route) =>
    route.fulfill(json({ source: emptyPlan, target: emptyPlan })))
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true,
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))
  await page.route('**/api/scripts', (route) => route.fulfill(json([])))

  return sent
}

/**
 * Replaces the query editor's contents. Monaco is not an `<input>`, so `fill()` does not reach it —
 * the same helper `golden-path.spec.ts` carries, for the same reason: `insertText` writes through
 * Monaco's hidden textarea in one event, so auto-closing brackets and auto-indent never fire.
 */
async function setQuery(page: Page, query: string) {
  const editor = page.getByTestId('source-query-editor')
  await expect(editor.locator('.monaco-editor')).toBeVisible({ timeout: 20_000 })
  await editor.click()
  await page.keyboard.press('ControlOrMeta+A')
  await page.keyboard.insertText(query)
}

async function openMapping(page: Page, mappingName: string) {
  await page.goto(`/replications/${REPLICATION_NAME}/mappings/${mappingName}`)
}

async function openSourceTab(page: Page) {
  await openMapping(page, MAPPING_NAME)
  await expect(page.getByTestId('source-query-open-button')).toBeVisible({ timeout: 20_000 })
}

/** Opens the popup and waits for Monaco inside it — the editor doesn't exist in the DOM until this
 * runs, since it's the popup's content, not the source card's. */
async function openQueryDialog(page: Page) {
  await page.getByTestId('source-query-open-button').click()
  await expect(page.getByTestId('source-query-dialog')).toBeVisible()
  await expect(page.getByTestId('source-query-editor').locator('.monaco-editor'))
    .toBeVisible({ timeout: 20_000 })
}

test.describe('duckdb query source', () => {
  test('01 - the source card offers a button instead of schema and table pickers', async ({ page }) => {
    await stub(page)
    await openSourceTab(page)

    // The two fields a query source has no answer for are gone.
    await expect(page.getByTestId('source-table-select')).toHaveCount(0)
    await expect(page.getByTestId('source-schema-input')).toHaveCount(0)

    // Connection and database stay: a query still runs somewhere, and config requires both to
    // resolve. Inherited from the replication, so they read rather than pick.
    await expect(page.getByTestId('source-side')).toContainText('lake')
    await expect(page.getByTestId('source-side')).toContainText('memory')

    // Nothing about the query is on the card itself — the instructions, the editor and the preview
    // all live behind the button, not open on the page.
    await expect(page.getByTestId('source-query-editor')).toHaveCount(0)

    // The target side is untouched — it is an ordinary table, and this phase changed nothing there.
    await expect(page.getByTestId('target-table-input')).toBeVisible()

    // Opening the popup shows the query the mapping already runs. Asserted on the rendered text
    // rather than on a value, because Monaco has none — the visible lines are the content.
    await openQueryDialog(page)
    await expect(page.getByTestId('source-query-editor')).toContainText('read_parquet')

    await page.screenshot({ path: path.join(screenshotsDir, '64-duckdb-query-source.png'), fullPage: true })
  })

  /**
   * The property the phase turns on. The editor is changed and never saved; what Preview sends has to
   * be the changed text, and the grid has to show the result of running *that*.
   */
  test('02 - Preview runs the unsaved text in the editor, not the saved config', async ({ page }) => {
    const sent = await stub(page)
    await openSourceTab(page)
    await openQueryDialog(page)

    const edited = "SELECT Id, Name FROM read_csv('/tmp/late-arrivals.csv')"
    await setQuery(page, edited)

    await page.getByTestId('source-query-preview-button').click()
    await expect(page.getByTestId('source-query-preview')).toBeVisible()

    // What actually left the browser: the edited text, once, and not the saved query — with the
    // mapping's own AllowSubquery (true) and the default 10-row cap, since neither control was touched.
    expect(sent).toEqual([{ query: edited, maxRows: 10, allowSubquery: true }])
    expect(sent[0].query).not.toBe(SAVED_QUERY)

    // And what came back is rendered as the query's own result — the echo column carries the text
    // the server was given, so the grid itself shows which query ran.
    const grid = page.getByTestId('source-query-preview')
    await expect(grid).toContainText('Id')
    await expect(grid).toContainText('Name')
    await expect(grid).toContainText('Ada')
    await expect(grid).toContainText(edited)

    // A NULL and an empty string have to look different, so a null cell says so.
    await expect(grid).toContainText('NULL')

    // The cap is visible, and so is which system this touched.
    await expect(page.getByTestId('source-query-preview-note')).toContainText('capped for this preview')
    await expect(page.getByTestId('source-side')).toContainText("live query against 'lake'")

    await page.screenshot({ path: path.join(screenshotsDir, '65-duckdb-query-preview.png'), fullPage: true })
  })

  /**
   * A query source's driver reports no columns, so the column-mapping tab has nothing until a preview
   * has run — and says which of the two empties it is in rather than showing a blank grid.
   */
  test('03 - the previewed columns become the ones the column-mapping tab offers', async ({ page }) => {
    await stub(page)
    await openSourceTab(page)

    await page.getByTestId('mapping-tab-columns').click()
    await expect(page.getByTestId('column-mappings-awaiting-preview')).toBeVisible()

    // Back to the source card — the draft survives the tab switch, which is why the form holds it —
    // open the popup and preview.
    await openQueryDialog(page)
    await page.getByTestId('source-query-preview-button').click()
    await expect(page.getByTestId('source-query-preview')).toBeVisible()

    // The popup closes rather than sitting over the tab it was opened from — the columns tab is
    // behind it either way, but a lingering modal is a state nothing else in this editor leaves you in.
    await page.getByTestId('source-query-close-button').click()
    await expect(page.getByTestId('source-query-dialog')).toHaveCount(0)

    await page.getByTestId('mapping-tab-columns').click()
    await expect(page.getByTestId('column-mappings-awaiting-preview')).toHaveCount(0)
    await expect(page.getByTestId('column-mappings-table')).toBeVisible()

    // The query's own result columns, offered as source columns — including `Echo`, which exists in
    // no catalog anywhere and could only have come from having run the query.
    const options = await page.getByTestId('column-mapping-source-0').locator('option').allInnerTexts()
    expect(options).toContain('Echo')
  })

  /**
   * Somebody writing SQL gets it wrong several times on the way to right. Each of those is an answer,
   * shown as what the engine said rather than as an empty grid or a failed request.
   */
  test('04 - a query the engine rejects shows its message', async ({ page }) => {
    await stub(page)
    await page.route('**/api/connections/lake/query-preview', (route) => route.fulfill(json({
      source: "live query against 'lake'",
      columns: [],
      rows: [],
      truncated: false,
      error: 'DuckDBException: Parser Error: syntax error at or near "FRM"',
    })))

    await openSourceTab(page)
    await openQueryDialog(page)
    await setQuery(page, 'SELECT * FRM nowhere')
    await page.getByTestId('source-query-preview-button').click()

    await expect(page.getByTestId('source-query-preview-error')).toContainText('Parser Error')
  })

  /**
   * Phase 193S: a query-shaped source is a switch directly on the Source card now, not something
   * decided by picking a reader Kind elsewhere — an operator can turn a plain table mapping into one
   * and back without touching the Pipeline tab at all.
   */
  test('05 - toggling into query mode replaces the table pickers, and back restores them', async ({ page }) => {
    await stub(page)
    await openMapping(page, TABLE_MAPPING_NAME)
    await expect(page.getByTestId('source-table-select')).toBeVisible({ timeout: 20_000 })
    await expect(page.getByTestId('source-query-open-button')).toHaveCount(0)

    await page.getByTestId('source-query-source-toggle').click()
    await expect(page.getByTestId('source-query-open-button')).toBeVisible()
    await expect(page.getByTestId('source-table-select')).toHaveCount(0)

    await page.getByTestId('source-query-source-toggle').click()
    await expect(page.getByTestId('source-table-select')).toBeVisible()
    await expect(page.getByTestId('source-query-open-button')).toHaveCount(0)
  })

  /**
   * The one-click recovery phase 193S asks for: never automatic, and it actually flips the setting a
   * save would persist rather than merely retrying once and forgetting.
   */
  test('06 - a wrapped preview failure offers a one-click retry without subqueries', async ({ page }) => {
    await stub(page)
    await page.route('**/api/connections/lake/query-preview', async (route) => {
      const body = route.request().postDataJSON() as { allowSubquery: boolean }
      if (body.allowSubquery) {
        return route.fulfill(json({
          source: "live query against 'lake'", columns: [], rows: [], truncated: false,
          error: 'this query cannot be used as a subquery',
        }))
      }
      return route.fulfill(json({
        source: "live query against 'lake'", columns: ['Id'], rows: [['1']], truncated: false, error: null,
      }))
    })

    await openSourceTab(page)
    await openQueryDialog(page)
    await expect(page.getByTestId('source-query-allow-subquery-toggle')).toHaveAttribute('aria-pressed', 'true')

    await page.getByTestId('source-query-preview-button').click()
    await expect(page.getByTestId('source-query-preview-error')).toContainText('cannot be used as a subquery')

    await page.getByTestId('source-query-retry-without-subqueries').click()
    await expect(page.getByTestId('source-query-preview')).toBeVisible()
    await expect(page.getByTestId('source-query-allow-subquery-toggle')).toHaveAttribute('aria-pressed', 'false')
  })

  test('07 - the max-rows choice is sent with the preview request', async ({ page }) => {
    const sent = await stub(page)
    await openSourceTab(page)
    await openQueryDialog(page)

    await page.getByTestId('source-query-maxrows-select').selectOption('50')
    await page.getByTestId('source-query-preview-button').click()
    await expect(page.getByTestId('source-query-preview')).toBeVisible()

    expect(sent[0].maxRows).toBe(50)
  })

  /**
   * The blocking guard phase 193S adds at save time: a query edited without a fresh preview must not
   * save silently on whatever metadata an earlier preview captured.
   */
  test('08 - saving a mapping whose query changed since the last preview is blocked, with both exits working', async ({ page }) => {
    let saved: unknown = null
    await stub(page)
    await page.route(`**/api/replications/${REPLICATION_NAME}/table-mappings/${MAPPING_NAME}`, async (route) => {
      if (route.request().method() === 'PUT') {
        saved = route.request().postDataJSON()
        return route.fulfill(json(MAPPING))
      }
      return route.fulfill(json(MAPPING))
    })

    await openSourceTab(page)
    await openQueryDialog(page)
    await setQuery(page, "SELECT Id, Name FROM read_csv('/tmp/edited-not-previewed.csv')")
    await page.getByTestId('source-query-close-button').click()

    await page.getByTestId('save-mapping-button').click()
    await expect(page.getByTestId('stale-query-dialog')).toBeVisible()
    await expect(page.getByTestId('stale-query-warning')).toContainText("haven't been validated")

    // "Run preview instead" reopens the same query editor rather than saving anything.
    await page.getByTestId('stale-query-run-preview').click()
    await expect(page.getByTestId('stale-query-dialog')).toHaveCount(0)
    await expect(page.getByTestId('source-query-dialog')).toBeVisible()
    expect(saved).toBeNull()
    await page.getByTestId('source-query-close-button').click()

    // "Save anyway" proceeds — the explicit, named acknowledgment the design calls for.
    await page.getByTestId('save-mapping-button').click()
    await expect(page.getByTestId('stale-query-dialog')).toBeVisible()
    await page.getByTestId('stale-query-save-anyway').click()
    await expect(page.getByTestId('stale-query-dialog')).toHaveCount(0)
    await expect.poll(() => saved).not.toBeNull()
  })
})

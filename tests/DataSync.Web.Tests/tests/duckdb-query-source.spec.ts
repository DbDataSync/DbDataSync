import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'duck-demo'
const MAPPING_NAME = 'orders'

/**
 * The mapping editor's source tab, for a reader whose configuration is a query — phase 89.
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
    // The replication's reader is the query reader, so the mapping inherits it and the source tab
    // swaps its pickers without the operator having overridden anything.
    reader: { kind: 'DuckDbQuery', parallelism: 1, options: { query: SAVED_QUERY } },
    cache: { kind: 'MsSqlStagingTable', options: {} },
    writer: { kind: 'MsSqlMerge', parallelism: 1, options: {} },
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
  sources: [{ connectionName: null, database: null, schema: '', table: '', filter: null }],
  targets: [{ connectionName: null, database: null, schema: 'dbo', table: 'Orders' }],
  columnMappings: [],
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
    kind: 'DuckDbQuery',
    supportsSegmentation: false,
    detectsDeletes: false,
    parameters: [{
      name: 'query', label: 'Query', description: null, type: 'Sql', required: true,
      cardinality: null, dropdownOptions: null, dropdownLabels: null, default: null,
      layout: null, visible: true, recalc: false,
    }],
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

/** Every query body the page sent to the preview endpoint, in order. */
type Sent = { query: string }[]

async function stub(page: Page): Promise<Sent> {
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
    const body = route.request().postDataJSON() as { query: string }
    sent.push({ query: body.query })
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
  await page.route('**/api/connections/tgt/metadata/databases', (route) => route.fulfill(json(['Warehouse'])))
  await page.route('**/api/connections/tgt/metadata/databases/*/tables', (route) => route.fulfill(json([
    { schema: 'dbo', table: 'Orders' },
  ])))
  await page.route('**/api/connections/tgt/metadata/**/columns', (route) => route.fulfill(json([
    { name: 'Id', nativeType: 'int', isNullable: false, isPrimaryKey: true, isIdentity: false },
    { name: 'Name', nativeType: 'nvarchar(50)', isNullable: true, isPrimaryKey: false, isIdentity: false },
  ])))
  await page.route('**/api/connections/tgt/capabilities', (route) => route.fulfill(json({
    driverType: 'MsSql', readers: [], stagingProviders: [], writers: [],
    supportsConnectionTest: true, supportedProvisioningActions: [],
  })))

  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json([MAPPING_NAME])))
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}`, (route) => route.fulfill(json(MAPPING)))
  // Everything else hanging off the mapping — inferred column types, inferred natural key — is a
  // list this screen can render empty.
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}/**`, (route) => route.fulfill(json([])))
  // Registered *after* that catch-all deliberately: Playwright matches the most recently added route
  // first, so the specific one has to come last or the catch-all swallows it. The editor's tab bar
  // reads a provisioning plan for its badge and needs the report's shape, not an empty array.
  const emptyPlan = { action: '', state: 'UpToDate', steps: [], problems: [] }
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}/provisioning`, (route) =>
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

async function openSourceTab(page: Page) {
  await page.goto(`/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}`)
  await expect(page.getByTestId('source-query-editor').locator('.monaco-editor'))
    .toBeVisible({ timeout: 20_000 })
}

test.describe('duckdb query source', () => {
  test('01 - the source card offers a query editor instead of schema and table pickers', async ({ page }) => {
    await stub(page)
    await openSourceTab(page)

    // The two fields a query source has no answer for are gone.
    await expect(page.getByTestId('source-table-select')).toHaveCount(0)
    await expect(page.getByTestId('source-schema-input')).toHaveCount(0)

    // Connection and database stay: a query still runs somewhere, and config requires both to
    // resolve. Inherited from the replication, so they read rather than pick.
    await expect(page.getByTestId('source-side')).toContainText('lake')
    await expect(page.getByTestId('source-side')).toContainText('memory')

    // And the editor opens on the query the mapping already runs. Asserted on the rendered text
    // rather than on a value, because Monaco has none — the visible lines are the content.
    await expect(page.getByTestId('source-query-editor')).toContainText('read_parquet')

    // The target side is untouched — it is an ordinary table, and this phase changed nothing there.
    await expect(page.getByTestId('target-table-input')).toBeVisible()

    await page.screenshot({ path: path.join(screenshotsDir, '64-duckdb-query-source.png'), fullPage: true })
  })

  /**
   * The property the phase turns on. The editor is changed and never saved; what Preview sends has to
   * be the changed text, and the grid has to show the result of running *that*.
   */
  test('02 - Preview runs the unsaved text in the editor, not the saved config', async ({ page }) => {
    const sent = await stub(page)
    await openSourceTab(page)

    const edited = "SELECT Id, Name FROM read_csv('/tmp/late-arrivals.csv')"
    await setQuery(page, edited)

    await page.getByTestId('source-query-preview-button').click()
    await expect(page.getByTestId('source-query-preview')).toBeVisible()

    // What actually left the browser: the edited text, once, and not the saved query.
    expect(sent).toEqual([{ query: edited }])
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
    // and preview.
    await page.getByTestId('source-query-preview-button').click()
    await expect(page.getByTestId('source-query-preview')).toBeVisible()

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
    await setQuery(page, 'SELECT * FRM nowhere')
    await page.getByTestId('source-query-preview-button').click()

    await expect(page.getByTestId('source-query-preview-error')).toContainText('Parser Error')
  })
})

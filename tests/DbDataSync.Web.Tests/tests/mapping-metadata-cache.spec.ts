import { test, expect, type Page } from '@playwright/test'
import path from 'node:path'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('mapping-metadata-cache')

const REPLICATION_NAME = 'metadata-cache-demo'
const MAPPING_NAME = 'orders'

/**
 * The mapping metadata cache on screen — phase 90.
 *
 * **Stubbed at the network boundary**, for the reason `lag-monitoring.spec.ts` is: the states this
 * card distinguishes are properties of the *payload*. A never-captured mapping, a source that read
 * cleanly beside a target that provisioning has not created, a refresh that found a column widened
 * and a key dropped — producing those against real databases means altering a live schema between
 * two assertions. `MappingMetadataTests` covers the server's half against the real service, real
 * config and a real git commit; this covers the client's half against the shape the server sends,
 * and the two meet at `MetadataRefreshResult`.
 *
 * Every request the screen makes is stubbed, so this file depends on nothing the golden path leaves
 * behind and runs in any order beside it.
 */

const col = (name: string, nativeType: string, isPrimaryKey = false) =>
  ({ name, nativeType, isNullable: false, isPrimaryKey, isIdentity: false })

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
}

/** The mapping as stored: captured once, a while ago, and two columns on each side. */
const STORED_MAPPING = {
  name: MAPPING_NAME,
  sources: [{ connectionName: null, database: null, schema: 'dbo', table: 'Orders', filter: null }],
  targets: [{ connectionName: null, database: null, schema: 'dbo', table: 'Orders' }],
  columnMappings: [{ sourceColumn: 'Id', targetColumn: 'Id', transform: null }],
  sourceColumns: [col('Id', 'int', true), col('Region', 'nvarchar(50)')],
  targetColumns: [col('Id', 'bigint', true), col('Region', 'nvarchar(50)')],
  columnsCapturedUtc: '2026-06-01T09:00:00Z',
}

/**
 * What a refresh found: `Region` widened on the source, `Currency` added, `Retired` gone — and a
 * target that could not be read at all, which must leave its cached columns alone.
 */
const REFRESH_RESULT = {
  mapping: {
    ...STORED_MAPPING,
    sourceColumns: [col('Id', 'int', true), col('Region', 'nvarchar(200)'), col('Currency', 'char(3)')],
    columnsCapturedUtc: new Date().toISOString(),
  },
  source: {
    side: 'source',
    refreshed: true,
    unavailable: null,
    columnCount: 3,
    added: ['Currency'],
    removed: ['Retired'],
    changed: ['Region'],
  },
  target: {
    side: 'target',
    refreshed: false,
    unavailable: "Table 'dbo.Orders' was not found in 'Warehouse'.",
    columnCount: 0,
    added: [],
    removed: [],
    changed: [],
  },
}

const json = (body: unknown) => ({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify(body),
})

async function stub(page: Page, mapping: unknown = STORED_MAPPING) {
  const base = `/api/replications/${REPLICATION_NAME}`

  // **Registration order is precedence, and it runs backwards**: Playwright matches the most
  // recently registered route first, so the catch-all goes on before the specific routes that have
  // to beat it. Registered the other way round, every one of these tests gets `{}` from the refresh.
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}/**`, (route) => route.fulfill(json([])))
  // A real shape, not `{}`. The tab bar's provisioning badge reads `plans.source.steps.length`, and
  // an empty object gets as far as `undefined.steps` — which throws, and React remounts the whole
  // editor in a loop, so every locator on it flickers in and out of the DOM.
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}/provisioning`, (route) => route.fulfill(json({
    source: { steps: [], statements: [] },
    target: { steps: [], statements: [] },
  })))
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}/refresh-metadata`, (route) =>
    route.fulfill(json(REFRESH_RESULT)))
  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json([MAPPING_NAME])))
  await page.route(`**${base}/table-mappings/${MAPPING_NAME}`, (route) => route.fulfill(json(mapping)))
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true,
  })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))

  // The catalog the pickers and the column grid read. Not what the card reports — that comes back
  // from the refresh endpoint — but the screen does not render without it.
  //
  // Each pattern is anchored on `/api/connections/`, deliberately. A bare `**/columns` also matches
  // the *page* being navigated to (`…/mappings/orders/columns`), and Playwright will happily fulfil
  // a document request with a JSON array — which renders the column list as text and nothing else.
  await page.route('**/api/connections', (route) => route.fulfill(json([{ name: 'src' }, { name: 'tgt' }])))
  await page.route('**/api/connections/*/metadata/databases', (route) =>
    route.fulfill(json(['AppDb', 'Warehouse'])))
  await page.route('**/api/connections/*/metadata/databases/*/tables', (route) =>
    route.fulfill(json([{ schema: 'dbo', table: 'Orders' }])))
  await page.route('**/api/connections/*/metadata/databases/*/schemas/*/tables/*/columns', (route) =>
    route.fulfill(json([col('Id', 'int', true), col('Region', 'nvarchar(50)')])))
}

const columnsTab = `/replications/${REPLICATION_NAME}/mappings/${MAPPING_NAME}/columns`

test.describe('mapping metadata cache', () => {
  test('01 - the card states what is cached and when it was captured', async ({ page }) => {
    await stub(page)
    await page.goto(columnsTab)

    const card = page.getByTestId('cached-metadata-card')
    await expect(card).toBeVisible()

    // Both sides' counts, from the stored cache rather than from the live catalog call beside it.
    await expect(page.getByTestId('cached-metadata-source')).toContainText('2 columns')
    await expect(page.getByTestId('cached-metadata-target')).toContainText('2 columns')

    // The age of the picture, which is the thing that makes staleness a decision rather than a
    // surprise — and the sentence saying nothing else will update it.
    await expect(card).toContainText('captured')
    await expect(card).toContainText('only Refresh updates it')

    await page.screenshot({ path: path.join(screenshotsDir, '62-cached-metadata-card.png'), fullPage: true })
  })

  test('02 - a mapping that has never been captured says so rather than showing zero', async ({ page }) => {
    // The pre-phase-90 row, and the state every existing mapping is in: nothing was backfilled.
    await stub(page, {
      ...STORED_MAPPING, sourceColumns: [], targetColumns: [], columnsCapturedUtc: null,
    })
    await page.goto(columnsTab)

    await expect(page.getByTestId('cached-metadata-card')).toContainText('never captured')
    // And the rest of the editor is unaffected by the empty cache — nothing reads it yet.
    await expect(page.getByTestId('column-mappings-table')).toBeVisible()
  })

  test('03 - Refresh names the columns that moved, per side', async ({ page }) => {
    await stub(page)
    await page.goto(columnsTab)

    await page.getByTestId('refresh-metadata-button').click()

    // Named, not counted. "1 changed" is a number somebody has to go and investigate; this is the
    // investigation.
    const source = page.getByTestId('cached-metadata-source-outcome')
    await expect(source).toContainText('added Currency')
    await expect(source).toContainText('removed Retired')
    await expect(source).toContainText('changed Region')

    // The refreshed count replaces the stored one, from the mapping the endpoint sent back.
    await expect(page.getByTestId('cached-metadata-source')).toContainText('3 columns')

    await page.screenshot({ path: path.join(screenshotsDir, '63-cached-metadata-refreshed.png'), fullPage: true })
  })

  test('04 - a side that could not be read says so and keeps its columns', async ({ page }) => {
    await stub(page)
    await page.goto(columnsTab)

    await page.getByTestId('refresh-metadata-button').click()

    const target = page.getByTestId('cached-metadata-target-outcome')
    await expect(target).toContainText('NOT READ')
    await expect(target).toHaveAttribute('title', /was not found/)

    // **The assertion this test exists for.** The refresh reported zero columns for the target, and
    // the card still shows two — because an unreadable side keeps its cached picture rather than
    // being emptied. A target provisioning has yet to create is not a target with no columns.
    await expect(page.getByTestId('cached-metadata-target')).toContainText('2 columns')

    // And the two sides reached different outcomes on the one press, which is the whole reason one
    // button reports two results.
    await expect(page.getByTestId('cached-metadata-source-outcome')).not.toContainText('NOT READ')
  })
})

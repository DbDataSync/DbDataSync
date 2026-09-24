import { test, expect, type Page } from '@playwright/test'
import { screenshotDir } from '../screenshots'

const screenshotsDir = screenshotDir('mapping-relationships')

const REPLICATION_NAME = 'relationships-demo'
const MAPPING = 'dbo.Orders'

/**
 * Declaring a relationship and mapping a column through it — phase 189J.
 *
 * **Stubbed at the network boundary**, following `mapping-column-add.spec.ts`'s own precedent: what is
 * under test is the Relationships card and the Column Mapping editor's own grouped picker, and the save
 * round-trip below captures the actual PUT body, which is where "both survive being saved" is really
 * decided — not something to infer from what the screen shows before a reload.
 */

const SOURCE_COLUMNS = [
  { name: 'Id', nativeType: 'int', isNullable: false, isPrimaryKey: true, isIdentity: true },
  { name: 'RegionId', nativeType: 'int', isNullable: true, isPrimaryKey: false, isIdentity: false },
]

const REGION_COLUMNS = [
  { name: 'Id', nativeType: 'int', isNullable: false, isPrimaryKey: true, isIdentity: false },
  { name: 'Label', nativeType: 'nvarchar(50)', isNullable: false, isPrimaryKey: false, isIdentity: false },
]

const TARGET_COLUMNS = [
  { name: 'Id', nativeType: 'int', isNullable: false, isPrimaryKey: true, isIdentity: false },
  { name: 'RegionLabel', nativeType: 'nvarchar(50)', isNullable: true, isPrimaryKey: false, isIdentity: false },
]

const MAPPING_BODY = {
  name: MAPPING,
  sources: [{ connectionName: null, database: null, schema: 'dbo', table: 'Orders', filter: null }],
  targets: [{ connectionName: null, database: null, schema: 'dbo', table: 'Orders' }],
  columnMappings: [
    { sourceColumn: 'Id', targetColumn: 'Id', transform: null, targetType: null, renames: [] },
    { sourceColumn: 'RegionLabel', targetColumn: 'RegionLabel', transform: null, targetType: null, renames: [] },
  ],
  relationships: [],
  sourceColumns: [],
  targetColumns: [],
  relationshipColumns: {},
  columnsCapturedUtc: null,
  scripts: {},
  hooks: {},
  checks: [],
  segmenting: [],
  provisioning: {},
  traceTiming: false,
  notes: null,
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
    target: { connectionName: 'tgt', database: 'AppDb' },
  },
  provisioning: {},
  scripts: {},
  hooks: {},
}

const json = (body: unknown) => ({
  status: 200,
  contentType: 'application/json',
  body: JSON.stringify(body),
})

interface Saved { body: Record<string, unknown> | null }

async function stub(page: Page): Promise<Saved> {
  const base = `/api/replications/${REPLICATION_NAME}`
  const saved: Saved = { body: null }

  // Told apart by the table name in the path: Orders is the primary source (and the target, both on
  // the same connection/database here), Region is the relationship's own foreign table.
  await page.route(/\/metadata\/databases\/[^/]+\/schemas\/[^/]+\/tables\/([^/]+)\/columns/, (route) => {
    const table = decodeURIComponent(route.request().url().match(/\/tables\/([^/]+)\/columns/)![1])
    return route.fulfill(json(table === 'Region' ? REGION_COLUMNS : (
      route.request().url().includes('schemas/dbo/tables/Orders/columns') ? SOURCE_COLUMNS : TARGET_COLUMNS
    )))
  })
  await page.route(/\/metadata\/databases\/[^/]+\/tables$/, (route) => route.fulfill(json([
    { schema: 'dbo', table: 'Orders' },
    { schema: 'dbo', table: 'Region' },
  ])))
  await page.route(/\/metadata\/databases$/, (route) => route.fulfill(json(['AppDb'])))

  await page.route(`**${base}/table-mappings/*/provisioning/inferred-column-types`, (route) =>
    route.fulfill(json(SOURCE_COLUMNS.map((c) => ({
      sourceColumn: c.name, sourceType: c.nativeType, targetType: c.nativeType, fidelity: null, problem: null,
    })))))
  await page.route(`**${base}/table-mappings`, (route) => route.fulfill(json([MAPPING])))
  await page.route(`**${base}/table-mappings/*`, (route) => {
    if (route.request().method() === 'PUT') {
      saved.body = route.request().postDataJSON()
      return route.fulfill(json(saved.body))
    }
    return route.fulfill(json(MAPPING_BODY))
  })
  await page.route(`**${base}/status`, (route) => route.fulfill(json({
    running: false, enabled: true, paused: false, pauseNote: null, shouldRun: true,
  })))
  await page.route(`**${base}/lag`, (route) => route.fulfill(json({
    mappings: {}, lowestLagMs: null, highestLagMs: null, rangeIncludesEstimates: false,
  })))
  await page.route(`**${base}/metrics*`, (route) => route.fulfill(json({
    runs: 0, failures: 0, rowsWritten: 0, rowsRead: 0, buckets: [],
    processingP50Ms: null, processingP95Ms: null, processingMaxMs: null, lastCompletedPassUtc: null,
  })))
  await page.route(/\/runs\?limit=/, (route) => route.fulfill(json({ runs: [], nextCursor: null })))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))

  return saved
}

test('declaring a relationship and mapping a column through it round-trips through a save', async ({ page }) => {
  const saved = await stub(page)

  await page.goto(`/replications/${REPLICATION_NAME}/mappings/${encodeURIComponent(MAPPING)}/relationships`)
  await expect(page.getByTestId('relationships-card')).toBeVisible({ timeout: 20_000 })

  // Declare the relationship: name, foreign table, one join key.
  await page.getByTestId('add-relationship-button').click()
  await page.getByTestId('relationship-name-0').fill('region')
  await page.getByTestId('relationship-table-0').selectOption({ label: 'dbo.Region' })
  await page.getByTestId('relationship-0-join-local-0').selectOption('RegionId')
  await page.getByTestId('relationship-0-join-foreign-0').selectOption('Id')

  await page.screenshot({ path: `${screenshotsDir}/01-relationship-declared.png`, fullPage: true })

  // Map RegionLabel through it, on the Column Mapping tab.
  await page.getByTestId('mapping-tab-columns').click()
  await expect(page.getByTestId('column-mappings-table')).toBeVisible()

  await page.getByTestId('add-target-column-input').fill('RegionLabel')
  await page.getByTestId('add-target-column-button').click()
  await page.getByTestId('column-mapping-source-1').selectOption({ label: 'Label' })

  await page.screenshot({ path: `${screenshotsDir}/02-column-mapped-through-relationship.png`, fullPage: true })

  await page.getByTestId('save-mapping-button').click()
  await expect.poll(() => saved.body).not.toBeNull()

  const body = saved.body as unknown as {
    relationships: { name: string; schema: string; table: string; joinKeys: { localColumn: string; foreignColumn: string }[] }[]
    columnMappings: { sourceColumn: string; targetColumn: string; relationship?: string | null }[]
  }

  expect(body.relationships).toEqual([
    { name: 'region', schema: 'dbo', table: 'Region', joinKeys: [{ localColumn: 'RegionId', foreignColumn: 'Id' }] },
  ])
  const regionMapping = body.columnMappings.find((m) => m.targetColumn === 'RegionLabel')
  expect(regionMapping).toMatchObject({ sourceColumn: 'Label', relationship: 'region' })
})

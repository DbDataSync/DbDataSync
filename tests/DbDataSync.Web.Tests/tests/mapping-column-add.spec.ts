import { test, expect, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const screenshotsDir = path.join(__dirname, '..', 'screenshots')
fs.mkdirSync(screenshotsDir, { recursive: true })

const REPLICATION_NAME = 'column-add-demo'
const MAPPING = 'dbo.Orders'

/**
 * Adding a target column to a mapping — phase 96.
 *
 * The control existed before this phase and offered the **target catalog** minus what was already
 * mapped, gated on that list being non-empty. Two consequences: remove a row whose target column is
 * not on the target and there was nothing left to add it back with, and a source table that gained a
 * column could not be mapped to a new target column *at all* on an existing target. The server could
 * always do it — `AlterTargetTablePlanner` emits `RenderAddColumn` for exactly this case — so the
 * editor was the only thing preventing anyone from asking.
 *
 * **Stubbed at the network boundary**, following `lag-monitoring.spec.ts`. What is under test is one
 * control's behaviour and the validation behind it; driving it through real catalogs would assert the
 * same thing about the same payload, more slowly. The save round-trip below captures the actual PUT
 * body, which is where "the row I added survives being saved" is really decided.
 */

const SOURCE_COLUMNS = [
  { name: 'Id', nativeType: 'int', isNullable: false, isPrimaryKey: true, isIdentity: true },
  { name: 'Total', nativeType: 'decimal(18,2)', isNullable: false, isPrimaryKey: false, isIdentity: false },
  // On the source and not on the target: the column an existing mapping cannot currently reach.
  { name: 'Currency', nativeType: 'nvarchar(3)', isNullable: true, isPrimaryKey: false, isIdentity: false },
]

const TARGET_COLUMNS = [
  { name: 'Id', nativeType: 'int', isNullable: false, isPrimaryKey: true, isIdentity: false },
  { name: 'Total', nativeType: 'decimal(18,2)', isNullable: false, isPrimaryKey: false, isIdentity: false },
]

/** Every catalog column already mapped — the state that used to hide the control entirely. */
const MAPPING_BODY = {
  name: MAPPING,
  sources: [{ connectionName: null, database: null, schema: 'dbo', table: 'Orders', filter: null }],
  targets: [{ connectionName: null, database: null, schema: 'dbo', table: 'Orders' }],
  columnMappings: [
    { sourceColumn: 'Id', targetColumn: 'Id', transform: null, targetType: null, renames: [] },
    { sourceColumn: 'Total', targetColumn: 'Total', transform: null, targetType: null, renames: [] },
  ],
  sourceColumns: [],
  targetColumns: [],
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
    reader: { kind: 'MsSqlChangeTracking', parallelism: 1, options: {} },
    cache: { kind: 'MsSqlStagingTable', options: {} },
    writer: { kind: 'MsSqlMerge', parallelism: 1, options: {} },
  },
  endpoints: {
    source: { connectionName: 'src', database: 'AppDb' },
    target: { connectionName: 'tgt', database: 'Warehouse' },
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

/** The body of the last mapping PUT, so a save can be asserted on rather than assumed. */
interface Saved { body: Record<string, never> | null }

async function stub(page: Page): Promise<Saved> {
  const base = `/api/replications/${REPLICATION_NAME}`
  const saved: Saved = { body: null }

  // Both sides' catalogs, told apart by the database in the path — the target has two columns, the
  // source has three, and `Currency` is the one only the source has.
  await page.route(/\/metadata\/databases\/[^/]+\/schemas\/[^/]+\/tables\/[^/]+\/columns/, (route) =>
    route.fulfill(json(route.request().url().includes('Warehouse') ? TARGET_COLUMNS : SOURCE_COLUMNS)))
  await page.route(/\/metadata\/databases\/[^/]+\/tables$/, (route) => route.fulfill(json([
    { schema: 'dbo', table: 'Orders' },
  ])))
  await page.route(/\/metadata\/databases$/, (route) => route.fulfill(json(['AppDb', 'Warehouse'])))

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
  await page.route(/\/runs\?limit=/, (route) => route.fulfill(json([])))
  await page.route(`**${base}`, (route) => route.fulfill(json(TASK)))

  return saved
}

/**
 * `EditableValue` exposes its displayed value as `${testId}-text` and keeps the bare `${testId}` for
 * the input it swaps in while editing. Asserting on the bare id would pass vacuously against a row
 * that is there — which is exactly what a "no row was appended" assertion must not do.
 */
const openEditor = async (page: Page) => {
  await page.goto(`/replications/${REPLICATION_NAME}/mappings/${encodeURIComponent(MAPPING)}/columns`)
  await expect(page.getByTestId('column-mappings-table')).toBeVisible({ timeout: 20_000 })
}

test.describe('adding a target column to a mapping', () => {
  /**
   * The regression that produced the original report. Both of the target's columns are mapped, so the
   * old control — which offered catalog columns minus mapped ones — rendered nothing at all, and an
   * operator who had just removed a row had no way back.
   */
  test('01 - the control is there even when every catalog column is already mapped', async ({ page }) => {
    await stub(page)
    await openEditor(page)

    await expect(page.getByTestId('add-target-column-input')).toBeVisible()
    await expect(page.getByTestId('add-target-column-button')).toBeVisible()
  })

  /**
   * The wider gap underneath the report: a source column with no target counterpart could not be
   * mapped at all. The badge on the resulting row is what says the difference — provisioning will add
   * the column — which is why it reports an intention now rather than the old `MISSING` fault.
   */
  test('02 - a column the target does not have can be added, and says it will be created', async ({ page }) => {
    await stub(page)
    await openEditor(page)

    await page.getByTestId('add-target-column-input').fill('Currency')
    await page.getByTestId('add-target-column-button').click()

    // The row exists, and took the same-named source column as its suggestion.
    await expect(page.getByTestId('column-mapping-target-2-text')).toContainText('Currency')
    await expect(page.getByTestId('column-mapping-source-2')).toHaveValue('Currency')

    // An intention, not a fault: provisioning adds it when applied.
    const badge = page.getByTestId('column-mapping-will-add-Currency')
    await expect(badge).toBeVisible()
    await expect(badge).toHaveText('WILL ADD')

    await page.screenshot({ path: path.join(screenshotsDir, '73-column-will-add.png'), fullPage: true })
  })

  /**
   * Two rows writing the same target column is a config error that fails at staging with a message
   * naming neither of them, so the editor refuses it — out loud. Silently doing nothing is
   * indistinguishable from a broken button.
   */
  test('03 - a duplicate target column is refused, with a reason, and adds no row', async ({ page }) => {
    await stub(page)
    await openEditor(page)

    await page.getByTestId('add-target-column-input').fill('Total')
    await page.getByTestId('add-target-column-button').click()

    await expect(page.getByTestId('add-target-column-problem')).toContainText('already mapped')
    // Still the two rows it started with — a third was not appended.
    await expect(page.getByTestId('column-mapping-target-2-text')).toHaveCount(0)
  })

  test('04 - an empty name is refused rather than adding a nameless row', async ({ page }) => {
    await stub(page)
    await openEditor(page)

    await page.getByTestId('add-target-column-input').fill('   ')
    await page.getByTestId('add-target-column-button').click()

    await expect(page.getByTestId('add-target-column-problem')).toBeVisible()
    await expect(page.getByTestId('column-mapping-target-2-text')).toHaveCount(0)
  })

  /**
   * The reported case end to end: remove a row, put it back by name, and save. The assertion is on
   * the PUT body rather than on the screen, because "it came back" is only true if it survives the
   * round trip.
   */
  test('05 - a column removed and re-added by name round-trips through a save', async ({ page }) => {
    const saved = await stub(page)
    await openEditor(page)

    await page.getByTestId('column-mappings-table').getByRole('button', { name: 'Remove' }).last().click()
    await expect(page.getByTestId('column-mapping-target-1-text')).toHaveCount(0)

    await page.getByTestId('add-target-column-input').fill('Total')
    await page.getByTestId('add-target-column-button').click()
    await expect(page.getByTestId('column-mapping-target-1-text')).toContainText('Total')

    await page.getByTestId('save-mapping-button').click()
    await expect.poll(() => saved.body).not.toBeNull()

    const columns = (saved.body as unknown as { columnMappings: { sourceColumn: string; targetColumn: string }[] })
      .columnMappings
    expect(columns.map((c) => [c.sourceColumn, c.targetColumn])).toEqual([['Id', 'Id'], ['Total', 'Total']])
  })
})

import type { Page } from '@playwright/test'

/**
 * Phase 134: a mapping whose reader captures its own position (Change Tracking, CDC, TriggerAudit)
 * never loads on its own first Primary pass — that pass only captures a position and requests a Bulk
 * Load, which runs concurrently. A run list containing a terminal `Succeeded` Primary run therefore
 * does not mean the data has landed; a test that reads real target rows right after seeing `Succeeded`
 * races that Bulk Load.
 *
 * This is the TypeScript side of `MappingLoadWaiter.WaitForLoadToCompleteAsync`
 * (tests/DbDataSync.Api.Tests/MappingLoadWaiter.cs, phase 141) — same idea, same endpoint: poll the
 * mapping's own read-state until its hold clears, since `SchedulerService.FilterHeld` is the one place
 * that ever waits for this and it does the identical check before handing a real mapping a further pass.
 */
export async function waitForLoadToComplete(
  page: Page, replicationName: string, mappingName: string, timeoutMs = 30_000,
): Promise<void> {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const response = await page.request.get(
      `/api/replications/${replicationName}/table-mappings/${mappingName}/read-state`)
    const state: { hold: string } = await response.json()
    if (state.hold !== 'Loading') return
    await new Promise((resolve) => setTimeout(resolve, 250))
  }

  throw new Error(`'${mappingName}' on '${replicationName}' was still Loading after ${timeoutMs}ms.`)
}

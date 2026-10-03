import type { BrowserContext, Page, TestInfo } from '@playwright/test'
import { writeFileSync } from 'node:fs'
import { performance } from 'node:perf_hooks'

const anchor = () => {
  const wallBefore = Date.now(), monotonic = performance.now(), wallAfter = Date.now()
  return { wallBefore, monotonic, wallAfter, timeOrigin: performance.timeOrigin, wallResolutionAtLeastMs: 1 }
}

type BrowserRow = {
  kind: 'initial' | 'periodic' | 'terminal', seq: number, wallBefore: number, monotonic: number,
  wallAfter: number, timeOrigin: number, visibility: 'hidden' | 'visible', focused: boolean,
  maxPeriodDriftMs: number, skippedWhileBindingPending: number, browserSampleWorkMs: number,
  terminalReason: 'tick-cap' | null,
}

/** Diagnostic only. No awaited heartbeat, readiness condition, product assertion or DOM/input values. */
export async function observeFmsPublicHeartbeat(context: BrowserContext, info: TestInfo) {
  const binding = '__fmsPublicHeartbeat'
  const maxRows = 1024, maxPages = 8, maxDocuments = 16
  const pages = new Map<Page, number>()
  const documents = new Map<string, { page: number, timeOrigin: number, terminalReceived: boolean }>()
  const pageClosures: { page: number, anchor: ReturnType<typeof anchor> }[] = []
  const closeHandlers = new Map<Page, () => void>()
  const rows: { page: number, browser: BrowserRow, workerArrival: ReturnType<typeof anchor>, workerRecordWorkMs: number }[] = []
  const errors: { stage: string, code: string }[] = []
  let finished = false, capped = false, rejected = 0
  const started = anchor()
  const onPage = (page: Page) => {
    if (pages.has(page)) return
    if (pages.size === maxPages) { capped = true; rejected++; return }
    const id = pages.size + 1
    pages.set(page, id)
    const close = () => { if (!finished) pageClosures.push({ page: id, anchor: anchor() }) }
    closeHandlers.set(page, close)
    page.on('close', close)
  }
  context.pages().forEach(onPage)
  context.on('page', onPage)
  await context.exposeBinding(binding, ({ page, frame }, value: unknown) => {
    if (finished) return false
    const workerArrival = anchor()
    if (frame !== page.mainFrame()) return false
    onPage(page)
    const id = pages.get(page)
    if (!id) return false
    const b = value as BrowserRow
    const numeric = ['seq', 'wallBefore', 'monotonic', 'wallAfter', 'timeOrigin', 'maxPeriodDriftMs', 'skippedWhileBindingPending', 'browserSampleWorkMs'] as const
    if (!b || !['initial', 'periodic', 'terminal'].includes(b.kind)
      || !['hidden', 'visible'].includes(b.visibility) || typeof b.focused !== 'boolean'
      || !numeric.every(key => Number.isFinite(b[key])) || !Number.isInteger(b.seq) || b.seq < 0 || b.seq > 180
      || ![null, 'tick-cap'].includes(b.terminalReason)) {
      errors.push({ stage: 'receiver', code: 'invalid-public-heartbeat-shape' })
      return false
    }
    const key = `${id}:${b.timeOrigin}`
    if (!documents.has(key)) {
      if (documents.size === maxDocuments) { capped = true; rejected++; return false }
      documents.set(key, { page: id, timeOrigin: b.timeOrigin, terminalReceived: false })
    }
    if (rows.length === maxRows) { capped = true; rejected++; return false }
    // Whitelist fields; do not retain URLs, titles, headers, credentials, DOM or error strings.
    const browser: BrowserRow = { kind: b.kind, seq: b.seq, wallBefore: b.wallBefore, monotonic: b.monotonic,
      wallAfter: b.wallAfter, timeOrigin: b.timeOrigin, visibility: b.visibility, focused: b.focused,
      maxPeriodDriftMs: b.maxPeriodDriftMs, skippedWhileBindingPending: b.skippedWhileBindingPending,
      browserSampleWorkMs: b.browserSampleWorkMs, terminalReason: b.terminalReason }
    rows.push({ page: id, browser, workerArrival, workerRecordWorkMs: performance.now() - workerArrival.monotonic })
    if (b.kind === 'terminal') documents.get(key)!.terminalReceived = true
    return true
  })
  await context.addInitScript(({ bindingName }) => {
    if (window !== window.top) return
    let seq = 0, pending = false, stopped = false, terminalSent = false, skipped = 0, maxDrift = 0
    let lastTick = performance.now()
    let timer: ReturnType<typeof setInterval>
    const stop = () => { stopped = true; clearInterval(timer) }
    const deliver = (kind: BrowserRow['kind']) => {
      if (pending) { skipped++; return }
      if (kind === 'terminal') terminalSent = true
      const sampleStart = performance.now(), wallBefore = Date.now(), monotonic = performance.now(), wallAfter = Date.now()
      const row: BrowserRow = { kind, seq, wallBefore, monotonic, wallAfter, timeOrigin: performance.timeOrigin,
        visibility: document.visibilityState, focused: document.hasFocus(), maxPeriodDriftMs: maxDrift,
        skippedWhileBindingPending: skipped, browserSampleWorkMs: performance.now() - sampleStart,
        terminalReason: kind === 'terminal' ? 'tick-cap' : null }
      const send = (window as unknown as Record<string, (payload: BrowserRow) => Promise<boolean>>)[bindingName]
      pending = true
      try {
        void send(row).then(accepted => {
          pending = false
          if (!accepted) { stop(); return }
          if (stopped && !terminalSent) deliver('terminal')
        }, () => { pending = false; stop() })
      } catch { pending = false; stop() }
    }
    timer = setInterval(() => {
      const now = performance.now()
      maxDrift = Math.max(maxDrift, now - lastTick - 1000)
      lastTick = now
      seq++
      if (seq === 180) { stop(); deliver('terminal') }
      else deliver('periodic')
    }, 1000)
    deliver('initial')
  }, { bindingName: binding })
  return { finish(ownerBodyThrew: boolean) {
    if (finished) return
    finished = true
    context.off('page', onPage)
    closeHandlers.forEach((handler, page) => page.off('close', handler))
    const record = { schema: 'aerolink.fms-public-heartbeat.v1', status: errors.length ? 'observation-failed' : 'retained',
      test: { id: info.testId, project: info.project.name, workerIndex: info.workerIndex,
        parallelIndex: info.parallelIndex, retry: info.retry, statusAtFinish: info.status,
        expectedStatus: info.expectedStatus, finalNativeOutcomeOwnedByReporter: true },
      browser: { version: context.browser()?.version() ?? null,
        type: context.browser()?.browserType().name() ?? null,
        executableIdentity: null, physicalGpuProof: null, frameOrPaintProof: null },
      started, ended: anchor(), requestedPeriodMs: 1000, perDocumentTickCap: 180, maxRows, maxPages, maxDocuments,
      rows, documents: [...documents.values()], pageClosures, errors, capped, rejected, ownerBodyThrew,
      missing: { unobservedDocumentCount: null, undeliveredBrowserTickCount: null,
        documentsWithoutTerminal: [...documents.values()].filter(d => !d.terminalReceived).length },
      coverageVerified: false, terminal: { kind: 'worker-finish', anchor: anchor(), awaitedBrowserFlush: false },
      boundaries: ['Timers are public main-thread availability, not frame paint or GPU telemetry.',
        'One outstanding binding per document; skipped ticks are cumulative observations, not zero delay.',
        'Navigation/closure/timeout can censor a document, buffered delivery and browser terminal.',
        'No catch-up sampling, drain, heartbeat wait, action fence, synchronized-clock or IPC-latency claim.',
        'Observer installation/recording/serialization add overhead within the original180s budget.',
        'Existing native report owns outcome; no product acceptance is inferred from this record.'] }
    writeFileSync(info.outputPath('fms-public-browser-heartbeat.json'), JSON.stringify(record, null, 2))
    if (errors.length && !ownerBodyThrew) throw new Error('Public heartbeat diagnostic observation failed; numeric artifact retained')
  } }
}
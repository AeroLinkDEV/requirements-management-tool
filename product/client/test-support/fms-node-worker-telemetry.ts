import { writeFileSync } from 'node:fs'
import { monitorEventLoopDelay, performance } from 'node:perf_hooks'
import type { TestInfo } from '@playwright/test'

function clockAnchor() {
  const wallBefore = Date.now(), monotonic = performance.now(), wallAfter = Date.now()
  return { wallBefore, monotonic, wallAfter, timeOrigin: performance.timeOrigin,
    bracketMs: wallAfter - wallBefore, wallResolutionAtLeastMs: 1 }
}

/** Bounded evidence from this test worker; no browser, protocol or product instrumentation. */
export function observeFmsNodeWorker(info: Pick<TestInfo, 'outputPath' | 'workerIndex' | 'parallelIndex' | 'retry'>) {
  const resolutionMs = 20, requestedPeriodMs = 1000, maxPeriodicRows = 220
  const histogram = monitorEventLoopDelay({ resolution: resolutionMs })
  let last = { at: performance.now(), wall: Date.now(), cpu: process.cpuUsage(), elu: performance.eventLoopUtilization() }
  let timer: ReturnType<typeof setInterval> | undefined
  let stopped = false, finished = false

  function sample(kind: 'periodic' | 'terminal') {
    const current = { at: performance.now(), wall: Date.now(), cpu: process.cpuUsage(), elu: performance.eventLoopUtilization() }
    const elapsedMs = current.at - last.at, elapsedWallMs = current.wall - last.wall
    const cpuUserUs = current.cpu.user - last.cpu.user, cpuSystemUs = current.cpu.system - last.cpu.system
    const elu = performance.eventLoopUtilization(current.elu, last.elu)
    const count = Number(histogram.count)
    const nsToMs = (value: number) => Number.isFinite(value) ? value / 1e6 : null
    const row = { kind, anchor: clockAnchor(), fromMonotonic: last.at, toMonotonic: current.at,
      elapsedMs, elapsedWallMs, requestedPeriodMs: kind === 'periodic' ? requestedPeriodMs : null,
      actualPeriodDriftMs: kind === 'periodic' ? elapsedMs - requestedPeriodMs : null,
      wallMinusMonotonicElapsedMs: elapsedWallMs - elapsedMs,
      cpuUserUs, cpuSystemUs, cpuTotalUs: cpuUserUs + cpuSystemUs,
      processCpuPerActualElapsedRatio: elapsedMs > 0 ? (cpuUserUs + cpuSystemUs) / (elapsedMs * 1000) : null,
      eluDelta: { activeMs: elu.active, idleMs: elu.idle, utilization: elu.utilization },
      eventLoopDelay: { count, minMs: count ? nsToMs(histogram.min) : null, maxMs: count ? nsToMs(histogram.max) : null,
        meanMs: count ? nsToMs(histogram.mean) : null, stddevMs: count ? nsToMs(histogram.stddev) : null,
        p50Ms: count ? nsToMs(histogram.percentile(50)) : null, p95Ms: count ? nsToMs(histogram.percentile(95)) : null,
        p99Ms: count ? nsToMs(histogram.percentile(99)) : null, exceeds: Number(histogram.exceeds) },
      samplerSynchronousMs: 0 }
    histogram.reset()
    last = current
    row.samplerSynchronousMs = performance.now() - current.at
    return row
  }

  const record = { schema: 'aerolink.fms-node-worker-telemetry.v1', status: 'incomplete',
    scope: 'Playwright test-worker process', pid: process.pid, nodeVersion: process.version,
    workerIndex: info.workerIndex, parallelIndex: info.parallelIndex, retry: info.retry,
    resolutionMs, requestedPeriodMs, maxPeriodicRows, started: clockAnchor(),
    rows: [] as ReturnType<typeof sample>[], terminal: null as ReturnType<typeof sample> | null,
    errors: [] as { stage: string, name: string, message: string }[], capped: false, droppedPeriodicRows: 0,
    ended: null as ReturnType<typeof clockAnchor> | null, ownerBodyThrew: false,
    boundaries: {
      eventLoopDelay: 'Raw delays at 20 ms resolution, not pure blocking duration. Actual gaps are not filled or caught up.',
      elu: 'Event-loop active versus idle time, not CPU utilization.',
      cpu: 'Process-wide test-worker CPU includes its threads; actual elapsed-time ratio may exceed 1. It is not browser CPU or GPU use.',
      clocks: 'Node wall brackets have at least 1 ms granularity and may reflect clock adjustment. No browser clock is sampled; these anchors do not prove synchronized clocks, IPC latency or causality.',
      overhead: 'Histogram 20 ms and unref 1 Hz sampler add overhead. Serialization/write occurs after monitoring stops, outside owner actions but within the original test budget.',
      censoring: 'Histogram stops at the row cap; later terminal CPU/ELU remains but histogram coverage is censored. Process kill or timeout can prevent the final artifact.',
      testOutcome: 'This artifact describes observation completion only. ownerBodyThrew records a caught body error; the native Playwright report owns the authoritative test outcome, including external timeout.',
    } }
  const retainError = (stage: string, error: unknown) => {
    record.errors.push({ stage, name: error instanceof Error ? error.name : 'Error',
      message: error instanceof Error ? error.message : 'Unknown telemetry error' })
  }
  const stop = () => {
    if (!stopped) { stopped = true; clearInterval(timer); histogram.disable() }
  }
  try {
    histogram.enable()
    timer = setInterval(() => {
      try {
        if (record.rows.length >= maxPeriodicRows) { record.capped = true; record.droppedPeriodicRows++; stop(); return }
        record.rows.push(sample('periodic'))
        if (record.rows.length === maxPeriodicRows) { record.capped = true; stop() }
      } catch (error) { retainError('periodic', error); stop() }
    }, requestedPeriodMs)
    timer.unref()
  } catch (error) { stop(); throw error }

  return { finish(ownerBodyThrew: boolean) {
    if (finished) return
    finished = true
    stop()
    try { record.terminal = sample('terminal') } catch (error) { retainError('terminal', error) }
    record.ended = clockAnchor()
    record.ownerBodyThrew = ownerBodyThrew
    record.status = record.errors.length ? 'observation-failed' : 'complete'
    writeFileSync(info.outputPath('fms-node-worker-telemetry.json'), JSON.stringify(record, null, 2))
    if (record.errors.length) throw new Error('FMS Node worker telemetry failed; observation errors retained')
  } }
}

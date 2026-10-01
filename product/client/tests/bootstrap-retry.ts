import { test } from '@playwright/test'
import type { Page, Request } from '@playwright/test'
import { SNAPSHOT_SCRIPT, runPowerShell } from '../scripts/socket-snapshot-reporter.mjs'

// #986: on the hosted Windows runners a loopback load intermittently fails with net::ERR_NO_BUFFER_SPACE (WSAENOBUFS):
// about 22 times in 1,582 browser jobs (29 Sep to 1 Oct). Usually page.goto itself succeeds and a bootstrap resource
// fails (the entry bundle, a Vite dependency, @react-refresh), so the app never mounts and login() waits out its 15 s.
// Port use, TIME_WAIT, memory and non-paged pool are all far from their limits in every snapshot taken so far.
//
// This retries the login bootstrap, and nothing else, when exactly that error is seen during the failed attempt: up to
// twice, with a short backoff. Every retry is annotated on the test and logged, with a socket snapshot taken at that
// moment (the reporter's snapshot comes only after the test ends), so a recovered run still shows it in the report. Any
// other failure is thrown at once, unchanged.

export const BUFFER_SPACE = 'net::ERR_NO_BUFFER_SPACE'
export const BUFFER_SPACE_BACKOFF_MS = [500, 2_000] as const
export const BUFFER_SPACE_ANNOTATION = 'net::ERR_NO_BUFFER_SPACE retry (#986)'

export type Annotation = { type: string; description: string }
export type RetryDeps = {
  annotate: (annotation: Annotation) => void
  log: (line: string) => void
  sleep: (ms: number) => Promise<void>
  /** The host's socket state now; null where it is not captured (off CI, or not Windows). */
  snapshot: () => unknown
}

const defaultDeps = (): RetryDeps => ({
  annotate: annotation => test.info().annotations.push(annotation),
  log: line => console.log(line),
  sleep: ms => new Promise(resolve => setTimeout(resolve, ms)),
  snapshot: () => (process.env.CI === 'true' && process.platform === 'win32' ? runPowerShell(SNAPSHOT_SCRIPT) : null),
})

/** The error's own text carries the buffer-space failure (page.goto rejects with "page.goto: net::ERR_NO_BUFFER_SPACE at ..."). */
const namesBufferSpace = (error: unknown) => error instanceof Error && error.message.includes(BUFFER_SPACE)

/**
 * Runs `attempt`; if it fails and, during that attempt, page.goto failed or any request failed with exactly
 * net::ERR_NO_BUFFER_SPACE, runs it again, at most twice more. Any other failure, or a failure after the last retry, is
 * thrown as it was.
 */
export async function retryOnBufferSpace<T>(
  page: Pick<Page, 'on' | 'off'>, step: string, attempt: () => Promise<T>, deps: RetryDeps = defaultDeps(),
): Promise<T> {
  for (let retry = 0; ; retry += 1) {
    const failed: string[] = []
    const onFailed = (request: Request) => { if (request.failure()?.errorText === BUFFER_SPACE) failed.push(request.url()) }
    page.on('requestfailed', onFailed)
    try {
      return await attempt()
    } catch (error) {
      if (!namesBufferSpace(error) && failed.length === 0) throw error
      const seen = failed.length ? failed.slice(0, 3).join(', ') + (failed.length > 3 ? ` and ${failed.length - 3} more` : '') : 'the navigation itself'
      const last = retry >= BUFFER_SPACE_BACKOFF_MS.length
      const description = `${step}, attempt ${retry + 1}: ${BUFFER_SPACE} on ${seen}; ${last ? 'not retried again, the failure stands' : `retrying in ${BUFFER_SPACE_BACKOFF_MS[retry]} ms`}; host ${JSON.stringify(deps.snapshot())}`
      deps.annotate({ type: BUFFER_SPACE_ANNOTATION, description })
      deps.log(`[#986] ${description}`)
      if (last) throw error
      await deps.sleep(BUFFER_SPACE_BACKOFF_MS[retry])
    } finally {
      page.off('requestfailed', onFailed)
    }
  }
}

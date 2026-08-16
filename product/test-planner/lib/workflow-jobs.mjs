// Reads which jobs a classification selects from the workflow itself, rather than restating it.
//
// The first version of the planner carried a hand-written list of jobs per area. That is the exact
// drift #568 exists to remove: a forecast maintained separately from the thing it forecasts is wrong
// the first time either changes, and nothing detects it. Here the conditions are parsed out of
// `ci.yml` and evaluated, so the planner's answer comes from the same text the runner obeys.
//
// The evaluator understands only the small expression subset the workflow actually uses, and throws on
// anything else. Guessing at an unrecognised condition would produce a confident wrong forecast, which
// is worse than refusing.

/** Extract `{ id, name, condition }` for every job that gates on the classifier or the event. */
export function parseJobConditions(workflowText) {
  const lines = String(workflowText).split(/\r?\n/)
  const jobsAt = lines.findIndex((line) => /^jobs:\s*$/.test(line))
  if (jobsAt < 0) throw new Error('The workflow has no top-level jobs: section.')

  const jobs = []
  let current = null
  for (let index = jobsAt + 1; index < lines.length; index += 1) {
    const line = lines[index]
    const header = /^ {2}([a-zA-Z0-9_-]+):\s*$/.exec(line)
    if (header) {
      if (current) jobs.push(current)
      current = { id: header[1], name: null, condition: null }
      continue
    }
    if (!current) continue
    const name = /^ {4}name:\s*(.+?)\s*$/.exec(line)
    if (name && current.name === null) current.name = name[1]
    const condition = /^ {4}if:\s*(.+?)\s*$/.exec(line)
    if (condition && current.condition === null) current.condition = condition[1]
  }
  if (current) jobs.push(current)
  return jobs
}

const OUTPUT = /^needs\.changes\.outputs\.(docs_only|backend|client|browser|postgresql)$/
const EVENT = /^github\.event_name$/

function literal(token) {
  const match = /^'([^']*)'$/.exec(token)
  return match ? match[1] : null
}

function resolve(token, context) {
  if (OUTPUT.test(token)) {
    const key = OUTPUT.exec(token)[1]
    return String(context.outputs[key])
  }
  if (EVENT.test(token)) return context.event
  const value = literal(token)
  if (value !== null) return value
  throw new Error(`Unsupported operand in workflow condition: ${token}`)
}

/**
 * Evaluate one condition. Supports `a == 'x'`, `a != 'x'`, `&&`, `||`, and parentheses — the whole of
 * what the workflow uses today. Anything else throws rather than being assumed true or false.
 */
export function evaluateCondition(condition, context) {
  if (condition === null || condition === undefined || String(condition).trim() === '') return true

  const evaluate = (text) => {
    const trimmed = text.trim()

    // Parentheses first, innermost outward.
    const open = trimmed.lastIndexOf('(')
    if (open >= 0) {
      const close = trimmed.indexOf(')', open)
      if (close < 0) throw new Error(`Unbalanced parentheses in condition: ${condition}`)
      const inner = evaluate(trimmed.slice(open + 1, close))
      return evaluate(`${trimmed.slice(0, open)}${inner}${trimmed.slice(close + 1)}`)
    }

    // `||` binds loosest, then `&&`.
    for (const [operator, combine] of [['||', (a, b) => a || b], ['&&', (a, b) => a && b]]) {
      const at = trimmed.indexOf(operator)
      if (at >= 0) return combine(evaluate(trimmed.slice(0, at)), evaluate(trimmed.slice(at + operator.length)))
    }

    if (trimmed === 'true') return true
    if (trimmed === 'false') return false

    const comparison = /^(\S+)\s*(==|!=)\s*(\S+)$/.exec(trimmed)
    if (!comparison) throw new Error(`Unsupported workflow condition: ${trimmed}`)
    const [, left, operator, right] = comparison
    const result = resolve(left, context) === resolve(right, context)
    return operator === '==' ? result : !result
  }

  return evaluate(condition)
}

/** Jobs that would run, derived from the workflow text. */
export function selectJobs(workflowText, classification, { event = 'pull_request' } = {}) {
  const context = {
    event,
    outputs: {
      docs_only: classification.docsOnly,
      backend: classification.backend,
      client: classification.client,
      browser: classification.browser,
      postgresql: classification.postgresql,
    },
  }

  const selected = []
  const skipped = []
  for (const job of parseJobConditions(workflowText)) {
    // `always()` and job-status functions describe reporting jobs that run regardless; they are not a
    // classification decision, so they are reported as always-running rather than evaluated.
    if (job.condition && /always\(\)|success\(\)|failure\(\)|cancelled\(\)/.test(job.condition)) {
      selected.push({ id: job.id, name: job.name, always: true })
      continue
    }
    if (evaluateCondition(job.condition, context)) selected.push({ id: job.id, name: job.name, always: false })
    else skipped.push({ id: job.id, name: job.name })
  }
  return { selected, skipped }
}

// Static inventory of the API's public mutating routes, and which API test classes reach each one.
//
// #566 moves deterministic rule matrices out of hosted tests. That is safe only while something notices when
// the last hosted proof of a route disappears — otherwise every speed metric improves while defect detection
// quietly drops, which is the failure the issue names. This inventory is that something: it is regenerated
// from source and compared against a committed manifest, so a route added without hosted coverage, a route
// whose method or path changes, and a migration that removes the last test touching a route all fail loudly.
//
// It is deliberately static. Reflecting over a built assembly would need the API host this exists to reduce,
// and would not see which *test class* reaches a route.

import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'

const MUTATING = ['MapPost', 'MapPut', 'MapPatch', 'MapDelete']

/** `{id:guid}` and `{id}` are the same slot for coverage purposes; case and trailing slash are not meaningful. */
export const normalisePath = (path) =>
  path.replace(/\{[^}]*\}/g, '{}').replace(/\/+$/, '').toLowerCase()

export const routeKey = (method, path) => `${method} ${normalisePath(path)}`

/**
 * A constrained route parameter can intentionally register a small set of literal public aliases, such as
 * `{artifactRoute:regex(procedures|cases)}`. Treating that segment as an ordinary parameter collapses two
 * observable routes into `/test-{}` and prevents a test of either alias from proving boundary coverage.
 * Expand only the deliberately narrow literal-alternative form; arbitrary route regexes remain parameters.
 */
function expandLiteralRegexAliases(path) {
  const match = path.match(/\{[^{}:]+:regex\(((?:[A-Za-z0-9-]+\|)+[A-Za-z0-9-]+)\)\}/)
  if (!match) return [path]
  return match[1].split('|').flatMap((alternative) =>
    expandLiteralRegexAliases(path.slice(0, match.index) + alternative + path.slice(match.index + match[0].length)),
  )
}

/**
 * Reads route declarations out of the endpoint sources.
 *
 * Paths written inside a `MapGroup` are group-relative even though they begin with `/`, so the group prefix
 * is resolved from the variable the group was assigned to rather than assumed absent.
 */
export function extractRoutes(apiDirectory) {
  const routes = []
  for (const file of readdirSync(apiDirectory).filter((name) => name.endsWith('.cs'))) {
    const source = readFileSync(join(apiDirectory, file), 'utf8')

    const groupOf = new Map()
    for (const match of source.matchAll(/(?:var|\w+)\s+(\w+)\s*=\s*[\w.]*MapGroup\("([^"]+)"\)/g)) {
      groupOf.set(match[1], match[2])
    }

    const verbs = MUTATING.join('|')
    for (const match of source.matchAll(new RegExp(`(\\w+)\\s*\\.\\s*(${verbs})\\("([^"]*)"`, 'g'))) {
      const [, receiver, verb, declared] = match
      const method = verb.replace('Map', '').toUpperCase()
      const prefix = groupOf.get(receiver) ?? ''
      const path = prefix
        ? prefix + (declared ? `/${declared.replace(/^\//, '')}` : '')
        : declared.startsWith('/') ? declared : `/${declared}`
      for (const expanded of expandLiteralRegexAliases(path))
        routes.push({ method, path: expanded, file, key: routeKey(method, expanded) })
    }
  }
  // One entry per distinct route; the same handler registered twice is one surface to cover.
  return [...new Map(routes.map((route) => [route.key, route])).values()].sort((a, b) => a.key.localeCompare(b.key))
}

/**
 * The HTTP verb a test uses, read from the call that carries the URL.
 *
 * Keyed by method as well as path because a path alone cannot say which operation was exercised:
 * `/api/enterprise-requirements/views/{}` carries both PUT and DELETE, and a PUT test would otherwise make
 * the DELETE route look covered. A new mutating method added to an already-mentioned path would inherit that
 * false coverage too.
 */
const METHOD_PATTERNS = [
  [/\b(?:PostAsync|PostAsJsonAsync)\b/, 'POST'],
  [/\b(?:PutAsync|PutAsJsonAsync)\b/, 'PUT'],
  [/\b(?:PatchAsync|PatchAsJsonAsync)\b/, 'PATCH'],
  [/\bDeleteAsync\b/, 'DELETE'],
  [/HttpMethod\.Post\b/, 'POST'],
  [/HttpMethod\.Put\b/, 'PUT'],
  [/HttpMethod\.Patch\b/, 'PATCH'],
  [/HttpMethod\.Delete\b/, 'DELETE'],
  [/"POST"/, 'POST'],
  [/"PUT"/, 'PUT'],
  [/"PATCH"/, 'PATCH'],
  [/"DELETE"/, 'DELETE'],
]

/** A generous ceiling; the statement boundary below is what actually bounds the search. */
const METHOD_LOOKBEHIND = 400

/**
 * The verb must appear in the *same statement* as the URL.
 *
 * A fixed character window is not enough: a bare `var url = $"{api}/api/…"` sitting a line below a
 * `PostAsync` call inherited that call's verb and was counted as evidence. Cutting the window at the previous
 * `;` — C#'s statement terminator — means only a call actually carrying this URL can claim it. Over-crediting
 * here is the failure mode that matters, since it manufactures coverage that was never written.
 */
function methodNear(source, index) {
  const ceiling = Math.max(0, index - METHOD_LOOKBEHIND)
  const boundary = source.lastIndexOf(';', index - 1)
  const window = source.slice(Math.max(ceiling, boundary + 1), index)
  let best = null
  for (const [pattern, method] of METHOD_PATTERNS) {
    const found = window.search(pattern)
    // The nearest preceding verb wins within the statement.
    if (found !== -1 && (best === null || found > best.at)) best = { at: found, method }
  }
  return best?.method ?? null
}

/**
 * Maps every `METHOD /api/...` pair appearing in a test source to the classes that exercise it.
 *
 * Tests build URLs by interpolation — `$"{api}/api/change-requests/{id}/submit"` — so the path is matched
 * wherever it appears rather than only at the start of a literal. A URL with no identifiable verb nearby is
 * recorded against no method, which leaves the route uncovered rather than guessing.
 */
export function extractTestReferences(testsDirectory) {
  const references = new Map()
  for (const file of readdirSync(testsDirectory).filter((name) => name.endsWith('.cs'))) {
    const source = readFileSync(join(testsDirectory, file), 'utf8')
    for (const match of source.matchAll(/\/api\/[A-Za-z0-9/_{}$.:()-]*/g)) {
      const method = methodNear(source, match.index)
      if (!method) continue
      const key = `${method} ${normalisePath(match[0].split('?')[0])}`
      if (!references.has(key)) references.set(key, new Set())
      references.get(key).add(file.replace(/\.cs$/, ''))
    }
  }
  return references
}

/** Route inventory joined to the test classes that exercise each one with its own method. */
export function buildRouteCoverage(apiDirectory, testsDirectory) {
  const routes = extractRoutes(apiDirectory)
  const references = extractTestReferences(testsDirectory)
  return routes.map((route) => ({
    method: route.method,
    path: route.path,
    file: route.file,
    coveredBy: [...(references.get(route.key) ?? [])].sort(),
  }))
}

/**
 * A reference is not a proof, and this does not pretend otherwise.
 *
 * A class mentioning a path may assert nothing about it. What the manifest establishes is the weaker but
 * checkable property that *some* hosted test still reaches the route — enough to fail when a migration
 * removes the last one, which is what #566 needs it for.
 */
export function summariseCoverage(coverage) {
  const uncovered = coverage.filter((route) => route.coveredBy.length === 0)
  return { total: coverage.length, covered: coverage.length - uncovered.length, uncovered }
}

/**
 * The safety property, and the only one that survives regeneration.
 *
 * Comparing the current tree against the generated manifest is worthless as a guard: the documented fix for a
 * failure is to regenerate, which makes the manifest agree with whatever just happened. So the question is
 * asked against the frozen policy baseline instead — is anything uncovered that was not already permitted to
 * be? A route that loses its last hosted test and a newly added uncovered route both answer yes, and
 * regenerating changes neither answer.
 */
export function uncoveredOutsideBaseline(coverage, grandfathered) {
  const allowed = grandfathered instanceof Set ? grandfathered : new Set(grandfathered)
  return coverage
    .filter((route) => route.coveredBy.length === 0)
    .map((route) => routeKey(route.method, route.path))
    .filter((key) => !allowed.has(key))
    .sort()
}

/**
 * Exceptions that have been earned out of, and must now be surrendered.
 *
 * The baseline has to agree with reality in *both* directions. Checking only that uncovered routes are
 * permitted leaves an exception standing after its route gains real coverage — and a later migration can then
 * remove that route's final hosted proof and still pass, because the stale exception still permits it. The
 * grandfathered list would become a permanent exemption instead of a shrinking record.
 *
 * So a grandfathered route that is currently covered fails until its entry is removed. The list can only get
 * shorter, which is the property that makes it a baseline rather than a loophole.
 */
export function coveredButStillGrandfathered(coverage, grandfathered) {
  const allowed = grandfathered instanceof Set ? grandfathered : new Set(grandfathered)
  return coverage
    .filter((route) => route.coveredBy.length > 0)
    .map((route) => routeKey(route.method, route.path))
    .filter((key) => allowed.has(key))
    .sort()
}

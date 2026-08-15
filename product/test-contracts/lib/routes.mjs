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
      routes.push({ method, path, file, key: routeKey(method, path) })
    }
  }
  // One entry per distinct route; the same handler registered twice is one surface to cover.
  return [...new Map(routes.map((route) => [route.key, route])).values()].sort((a, b) => a.key.localeCompare(b.key))
}

/**
 * Maps every `/api/...` path appearing in a test source to the classes that mention it.
 *
 * Tests build URLs by interpolation — `$"{api}/api/change-requests/{id}/submit"` — so the path is matched
 * wherever it appears rather than only at the start of a literal.
 */
export function extractTestReferences(testsDirectory) {
  const references = new Map()
  for (const file of readdirSync(testsDirectory).filter((name) => name.endsWith('.cs'))) {
    const source = readFileSync(join(testsDirectory, file), 'utf8')
    for (const match of source.matchAll(/\/api\/[A-Za-z0-9/_{}$.:()-]*/g)) {
      const key = normalisePath(match[0].split('?')[0])
      if (!references.has(key)) references.set(key, new Set())
      references.get(key).add(file.replace(/\.cs$/, ''))
    }
  }
  return references
}

/** Route inventory joined to the test classes that reach each one. */
export function buildRouteCoverage(apiDirectory, testsDirectory) {
  const routes = extractRoutes(apiDirectory)
  const references = extractTestReferences(testsDirectory)
  return routes.map((route) => {
    const path = route.key.slice(route.key.indexOf(' ') + 1)
    const covering = [...(references.get(path) ?? [])].sort()
    return { method: route.method, path: route.path, file: route.file, coveredBy: covering }
  })
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

// Pure overlap classification and bounded comment rendering for pull request #569.

import { classify } from './classify.mjs'

/** Canonicalise a GitHub path before comparing it. */
export function normalizePath(value) {
  if (typeof value !== 'string' || value.trim() === '') return null
  // Git permits leading and trailing spaces in path components. Preserve them so two distinct
  // repository paths cannot collapse into a false overlap; only separators, dot segments and
  // Windows case behavior are canonicalized.
  const parts = value.replaceAll('\\', '/').split('/')
  const normalized = []
  for (const part of parts) {
    if (!part || part === '.') continue
    if (part === '..') {
      normalized.pop()
      continue
    }
    normalized.push(part)
  }
  return normalized.join('/').toLowerCase() || null
}

/** Include both sides of a rename; a deletion still has a useful filename. */
export function pathsFromFile(file) {
  if (!file || typeof file !== 'object') return []
  return [file.filename, file.previous_filename].map(normalizePath).filter(Boolean)
}

export function normalizeFileList(files) {
  return [...new Set((Array.isArray(files) ? files : []).flatMap((file) => {
    if (typeof file === 'string') return [normalizePath(file)].filter(Boolean)
    return pathsFromFile(file)
  }))].sort()
}

/**
 * Planner/CI lanes are a small structured manifest, not prose inferred from a path. The job ids are
 * the names used by ci.yml; reasons explain why an agent should inspect that lane before requesting
 * the full gate. The overlap advisory never executes these lanes and never turns them into a gate.
 */
export const PLANNER_LANES = Object.freeze({
  full: Object.freeze({
    key: 'full', label: 'full quality-gate forecast', jobs: Object.freeze(['backend-api', 'backend-core-domain', 'backend-core-infrastructure', 'client', 'script-contracts', 'browser-pr', 'browser-production', 'postgresql-smoke', 'gate', 'metrics-tooling', 'metrics-report']),
    reason: 'Workflow, planner, graph, or otherwise broad changes can alter which validation runs; inspect the complete CI forecast.',
  }),
  backend: Object.freeze({
    key: 'backend', label: 'backend API/core lanes', jobs: Object.freeze(['backend-api', 'backend-core-domain', 'backend-core-infrastructure']),
    reason: 'Backend, contract, routing, harness, or domain changes can affect API and domain/infrastructure validation.',
  }),
  client: Object.freeze({
    key: 'client', label: 'client lane', jobs: Object.freeze(['client']),
    reason: 'Client-shell changes can alter the client build, lint, and type-check contract.',
  }),
  browser: Object.freeze({
    key: 'browser', label: 'browser lanes', jobs: Object.freeze(['browser-pr', 'browser-production']),
    reason: 'Hosted API, client-shell, routing, and startup changes can alter browser journeys or their served API.',
  }),
  postgresql: Object.freeze({
    key: 'postgresql', label: 'PostgreSQL smoke lane', jobs: Object.freeze(['postgresql-smoke']),
    reason: 'Persistence, migration, identity, and relational changes need provider-sensitive validation.',
  }),
  metrics: Object.freeze({
    key: 'metrics', label: 'metrics/provenance lanes', jobs: Object.freeze(['metrics-tooling', 'metrics-report', 'gate']),
    reason: 'Metrics and provenance changes can alter the evidence used to describe or authorize a gate.',
  }),
  documentation: Object.freeze({
    key: 'documentation', label: 'documentation-only review', jobs: Object.freeze([]),
    reason: 'The shared path is documentation-only according to the planner; coordinate the textual edit without claiming product-lane validation.',
  }),
  unknown: Object.freeze({
    key: 'unknown', label: 'unknown planner area', jobs: Object.freeze([]),
    reason: 'The planner could not identify a lane for this exact path; inspect the full forecast before treating it as clear.',
  }),
})

// The artifact and comment must carry every job declared by a lane. Keep this explicit bound above
// the largest reviewed manifest entry; the contract test fails if the manifest ever outgrows it.
export const MAX_JOBS_PER_AFFECTED_LANE = 12

const LANE_ORDER = ['full', 'backend', 'client', 'browser', 'postgresql', 'metrics', 'documentation', 'unknown']

const laneKeysForClassification = (classification) => {
  if (classification.broad) return ['full']
  const keys = []
  if (classification.backend) keys.push('backend')
  if (classification.client) keys.push('client')
  if (classification.browser) keys.push('browser')
  if (classification.postgresql) keys.push('postgresql')
  if (classification.docsOnly) return ['documentation']
  return keys.length > 0 ? keys : ['unknown']
}

/** Return bounded, deterministic lane metadata for a path set using the shared #568 classifier. */
export function plannerLanesForPaths(paths) {
  const values = Array.isArray(paths) ? paths.filter((path) => typeof path === 'string' && path.length > 0) : []
  const keys = laneKeysForClassification(classify(values))
  return LANE_ORDER.filter((key) => keys.includes(key)).map((key) => PLANNER_LANES[key])
}

function laneMetadataForOverlap(sharedFiles, sharedSurfaces) {
  // An empty path set is documentation-only to the general planner, but a surface-only overlap has
  // no exact shared path to classify. In that case only the reviewed surface metadata is evidence.
  const keys = new Set(sharedFiles.length > 0 ? plannerLanesForPaths(sharedFiles).map((lane) => lane.key) : [])
  for (const surface of sharedSurfaces) for (const key of surface.laneKeys ?? []) if (PLANNER_LANES[key]) keys.add(key)
  return LANE_ORDER.filter((key) => keys.has(key)).map((key) => PLANNER_LANES[key])
}

/**
 * Shared hotspots. A reason is required for every hotspot because a warning is only useful when an
 * engineer can decide what to coordinate. Patterns are lower-case; surfacesFor normalizes first.
 */
export const SURFACES = [
  {
    key: 'ci-gate', label: 'the CI gate definition',
    why: 'Both change which jobs run or how they are enforced. Two independently correct edits can compose into a gate that validates less than either author intended.',
    laneKeys: ['full'],
    patterns: [/^\.github\/workflows\//, /^product\/test-planner\//],
  },
  {
    key: 'solution', label: 'the solution boundary',
    why: 'Adding or moving projects in the solution changes what developers and CI build together, so two independently valid solution edits can omit or duplicate a project after integration.',
    laneKeys: ['full'],
    patterns: [/^product\/.*\.(sln|slnx)$/],
  },
  {
    key: 'project', label: 'a project definition',
    why: 'Project references and target frameworks define the compile and test graph. Parallel edits can leave a project building locally while the integrated graph is missing a reference or target.',
    laneKeys: ['full'],
    patterns: [/^product\/src\/[^/]+\/[^/]+\.csproj$/, /^product\/tests\/[^/]+\/[^/]+\.csproj$/],
  },
  {
    key: 'lock', label: 'a dependency lockfile',
    why: 'Lockfiles freeze the dependency graph. Independent updates can silently select incompatible transitive versions or invalidate the package cache used by CI.',
    laneKeys: ['full'],
    patterns: [/(^|\/)(package-lock\.json|npm-shrinkwrap\.json|yarn\.lock|pnpm-lock\.yaml|packages\.lock\.json|[^/]+\.lock)$/],
  },
  {
    key: 'build', label: 'the build and toolchain contract',
    why: 'Build properties, SDK selection, package feeds and container entry points affect every lane. Two local fixes can produce a green developer build and a different CI toolchain.',
    laneKeys: ['full'],
    patterns: [/(^|\/)(directory\.build\.(props|targets)|global\.json|nuget\.config|dockerfile[^/]*|makefile|build\.(ps1|sh))$/, /^product\/.*\.(props|targets)$/],
  },
  {
    key: 'startup', label: 'application startup and hosting',
    why: 'Startup wiring determines which services, middleware and test hosts exist. Parallel changes can compile while changing readiness, dependency order or the process contract.',
    laneKeys: ['backend', 'browser'],
    patterns: [/^product\/src\/[^/]+\/(program|startup)\.cs$/, /^product\/scripts\//, /(^|\/)launchsettings\.json$/],
  },
  {
    key: 'identity', label: 'identity and authority mapping',
    why: 'Identity fixtures and authority mapping decide who may perform a mutation. Separate edits can preserve compilation while changing the effective role or audit principal.',
    laneKeys: ['backend', 'browser', 'postgresql'],
    patterns: [/(^|\/)(identityservice|peopleregistry)\.(cs|ts)$/, /(^|\/)(identity|users|roles|claims)\//],
  },
  {
    key: 'security', label: 'the security boundary',
    why: 'Authentication, authorization and security-boundary tests are a shared safety contract. Parallel changes can weaken coverage or create a policy that only one branch exercises.',
    laneKeys: ['backend', 'browser'],
    patterns: [/(^|\/)(securityboundarytests|authorization|authentication|policies)\.(cs|ts)$/, /(^|\/)security\//],
  },
  {
    key: 'routing', label: 'the public API routing contract',
    why: 'Route tables, endpoint declarations and route-coverage manifests jointly define the public API. Different files can still introduce ambiguous verbs, paths or coverage assumptions.',
    laneKeys: ['backend', 'browser'],
    patterns: [/^product\/src\/aerolink\.api\/routing\//, /(^|\/)route(table|s?)\.(cs|ts|tsx|json)$/],
  },
  {
    key: 'api-routes', label: 'the public API route table',
    why: 'Two pull requests can add, move or rename routes independently and only discover the clash when both are merged and the route contract test or a journey fails.',
    laneKeys: ['backend', 'browser'],
    patterns: [/^product\/src\/aerolink\.api\/.*endpoints\.cs$/, /^product\/test-contracts\/(route-coverage|grandfathered-uncovered)\.json$/],
  },
  {
    key: 'api-contracts', label: 'shared API contracts and DTOs',
    why: 'Shared API contracts and DTO definitions are consumed by many endpoints and clients. Two independently valid edits can change the wire shape, validation assumptions or serialization contract without touching the same feature endpoint.',
    laneKeys: ['backend', 'browser'],
    patterns: [/^product\/src\/aerolink\.api\/apicontracts\.cs$/, /^product\/src\/aerolink\.api\/(contracts|dto)\/[^/]+\.(cs|json|ts)$/],
  },
  {
    key: 'migrations', label: 'the migration sequence',
    why: 'Migrations are ordered. Two branches each appending a migration will both apply cleanly in isolation and produce a broken or ambiguous sequence once both land.',
    laneKeys: ['backend', 'browser', 'postgresql'],
    patterns: [/^product\/src\/aerolink\.infrastructure\/persistence\/migrations\//],
  },
  {
    key: 'persistence-model', label: 'the persistence model',
    why: 'Changes to the DbContext or entity configuration can be individually valid and jointly contradictory, and EF translation failures do not surface until the PostgreSQL lane runs.',
    laneKeys: ['backend', 'browser', 'postgresql'],
    patterns: [/^product\/src\/aerolink\.infrastructure\/persistence\/(?!migrations\/)/, /aerolinkdbcontext\.cs$/],
  },
  {
    key: 'domain-contracts', label: 'shared domain contracts',
    why: 'A domain type edited by two branches usually means two different readings of the same rule, which merges cleanly and behaves wrongly.',
    laneKeys: ['backend', 'browser'],
    patterns: [/^product\/src\/aerolink\.domain\//],
  },
  {
    key: 'test-harness', label: 'the API test harness',
    why: 'The factory, shared host and fixtures are used by every API test. Two changes here can each pass their own branch and interact badly, and the failure appears in tests neither author touched.',
    laneKeys: ['backend', 'browser'],
    patterns: [/^product\/tests\/aerolink\.api\.tests\/(securityboundarytests|sharedapihost|showcaseapifixture)\.cs$/, /^product\/tests\/aerolink\.api\.tests\/(fixtures|harness)\//],
  },
  {
    key: 'metrics-contract', label: 'the CI metrics and provenance contract',
    why: 'These produce the totals every gate is judged on and the provenance decision that can skip a post-merge run. Two branches changing them can leave the evidence internally inconsistent.',
    laneKeys: ['metrics'],
    patterns: [/^product\/ci-metrics\//],
  },
  {
    key: 'client-shell', label: 'the client application shell',
    why: 'App-level routing and workspace layout are touched by most feature work, so two branches editing them conflict textually more often than anything else in the repository.',
    laneKeys: ['client', 'browser'],
    patterns: [/^product\/client\/src\/(app|main)\.tsx$/, /^product\/client\/src\/.*workspace\.tsx$/],
  },
]

/** The surfaces a set of changed paths touches. Values are canonical paths for stable evidence. */
export function surfacesFor(changedPaths) {
  const hits = new Map()
  for (const rawPath of Array.isArray(changedPaths) ? changedPaths : []) {
    const path = normalizePath(rawPath)
    if (!path) continue
    for (const surface of SURFACES) {
      if (surface.patterns.some((pattern) => pattern.test(path))) {
        const list = hits.get(surface.key) ?? []
        list.push(path)
        hits.set(surface.key, list)
      }
    }
  }
  for (const [key, paths] of hits) hits.set(key, [...new Set(paths)].sort())
  return hits
}

const SEVERITY_ORDER = { high: 0, medium: 1, low: 2 }

/** Find overlaps between open pull requests. */
export function detectOverlaps(pullRequests = []) {
  const prs = (Array.isArray(pullRequests) ? pullRequests : [])
    .filter((pr) => pr && Number.isInteger(pr.number) && Array.isArray(pr.files))
    .map((pr) => ({ ...pr, files: normalizeFileList(pr.files) }))
  const overlaps = []
  for (let i = 0; i < prs.length; i += 1) {
    for (let j = i + 1; j < prs.length; j += 1) {
      const a = prs[i]; const b = prs[j]
      const aFiles = new Set(a.files)
      const sharedFiles = b.files.filter((file) => aFiles.has(file)).sort()
      const aSurfaces = surfacesFor(a.files); const bSurfaces = surfacesFor(b.files)
      const sharedSurfaces = []
      for (const [key, aPaths] of aSurfaces) {
        if (!bSurfaces.has(key)) continue
        const surface = SURFACES.find((candidate) => candidate.key === key)
        sharedSurfaces.push({ key, label: surface.label, why: surface.why, laneKeys: surface.laneKeys, aPaths: [...aPaths].sort(), bPaths: [...bSurfaces.get(key)].sort() })
      }
      if (sharedFiles.length === 0 && sharedSurfaces.length === 0) continue
      overlaps.push({
        a: { number: a.number, title: a.title ?? null, author: a.author ?? null, branch: a.branch ?? null, headSha: a.headSha ?? null, baseSha: a.baseSha ?? null, reviewedDisposition: a.reviewedDisposition === true },
        b: { number: b.number, title: b.title ?? null, author: b.author ?? null, branch: b.branch ?? null, headSha: b.headSha ?? null, baseSha: b.baseSha ?? null, reviewedDisposition: b.reviewedDisposition === true },
        severity: sharedFiles.length > 0 ? 'high' : 'medium', sharedFiles, sharedSurfaces,
        affectedLanes: laneMetadataForOverlap(sharedFiles, sharedSurfaces),
      })
    }
  }
  return overlaps.sort((x, y) => SEVERITY_ORDER[x.severity] - SEVERITY_ORDER[y.severity]
    || y.sharedFiles.length - x.sharedFiles.length || y.sharedSurfaces.length - x.sharedSurfaces.length
    || x.a.number - y.a.number || x.b.number - y.b.number)
}

/** Only overlaps involving one pull request, with that request as `a`. */
export function overlapsFor(prNumber, overlaps) {
  return (Array.isArray(overlaps) ? overlaps : [])
    .filter((entry) => entry?.a?.number === prNumber || entry?.b?.number === prNumber)
    .map((entry) => {
      if (entry.a.number === prNumber) return entry
      return {
        ...entry,
        a: entry.b,
        b: entry.a,
        // Surface paths are directional evidence. Keep them aligned with the
        // normalized endpoint identities when the requested PR was originally
        // detected as `b`.
        sharedSurfaces: entry.sharedSurfaces.map((surface) => ({
          ...surface,
          aPaths: [...surface.bPaths],
          bPaths: [...surface.aPaths],
        })),
      }
    })
}

const MAX_LISTED = 10
const MAX_AFFECTED_LANES = 8
const MAX_COMMENT_LENGTH = 60_000
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/g
const shorten = (value, max = 240) => String(value ?? '').replace(CONTROL_CHARACTERS, ' ').replace(/\s+/g, ' ').trim().slice(0, max)
const escapeMarkdown = (value, max = 240) => shorten(value, max).replace(/[&<>\\`*_[\]{}()#+\-.!|>]/g, '\\$&')
// A backslash does not escape a backtick inside a Markdown code span. Replace the delimiter itself
// with a visible escape sequence so PR-controlled text cannot close the surrounding span.
const escapeCode = (value, max = 240) => shorten(value, max).replaceAll('`', '\\x60')
// Provenance is rendered as ordinary text, so also neutralize Markdown punctuation while
// preserving the readable punctuation of normal ISO timestamps and hexadecimal SHAs.
const escapeMetadata = (value, max = 240) => escapeCode(value, max).replace(/[&<>`*_[\]{}()#+!|>]/g, '\\$&')

export function boundComment(body) {
  const value = String(body ?? '')
  return value.length <= MAX_COMMENT_LENGTH ? value : `${value.slice(0, MAX_COMMENT_LENGTH - 80)}\n\n… evidence truncated to keep this advisory comment bounded.`
}

export const OVERLAP_STATUSES = Object.freeze({
  critical: 'Critical overlap',
  coordinate: 'Coordinate',
  clear: 'Clear',
  unknown: 'Unknown',
})

/** Map completed overlap evidence to the explicit issue vocabulary. */
export function statusForOverlapList(overlaps) {
  if (!Array.isArray(overlaps) || overlaps.length === 0) return OVERLAP_STATUSES.clear
  return overlaps.some((entry) => entry?.severity === 'high') ? OVERLAP_STATUSES.critical : OVERLAP_STATUSES.coordinate
}

const dispositionLine = 'Reviewed disposition: `overlap-reviewed` acknowledges coordination; it does not suppress this warning or make the advisory blocking.'

function dispositionLines(reviewedDisposition) {
  return reviewedDisposition ? [dispositionLine, ''] : []
}

/** A warning body for one pull request, or null when there is nothing worth saying. */
export function renderComment(prNumber, overlaps, metadata = {}) {
  const mine = overlapsFor(prNumber, overlaps)
  if (mine.length === 0) return null
  const lanes = LANE_ORDER
    .filter((key) => mine.some((entry) => entry.affectedLanes?.some((lane) => lane.key === key)))
    .map((key) => PLANNER_LANES[key])
  const lines = [
    '## Open pull requests touching the same ground', '',
    'This is an advisory warning, not a block — nothing here prevents this pull request from proceeding.',
    `Status: ${statusForOverlapList(mine)}`,
    ...dispositionLines(metadata.reviewedDisposition),
    `Analysis timestamp: ${escapeMetadata(metadata.analysisTimestamp || 'Unknown', 80)}`,
    `Current head SHA: ${escapeMetadata(metadata.currentSha || 'Unknown', 80)}`, '',
  ]
  lines.push('Affected planner/CI lanes:', ...lanes.slice(0, MAX_AFFECTED_LANES).map((lane) => {
    const jobs = lane.jobs.length > 0 ? ` (${lane.jobs.map((job) => `\`${job}\``).join(', ')})` : ''
    return `- **${lane.label}**${jobs}: ${lane.reason}`
  }), '')
  for (const entry of mine.slice(0, MAX_LISTED)) {
    const other = entry.b
    lines.push(`### #${other.number}${other.title ? ` — ${escapeMarkdown(other.title)}` : ''}`)
    if (other.branch) lines.push(`Branch \`${escapeCode(other.branch)}\`${other.author ? ` by @${escapeMarkdown(other.author, 80)}` : ''}.`)
    lines.push(`Peer head SHA: ${escapeCode(other.headSha || 'Unknown', 80)}`, '')
    if (entry.sharedFiles.length > 0) {
      lines.push(`**Same files (${entry.sharedFiles.length}) — a textual conflict is likely:**`, '')
      for (const file of entry.sharedFiles.slice(0, MAX_LISTED)) lines.push(`- \`${escapeCode(file)}\``)
      if (entry.sharedFiles.length > MAX_LISTED) lines.push(`- …and ${entry.sharedFiles.length - MAX_LISTED} more`)
      lines.push('')
    }
    for (const surface of entry.sharedSurfaces.slice(0, MAX_LISTED)) {
      lines.push(`**Same surface: ${surface.label}**`, '', surface.why, '')
      lines.push(`- this pull request: ${surface.aPaths.slice(0, 4).map((path) => `\`${escapeCode(path)}\``).join(', ')}${surface.aPaths.length > 4 ? `, +${surface.aPaths.length - 4}` : ''}`)
      lines.push(`- #${other.number}: ${surface.bPaths.slice(0, 4).map((path) => `\`${escapeCode(path)}\``).join(', ')}${surface.bPaths.length > 4 ? `, +${surface.bPaths.length - 4}` : ''}`, '')
    }
    if (entry.sharedFiles.length === 0) lines.push('No file is shared, so git will merge both cleanly. The shared surface is the evidence that the incompatibility could survive both merges.', '')
  }
  if (mine.length > MAX_LISTED) lines.push(`…and ${mine.length - MAX_LISTED} more overlapping pull requests.`, '')
  lines.push('---', '', 'Whoever merges second should expect to integrate and re-run. This evidence is regenerated on every lifecycle event and is not authoritative about merge order.')
  return boundComment(lines.join('\n'))
}

export function renderClearComment({ analysisTimestamp = 'Unknown', currentSha = 'Unknown', reason = 'No open pull request currently overlaps this one', action = 'unknown', peerHeads = [], reviewedDisposition = false } = {}) {
  const peers = peerHeads.filter(Boolean).slice(0, 20).map((sha) => `\`${escapeCode(sha, 80)}\``).join(', ') || 'None'
  return boundComment(['## Open pull request overlap status', '', 'No current overlap is reported for this pull request. Any earlier warning is retained as this single marker comment so stale claims are visibly cleared.', `Status: ${OVERLAP_STATUSES.clear}`, ...dispositionLines(reviewedDisposition), `Reason: ${escapeMarkdown(reason)}`, `Action: ${escapeMarkdown(action, 40)}`, `Analysis timestamp: ${escapeMetadata(analysisTimestamp, 80)}`, `Current head SHA: ${escapeMetadata(currentSha || 'Unknown', 80)}`, `Peer head SHAs considered: ${peers}`].join('\n'))
}

export function renderUnknownComment({ analysisTimestamp = 'Unknown', currentSha = 'Unknown', reason = 'GitHub API failure', action = 'unknown' } = {}) {
  return boundComment(['## Open pull request overlap status', '', '**Status: Unknown** — the advisory checker could not establish whether an overlap exists. It did not treat an API failure as a clean result.', `Reason: ${escapeMarkdown(reason)}`, `Action: ${escapeMarkdown(action, 40)}`, `Analysis timestamp: ${escapeMetadata(analysisTimestamp, 80)}`, `Current head SHA: ${escapeMetadata(currentSha || 'Unknown', 80)}`].join('\n'))
}

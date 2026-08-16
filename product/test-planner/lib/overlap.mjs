// Pure overlap classification and bounded comment rendering for pull request #569.

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
 * Shared hotspots. A reason is required for every hotspot because a warning is only useful when an
 * engineer can decide what to coordinate. Patterns are lower-case; surfacesFor normalizes first.
 */
export const SURFACES = [
  {
    key: 'ci-gate', label: 'the CI gate definition',
    why: 'Both change which jobs run or how they are enforced. Two independently correct edits can compose into a gate that validates less than either author intended.',
    patterns: [/^\.github\/workflows\//, /^product\/test-planner\//],
  },
  {
    key: 'solution', label: 'the solution boundary',
    why: 'Adding or moving projects in the solution changes what developers and CI build together, so two independently valid solution edits can omit or duplicate a project after integration.',
    patterns: [/^product\/.*\.(sln|slnx)$/],
  },
  {
    key: 'project', label: 'a project definition',
    why: 'Project references and target frameworks define the compile and test graph. Parallel edits can leave a project building locally while the integrated graph is missing a reference or target.',
    patterns: [/^product\/src\/[^/]+\/[^/]+\.csproj$/, /^product\/tests\/[^/]+\/[^/]+\.csproj$/],
  },
  {
    key: 'lock', label: 'a dependency lockfile',
    why: 'Lockfiles freeze the dependency graph. Independent updates can silently select incompatible transitive versions or invalidate the package cache used by CI.',
    patterns: [/(^|\/)(package-lock\.json|npm-shrinkwrap\.json|yarn\.lock|pnpm-lock\.yaml|packages\.lock\.json|[^/]+\.lock)$/],
  },
  {
    key: 'build', label: 'the build and toolchain contract',
    why: 'Build properties, SDK selection, package feeds and container entry points affect every lane. Two local fixes can produce a green developer build and a different CI toolchain.',
    patterns: [/(^|\/)(directory\.build\.(props|targets)|global\.json|nuget\.config|dockerfile[^/]*|makefile|build\.(ps1|sh))$/, /^product\/.*\.(props|targets)$/],
  },
  {
    key: 'startup', label: 'application startup and hosting',
    why: 'Startup wiring determines which services, middleware and test hosts exist. Parallel changes can compile while changing readiness, dependency order or the process contract.',
    patterns: [/^product\/src\/[^/]+\/(program|startup)\.cs$/, /^product\/scripts\//, /(^|\/)launchsettings\.json$/],
  },
  {
    key: 'identity', label: 'identity and authority mapping',
    why: 'Identity fixtures and authority mapping decide who may perform a mutation. Separate edits can preserve compilation while changing the effective role or audit principal.',
    patterns: [/(^|\/)(identityservice|peopleregistry)\.(cs|ts)$/, /(^|\/)(identity|users|roles|claims)\//],
  },
  {
    key: 'security', label: 'the security boundary',
    why: 'Authentication, authorization and security-boundary tests are a shared safety contract. Parallel changes can weaken coverage or create a policy that only one branch exercises.',
    patterns: [/(^|\/)(securityboundarytests|authorization|authentication|policies)\.(cs|ts)$/, /(^|\/)security\//],
  },
  {
    key: 'routing', label: 'the public API routing contract',
    why: 'Route tables, endpoint declarations and route-coverage manifests jointly define the public API. Different files can still introduce ambiguous verbs, paths or coverage assumptions.',
    patterns: [/^product\/src\/aerolink\.api\/routing\//, /(^|\/)route(table|s?)\.(cs|ts|tsx|json)$/],
  },
  {
    key: 'api-routes', label: 'the public API route table',
    why: 'Two pull requests can add, move or rename routes independently and only discover the clash when both are merged and the route contract test or a journey fails.',
    patterns: [/^product\/src\/aerolink\.api\/.*endpoints\.cs$/, /^product\/test-contracts\/(route-coverage|grandfathered-uncovered)\.json$/],
  },
  {
    key: 'migrations', label: 'the migration sequence',
    why: 'Migrations are ordered. Two branches each appending a migration will both apply cleanly in isolation and produce a broken or ambiguous sequence once both land.',
    patterns: [/^product\/src\/aerolink\.infrastructure\/persistence\/migrations\//],
  },
  {
    key: 'persistence-model', label: 'the persistence model',
    why: 'Changes to the DbContext or entity configuration can be individually valid and jointly contradictory, and EF translation failures do not surface until the PostgreSQL lane runs.',
    patterns: [/^product\/src\/aerolink\.infrastructure\/persistence\/(?!migrations\/)/, /aerolinkdbcontext\.cs$/],
  },
  {
    key: 'domain-contracts', label: 'shared domain contracts',
    why: 'A domain type edited by two branches usually means two different readings of the same rule, which merges cleanly and behaves wrongly.',
    patterns: [/^product\/src\/aerolink\.domain\//],
  },
  {
    key: 'test-harness', label: 'the API test harness',
    why: 'The factory, shared host and fixtures are used by every API test. Two changes here can each pass their own branch and interact badly, and the failure appears in tests neither author touched.',
    patterns: [/^product\/tests\/aerolink\.api\.tests\/(securityboundarytests|sharedapihost|showcaseapifixture)\.cs$/, /^product\/tests\/aerolink\.api\.tests\/(fixtures|harness)\//],
  },
  {
    key: 'metrics-contract', label: 'the CI metrics and provenance contract',
    why: 'These produce the totals every gate is judged on and the provenance decision that can skip a post-merge run. Two branches changing them can leave the evidence internally inconsistent.',
    patterns: [/^product\/ci-metrics\//],
  },
  {
    key: 'client-shell', label: 'the client application shell',
    why: 'App-level routing and workspace layout are touched by most feature work, so two branches editing them conflict textually more often than anything else in the repository.',
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
        sharedSurfaces.push({ key, label: surface.label, why: surface.why, aPaths: [...aPaths].sort(), bPaths: [...bSurfaces.get(key)].sort() })
      }
      if (sharedFiles.length === 0 && sharedSurfaces.length === 0) continue
      overlaps.push({
        a: { number: a.number, title: a.title ?? null, author: a.author ?? null, branch: a.branch ?? null, headSha: a.headSha ?? null, baseSha: a.baseSha ?? null },
        b: { number: b.number, title: b.title ?? null, author: b.author ?? null, branch: b.branch ?? null, headSha: b.headSha ?? null, baseSha: b.baseSha ?? null },
        severity: sharedFiles.length > 0 ? 'high' : 'medium', sharedFiles, sharedSurfaces,
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
    .map((entry) => (entry.a.number === prNumber ? entry : { ...entry, a: entry.b, b: entry.a }))
}

const MAX_LISTED = 10
const MAX_COMMENT_LENGTH = 60_000
const CONTROL_CHARACTERS = /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/g
const shorten = (value, max = 240) => String(value ?? '').replace(CONTROL_CHARACTERS, ' ').replace(/\s+/g, ' ').trim().slice(0, max)
const escapeMarkdown = (value, max = 240) => shorten(value, max).replace(/[&<>\\`*_[\]{}()#+\-.!|>]/g, '\\$&')
// A backslash does not escape a backtick inside a Markdown code span. Replace the delimiter itself
// with a visible escape sequence so PR-controlled text cannot close the surrounding span.
const escapeCode = (value, max = 240) => shorten(value, max).replaceAll('`', '\\x60')

export function boundComment(body) {
  const value = String(body ?? '')
  return value.length <= MAX_COMMENT_LENGTH ? value : `${value.slice(0, MAX_COMMENT_LENGTH - 80)}\n\n… evidence truncated to keep this advisory comment bounded.`
}

/** A warning body for one pull request, or null when there is nothing worth saying. */
export function renderComment(prNumber, overlaps, metadata = {}) {
  const mine = overlapsFor(prNumber, overlaps)
  if (mine.length === 0) return null
  const lines = [
    '## Open pull requests touching the same ground', '',
    'This is an advisory warning, not a block — nothing here prevents this pull request from proceeding.',
    `Analysis timestamp: ${shorten(metadata.analysisTimestamp || 'Unknown', 80)}`,
    `Current head SHA: ${shorten(metadata.currentSha || 'Unknown', 80)}`, '',
  ]
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

export function renderClearComment({ analysisTimestamp = 'Unknown', currentSha = 'Unknown', reason = 'No open pull request currently overlaps this one', action = 'unknown', peerHeads = [] } = {}) {
  const peers = peerHeads.filter(Boolean).slice(0, 20).map((sha) => `\`${escapeCode(sha, 80)}\``).join(', ') || 'None'
  return boundComment(['## Open pull request overlap status', '', 'No current overlap is reported for this pull request. Any earlier warning is retained as this single marker comment so stale claims are visibly cleared.', `Status: Clear`, `Reason: ${escapeMarkdown(reason)}`, `Action: ${escapeMarkdown(action, 40)}`, `Analysis timestamp: ${escapeCode(analysisTimestamp, 80)}`, `Current head SHA: ${escapeCode(currentSha || 'Unknown', 80)}`, `Peer head SHAs considered: ${peers}`].join('\n'))
}

export function renderUnknownComment({ analysisTimestamp = 'Unknown', currentSha = 'Unknown', reason = 'GitHub API failure', action = 'unknown' } = {}) {
  return boundComment(['## Open pull request overlap status', '', '**Status: Unknown** — the advisory checker could not establish whether an overlap exists. It did not treat an API failure as a clean result.', `Reason: ${escapeMarkdown(reason)}`, `Action: ${escapeMarkdown(action, 40)}`, `Analysis timestamp: ${escapeCode(analysisTimestamp, 80)}`, `Current head SHA: ${escapeCode(currentSha || 'Unknown', 80)}`].join('\n'))
}

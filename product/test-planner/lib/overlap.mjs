// Multi-agent pull-request overlap detection (#569).
//
// Branch-per-task with a protected main is the right safety model, but it gives no early warning that
// two otherwise valid pull requests are editing the same high-risk surface. Today the collision shows up
// only after both branches have paid for a full gate, one merges, the other goes behind, and the whole
// thing runs again.
//
// The harder half is that this is not only a git-conflict problem. Two agents can touch entirely
// different files and still change the same endpoint contract, migration sequence, route table, shared
// DTO, test harness, or the CI classifier. Git sees nothing; the incompatibility survives both merges.
// So overlap is computed on two axes: the files themselves, and the *surfaces* those files define.

/**
 * Shared surfaces. Two pull requests touching the same surface are making assumptions about the same
 * contract even when no file is shared.
 *
 * Each surface is deliberately narrow. A surface that matches most of the repository would flag every
 * pair and be ignored within a day, which is worse than not having it.
 */
export const SURFACES = [
  {
    key: 'ci-gate',
    label: 'the CI gate definition',
    why: 'Both change which jobs run or how they are enforced. Two independently correct edits can compose into a gate that validates less than either author intended.',
    patterns: [/^\.github\/workflows\//, /^product\/test-planner\//],
  },
  {
    key: 'api-routes',
    label: 'the public API route table',
    why: 'Two pull requests can add, move or rename routes independently and only discover the clash when both are merged and the route contract test or a journey fails.',
    patterns: [/^product\/src\/AeroLink\.Api\/.*Endpoints\.cs$/, /^product\/test-contracts\/route-coverage\.json$/, /^product\/test-contracts\/grandfathered-uncovered\.json$/],
  },
  {
    key: 'migrations',
    label: 'the migration sequence',
    why: 'Migrations are ordered. Two branches each appending a migration will both apply cleanly in isolation and produce a broken or ambiguous sequence once both land.',
    patterns: [/^product\/src\/AeroLink\.Infrastructure\/Persistence\/Migrations\//],
  },
  {
    key: 'persistence-model',
    label: 'the persistence model',
    why: 'Changes to the DbContext or entity configuration can be individually valid and jointly contradictory, and EF translation failures do not surface until the PostgreSQL lane runs.',
    patterns: [/^product\/src\/AeroLink\.Infrastructure\/Persistence\/(?!Migrations\/)/, /AeroLinkDbContext\.cs$/],
  },
  {
    key: 'domain-contracts',
    label: 'shared domain contracts',
    why: 'A domain type edited by two branches usually means two different readings of the same rule, which merges cleanly and behaves wrongly.',
    patterns: [/^product\/src\/AeroLink\.Domain\//],
  },
  {
    key: 'test-harness',
    label: 'the API test harness',
    why: 'The factory, shared host and fixtures are used by every API test. Two changes here can each pass their own branch and interact badly, and the failure appears in tests neither author touched.',
    patterns: [/^product\/tests\/AeroLink\.Api\.Tests\/(SecurityBoundaryTests|SharedApiHost|ShowcaseApiFixture)\.cs$/],
  },
  {
    key: 'metrics-contract',
    label: 'the CI metrics and provenance contract',
    why: 'These produce the totals every gate is judged on and the provenance decision that can skip a post-merge run. Two branches changing them can leave the evidence internally inconsistent.',
    patterns: [/^product\/ci-metrics\//],
  },
  {
    key: 'client-shell',
    label: 'the client application shell',
    why: 'App-level routing and workspace layout are touched by most feature work, so two branches editing them conflict textually more often than anything else in the repository.',
    patterns: [/^product\/client\/src\/(App|main)\.tsx$/, /^product\/client\/src\/.*Workspace\.tsx$/],
  },
]

/** The surfaces a set of changed paths touches. */
export function surfacesFor(changedPaths) {
  const hits = new Map()
  for (const path of Array.isArray(changedPaths) ? changedPaths : []) {
    if (typeof path !== 'string') continue
    for (const surface of SURFACES) {
      if (surface.patterns.some((pattern) => pattern.test(path))) {
        const list = hits.get(surface.key) ?? []
        list.push(path)
        hits.set(surface.key, list)
      }
    }
  }
  return hits
}

const SEVERITY_ORDER = { high: 0, medium: 1, low: 2 }

/**
 * Find overlaps between open pull requests.
 *
 * `pullRequests` is `[{ number, title, author, branch, files: string[] }]`. Returns one entry per
 * colliding pair, most severe first, each carrying the evidence rather than only a verdict — a warning
 * that cannot be checked is a warning that gets muted.
 */
export function detectOverlaps(pullRequests = []) {
  const prs = (Array.isArray(pullRequests) ? pullRequests : []).filter(
    (pr) => pr && Number.isInteger(pr.number) && Array.isArray(pr.files),
  )
  const overlaps = []

  for (let i = 0; i < prs.length; i += 1) {
    for (let j = i + 1; j < prs.length; j += 1) {
      const a = prs[i]
      const b = prs[j]
      const aFiles = new Set(a.files)
      const sharedFiles = b.files.filter((file) => aFiles.has(file)).sort()

      const aSurfaces = surfacesFor(a.files)
      const bSurfaces = surfacesFor(b.files)
      const sharedSurfaces = []
      for (const [key, aPaths] of aSurfaces) {
        if (!bSurfaces.has(key)) continue
        const surface = SURFACES.find((s) => s.key === key)
        sharedSurfaces.push({
          key,
          label: surface.label,
          why: surface.why,
          aPaths: [...new Set(aPaths)].sort(),
          bPaths: [...new Set(bSurfaces.get(key))].sort(),
        })
      }

      if (sharedFiles.length === 0 && sharedSurfaces.length === 0) continue

      // A shared file is a probable textual conflict and is knowable from git. A shared surface without
      // a shared file is the case git cannot see, and is the reason this exists — so it is reported
      // even though it is the quieter signal.
      const severity = sharedFiles.length > 0 ? 'high' : 'medium'
      overlaps.push({
        a: { number: a.number, title: a.title ?? null, author: a.author ?? null, branch: a.branch ?? null },
        b: { number: b.number, title: b.title ?? null, author: b.author ?? null, branch: b.branch ?? null },
        severity,
        sharedFiles,
        sharedSurfaces,
      })
    }
  }

  return overlaps.sort(
    (x, y) =>
      SEVERITY_ORDER[x.severity] - SEVERITY_ORDER[y.severity] ||
      y.sharedFiles.length - x.sharedFiles.length ||
      y.sharedSurfaces.length - x.sharedSurfaces.length ||
      x.a.number - y.a.number,
  )
}

/** Only the overlaps involving one pull request, for a comment posted on that pull request. */
export function overlapsFor(prNumber, overlaps) {
  return overlaps
    .filter((entry) => entry.a.number === prNumber || entry.b.number === prNumber)
    .map((entry) => (entry.a.number === prNumber ? entry : { ...entry, a: entry.b, b: entry.a }))
}

const MAX_LISTED = 10

/** A comment body for one pull request, or null when there is nothing worth saying. */
export function renderComment(prNumber, overlaps) {
  const mine = overlapsFor(prNumber, overlaps)
  if (mine.length === 0) return null

  const lines = []
  lines.push('## Open pull requests touching the same ground')
  lines.push('')
  lines.push(`This is posted before the expensive gates run, so a collision can be settled while it is still cheap. It is a warning, not a block — nothing here prevents this pull request from proceeding.`)
  lines.push('')

  for (const entry of mine) {
    const other = entry.b
    lines.push(`### #${other.number}${other.title ? ` — ${other.title}` : ''}`)
    if (other.branch) lines.push(`Branch \`${other.branch}\`${other.author ? ` by @${other.author}` : ''}.`)
    lines.push('')

    if (entry.sharedFiles.length > 0) {
      lines.push(`**Same files (${entry.sharedFiles.length}) — a textual conflict is likely:**`)
      lines.push('')
      for (const file of entry.sharedFiles.slice(0, MAX_LISTED)) lines.push(`- \`${file}\``)
      if (entry.sharedFiles.length > MAX_LISTED) lines.push(`- …and ${entry.sharedFiles.length - MAX_LISTED} more`)
      lines.push('')
    }

    for (const surface of entry.sharedSurfaces) {
      lines.push(`**Same surface: ${surface.label}**`)
      lines.push('')
      lines.push(surface.why)
      lines.push('')
      lines.push(`- this pull request: ${surface.aPaths.slice(0, 4).map((p) => `\`${p}\``).join(', ')}${surface.aPaths.length > 4 ? `, +${surface.aPaths.length - 4}` : ''}`)
      lines.push(`- #${other.number}: ${surface.bPaths.slice(0, 4).map((p) => `\`${p}\``).join(', ')}${surface.bPaths.length > 4 ? `, +${surface.bPaths.length - 4}` : ''}`)
      lines.push('')
    }

    if (entry.sharedFiles.length === 0) {
      lines.push('No file is shared, so git will merge both cleanly. That is the case this check exists for: the incompatibility would survive both merges and surface later as a failing test neither author touched.')
      lines.push('')
    }
  }

  lines.push('---')
  lines.push('')
  lines.push('Whoever merges second should expect to integrate and re-run, and may want to agree the shared contract first. Regenerated on every push; nothing here is authoritative about merge order.')
  return lines.join('\n')
}

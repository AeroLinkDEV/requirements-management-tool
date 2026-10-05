// The advisory Fast workflow runs the logic and rendered tiers as shards on separate runners (#1456, #1232).
// AEROLINK_FAST_SHARD is "current/total"; unset, every test runs, as it does locally. The rendered config passes it to
// Playwright's count-based shard; the logic config packs whole files by duration (packedFiles).
export function fastShard(): { current: number; total: number } | null {
  const value = process.env.AEROLINK_FAST_SHARD
  if (!value) return null
  const match = /^(\d+)\/(\d+)$/.exec(value)
  const current = match ? Number(match[1]) : NaN
  const total = match ? Number(match[2]) : NaN
  if (!(current >= 1 && current <= total)) throw new Error(`Invalid AEROLINK_FAST_SHARD: ${value}`)
  return { current, total }
}

// The spec files one shard runs, packed by recorded duration as the Full journey shards are
// (scripts/plan-journey-shard.mjs): heaviest first into whichever shard is lightest. Playwright's count-based
// shards put the logic tier's FMS scenario files side by side, so one shard held 938 of 1,706 test-seconds and
// took 5.6 minutes while another took 3.1 (Fast run 37359815736). Every shard computes the same assignment over
// the same files, so together they run each file exactly once. Durations only weigh the packing: an unknown
// file is weighted at the median, and no file is ever left out.
export function packedFiles(files: readonly string[], durations: Record<string, unknown>, shard: { current: number; total: number }): string[] {
  const known = files.map((file) => durations[file]).filter((value): value is number => typeof value === 'number' && value > 0).sort((a, b) => a - b)
  const median = known.length ? known[Math.floor(known.length / 2)] : 1
  const weighted = [...new Set(files)]
    .map((file) => ({ file, weight: typeof durations[file] === 'number' && (durations[file] as number) > 0 ? durations[file] as number : median }))
    // Heaviest first, ties broken by name so every runner computes the identical assignment.
    .sort((a, b) => b.weight - a.weight || a.file.localeCompare(b.file))
  const load = Array.from({ length: shard.total }, () => 0)
  const mine: string[] = []
  for (const { file, weight } of weighted) {
    let lightest = 0
    for (let i = 1; i < shard.total; i++) if (load[i] < load[lightest]) lightest = i
    load[lightest] += weight
    if (lightest === shard.current - 1) mine.push(file)
  }
  return mine
}

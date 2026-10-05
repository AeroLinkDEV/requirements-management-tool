// The advisory Fast workflow runs the logic and rendered tiers as test-level shards on separate runners (#1456, #1232).
// AEROLINK_FAST_SHARD is "current/total"; unset, every test runs, as it does locally.
export function fastShard(): { current: number; total: number } | null {
  const value = process.env.AEROLINK_FAST_SHARD
  if (!value) return null
  const match = /^(\d+)\/(\d+)$/.exec(value)
  const current = match ? Number(match[1]) : NaN
  const total = match ? Number(match[2]) : NaN
  if (!(current >= 1 && current <= total)) throw new Error(`Invalid AEROLINK_FAST_SHARD: ${value}`)
  return { current, total }
}

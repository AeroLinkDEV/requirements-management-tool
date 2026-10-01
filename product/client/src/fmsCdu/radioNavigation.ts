import { bearingDeg, distanceNm, longitudeDelta, offset, type LatLon } from "./fmsModel";
import type { Navaid } from "./navData";
import { radioRange } from "./navigation";
import { sampled, validPosition, type RadioObservation, type Sample } from "./sensorPorts";
import { HELICOPTER_PROFILE, type AircraftProfile } from "./profile";

/** Laboratory radio model: 3 s acquisition, 0.02 NM range bias and 0.25 degree bearing bias. Ranges are measured from
 * the DME antenna (its own position when the data gives one) to the aircraft, over the height between them (the
 * station's elevation, Stage F1). Terrain masking, propagation and installed receiver algorithms are absent. */
const rad = (degrees: number) => degrees * Math.PI / 180;
const hasDme = (station: Navaid) => ["DME", "VORDME", "VORTAC", "TACAN"].includes(station.type);
/**
 * The horizontal range from a slant range and the height between the aircraft and the station, with the height
 * allowance carried through exactly (Stage F1, SF-08): the range is sqrt(slant² − height²), and over heights within
 * ±allowance it spans [sqrt(slant² − (|height| + allowance)²), sqrt(slant² − max(0, |height| − allowance)²)], so the
 * same allowance costs more horizontally the nearer the aircraft is to overhead. Refused, with the reason, rather than
 * clipped: a slant range no longer than the height (impossible), or one the height allowance could entirely explain
 * (near overhead: the horizontal range is undetermined), or values that are not finite. All in NM.
 */
export type HorizontalRange = { ok: true; rangeNm: number; allowanceNm: number } | { ok: false; reason: string };
export function horizontalRange(slantNm: number, heightNm: number, heightAllowanceNm: number): HorizontalRange {
  if (![slantNm, heightNm, heightAllowanceNm].every(Number.isFinite) || slantNm < 0 || heightAllowanceNm < 0) return { ok: false, reason: "slant range or height not valid" };
  const h = Math.abs(heightNm);
  if (slantNm <= h) return { ok: false, reason: "slant range no longer than the height to the station: impossible geometry" };
  const rangeNm = Math.sqrt(slantNm * slantNm - h * h);
  const high = h + heightAllowanceNm, low = Math.max(0, h - heightAllowanceNm);
  if (slantNm <= high) return { ok: false, reason: "near overhead: the station elevation allowance could explain the whole slant range" };
  const shortest = Math.sqrt(slantNm * slantNm - high * high), longest = Math.sqrt(slantNm * slantNm - low * low);
  return { ok: true, rangeNm, allowanceNm: Math.max(rangeNm - shortest, longest - rangeNm) };
}
/** Where a DME range is measured from: the DME's own position, or the station's. */
export const dmeAt = (station: Navaid) => station.dmePosition ?? station.position;

const hasVor = (station: Navaid) => ["VOR", "VORDME", "VORTAC"].includes(station.type);

export class BenchRadioReceiver {
  private tuning = new Map<string, { station: Navaid; since: number; acquired: boolean; inRange: boolean; acquisitionS: number }>();
  /** Which tuned stations give a range (a DME the radios report) and which a bearing (a NAV the radios report); by
   * default both, for callers without radio management. */
  private use: { range: ReadonlySet<string>; bearing: ReadonlySet<string> } | null = null;
  private sequence = 0;
  private readonly parameters: AircraftProfile["parameters"];
  constructor(parameters: AircraftProfile["parameters"] = HELICOPTER_PROFILE.parameters) { this.parameters = parameters; }
  /** `acquisitionS`: a per-station acquisition time (a DME scan channel's, plan C3); otherwise the AUTO facility one. */
  tune(stations: readonly Navaid[], now: number, use: { range: ReadonlySet<string>; bearing: ReadonlySet<string> } | null = null,
    acquisitionS: ReadonlyMap<string, number> = new Map()) {
    this.use = use;
    const next = new Map<string, { station: Navaid; since: number; acquired: boolean; inRange: boolean; acquisitionS: number }>();
    for (const station of stations) {
      const old = this.tuning.get(station.ident);
      const acquisition = acquisitionS.get(station.ident) ?? this.parameters.radioAcquisition.value;
      next.set(station.ident, old && old.station.frequency === station.frequency
        && distanceNm(old.station.position, station.position) < 1e-8 ? { ...old, acquisitionS: acquisition } : { station, since: now, acquired: false, inRange: true, acquisitionS: acquisition });
    }
    this.tuning = next;
  }
  /** Only the sensor simulator receives truth. The navigation solver below has no access to it. */
  sample(truth: LatLon, altitudeFt: number, now: number, failed = false): RadioObservation[] {
    this.sequence += 1;
    return [...this.tuning.values()].map(entry => {
      const distance = distanceNm(truth, dmeAt(entry.station));
      const inRange = !failed && distance <= radioRange(altitudeFt);
      if (!inRange) { entry.since = now; entry.acquired = false; }
      else if (!entry.inRange) { entry.since = now; entry.acquired = false; }
      else if (now - entry.since >= entry.acquisitionS * 1000) entry.acquired = true;
      entry.inRange = inRange;
      const normal = inRange && entry.acquired;
      const word = (value: number | null): Sample<number> => ({ at: now, sequence: this.sequence,
        status: failed ? "FAIL" : normal && value !== null ? "NORMAL" : "NCD", value: normal ? value : null });
      const sign = entry.station.ident.charCodeAt(0) % 2 ? 1 : -1;
      return { station: entry.station,
        slantRangeNm: word(hasDme(entry.station) && (!this.use || this.use.range.has(entry.station.ident))
          ? Math.hypot(distance, (altitudeFt - entry.station.elevation.feet) / 6076.12) + sign * this.parameters.radioRangeBias.value : null),
        bearingTrue: word(hasVor(entry.station) && (!this.use || this.use.bearing.has(entry.station.ident))
          ? (bearingDeg(entry.station.position, truth) + sign * this.parameters.radioBearingBias.value + 360) % 360 : null) };
    });
  }
}

/**
 * `assumedElevation` and `terrainElevation`: the DMEs used whose elevation the data did not give, assumed or taken
 * from the ground at an invented site; the fix's ANP carries that allowance, propagated through the geometry.
 * `rejected`: the ranges refused, and why (horizontalRange). `accuracyBasis` is always `laboratory` (Stage F plan C1):
 * the radio accuracy model and any elevation allowance are declared models, so the ANP is the simulator's estimate, not
 * a validated 95 percent bound or installation accuracy. F2 carries the basis with accuracy95Nm to the bus (C4).
 * `priorResolved` (plan C1, transitive provenance): the ranges alone admitted more than one consistent position, and the
 * prior estimate chose between them. The fix then inherits the prior's dependencies (a GPS-derived prior makes it
 * GPS-dependent); otherwise the measurements alone determine it.
 */
export type RadioFix = { position: LatLon; at: number; anp: number; mode: "DME/DME" | "VOR/DME"; dmes: string[]; vor: string | null; assumedElevation: string[]; terrainElevation: string[]; rejected: { ident: string; reason: string }[]; accuracyBasis: "laboratory"; priorResolved: boolean;
  /** Plan C3: the oldest contributing range's measurement time. A newer fix epoch never renews it. */
  oldestAt: number };
/** The ranges a solution may use, and those refused with the reason. */
export function rangeObservations(observations: readonly RadioObservation[], altitudeFt: number, now: number,
  parameters: AircraftProfile["parameters"] = HELICOPTER_PROFILE.parameters, maxAgeS = parameters.sensorMaxAge.value) {
  const rejected: { ident: string; reason: string }[] = [];
  const usable = observations.flatMap(observation => {
    const slant = sampled(observation.slantRangeNm, now, maxAgeS * 1000);
    const station = observation.station, at = dmeAt(station);
    if (!validPosition(at) || !hasDme(station) || slant === null) return [];
    const allowanceFt = station.elevation.source === "assumed" ? parameters.assumedNavaidElevationUncertainty.value
      : station.elevation.source === "terrain" ? parameters.terrainNavaidElevationUncertainty.value : 0;
    const result = horizontalRange(slant, (altitudeFt - station.elevation.feet) / 6076.12, allowanceFt / 6076.12);
    if (!result.ok) { rejected.push({ ident: station.ident, reason: result.reason }); return []; }
    return [{ observation, at, range: result.rangeNm, elevationError: result.allowanceNm, measuredAt: observation.slantRangeNm.at }];
  });
  return { usable, rejected };
}

/** Horizontal position from measured slant ranges (air-data altitude correction), never a fixed offset from truth.
 * Range-circle intersections use a local tangent plane. The prior estimate chooses the two-circle ambiguity;
 * additional ranges check residuals. This solver is a bench approximation, not CMA's Kalman implementation. */
/**
 * Plan C3's cached ranges: `rangeMaxAgeS` admits station-owned ranges up to the cache age (default: the sensor age), and
 * `motionKt` bounds how far the aircraft can have moved over a range's age. The ranges are not repropagated: each keeps
 * its measurement time, and the fix's accuracy adds the motion over its oldest range's age (laboratory). The motion bound
 * comes from air data, never GPS, so the fix stays GPS-independent; without it only fresh ranges are used.
 */
export type RangeOptions = { rangeMaxAgeS?: number; motionKt?: number | null };
export function radioFixes(observations: readonly RadioObservation[], prior: LatLon, altitudeFt: number, now: number,
  parameters: AircraftProfile["parameters"] = HELICOPTER_PROFILE.parameters, options: RangeOptions = {}): RadioFix[] {
  if (!validPosition(prior) || !Number.isFinite(altitudeFt)) return [];
  const motionKt = options.motionKt ?? null;
  const maxAgeS = motionKt === null ? parameters.sensorMaxAge.value : options.rangeMaxAgeS ?? parameters.sensorMaxAge.value;
  const { usable: ranges, rejected } = rangeObservations(observations, altitudeFt, now, parameters, maxAgeS);
  const oldestAt = (used: typeof ranges) => Math.min(...used.map(r => r.measuredAt));
  const motionNm = (used: typeof ranges) => motionKt === null ? 0 : Math.max(0, now - oldestAt(used)) / 1000 / 3600 * motionKt;
  const sourced = (used: typeof ranges, source: "assumed" | "terrain") => used.filter(r => r.observation.station.elevation.source === source).map(r => r.observation.station.ident);
  let best: (RadioFix & { score: number }) | null = null;
  const accepted: LatLon[] = [];
  // S300 1-8 falls back to collocated VOR/DME when fewer than three DME facilities are available.
  for (let i = 0; ranges.length >= parameters.radioMinFacilities.value && i < ranges.length; i++) for (let j = i + 1; j < ranges.length; j++) {
    const a = ranges[i], b = ranges[j], origin = a.at;
    const x = longitudeDelta(origin.lon, b.at.lon) * 60 * Math.cos(rad(origin.lat));
    const y = (b.at.lat - origin.lat) * 60;
    const d = Math.hypot(x, y);
    if (d < 0.1) continue;
    const along = (a.range ** 2 - b.range ** 2 + d ** 2) / (2 * d);
    const heightSquared = a.range ** 2 - along ** 2;
    if (heightSquared < 0) continue;
    const across = Math.sqrt(heightSquared);
    // The pair's two mirror points: the prior estimate only chooses between them (C1, R3-01); pairs are ranked by their
    // own accuracy, never by closeness to the prior, so a GPS-derived prior cannot pick among consistent pairs.
    let pairBest: (RadioFix & { score: number }) | null = null;
    for (const sign of [-1, 1]) {
      const east = along * x / d - sign * across * y / d;
      const north = along * y / d + sign * across * x / d;
      const position = offset(origin, Math.atan2(east, north) * 180 / Math.PI, Math.hypot(east, north));
      const angle = Math.abs(((bearingDeg(position, origin) - bearingDeg(position, b.at) + 540) % 360) - 180);
      if (angle < parameters.radioCrossAngle.value || angle > 180 - parameters.radioCrossAngle.value) continue;
      const residual = Math.max(...ranges.map(r => Math.abs(distanceNm(position, r.at) - r.range)));
      if (residual > parameters.radioResidualLimit.value) continue;
      accepted.push(position);
      const score = distanceNm(position, prior);
      if (!pairBest || score < pairBest.score) pairBest = { position, at: Math.min(a.observation.slantRangeNm.at, b.observation.slantRangeNm.at), mode: "DME/DME",
        anp: 0.1 + 0.15 / Math.sin(rad(angle)) + residual + Math.hypot(a.elevationError, b.elevationError) / Math.sin(rad(angle)) + motionNm(ranges),
        dmes: [a.observation.station.ident, b.observation.station.ident], vor: null, assumedElevation: sourced([a, b], "assumed"), terrainElevation: sourced([a, b], "terrain"), rejected, accuracyBasis: "laboratory" as const, priorResolved: false, oldestAt: oldestAt(ranges), score };
    }
    if (pairBest && (!best || pairBest.anp < best.anp)) best = pairBest;
  }
  const fixes: RadioFix[] = [];
  if (best) {
    // Another position, materially apart, also met every range and the geometry checks: only the prior chose.
    const apart = 2 * parameters.radioResidualLimit.value;
    const { score: _score, ...fix } = best; void _score;
    fixes.push({ ...fix, priorResolved: accepted.some(position => distanceNm(position, fix.position) > apart) });
  }
  // Plan F3: VOR/DME is its own candidate whenever it can be solved (M300 1-5's "fewer than three DMEs" describes
  // where it is typically used, not a gate); the most accurate one is offered.
  let vorDme: RadioFix | null = null;
  for (const entry of ranges) {
    const { observation, range } = entry;
    const bearing = sampled(observation.bearingTrue, now, parameters.sensorMaxAge.value * 1000);
    if (bearing === null || !Number.isFinite(bearing) || !hasVor(observation.station)) continue;
    // The bearing is from the VOR; the range from the DME (co-located, a few metres apart at most).
    const candidate: RadioFix = { position: offset(observation.station.position, bearing, range), at: Math.min(observation.slantRangeNm.at, observation.bearingTrue.at), mode: "VOR/DME",
      anp: 0.2 + 0.03 * range + entry.elevationError + motionNm([entry]), dmes: [observation.station.ident], vor: observation.station.ident, assumedElevation: sourced([entry], "assumed"), terrainElevation: sourced([entry], "terrain"), rejected, accuracyBasis: "laboratory", priorResolved: false, oldestAt: entry.measuredAt };
    if (!vorDme || candidate.anp < vorDme.anp) vorDme = candidate;
  }
  if (vorDme) fixes.push(vorDme);
  return fixes;
}

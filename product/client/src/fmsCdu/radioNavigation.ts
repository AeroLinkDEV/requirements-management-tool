import { bearingDeg, distanceNm, longitudeDelta, offset, type LatLon } from "./fmsModel";
import type { Navaid } from "./navData";
import { radioRange } from "./navigation";
import { sampled, validPosition, validRangeIdentity, type RadioObservation, type Sample } from "./sensorPorts";
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
/** A station that gives a bearing: a VOR, or a TACAN (plan F7: VOR/DME/TCN, M300 12-19). */
const givesBearing = (station: Navaid) => hasVor(station) || station.type === "TACAN";
/**
 * Plan F7 (M300 15-3): the VOR/DME 95% accuracy, a declared model of the manual's typical figures: 0.6 NM at the station
 * rising to 0.8 NM at 7 NM, and 1.5 NM beyond 7 NM (laboratory interpolation).
 */
const vorDmeAccuracy = (rangeNm: number) => rangeNm <= 7 ? 0.6 + 0.2 * Math.max(0, rangeNm) / 7 : 1.5;

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
        bearingTrue: word(givesBearing(entry.station) && (!this.use || this.use.bearing.has(entry.station.ident))
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
  oldestAt: number;
  observations?: readonly RadioObservation[];
  motion?: RadioMotion | null;
  naimEligible?: boolean };
/** Measured compensation velocity; sample age and dependency are separate from range age. */
export type RadioMotion = { source: "RADIO" | "DVS" | "AIR_WIND"; at: number; northKt: number; eastKt: number; gpsDependent: boolean };
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
 * C3: translate each range circle to the fix epoch using measured motion. Original observations stay immutable.
 * The declared residual allowance covers velocity uncertainty; absent motion, old geometry remains usable for
 * navigation with a conservative allowance, but cannot qualify as a NAIM backup.
 */
export type RangeOptions = { rangeMaxAgeS?: number; motion?: RadioMotion | null; unalignedMotionKt?: number;
  /** Plan F6: M300 15-3's typical DME/DME 95% accuracy for the phase (0.5 NM en route, 0.4 NM terminal). */
  typical95Nm?: number;
  /** Plan C3, F6: stations the crew deselected (DME DESELECT, M300 12-18): never used for DME/DME; VOR/DME is unaffected. */
  dmeDeselected?: ReadonlySet<string> };
/** A ranged station's state for DME STATUS (M300 12-17): used, or rejected with the reason. N/A is the radios' (no range). */
export type DmeStationStatus = { ident: string; status: "USED" | "REJ"; reason?: string };
export function radioFixes(observations: readonly RadioObservation[], prior: LatLon, altitudeFt: number, now: number,
  parameters: AircraftProfile["parameters"] = HELICOPTER_PROFILE.parameters, options: RangeOptions = {}, stationsOut?: DmeStationStatus[]): RadioFix[] {
  if (!validPosition(prior) || !Number.isFinite(altitudeFt)) return [];
  const motion = options.motion && sampled({ at: options.motion.at, sequence: 0, status: "NORMAL", value: options.motion }, now,
    parameters.sensorMaxAge.value * 1000) && [options.motion.northKt, options.motion.eastKt].every(Number.isFinite) ? options.motion : null;
  const maxAgeS = options.rangeMaxAgeS ?? parameters.sensorMaxAge.value;
  const { usable, rejected } = rangeObservations(observations, altitudeFt, now, parameters, maxAgeS);
  const ranges = usable.map(range => ({ ...range, at: motion ? offset(range.at, Math.atan2(motion.eastKt, motion.northKt) * 180 / Math.PI,
    Math.hypot(motion.northKt, motion.eastKt) * (now - range.measuredAt) / 3_600_000) : range.at }));
  const oldestAt = (used: typeof ranges) => Math.min(...used.map(r => r.measuredAt));
  const motionNm = (used: typeof ranges) => Math.max(0, now - oldestAt(used)) / 3_600_000
    * (motion ? parameters.rangeMotionWindAllowance.value : options.unalignedMotionKt ?? parameters.rangeMotionWindAllowance.value);
  const provenance = (used: typeof ranges) => ({ observations: used.map(r => structuredClone(r.observation)), motion: now === oldestAt(used) ? null : motion,
    naimEligible: used.every(r => validRangeIdentity(r.observation.rangeIdentity, r.observation.station.frequency)) && (now === oldestAt(used) || motion !== null && !motion.gpsDependent) });
  const sourced = (used: typeof ranges, source: "assumed" | "terrain") => used.filter(r => r.observation.station.elevation.source === source).map(r => r.observation.station.ident);
  type Range = (typeof ranges)[number];
  // Plan F6 (M300 15-3): the DME/DME 95% accuracy reproduces the manual's typical figures (0.5 NM en route, 0.4 NM
  // terminal) at a 90-degree crossing, widening as 1/sin of the crossing angle; residual, elevation and range-age terms
  // are added (laboratory).
  const typical = options.typical95Nm ?? 0.5;
  /** The best DME/DME position consistent with every range in the set (or null), and the stations no pairing could use. */
  const solveSet = (set: Range[]): { fix: RadioFix | null; unpaired: Set<string> } => {
    let best: (RadioFix & { score: number }) | null = null;
    const accepted: LatLon[] = [];
    const pairedOk = new Set<string>();
    for (let i = 0; set.length >= parameters.radioMinFacilities.value && i < set.length; i++) for (let j = i + 1; j < set.length; j++) {
      const a = set[i], b = set[j], origin = a.at;
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
        pairedOk.add(a.observation.station.ident); pairedOk.add(b.observation.station.ident);
        const residual = Math.max(...set.map(r => Math.abs(distanceNm(position, r.at) - r.range)));
        if (residual > parameters.radioResidualLimit.value) continue;
        accepted.push(position);
        const score = distanceNm(position, prior);
        if (!pairBest || score < pairBest.score) pairBest = { position, at: now, mode: "DME/DME",
          anp: typical / Math.sin(rad(angle)) + residual + Math.hypot(a.elevationError, b.elevationError) / Math.sin(rad(angle)) + motionNm(set),
          dmes: [a.observation.station.ident, b.observation.station.ident], vor: null, assumedElevation: sourced([a, b], "assumed"), terrainElevation: sourced([a, b], "terrain"), rejected, accuracyBasis: "laboratory" as const, priorResolved: false, oldestAt: oldestAt(set), ...provenance(set), score };
      }
      if (pairBest && (!best || pairBest.anp < best.anp)) best = pairBest;
    }
    // A station whose every pairing failed the crossing-angle check is rejected for geometry (M300 12-17 REJ).
    const unpaired = new Set(set.map(r => r.observation.station.ident).filter(ident => !pairedOk.has(ident)));
    if (!best) return { fix: null, unpaired };
    // Another position, materially apart, also met every range and the geometry checks: only the prior chose.
    const apart = 2 * parameters.radioResidualLimit.value;
    const { score: _score, ...fix } = best; void _score;
    return { fix: { ...fix, priorResolved: accepted.some(position => distanceNm(position, fix.position) > apart) } as RadioFix, unpaired };
  };
  // Plan F6 consistency and isolation: all ranges consistent, or (with four or more) exactly one station whose exclusion
  // leaves a consistent solution (a unique hypothesis). Three inconsistent ranges, or more than one plausible exclusion,
  // leave DME/DME unavailable with no culprit named.
  const dmeRanges = ranges.filter(r => !options.dmeDeselected?.has(r.observation.station.ident));
  const full = solveSet(dmeRanges), geometryRejected = full.unpaired;
  let dmeDme = full.fix;
  let isolated: string | null = null;
  let inconsistent = false;
  if (!dmeDme && dmeRanges.length >= parameters.radioMinFacilities.value && geometryRejected.size < dmeRanges.length) {
    inconsistent = true;
    if (dmeRanges.length >= parameters.radioMinFacilities.value + 1) {
      const hypotheses = dmeRanges.map((excluded, k) => ({ excluded, fix: solveSet(dmeRanges.filter((_, i) => i !== k)).fix })).filter(h => h.fix !== null);
      if (hypotheses.length === 1) { dmeDme = hypotheses[0].fix; isolated = hypotheses[0].excluded.observation.station.ident; inconsistent = false; }
    }
  }
  stationsOut?.splice(0, stationsOut.length, ...dmeRanges.map(r => {
    const ident = r.observation.station.ident;
    const status: DmeStationStatus = ident === isolated ? { ident, status: "REJ", reason: "range inconsistent with the others (isolated)" }
      : inconsistent ? { ident, status: "REJ", reason: "ranges inconsistent; no unique station to exclude" }
      : geometryRejected.has(ident) ? { ident, status: "REJ", reason: "geometry" }
      : dmeDme ? { ident, status: "USED" } : { ident, status: "REJ", reason: "fewer than three usable ranges" };
    return status;
  }));
  const fixes: RadioFix[] = [];
  if (dmeDme) fixes.push(dmeDme);
  // Plan F3: VOR/DME is its own candidate whenever it can be solved (M300 1-5's "fewer than three DMEs" describes
  // where it is typically used, not a gate); the most accurate one is offered.
  const vorCandidates: RadioFix[] = [];
  for (const entry of ranges) {
    const { observation, range } = entry;
    const bearing = sampled(observation.bearingTrue, now, parameters.sensorMaxAge.value * 1000);
    if (bearing === null || !Number.isFinite(bearing) || observation.bearingTrue.at !== entry.measuredAt || !givesBearing(observation.station)) continue;
    // Reasonableness (M300 15-3): a range beyond line-of-sight coverage cannot be the station's.
    if (range > radioRange(altitudeFt)) { rejected.push({ ident: observation.station.ident, reason: "VOR/DME range beyond radio coverage" }); continue; }
    // The bearing is from the VOR (or TACAN); the range from the DME (co-located, a few metres apart at most).
    const measuredPosition = offset(observation.station.position, bearing, range);
    const candidate: RadioFix = { position: motion ? offset(measuredPosition, Math.atan2(motion.eastKt, motion.northKt) * 180 / Math.PI,
      Math.hypot(motion.northKt, motion.eastKt) * (now - entry.measuredAt) / 3_600_000) : measuredPosition, at: now, mode: "VOR/DME",
      anp: vorDmeAccuracy(range) + entry.elevationError + motionNm([entry]), dmes: [observation.station.ident], vor: observation.station.ident, assumedElevation: sourced([entry], "assumed"), terrainElevation: sourced([entry], "terrain"), rejected, accuracyBasis: "laboratory", priorResolved: false, oldestAt: entry.measuredAt, ...provenance([entry]) };
    vorCandidates.push(candidate);
  }
  // Reasonableness between sources (plan F7, without the prior): two VOR/DME positions that disagree by more than their
  // accuracies together cannot tell which is wrong, so neither is used; a VOR/DME that disagrees with the DME/DME fix is
  // rejected when that DME/DME position is independent. A mirror chosen using the prior, or a position moved with
  // GPS-dependent motion, cannot veto an independent bearing/range candidate. Agreeing candidates offer the most accurate.
  const agree = (a: RadioFix, b: RadioFix) => distanceNm(a.position, b.position) <= a.anp + b.anp;
  const independentDme = dmeDme && !dmeDme.priorResolved && !dmeDme.motion?.gpsDependent ? dmeDme : null;
  const reasonable = vorCandidates.filter(candidate => (!independentDme || agree(candidate, independentDme))
    && vorCandidates.every(other => other === candidate || agree(candidate, other)));
  for (const candidate of vorCandidates) if (!reasonable.includes(candidate)) rejected.push({ ident: candidate.vor!, reason: "VOR/DME position disagrees with another source" });
  const vorDme = [...reasonable].sort((a, b) => a.anp - b.anp)[0];
  if (vorDme) fixes.push(vorDme);
  return fixes;
}

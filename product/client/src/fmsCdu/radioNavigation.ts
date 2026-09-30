import { bearingDeg, distanceNm, longitudeDelta, offset, type LatLon } from "./fmsModel";
import type { Navaid } from "./navData";
import { radioRange } from "./navigation";
import { sampled, validPosition, type RadioObservation, type Sample } from "./sensorPorts";
import { HELICOPTER_PROFILE, type AircraftProfile } from "./profile";

/** Laboratory radio model: 3 s acquisition, 0.02 NM range bias and 0.25 degree bearing bias, station elevation zero
 * unless supplied by a later sensor adapter. Terrain masking, propagation and installed receiver algorithms are absent. */
const rad = (degrees: number) => degrees * Math.PI / 180;
const hasDme = (station: Navaid) => ["DME", "VORDME", "VORTAC"].includes(station.type);
const hasVor = (station: Navaid) => ["VOR", "VORDME", "VORTAC"].includes(station.type);

export class BenchRadioReceiver {
  private tuning = new Map<string, { station: Navaid; since: number; acquired: boolean; inRange: boolean }>();
  private sequence = 0;
  private readonly parameters: AircraftProfile["parameters"];
  constructor(parameters: AircraftProfile["parameters"] = HELICOPTER_PROFILE.parameters) { this.parameters = parameters; }
  tune(stations: readonly Navaid[], now: number) {
    const next = new Map<string, { station: Navaid; since: number; acquired: boolean; inRange: boolean }>();
    for (const station of stations) {
      const old = this.tuning.get(station.ident);
      next.set(station.ident, old && old.station.frequency === station.frequency
        && distanceNm(old.station.position, station.position) < 1e-8 ? old : { station, since: now, acquired: false, inRange: true });
    }
    this.tuning = next;
  }
  /** Only the sensor simulator receives truth. The navigation solver below has no access to it. */
  sample(truth: LatLon, altitudeFt: number, now: number, failed = false): RadioObservation[] {
    this.sequence += 1;
    return [...this.tuning.values()].map(entry => {
      const distance = distanceNm(truth, entry.station.position);
      const inRange = !failed && distance <= radioRange(altitudeFt);
      if (!inRange) { entry.since = now; entry.acquired = false; }
      else if (!entry.inRange) { entry.since = now; entry.acquired = false; }
      else if (now - entry.since >= this.parameters.radioAcquisition.value * 1000) entry.acquired = true;
      entry.inRange = inRange;
      const normal = inRange && entry.acquired;
      const word = (value: number | null): Sample<number> => ({ at: now, sequence: this.sequence,
        status: failed ? "FAIL" : normal && value !== null ? "NORMAL" : "NCD", value: normal ? value : null });
      const sign = entry.station.ident.charCodeAt(0) % 2 ? 1 : -1;
      return { station: entry.station,
        slantRangeNm: word(hasDme(entry.station) ? Math.hypot(distance, altitudeFt / 6076.12) + sign * this.parameters.radioRangeBias.value : null),
        bearingTrue: word(hasVor(entry.station) ? (bearingDeg(entry.station.position, truth) + sign * this.parameters.radioBearingBias.value + 360) % 360 : null) };
    });
  }
}

export type RadioFix = { position: LatLon; at: number; anp: number; mode: "DME/DME" | "VOR/DME"; dmes: string[]; vor: string | null };

/** Horizontal position from measured slant ranges (air-data altitude correction), never a fixed offset from truth.
 * Range-circle intersections use a local tangent plane. The prior estimate chooses the two-circle ambiguity;
 * additional ranges check residuals. This solver is a bench approximation, not CMA's Kalman implementation. */
export function solveRadio(observations: readonly RadioObservation[], prior: LatLon, altitudeFt: number, now: number,
  parameters: AircraftProfile["parameters"] = HELICOPTER_PROFILE.parameters): RadioFix | null {
  if (!validPosition(prior) || !Number.isFinite(altitudeFt)) return null;
  const ranges = observations.flatMap(observation => {
    const slant = sampled(observation.slantRangeNm, now, parameters.sensorMaxAge.value * 1000);
    const height = altitudeFt / 6076.12;
    if (!validPosition(observation.station.position) || !hasDme(observation.station)
      || slant === null || !Number.isFinite(slant) || slant <= Math.abs(height)) return [];
    return [{ observation, range: Math.sqrt(slant * slant - height * height) }];
  });
  let best: (RadioFix & { score: number }) | null = null;
  // S300 1-8 falls back to collocated VOR/DME when fewer than three DME facilities are available.
  for (let i = 0; ranges.length >= parameters.radioMinFacilities.value && i < ranges.length; i++) for (let j = i + 1; j < ranges.length; j++) {
    const a = ranges[i], b = ranges[j], origin = a.observation.station.position;
    const x = longitudeDelta(origin.lon, b.observation.station.position.lon) * 60 * Math.cos(rad(origin.lat));
    const y = (b.observation.station.position.lat - origin.lat) * 60;
    const d = Math.hypot(x, y);
    if (d < 0.1) continue;
    const along = (a.range ** 2 - b.range ** 2 + d ** 2) / (2 * d);
    const heightSquared = a.range ** 2 - along ** 2;
    if (heightSquared < 0) continue;
    const across = Math.sqrt(heightSquared);
    for (const sign of [-1, 1]) {
      const east = along * x / d - sign * across * y / d;
      const north = along * y / d + sign * across * x / d;
      const position = offset(origin, Math.atan2(east, north) * 180 / Math.PI, Math.hypot(east, north));
      const angle = Math.abs(((bearingDeg(position, origin) - bearingDeg(position, b.observation.station.position) + 540) % 360) - 180);
      if (angle < parameters.radioCrossAngle.value || angle > 180 - parameters.radioCrossAngle.value) continue;
      const residual = Math.max(...ranges.map(r => Math.abs(distanceNm(position, r.observation.station.position) - r.range)));
      if (residual > parameters.radioResidualLimit.value) continue;
      const score = distanceNm(position, prior) + residual;
      if (!best || score < best.score) best = { position, at: Math.min(a.observation.slantRangeNm.at, b.observation.slantRangeNm.at), mode: "DME/DME", anp: 0.1 + 0.15 / Math.sin(rad(angle)) + residual,
        dmes: [a.observation.station.ident, b.observation.station.ident], vor: null, score };
    }
  }
  if (best) return best;
  for (const { observation, range } of ranges) {
    const bearing = sampled(observation.bearingTrue, now, parameters.sensorMaxAge.value * 1000);
    if (bearing === null || !Number.isFinite(bearing) || !hasVor(observation.station)) continue;
    return { position: offset(observation.station.position, bearing, range), at: Math.min(observation.slantRangeNm.at, observation.bearingTrue.at), mode: "VOR/DME",
      anp: 0.2 + 0.03 * range, dmes: [observation.station.ident], vor: observation.station.ident };
  }
  return null;
}

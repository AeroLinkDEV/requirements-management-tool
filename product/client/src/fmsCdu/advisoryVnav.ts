import { bearingDeg, distanceNm, type LatLon } from "./fmsModel";
import type { Procedure, Runway } from "./navData";

/** M300 7-22..27. An advisory geometric path; no autopilot control input. All lengths are nautical miles. */
export type AdvisoryVnav = {
  available: boolean; reason: string | null; angleDeg: number | null; angleAlert: string | null;
  deviationFt: number | null; targetVsFpm: number | null; pathAltitudeFt: number | null;
  fullScaleFt: number; crossTrackNm: number | null; source: "DATABASE" | "FAF" | "DEFAULT" | null;
};
export function advisoryVnav(input: {
  approach?: Procedure; runway?: Runway; faf?: LatLon; fafAltitude: number | null;
  position: LatLon; track: number | null; groundSpeed: number | null; altitude: number | null;
  terminal: boolean; approachPhase: boolean; gps: boolean; systemValid: boolean;
  rateValid: boolean; altitudesAgree: boolean; temperatureRequired: boolean; temperature: number | null;
  thresholdPassed: boolean;
  minimumProgress?: number; temperatureLapseRate?: number;
}): AdvisoryVnav {
  const result: AdvisoryVnav = { available: false, reason: null, angleDeg: null, angleAlert: null, deviationFt: null,
    targetVsFpm: null, pathAltitudeFt: null, fullScaleFt: input.approachPhase ? 200 : 500, crossTrackNm: null, source: null };
  const deny = (reason: string) => ({ ...result, reason });
  const { approach, runway, faf } = input;
  if (!approach || !runway || !/\d{2}/.test(runway.ident) || approach.pointInSpace) return deny("NO RUNWAY THRESHOLD");
  const coded = approach.endpoint?.vertical.kind === "VPA" ? approach.endpoint.vertical.angleDeg
    : approach.legs.find(leg => "ident" in leg && leg.ident === runway.ident && leg.verticalAngleDeg !== undefined);
  const codedAngle = typeof coded === "number" ? Math.abs(coded) : coded && "verticalAngleDeg" in coded ? Math.abs(coded.verticalAngleDeg!) : null;
  const fafDistance = faf ? distanceNm(runway.threshold, faf) : null;
  const computed = fafDistance !== null && fafDistance > 0 && input.fafAltitude !== null
    ? Math.atan((input.fafAltitude - runway.elevation) / (fafDistance * 6076.12)) * 180 / Math.PI : 3;
  const angle = codedAngle !== null && codedAngle > 0 ? codedAngle : computed;
  result.angleDeg = angle;
  result.source = codedAngle !== null && codedAngle > 0 ? "DATABASE" : input.fafAltitude !== null && fafDistance !== null ? "FAF" : "DEFAULT";
  result.angleAlert = angle < 2.75 ? "LOW GLIDEPATH ANGLE" : angle > 3.77 ? "HIGH GLIDEPATH ANGLE" : null;
  if (!Number.isFinite(angle) || angle <= 0 || angle > 10) return deny("INVALID GLIDEPATH ANGLE");
  if (!input.gps) return deny("NO GPS NAVIGATION");
  if (!input.systemValid) return deny("FMS INVALID");
  if (input.altitude === null || !Number.isFinite(input.altitude)) return deny("INVALID BARO ALTITUDE");
  if (!input.rateValid) return deny("ABNORMAL ALTITUDE RATE");
  if (!input.altitudesAgree) return deny("ALTITUDES DISAGREE");
  if (input.temperatureRequired && input.temperature === null) return deny("AIRPORT TEMP REQUIRED");
  if (input.temperature !== null && (!Number.isFinite(input.temperature) || input.temperature < -55 || input.temperature > 55)) return deny("INVALID AIRPORT TEMP");
  if (!input.terminal || input.thresholdPassed) return deny("OUTSIDE TERMINAL APPROACH");
  if (input.track === null || !Number.isFinite(input.track) || input.groundSpeed === null || !Number.isFinite(input.groundSpeed) || input.groundSpeed < (input.minimumProgress ?? 1)) return deny("NO VALID VELOCITY");
  // One knot is the bench's declared minimum progress value, not an OEM advisory-VNAV threshold.
  const inbound = faf ? bearingDeg(faf, runway.threshold) : runway.course;
  const d = distanceNm(runway.threshold, input.position) / 3440.065;
  const theta = (bearingDeg(runway.threshold, input.position) - (inbound + 180)) * Math.PI / 180;
  const cross = Math.asin(Math.sin(d) * Math.sin(theta)) * 3440.065;
  const along = Math.atan2(Math.sin(d) * Math.cos(theta), Math.cos(d)) * 3440.065;
  result.crossTrackNm = cross;
  const beforeFaf = fafDistance !== null && along > fafDistance;
  const headingError = ((input.track - inbound + 540) % 360 + 360) % 360 - 180;
  if (along <= 0 || Math.abs(cross) > (beforeFaf ? 6 : 0.6) || Math.abs(headingError) >= 90) return deny("OUTSIDE INBOUND GLIDEPATH ZONE");
  const height = along * 6076.12 * Math.tan(angle * Math.PI / 180);
  // Declared bench temperature model: linear ISA lapse-rate ratio at airport elevation, including warm corrections.
  // The operator manual specifies compensation but does not publish its OEM computation; this is not that algorithm.
  const isa = 15 - runway.elevation * (input.temperatureLapseRate ?? 0.0019812);
  const factor = input.temperature === null ? 1 : (273.15 + isa) / (273.15 + input.temperature);
  const path = runway.elevation + (50 + height) * factor;
  return { ...result, available: true, reason: null, pathAltitudeFt: path, deviationFt: input.altitude - path,
    targetVsFpm: -input.groundSpeed * 6076.12 / 60 * Math.tan(angle * Math.PI / 180) * factor };
}

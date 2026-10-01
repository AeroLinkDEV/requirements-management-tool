import { bearingDeg, distanceNm, offset, type LatLon } from "./fmsModel";
import type { ProcedureHold, ProcedureLeg, SpeedLimit } from "./navData";
import { HELICOPTER_PROFILE } from "./profile";

/** ARINC 424-17/18 primary terminal record layout, 4.1.9.1 and 4.2.3.1. */
export type ProcedureRecord = {
  kind: "SID" | "APPROACH"; routeType: string; transition: string; sequence: number; fix: string; description: string; turn: string; path: string;
  course: string; distance: string; altitudeDescription: string; altitude1: string; altitude2: string;
  speed: string; verticalAngle: string; speedDescription: string;
  recommended: string; centre: string; radius: string; rho: string;
  /** Section and subsection of the fix (columns 37-38) and of the recommended navaid (79-80): "DB" an NDB, "D " a VHF navaid. */
  fixSection: string; recommendedSection: string;
};

/**
 * Where a coded ident is. `place` resolves it, by its section code where the data gives one. `pin` gives a position
 * only when the ident names more than one record and the section picks one: the leg then carries it.
 */
export type CodedPlace = (ident: string, section?: string) => LatLon | undefined;

function speedLimit(record: ProcedureRecord): SpeedLimit | undefined {
  if (!/^\d{3}$/.test(record.speed) || Number(record.speed) === 0) return undefined;
  return { kt: Number(record.speed), descriptor: record.speedDescription === "+" ? "AT OR ABOVE" : record.speedDescription === "-" ? "AT OR BELOW" : "AT" };
}

export function constraintText(record: ProcedureRecord): string | undefined {
  const { altitudeDescription: d, altitude1: a, altitude2: b } = record;
  const value = (text: string) => /^FL\d{3}$/.test(text) ? text : /^\d{1,5}$/.test(text) ? String(Number(text)) : null;
  const first = value(a);
  if (first === null) return undefined;
  if (d === "-") return `${first}B`;
  if (d === " ") return first;
  if (d === "B") { const second = value(b); return second === null ? `${first}B` : `${first}B${second}A`; }
  return `${first}A`;
}

export const codedVerticalAngle = (text: string) => /^[+-]?\d{3}$/.test(text.trim()) ? Number(text.trim()) / 100 : undefined;

/** Shared departure/approach decoding. Bad geometry refuses the containing procedure or transition. */
export function decodeProcedureLegs(records: ProcedureRecord[], variation: number, place: CodedPlace, pin: CodedPlace = () => undefined): ProcedureLeg[] | string {
  // 5.26: T occupies the tenths column (194T means 194 degrees true), never the distance field.
  const trueCourse = (text: string) => /^\d{3}T$/.test(text) && Number(text.slice(0, 3)) <= 360 ? Number(text.slice(0, 3)) % 360
    : /^\d{4}$/.test(text) && Number(text) <= 3600 ? (Number(text) / 10 + variation + 360) % 360 : null;
  const out: ProcedureLeg[] = [];
  for (const [index, r] of records.entries()) {
    const altitude = constraintText(r), limit = speedLimit(r), angle = codedVerticalAngle(r.verticalAngle);
    const direction = r.turn === "L" ? "LEFT" as const : r.turn === "R" ? "RIGHT" as const : undefined;
    const common = { ...(altitude ? { altitude } : {}), ...(direction ? { turnDirection: direction } : {}), ...(limit ? { speedLimit: limit } : {}) };
    const pinned = pin(r.fix, r.fixSection), coded = pinned ? { position: pinned } : {};
    const distance = r.recommended && /^\d{4}$/.test(r.rho) ? { navaidDistance: { ident: r.recommended, nm: Number(r.rho) / 10 } } : {};
    if (r.path === "HF" || r.path === "HA" || r.path === "HM") {
      const inbound = trueCourse(r.course);
      if (inbound === null) return `${r.path} hold without an inbound course`;
      const hold: ProcedureHold = {
        path: r.path, inbound: Math.round(inbound * 10) / 10, turn: direction ?? "RIGHT",
        legDistanceNm: /^\d{4}$/.test(r.distance) ? Number(r.distance) / 10 : null,
        legTimeMin: /^T\d{3}$/.test(r.distance) ? Number(r.distance.slice(1)) / 10 : null,
        exit: r.path === "HF" ? "ONCE" : r.path === "HA" ? "AT ALT" : "MANUAL",
        ...(altitude ? { altitude } : {}), ...(limit ? { speedLimit: limit } : {}),
      };
      const last = out.at(-1);
      if (last && "ident" in last && last.ident === r.fix && !last.hold) out[out.length - 1] = { ...last, hold };
      else out.push({ ident: r.fix, ...coded, ...(altitude ? { altitude } : {}), hold });
      continue;
    }
    if (r.path === "PI") {
      const reference = place(r.fix, r.fixSection), course = trueCourse(r.course), next = records[index + 1];
      if (!reference || course === null || !direction || next?.path !== "CF" || trueCourse(next.course) === null) return "PI without reference, direction, outbound course or following CF";
      const maximum = /^\d{4}$/.test(r.distance) ? Number(r.distance) / 10 : null;
      if (maximum === null || maximum <= 0) return "PI without a positive maximum distance";
      // M300 7-1: 1 min then 45 s at 180 kt, 3.00/2.25 NM, with a 45-degree outbound turn (B-4).
      // The manual does not publish the short-limit reduction formula. This bench reserves 2.25 NM for leg 2.
      const firstDistance = maximum >= 10 ? 3 : Math.max(0, Math.min(3, maximum - HELICOPTER_PROFILE.parameters.piSecondLegReserve.value));
      if (firstDistance === 0) return "PI maximum distance too small for the declared outbound construction";
      const first = offset(reference, course, firstDistance), secondCourse = (course + (direction === "LEFT" ? -45 : 45) + 360) % 360;
      const second = offset(first, secondCourse, 2.25), opposite = direction === "LEFT" ? "RIGHT" as const : "LEFT" as const;
      const last = out.at(-1);
      const ref: ProcedureLeg = { ident: r.fix, ...coded, ...common, overfly: true, procedureTurn: { reference: r.fix, role: "REFERENCE" } };
      if (last && "ident" in last && last.ident === r.fix) out[out.length - 1] = { ...last, ...ref };
      else out.push(ref);
      out.push({ ident: `${r.fix.slice(0, 3)}PT${direction[0]}`, position: first, ...common, path: "TF", overfly: true, procedureTurn: { reference: r.fix, role: "OUTBOUND" } });
      out.push({ ident: `${r.fix.slice(0, 3)}PT${opposite[0]}`, position: second, ...common, turnDirection: direction, path: "TF", overfly: true, procedureTurn: { reference: r.fix, role: "OUTBOUND" } });
      continue;
    }
    if (r.path === "AF" || r.path === "RF") {
      const centre = r.path === "AF" ? place(r.recommended, r.recommendedSection) : place(r.centre), end = place(r.fix, r.fixSection);
      if (!centre || !end || !direction) return `${r.path} without centre, endpoint or turn direction`;
      const radius = r.path === "AF" ? (/^\d{4}$/.test(r.rho) ? Number(r.rho) / 10 : null) : (/^\d{6}$/.test(r.radius) ? Number(r.radius) / 1000 : null);
      if (radius === null || radius <= 0) return `${r.path} without a positive coded radius`;
      // Laboratory import consistency limit: 0.15 NM for 0.1-NM AF rho; 0.02 NM for millinmile RF radius.
      const tolerance = r.path === "AF" ? HELICOPTER_PROFILE.parameters.afRadiusTolerance.value : HELICOPTER_PROFILE.parameters.rfRadiusTolerance.value;
      if (Math.abs(distanceNm(centre, end) - radius) > tolerance) return `${r.path} endpoint disagrees with coded radius`;
      const previous = out.at(-1), start = previous && "ident" in previous ? previous.position ?? place(previous.ident) : undefined;
      if (!start || Math.abs(distanceNm(centre, start) - radius) > tolerance) return `${r.path} start is missing or disagrees with coded radius`;
      out.push({ ident: r.fix, ...coded, ...common, path: r.path, arc: { centre, turn: r.turn as "L" | "R" }, course: (bearingDeg(centre, end) + (direction === "RIGHT" ? 90 : -90) + 360) % 360 });
      continue;
    }
    const inboundPi = records[index - 1]?.path === "PI" ? records[index - 1] : null;
    const turn = inboundPi ? (inboundPi.turn === "L" ? "RIGHT" as const : "LEFT" as const) : direction;
    switch (r.path) {
      case "IF": case "TF": case "DF":
        out.push({ ident: r.fix, ...coded, ...common, ...(r.path !== "IF" ? { path: r.path } : {}), ...(angle !== undefined ? { verticalAngleDeg: angle } : {}), ...distance }); break;
      case "CF": {
        const course = trueCourse(r.course);
        if (course === null) return "CF leg without a course";
        out.push({ ident: r.fix, ...coded, ...common, ...distance, path: "CF", course, ...(turn ? { turnDirection: turn } : {}), ...(inboundPi ? { procedureTurn: { reference: inboundPi.fix, role: "INBOUND" } } : {}) }); break;
      }
      case "CA": case "FA": case "VA": case "VI": case "VM": case "FM": {
        const course = trueCourse(r.course), feet = Number(/^\d{1,5}$/.test(r.altitude1) ? r.altitude1 : NaN);
        if (course === null) return `${r.path} leg without a course`;
        out.push({ path: r.path, course, ...(Number.isFinite(feet) ? { altitude: feet } : {}), ...(direction ? { turnDirection: direction } : {}), ...(limit ? { speedLimit: limit } : {}) }); break;
      }
      default: return `${r.path} legs are not flown by this simulation`;
    }
  }
  return out;
}

import { alert } from "./alerts";
import { formatConstraint, parseAltitude, parseConstraint } from "./vnav";
import {
  WAYPOINT, boxes, caption, conditionalLabel, courseDeg, dashes, distanceNm, fixed, formatPosition, hhmm, medium, numberIn, offset, pad, parsePosition, prompt, simulated,
  small, three, title, type CorePageId, type Leg, type LskResult, type Page, type PageId,
} from "./fmsModel";
import { HAL_NM, MODE_TEXT, shownReceiver } from "./gpsSensors";
import { makingProgress } from "./kinematics";
import { gpsSummary, navModeText, sbasSummary } from "./navPages";
import type { Line } from "./screen";
import type { ScriptedFms } from "./scriptedFms";

/** The core CMA-9000 pages: index, identification, route, legs, progress, radio, fuel, hold, VNAV and timer. */

const back = (target: string): Line["left"] => prompt(`<${target}`);

/** The final approach fix is the leg before the runway; the vertical path runs from it to the threshold. */
function approach(fms: ScriptedFms) {
  const legs = fms.activeRoute.legs;
  const runwayAt = legs.findIndex(leg => leg.kind === "wpt" && /^RW\d{2}/.test(leg.ident));
  const runway = legs[runwayAt];
  const before = legs[runwayAt - 1];
  // Once the FAF is sequenced the runway is the active waypoint; the path still runs from the FAF.
  const faf = before?.kind === "wpt" ? before.ident : runwayAt === 0 ? fms.lastSequenced : null;
  if (runway?.kind !== "wpt" || !faf) return null;
  const runwayPos = fms.coordinates(runway.ident), fafPos = fms.coordinates(faf);
  if (!runwayPos || !fafPos) return null;
  const length = distanceNm(fafPos, runwayPos);
  const vpa = (Math.atan((fms.vnav.fafAltitude - fms.vnav.runwayElevation) / (length * 6076.12)) * 180) / Math.PI;
  return { runway: runway.ident, faf, runwayPos, length, vpa };
}

/** The manual's glidepath limits for the LOW and HIGH GLIDEPATH ANGLE alerts. */
export const GLIDEPATH_LIMITS = { low: 2.75, high: 3.77 };

export function verticalPathAngle(fms: ScriptedFms) { return approach(fms)?.vpa ?? null; }

/** The right side of a LEGS line: the speed and altitude constraints, as 180/4500. */
const altitudeText = (leg: Leg) => {
  if (leg.kind !== "wpt") return "";
  const altitude = leg.altitude ? formatConstraint(parseConstraint(leg.altitude) ?? { kind: "AT", altitude: 0 }) : "-----";
  // A coded procedure speed limit (at, or at or below) shows like a speed constraint; the crew flies it (ADVISORY).
  const speed = leg.speed ?? (leg.speedLimit && leg.speedLimit.descriptor !== "AT OR ABOVE" ? leg.speedLimit.kt : undefined);
  return speed ? `${speed}/${leg.altitude ? altitude : "-----"}` : altitude;
};

const eta = (ms: number) => hhmm(new Date(ms)).slice(0, 4) + "Z";

const MONTHS = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
/** A database cycle's effective dates as the FMS prints them: 03SEP-30SEP, or UNKNOWN when its data gives none. */
const cycleDates = (cycle: { from: number | null; to: number | null }) => {
  if (cycle.from === null || cycle.to === null) return "UNKNOWN";
  const day = (ms: number) => { const d = new Date(ms); return `${String(d.getUTCDate()).padStart(2, "0")}${MONTHS[d.getUTCMonth()]}`; };
  return `${day(cycle.from)}-${day(cycle.to)}`;
};

/** EFOB at the prediction endpoint (the MAP, or over the landing site), or dashes when it is not computed. */
const efobText = (fms: ScriptedFms) => {
  const fuel = fms.profile().destination?.fuel ?? null;
  return fuel === null ? "-----KG" : `${Math.max(0, Math.round(fuel))}KG`;
};

/**
 * The endpoint's basis as the pages state it (plan C.11, R3-03): the endpoint kind (INSTR END, the MAP; SITE ARR, over
 * the landing site) and the status (KNOWN; COND with its assumption; UNKNOWN with the reason). Never a landing.
 */
const basisText = (fms: ScriptedFms) => {
  const endpoint = fms.profile().endpoint;
  if (!endpoint) return { kind: "NO ENDPOINT", status: "UNKNOWN", reason: "NO MAP OR LANDING SITE" };
  const { status, reason } = endpoint.point;
  return { kind: endpoint.kind === "INSTRUMENT END" ? "INSTR END" : "SITE ARR", status: status === "CONDITIONAL" ? "COND" : status, reason };
};

/** VNAV CRZ: the planned cruise, path angle and wind, with the top and end of descent the profile works out. */
function vnavCruise(fms: ScriptedFms): (Line | undefined)[] {
  const profile = fms.profile();
  const tod = profile.topOfDescent;
  const todEta = tod === null || !makingProgress(fms.closureSpeed) ? null : fms.now.getTime() + (tod / fms.closureSpeed) * 3_600_000;
  const next = profile.points.find(p => { const leg = fms.activeRoute.legs.find(l => l.kind === "wpt" && l.ident === p.ident); return leg?.kind === "wpt" && leg.altitude; });
  const nextLeg = next ? fms.activeRoute.legs.find(l => l.kind === "wpt" && l.ident === next.ident) : undefined;
  return [
    title("VNAV CRZ", "2/3", "ACT"),
    caption(" CRZ ALT", "CRZ SPD "),
    { left: { text: formatConstraint({ kind: "AT", altitude: fms.vnav.cruiseAltitude }) }, right: { text: `${fms.vnav.cruiseSpeed}KT` } },
    caption(" PATH ANGLE", "WIND "),
    { left: { text: `${fixed(fms.vnav.pathAngle, 1)}°` }, right: { text: `${three(fms.wind.direction)}°/${fms.wind.speed}KT` } },
    caption(" T/D", "E/D "),
    { left: medium(tod === null ? (profile.descending ? "PASSED" : "-----") : `${fixed(tod, 1)}NM ${eta(todEta!)}`), right: medium(profile.endOfDescent ?? "-----") },
    caption(" NEXT RESTR"),
    {
      left: nextLeg?.kind === "wpt" ? medium(`${nextLeg.ident} ${altitudeText(nextLeg)}`) : dashes(5),
      right: profile.unableNext ? medium(`UNABLE ${profile.unableNext}`, "amber") : undefined,
    },
    undefined, undefined,
    { left: dashes(24) },
    { left: back("INDEX") },
  ];
}

function vnavCruiseLsk(fms: ScriptedFms, side: "L" | "R", row: number, scratch: string): LskResult {
  if (side === "L" && row === 6) { fms.open("INIT_REF"); return; }
  if (!scratch) return;
  if (side === "L" && row === 1) {
    const altitude = parseAltitude(scratch, true);
    if (altitude === null || altitude < 500 || altitude > 25000) return "invalid";
    fms.vnav.cruiseAltitude = altitude;
    return void fms.setScratch("");
  }
  if (side === "R" && row === 1) {
    const speed = numberIn(scratch, 60, 250, /^\d{2,3}$/);
    if (speed === null) return "invalid";
    fms.vnav.cruiseSpeed = speed;
    return void fms.setScratch("");
  }
  if (side === "L" && row === 2) {
    const angle = numberIn(scratch, 2, 4.5, /^\d(\.\d{1,2})?$/);
    if (angle === null) return "invalid";
    fms.vnav.pathAngle = angle;
    return void fms.setScratch("");
  }
  if (side === "R" && row === 2) {
    // WIND: direction/speed, as 270/12.
    const wind = /^(\d{3})\/(\d{1,3})$/.exec(scratch);
    if (!wind || Number(wind[1]) > 360 || Number(wind[2]) > 150) return "invalid";
    fms.wind.direction = Number(wind[1]) % 360;
    fms.wind.speed = Number(wind[2]);
    return void fms.setScratch("");
  }
}

/**
 * The prediction at the endpoint (plan C.11, R3-03): the MAP ("CRANN (MAP)") or arrival over the landing site ("RW15
 * (THR)", "87N"), its ETA and fuel on board there (amber below the reserve), and its basis: the endpoint kind and the
 * status, with the assumption of a CONDITIONAL prediction or why an UNKNOWN one is not computed. No landing is modelled,
 * so the landing reserve is never shown as met: LANDING NOT MODELLED.
 */
function destinationPrediction(fms: ScriptedFms): (Line | undefined)[] {
  const profile = fms.profile(), endpoint = profile.endpoint, point = endpoint?.point;
  const basis = basisText(fms);
  const timed = point && point.eta !== null && point.fuel !== null ? point : null;
  const short = timed !== null && timed.fuel! < fms.fuelState.reserve;
  return [
    caption(` ${endpoint ? endpoint.label : fms.activeRoute.dest}`, "EFOB "),
    { left: medium(timed ? eta(timed.eta!) : "-----"), right: medium(timed ? `${Math.max(0, Math.round(timed.fuel!))}KG` : "-----KG", short ? "amber" : "white") },
    caption(` ${basis.kind}`, `${basis.status} `),
    basis.reason ? { left: small(basis.reason, basis.status === "UNKNOWN" ? "amber" : "white") } : undefined,
    caption(" LDG RESERVE"),
    { left: medium(profile.reserve.reason.toUpperCase()) },
  ];
}

/** VNAV DES: the end of descent, the path, the deviation from it and DES NOW. */
function vnavDescent(fms: ScriptedFms): (Line | undefined)[] {
  const profile = fms.profile();
  const first = profile.points[0];
  const tan = Math.tan((fms.vnav.pathAngle * Math.PI) / 180);
  const pathAltitude = first && first.distance !== null && first.altitude !== null ? Math.min(fms.vnav.cruiseAltitude, first.altitude + first.distance * 6076.12 * tan) : null;
  const vdev = profile.descending && pathAltitude !== null ? Math.round((fms.altitude - pathAltitude) / 10) * 10 : null;
  const edLeg = fms.activeRoute.legs.find(l => l.kind === "wpt" && l.ident === profile.endOfDescent);
  return [
    title("VNAV DES", "3/3", "ACT"),
    caption(" E/D", "PATH "),
    { left: medium(edLeg?.kind === "wpt" ? `${edLeg.ident} ${altitudeText(edLeg)}` : "-----"), right: { text: `${fixed(fms.vnav.pathAngle, 1)}°` } },
    caption(" VDEV", "TGT VS "),
    { left: medium(vdev === null ? "-----" : `${vdev >= 0 ? "+" : ""}${vdev}FT`), right: medium(profile.descending ? `-${Math.round((fms.groundSpeed * 101.27 * tan) / 10) * 10}FPM` : "-----") },
    caption(" TO T/D"),
    { left: medium(profile.topOfDescent === null ? (profile.descending ? "DESCENDING" : "-----") : `${fixed(profile.topOfDescent, 1)}NM`) },
    undefined, undefined, undefined, undefined,
    { left: dashes(24) },
    { left: !profile.descending && profile.endOfDescent ? prompt("<DES NOW") : back("INDEX") },
  ];
}

/** The fix of the GPS receiver the FMS navigates on, or null when it navigates on none. */
const gpsFix = (fms: ScriptedFms) => (fms.gpsStatus.chosen === null ? null : fms.gpsStatus.assessed[fms.gpsStatus.chosen].fix);

export const CORE_PAGES: Record<CorePageId, Page> = {
  MENU: {
    pages: () => 1,
    render: fms => [
      title("MCDU MENU", "1/1"),
      undefined,
      { left: prompt("<FMS"), right: small("ACT", "green") },
      undefined,
      { left: medium("<ACARS"), right: fms.hasCondition("menuRequest") ? { text: "REQ", color: "amber" } : undefined },
      undefined,
      { left: medium("<SATCOM") },
      undefined, undefined, undefined, undefined, undefined,
      simulated("SUBSYSTEMS SIMULATED"),
    ],
    lsk: (fms, side, row) => {
      if (side === "L" && row === 1) fms.open("INIT_REF");
      // Selecting the requesting subsystem acknowledges its request, which puts the MENU light out.
      if (side === "L" && row === 2) fms.setCondition("menuRequest", false);
    },
  },

  INIT_REF: {
    pages: () => 2,
    render: (_fms, index) => index === 0
      ? [
        title("INIT/REF INDEX", "1/2"),
        undefined,
        { left: prompt("<IDENT"), right: prompt("NAV DATA>") },
        undefined,
        { left: prompt("<POS"), right: prompt("PREDEF WPT>") },
        undefined,
        { left: prompt("<FUEL"), right: prompt("MSG RECALL>") },
        undefined,
        { left: prompt("<RTE"), right: prompt("RADIO>") },
        undefined,
        { left: prompt("<HOLD"), right: prompt("TIMER>") },
        undefined,
        // M300 reaches PLAN DATA from INIT/REF 2/2 at 4L, where the bench has FIX INFO; here it is the free 6R.
        { left: prompt("<MAINT"), right: prompt("PLAN DATA>") },
      ]
      : [
        title("INIT/REF INDEX", "2/2"),
        undefined,
        { left: prompt("<DES+SAR"), right: prompt("VNAV>") },
        undefined,
        { left: prompt("<TACT APPR"), right: prompt("ATC>") },
        undefined,
        { left: prompt("<HOVER"), right: prompt("FMC COMM>") },
        undefined,
        { left: prompt("<FIX INFO"), right: prompt("GSM/SMS>") },
        undefined,
        { left: prompt("<SEC FPLN"), right: prompt("NAV STATUS>") },
        undefined,
        { left: prompt("<MOVING WPT"), right: prompt("RNDZ>") },
      ],
    lsk: (fms, side, row, _scratch, index) => {
      const target: Record<string, PageId> = index === 0
        ? { L1: "IDENT", L2: "POS", L3: "FUEL", L4: "RTE", L5: "HOLD", R1: "NAV_DATA", R2: "PREDEF", R3: "MSG_RECALL", R4: "RADIO", R5: "TIMER", L6: "MAINT", R6: "PLAN_DATA" }
        : { L1: "TACT", L2: "TACT_APPR", L3: "HOVER", L4: "FIX", L5: "SEC_FPLN", R1: "VNAV", R2: "ATC", R3: "FMC_COMM", R4: "ANS", R5: "NAV_STATUS", L6: "MOVING_WPT", R6: "RNDZ" };
      const page = target[`${side}${row}`];
      if (page === "HOLD" && !fms.route.hold) { fms.open("LEGS"); fms.setScratch("/H"); return; }
      if (page) fms.open(page);
    },
  },

  IDENT: {
    pages: () => 1,
    render: fms => [
      title("IDENT", "1/1"),
      caption(" MODEL", "OP PROGRAM "),
      { left: medium("CMA-9000"), right: medium("AEROLINK SIM") },
      caption(" NAV DATA", "ACTIVE "),
      { left: medium(fms.activeCycle.id), right: medium(cycleDates(fms.activeCycle), fms.activeCycle.to !== null && fms.now.getTime() > fms.activeCycle.to ? "amber" : "white") },
      caption(undefined, fms.inactiveCycle ? "INACTIVE " : undefined),
      fms.inactiveCycle ? { left: small(fms.inactiveCycle.id), right: prompt(cycleDates(fms.inactiveCycle)) } : undefined,
      undefined,
      { center: small("SIMULATION - NOT FOR", "amber") },
      { center: small("NAVIGATION", "amber") },
      undefined,
      { left: dashes(24) },
      { left: back("INDEX"), right: prompt("POS INIT>") },
    ],
    lsk: (fms, side, row) => {
      if (row === 6) fms.open(side === "L" ? "INIT_REF" : "POS");
      if (side === "R" && row === 3 && fms.inactiveCycle) fms.swapCycles();
    },
  },

  POS: {
    pages: () => 1,
    render: fms => [
      title("POS INIT", "1/1"),
      caption(" FMS POS"),
      { left: medium(formatPosition(fms.position)) },
      caption(" GPS POS"),
      // The fix of the receiver navigated on, which in GPS mode is the FMS position (3a.3).
      { left: gpsFix(fms) ? medium(formatPosition(gpsFix(fms)!)) : dashes(15) },
      caption(" UTC", "SET POS "),
      // The crew's last SET POS entry, or boxes until there is one (R26).
      { left: medium(hhmm(fms.now)), right: fms.positionReferenceEntry ? medium(formatPosition(fms.positionReferenceEntry.position)) : boxes(15) },
      undefined, undefined, undefined, undefined,
      { left: dashes(24) },
      { left: back("INDEX"), right: prompt("RTE>") },
    ],
    lsk: (fms, side, row, scratch) => {
      if (row === 6) { fms.open(side === "L" ? "INIT_REF" : "RTE"); return; }
      // SET POS: a real position-reference initialisation, or a refusal that changes nothing (R26).
      if (side === "R" && row === 3) {
        const position = parsePosition(scratch);
        if (!position) return "invalid";
        fms.initializePosition(position);
        fms.setScratch("");
        return;
      }
      if (side === "L" && row === 1 && !scratch) fms.setScratch(formatPosition(fms.position));
      if (side === "L" && row === 2 && !scratch && gpsFix(fms)) fms.setScratch(formatPosition(gpsFix(fms)!));
    },
  },

  MSG_RECALL: {
    pages: fms => Math.max(1, Math.ceil(fms.recallList.length / 5)),
    render: (fms, index) => {
      const count = Math.max(1, Math.ceil(fms.recallList.length / 5));
      const lines: (Line | undefined)[] = [title("MESSAGE RECALL", `${index + 1}/${count}`)];
      const page = fms.recallList.slice(index * 5, index * 5 + 5);
      if (!page.length) lines[2] = { center: medium("NO MESSAGES") };
      page.forEach((message, i) => { lines[2 + i * 2] = { left: { text: message.text, color: message.alert ? "amber" : "white" } }; });
      return lines;
    },
  },

  LEGS: {
    pages: fms => Math.max(1, Math.ceil(fms.route.legs.length / 5)),
    render: (fms, index) => {
      const route = fms.route;
      const geometry = fms.legGeometry(route);
      const count = Math.max(1, Math.ceil(route.legs.length / 5));
      const lines: (Line | undefined)[] = [title("RTE 1 LEGS", `${index + 1}/${count}`, fms.routeStatus)];
      route.legs.slice(index * 5, index * 5 + 5).forEach((leg, i) => {
        const at = index * 5 + i;
        if (leg.kind === "disco") {
          lines[1 + i * 2] = { center: small("- ROUTE DISCONTINUITY -") };
          lines[2 + i * 2] = { left: boxes(5) };
          return;
        }
        const active = at === 0;
        if (leg.kind === "cond") {
          // A conditional leg: its course or heading, and the event that ends it.
          lines[1 + i * 2] = { left: small(` ${three(leg.course)}° ${leg.path[0] === "V" ? "HDG" : "CRS"}`) };
          lines[2 + i * 2] = { left: { text: conditionalLabel(leg), color: active ? "magenta" : "green", inverse: active } };
          return;
        }
        const leg3 = geometry[at];
        const marker = leg.qualifier === "/H" ? `HOLD ${route.hold?.turn === "LEFT" ? "L" : "R"}` : leg.qualifier === "/S" ? "SAR" : undefined;
        lines[1 + i * 2] = {
          left: small(leg3 ? ` ${three(leg3.course)}°` : " ---°"),
          center: small(leg3 ? `${fixed(leg3.distance, 1)}NM` : "--.-NM"),
          right: marker ? small(`${marker} `, "cyan") : undefined,
        };
        lines[2 + i * 2] = {
          left: { text: pad(leg.ident, 5), color: active ? "magenta" : "green", inverse: active },
          right: medium(altitudeText(leg)),
        };
      });
      lines[11] = { left: dashes(24) };
      lines[12] = fms.routeStatus === "MOD" ? { left: back("ERASE"), right: prompt("RTE DATA>") } : { right: prompt("RTE DATA>") };
      // A direct-to offers INTC CRS (fly a course into the fix instead) and ABEAM PTS (keep the bypassed points).
      if (fms.directModification && index === 0) {
        const first = route.legs[0];
        const course = first?.kind === "wpt" && first.path === "CF" && first.course !== undefined ? first.course : geometry[0]?.course;
        lines[11] = { left: dashes(13), right: small("INTC CRS ", "green") };
        lines[12] = { left: back("ERASE"), right: { text: course === undefined ? "---" : three(course), color: first?.kind === "wpt" && first.path === "CF" ? "white" : "cyan" } };
        if (fms.bypassedByDirect.length) lines[10] = { ...lines[10], right: prompt("ABEAM PTS>") };
      }
      return lines;
    },
    lsk: (fms, side, row, scratch, index): LskResult => {
      if (fms.directModification && index === 0 && side === "R") {
        if (row === 5 && fms.bypassedByDirect.length) { fms.abeamPoints(); return; }
        if (row === 6) {
          const course = scratch ? numberIn(scratch, 0, 360, /^\d{1,3}$/) : fms.legGeometry(fms.route)[0]?.course ?? null;
          if (course === null) return "invalid";
          fms.interceptCourse(course === 0 ? 360 : course);
          fms.setScratch("");
          return;
        }
      }
      if (row === 6) {
        if (side === "L" && fms.routeStatus === "MOD") fms.eraseModification();
        if (side === "R") fms.open("RTE", 1);
        return;
      }
      if (side === "R") {
        // Speed and altitude constraints: 180/5000A, 180/, /FL080, 5000B, 7000B5000A; DELETE removes both.
        const at = index * 5 + row - 1;
        const leg = fms.route.legs[at];
        if (leg?.kind !== "wpt") return scratch ? "not-allowed" : undefined;
        if (!scratch) { fms.setScratch(altitudeText(leg).replace(/^-----$/, "")); return; }
        if (scratch === "DELETE") {
          fms.modify(route => { const l = route.legs[at]; if (l?.kind === "wpt") { delete l.altitude; delete l.speed; } });
          fms.setScratch("");
          return;
        }
        const shape = /^(?:(\d{2,3})\/)?(\/?)(.*)$/.exec(scratch)!;
        const speed = shape[1] ? numberIn(shape[1], 60, 300) : undefined;
        const altitudeText_ = shape[3];
        const constraint = altitudeText_ ? parseConstraint(altitudeText_, true) : undefined;
        if (speed === null || constraint === null || (speed === undefined && constraint === undefined)) return "invalid";
        fms.modify(route => {
          const l = route.legs[at];
          if (l?.kind !== "wpt") return;
          if (speed !== undefined) l.speed = speed;
          if (constraint) l.altitude = formatConstraint(constraint).replace(/^FL(\d{3})/, (_, fl: string) => String(Number(fl) * 100));
        });
        fms.setScratch("");
        return;
      }
      const at = index * 5 + row - 1;
      const legs = fms.route.legs;
      const leg = legs[at];
      if (!scratch) { if (leg?.kind === "wpt") fms.setScratch(leg.ident); return; }
      if (scratch === "DELETE") {
        if (!leg || at === legs.length - 1) return "not-allowed";
        fms.modify(route => { route.legs.splice(at, 1); });
        fms.setScratch("");
        return;
      }
      if (scratch === "/H" || scratch.endsWith("/H")) {
        const fix = scratch === "/H" ? (leg?.kind === "wpt" ? leg.ident : undefined) : scratch.slice(0, -2);
        if (!fix || !WAYPOINT.test(fix)) return "invalid";
        const result = fms.defineHold(fix);
        if (result) return result;
        fms.setScratch("");
        fms.open("HOLD");
        return;
      }
      // Along-track: RDG/-5 is five miles before RDG on the route, RDG/5 five miles after it.
      const alongTrack = /^([A-Z0-9]{2,5})\/([+-]?\d{1,3}(?:\.\d)?)$/.exec(scratch);
      if (alongTrack) {
        const place = legs.findIndex(next => next.kind === "wpt" && next.ident === alongTrack[1]);
        const distance = Number(alongTrack[2]);
        const neighbour = legs[distance < 0 ? place - 1 : place + 1];
        const from = fms.coordinates(alongTrack[1], fms.route);
        const toward = neighbour?.kind === "wpt" ? fms.coordinates(neighbour.ident, fms.route) : place === 0 && distance < 0 ? fms.position : undefined;
        if (place < 0 || !from) return "not-in-database";
        if (!toward || distance === 0 || Math.abs(distance) >= distanceNm(from, toward)) return "invalid";
        const ident = fms.createPilot(alongTrack[1].slice(0, 3), offset(from, courseDeg(from, toward), Math.abs(distance)), scratch);
        if (fms.splitsHover(legs, distance < 0 ? place : place + 1)) { fms.advisory("!HOVER MRK WPT"); return; }
        fms.modify(route => { route.legs.splice(distance < 0 ? place : place + 1, 0, { kind: "wpt", ident }); });
        fms.setScratch("");
        return;
      }
      return fms.enterWaypoint(scratch, ident => {
        // Line 1 of the first page is the active waypoint: an entry there is a DIRECT-TO from present position.
        if (at === 0) {
          const result = fms.directTo(ident);
          if (!result) fms.setScratch("");
          return result;
        }
        // A waypoint further down the route closes the gap: the legs in between are deleted.
        const later = legs.findIndex((next, i) => i >= at && next.kind === "wpt" && next.ident === ident);
        if (later >= 0) {
          fms.modify(route => { route.legs.splice(at, later - at); });
          fms.setScratch("");
          return;
        }
        if (fms.splitsHover(legs, at)) { fms.advisory("!HOVER MRK WPT"); return; }
        fms.modify(route => { route.legs.splice(at, leg?.kind === "disco" ? 1 : 0, { kind: "wpt", ident }); });
        fms.setScratch("");
      });
    },
  },

  PROG: {
    pages: () => 4,
    render: (fms, index) => {
      const legs = fms.activeRoute.legs;
      const geometry = fms.legGeometry(fms.activeRoute);
      const [to, next] = legs;
      const toLeg = geometry[0], nextLeg = geometry[1];
      const now = fms.now.getTime();
      // No time without progress toward the active waypoint: dashes, never a time from an invented or wrong speed.
      const eta = (miles: number) => (makingProgress(fms.closureSpeed) ? hhmm(new Date(now + (miles / fms.closureSpeed) * 3_600_000)) : "----.-");
      const ident = (leg: Leg | undefined) => (leg?.kind === "wpt" ? leg.ident : leg?.kind === "cond" ? conditionalLabel(leg) : "-----");
      const nav = fms.navState;
      if (index === 0) {
        // RNP and ANP as every page, lamp and alert reads them; a bench-forced value is labelled TEST (R11).
        const { rnp, anp, forced } = fms.navPerformance;
        const toDistance = toLeg?.distance ?? 0;
        return [
          title("PROGRESS", "1/4", "ACT"),
          { left: small(` ${toLeg ? three(toLeg.course) : "---"}°`), center: small("DTG", "green"), right: small("ETA ", "green") },
          { left: { text: pad(ident(to), 5), color: "magenta", inverse: true }, right: medium(toLeg ? `${fixed(toDistance, 1)}NM ${eta(toDistance)}` : "") },
          { left: small(` ${nextLeg ? three(nextLeg.course) : "---"}°`) },
          { left: { text: pad(ident(next), 5), color: "green" }, right: medium(nextLeg ? `${fixed(toDistance + nextLeg.distance, 1)}NM ${eta(toDistance + nextLeg.distance)}` : "") },
          caption("TRUE WIND", "TK/GS "),
          { left: medium(` ${three(fms.wind.direction)}°/ ${fms.wind.speed}KT`), right: medium(`${three(fms.track)}°/${Math.round(fms.groundSpeed)}KT`) },
          caption(undefined, "TKE/XTK "),
          { right: medium(`${fms.trackError < 0 ? "L" : "R"}${three(Math.abs(fms.trackError))}°/${fms.crossTrack < 0 ? "L" : "R"}${fixed(Math.abs(fms.crossTrack), 2)}NM`) },
          caption(`RNP/ANP ${forced ? "TEST" : nav.rnpManual === null ? fms.flightPhase : "MANUAL"}`),
          { left: medium(`${fixed(rnp, 2)}/${fixed(anp, 2)}NM`, anp > rnp ? "amber" : "white") },
          caption("NAV MODE"),
          { left: { text: navModeText(fms), color: nav.mode === "DR" ? "amber" : "cyan" }, right: prompt("NAV STATUS>") },
        ];
      }
      if (index === 1)
        return [
          title("PROGRESS", "2/4", "ACT"),
          caption(" FUEL QTY", "FUEL FLOW "),
          { left: medium(`${fms.fuelState.quantity}KG`), right: medium(`${fms.fuelState.flow}KG/H`) },
          // The endpoint of the predictions (the MAP, or over the landing site), its EFOB and its basis (plan C.11, R3-03).
          caption(` ${basisText(fms).kind}`, "EFOB "),
          { left: { text: fms.profile().endpoint?.label ?? fms.activeRoute.dest, color: "green" }, right: medium(efobText(fms)) },
          caption(" BASIS"),
          { left: medium(basisText(fms).status), right: basisText(fms).reason ? small(basisText(fms).reason!) : undefined },
        ];
      if (index === 2) {
        // The receiver navigated on (or the one chosen, or GPS1) as its bus reports it: mode, satellites used, HIL (3a.6).
        const { name, assessment } = shownReceiver(fms.gpsStatus, fms.gpsReceiverChoice);
        const summary = fms.gpsNavSelected ? gpsSummary(assessment) : { text: "DESELECTED", ok: false };
        const hil = fms.gpsNavSelected && assessment?.fix ? assessment.hil : null;
        const intact = fms.gpsNavSelected && assessment?.usable === true && assessment.integrity === "OK";
        return [
          title("PROGRESS", "3/4", "ACT"),
          caption(` ${name}`, "HIL "),
          {
            left: assessment?.fix && assessment.mode && fms.gpsNavSelected ? medium(`${MODE_TEXT[assessment.mode]} ${assessment.used} SAT`) : medium(summary.text, "amber"),
            right: hil === null ? dashes(6) : medium(`${fixed(hil, 2)}NM`, hil > HAL_NM[fms.flightPhase] ? "amber" : "white"),
          },
          caption(" SBAS", "INTEGRITY "),
          { left: medium(fms.gpsNavSelected ? sbasSummary(assessment) : "----"), right: intact ? medium("OK", "green") : medium("LOST", "amber") },
        ];
      }
      const offset = fms.lateralOffset;
      return [
        title("PROGRESS", "4/4", fms.routeStatus),
        caption(" OFFSET"),
        { left: offset ? { text: `${offset.nm < 0 ? "L" : "R"}${fixed(Math.abs(offset.nm), 1)}NM` } : dashes(5) },
        caption(" START WPT", "END WPT "),
        offset ? { left: offset.start ? { text: offset.start } : dashes(5), right: offset.end ? { text: offset.end } : dashes(5) } : undefined,
        caption(" ALT", "VS "),
        { left: medium(`${Math.round(fms.altitude)}FT`), right: medium(`${fms.verticalSpeed >= 0 ? "+" : ""}${Math.round(fms.verticalSpeed / 10) * 10}FPM`) },
      ];
    },
    lsk: (fms, side, row, scratch, index) => {
      if (index === 0) {
        if (side === "R" && row === 6) { fms.open("NAV_STATUS"); return; }
        if (side !== "L" || row !== 5 || !scratch) return;
        // RNP: a manual value (0.01 to 30 NM) replaces the phase default until it is deleted.
        if (scratch === "DELETE") { fms.setRnp(null); fms.setScratch(""); return; }
        const rnp = numberIn(scratch, 0.01, 30, /^\d{0,2}\.?\d{1,2}$/);
        if (rnp === null) return "invalid";
        fms.setScratch("");
        fms.setRnp(rnp);
        return;
      }
      if (index !== 3 || !scratch) return;
      if (side === "L" && row === 1) {
        if (scratch === "DELETE") { fms.setOffset(null); fms.setScratch(""); return; }
        // An offset is L or R, before or after the distance, and 0.1 to 20.0 NM: L2.0, 2L, R10.
        const before = /^([LR])(\d{1,2}(?:\.\d)?)$/.exec(scratch), after = /^(\d{1,2}(?:\.\d)?)([LR])$/.exec(scratch);
        const direction = before?.[1] ?? after?.[2];
        const nm = numberIn(before?.[2] ?? after?.[1] ?? "", 0.1, 20);
        if (!direction || nm === null) return "invalid";
        fms.setOffset({ nm: direction === "L" ? -nm : nm });
        fms.setScratch("");
        return;
      }
      // START and END WPT: route waypoints the offset begins after and ends at.
      if (row === 2 && fms.lateralOffset) {
        const key = side === "L" ? "start" : "end";
        if (scratch === "DELETE") { fms.setOffset({ [key]: undefined }); fms.setScratch(""); return; }
        if (!fms.route.legs.some(leg => leg.kind === "wpt" && leg.ident === scratch)) return "invalid";
        fms.setOffset({ [key]: scratch });
        fms.setScratch("");
      }
    },
  },

  RADIO: {
    pages: () => 2,
    render: (fms, index) => {
      const r = fms.radioState;
      const tx = (on: boolean) => (on ? [small(" TX", "green")] : []);
      if (index === 0)
        return [
          title("RADIO", "1/2"),
          { left: [small(" COM1", "green"), ...tx(fms.hasCondition("tx1"))], right: small("STBY ", "green") },
          { left: { text: r.com1, color: "green" }, right: { text: r.com1Stby } },
          { left: [small(" COM2", "green"), ...tx(fms.hasCondition("tx2"))], right: small("STBY ", "green") },
          { left: { text: r.com2, color: "green" }, right: { text: r.com2Stby } },
          caption(" NAV1", "NAV2 "),
          { left: { text: r.nav1 }, right: { text: r.nav2 } },
        ];
      return [
        title("RADIO", "2/2"),
        caption(" ADF"),
        { left: { text: r.adf } },
        caption(" TPDR", "MODE "),
        { left: { text: r.tpdr, color: fms.squawkIdent ? "green" : "white", inverse: fms.squawkIdent }, right: { text: "ALT", color: "green" } },
      ];
    },
    lsk: (fms, side, row, scratch, index) => {
      const com = /^1[1-3]\d\.\d{2,3}$/, nav = /^1[01]\d\.\d{1,2}$/;
      const inCom = (v: string) => Number(v) >= 118 && Number(v) < 137, inNav = (v: string) => Number(v) >= 108 && Number(v) < 118;
      const fields: Record<string, [keyof ScriptedFms["radioState"], RegExp, (v: string) => boolean]> = {
        "0L1": ["com1", com, inCom], "0R1": ["com1Stby", com, inCom], "0L2": ["com2", com, inCom], "0R2": ["com2Stby", com, inCom],
        "0L3": ["nav1", nav, inNav], "0R3": ["nav2", nav, inNav],
        "1L1": ["adf", /^\d{3,4}(\.\d)?$/, v => Number(v) >= 190 && Number(v) <= 1750],
        "1L2": ["tpdr", /^[0-7]{4}$/, () => true],
      };
      const field = fields[`${index}${side}${row}`];
      if (!field) return;
      const [key, shape, range] = field;
      if (!scratch) {
        // Pressing the standby field with an empty scratchpad swaps active and standby, as a transfer key would.
        if (key === "com1Stby" || key === "com2Stby") {
          const activeKey = key === "com1Stby" ? "com1" : "com2";
          const standby = fms.radioState[key];
          fms.setRadio(key, fms.radioState[activeKey]);
          fms.setRadio(activeKey, standby);
        } else fms.setScratch(fms.radioState[key]);
        return;
      }
      if (!shape.test(scratch) || !range(scratch)) return "invalid";
      const text = key.startsWith("com") ? Number(scratch).toFixed(3) : key.startsWith("nav") ? Number(scratch).toFixed(2) : scratch;
      fms.setRadio(key, text);
      fms.setScratch("");
    },
  },

  // PLAN DATA (M300 3-19): TRANS ALT and TRANS LVL, CRZ WIND and CRZ TAS (130 kt for ROTOR). Planning data only.
  PLAN_DATA: {
    pages: () => 1,
    render: fms => {
      const plan = fms.planData;
      return [
        title("PLAN DATA", "1/1"),
        caption(" TRANS ALT", "CRZ WIND "),
        { left: medium(`${plan.transAlt}FT`), right: medium(`${three(plan.cruiseWind.direction)}T/${String(plan.cruiseWind.speed).padStart(3)}KT`) },
        caption(" TRANS LVL", "CRZ TAS "),
        { left: medium(`FL${String(plan.transLevel).padStart(3, "0")}`), right: medium(`${plan.cruiseTas}KT`) },
        undefined, undefined, undefined, undefined, undefined, undefined,
        { left: dashes(24) },
        { left: prompt("<INIT/REF") },
      ];
    },
    lsk: (fms, side, row, scratch) => {
      const plan = fms.planData;
      if (side === "L" && row === 6) { fms.open("INIT_REF"); return; }
      if (!scratch) return;
      if (side === "L" && row === 1) {
        // Feet (18000), or a flight level (180) that is multiplied by 100.
        const value = /^\d{3}$/.test(scratch) ? Number(scratch) * 100 : /^\d{4,5}$/.test(scratch) ? Number(scratch) : NaN;
        if (!(value >= 1000 && value <= 60000)) return "invalid";
        plan.transAlt = value;
      } else if (side === "L" && row === 2) {
        const match = /^(?:FL)?(\d{2,3})$/.exec(scratch);
        if (!match || Number(match[1]) < 10 || Number(match[1]) > 600) return "invalid";
        plan.transLevel = Number(match[1]);
      } else if (side === "R" && row === 1) {
        const match = /^(\d{3})\/(\d{1,3})$/.exec(scratch);
        if (!match || Number(match[1]) > 360 || Number(match[2]) > 250) return "invalid";
        plan.cruiseWind = { direction: Number(match[1]) % 360, speed: Number(match[2]) };
      } else if (side === "R" && row === 2) {
        if (!/^\d{2,3}$/.test(scratch) || Number(scratch) < 40 || Number(scratch) > 400) return "invalid";
        plan.cruiseTas = Number(scratch);
      } else return;
      fms.setScratch("");
    },
  },

  FUEL: {
    pages: () => 1,
    render: fms => [
      title("FUEL", "1/1"),
      caption(" FUEL QTY", "FLOW "),
      { left: { text: `${Math.round(fms.fuelState.quantity)}KG` }, right: medium(`${fms.fuelState.flow}KG/H`) },
      caption(" RESERVE", "ENDURANCE "),
      { left: { text: `${fms.fuelState.reserve}KG` }, right: medium(`${fixed(Math.max(0, fms.fuelState.quantity - fms.fuelState.reserve) / fms.fuelState.flow, 1)}H`) },
      ...destinationPrediction(fms),
    ],
    lsk: (fms, side, row, scratch) => {
      const key = side === "L" ? (row === 1 ? "quantity" : row === 2 ? "reserve" : null) : row === 1 ? "flow" : null;
      if (!key) return;
      if (!scratch) { fms.setScratch(String(fms.fuelState[key])); return; }
      if (!/^\d{1,5}$/.test(scratch) || Number(scratch) <= 0) return "invalid";
      fms.setFuel(key, Number(scratch));
      fms.setScratch("");
    },
  },

  HOLD: {
    pages: () => 1,
    render: fms => {
      const hold = fms.route.hold;
      const status = fms.routeStatus;
      const footer: Line = status === "MOD" ? { left: back("ERASE"), right: prompt("LEGS>") } : { left: back("NEW HOLD"), right: prompt("LEGS>") };
      if (!hold)
        return [title("HOLD", "1/1", status), undefined, { center: medium("NO HOLD IN ROUTE") }, undefined, undefined, undefined, undefined,
          undefined, undefined, undefined, undefined, { left: dashes(24) }, footer];
      const at = fms.route.legs.findIndex(leg => leg.kind === "wpt" && leg.ident === hold.fix);
      const toFix = fms.legGeometry(fms.route).slice(0, at + 1).reduce((sum, leg) => sum + (leg?.distance ?? 0), 0);
      const eta = makingProgress(fms.closureSpeed) ? hhmm(new Date(fms.now.getTime() + (toFix / fms.closureSpeed) * 3_600_000)) : "----.-";
      const entry = fms.holdEntryFor(fms.route);
      const exitPrompt = hold.status === "IN PROGRESS" ? prompt("EXIT HOLD>") : hold.status === "EXIT ARMED" ? prompt("RESUME HOLD>") : undefined;
      return [
        title("HOLD", "1/1", status),
        caption(" FIX", "SPD/TGT ALT "),
        { left: { text: hold.fix, color: "green" }, right: { text: `${hold.speed}/${hold.altitude.padStart(6)}` } },
        caption(" TURN DIR", "FIX ETA "),
        { left: { text: `>${hold.turn}`, color: "cyan" }, right: medium(at >= 0 ? eta : "-----") },
        caption(" INBD CRS", "STATUS "),
        { left: { text: `${three(hold.inbound)}°` }, right: medium(hold.status, hold.status === "INACTIVE" ? "white" : "green") },
        caption(" LEG TIME/DIS", "ENTRY "),
        { left: { text: `${hold.legTime === null ? "-.-" : fixed(hold.legTime, 1)}MIN/${hold.legDistance === null ? "--.-" : fixed(hold.legDistance, 1)}NM` }, right: medium(entry ?? "-----") },
        caption(" EXIT TYPE"),
        { left: { text: `>${hold.exit}`, color: "cyan" }, right: exitPrompt },
        { left: dashes(24) },
        footer,
      ];
    },
    lsk: (fms, side, row, scratch) => {
      const hold = fms.route.hold;
      if (row === 6) {
        if (side === "R") fms.open("LEGS");
        else if (fms.routeStatus === "MOD") fms.eraseModification();
        else { fms.open("LEGS"); fms.setScratch("/H"); }
        return;
      }
      if (!hold) return;
      const done = () => { fms.setScratch(""); };
      if (side === "L") {
        switch (row) {
          case 1: {
            if (!scratch) { fms.setScratch(hold.fix); return; }
            if (!WAYPOINT.test(scratch)) return "invalid";
            const result = fms.defineHold(scratch);
            if (!result) done();
            return result;
          }
          case 2: {
            // TURN DIR alternates on each press, or takes L or R from the scratchpad.
            const turn = scratch === "L" ? "LEFT" : scratch === "R" ? "RIGHT" : !scratch ? (hold.turn === "RIGHT" ? "LEFT" : "RIGHT") : null;
            if (!turn) return "invalid";
            fms.changeHold(h => { h.turn = turn; });
            return done();
          }
          case 3: {
            const course = numberIn(scratch, 1, 360, /^\d{1,3}$/);
            if (course === null) return "invalid";
            fms.changeHold(h => { h.inbound = course; });
            return done();
          }
          case 4: {
            const time = /^\d(\.\d)?$/.test(scratch) ? numberIn(scratch, 0.5, 9.9) : null;
            const dist = /^\/\d{1,2}(\.\d)?$/.test(scratch) ? numberIn(scratch.slice(1), 0.5, 30) : null;
            if (time === null && dist === null) return "invalid";
            fms.changeHold(h => { h.legTime = time; h.legDistance = dist; });
            return done();
          }
          case 5:
            // EXIT TYPE cycles MANUAL, ONCE, AT TGT ALT (M300 10-10).
            fms.changeHold(h => { h.exit = h.exit === "MANUAL" ? "ONCE" : h.exit === "ONCE" ? "AT TGT ALT" : "MANUAL"; });
            return;
        }
        return;
      }
      if (row === 1) {
        const shape = /^(\d{3})?(?:\/(\d{3,5}[AB]?))?$/.exec(scratch);
        // From 40 kt: helicopter holds are charted as slow as 70 to 90 KIAS (87N BEADS, 90).
        const speed = shape?.[1] ? numberIn(shape[1], 40, 300) : undefined;
        if (!scratch || !shape || (!shape[1] && !shape[2]) || speed === null) return "invalid";
        fms.changeHold(h => { if (speed !== undefined) h.speed = speed; if (shape[2]) h.altitude = shape[2]; });
        return done();
      }
      if (row === 5) {
        if (hold.status === "IN PROGRESS") fms.changeHold(h => { h.status = "EXIT ARMED"; });
        // RESUME HOLD converts any exit to MANUAL: held until the crew exits it again (missed-approach holds too).
        else if (hold.status === "EXIT ARMED") fms.changeHold(h => { h.status = "IN PROGRESS"; h.exit = "MANUAL"; h.missed = false; });
      }
    },
  },

  FIX: {
    pages: () => 1,
    render: fms => [
      title("FIX INFO", "1/1"),
      caption(" REF", "RAD/DIS "),
      { left: boxes(5), right: medium("---°/---NM") },
      caption(" PRESENT POS"),
      { left: medium(formatPosition(fms.position)) },
    ],
    lsk: (fms, side, row, scratch) => {
      if (side === "L" && row === 1 && scratch) return WAYPOINT.test(scratch) ? void fms.setScratch("") : "invalid";
    },
  },

  PREDEF: {
    pages: () => 2,
    render: (fms, index) => {
      const lines: (Line | undefined)[] = [title("PREDEF WPT", `${index + 1}/2`)];
      if (index === 0) {
        lines[1] = caption(" PILOT WPT", "DEFINED AS ");
        fms.pilotWaypoints.slice(-5).forEach((wpt, i) => {
          lines[2 + i * 2] = { left: { text: wpt.ident, color: "green" }, right: medium(wpt.definition.slice(0, 18)) };
        });
        if (!fms.pilotWaypoints.length) lines[2] = { left: medium("NO PILOT WAYPOINTS") };
        return lines;
      }
      lines[1] = caption(" MARK ON TOP");
      fms.markList.slice(-5).forEach((mark, i) => {
        lines[2 + i * 2] = { left: { text: mark.ident, color: "green" }, right: medium(formatPosition(mark.position)) };
      });
      if (!fms.markList.length) lines[2] = { left: medium("NONE") };
      // M300 11-31: NEW USER WPT takes the most recent mark on top to USER WPT 1/2 as its reference.
      lines[12] = { left: prompt("<NEW USER WPT") };
      return lines;
    },
    lsk: (fms, side, row, scratch, index) => {
      if (index === 1 && side === "L" && row === 6) {
        const mark = fms.markList.at(-1);
        fms.userWaypointDraft = { ident: null, position: mark ? { ...mark.position } : null, ref: mark ? { ident: "ONTOP", position: { ...mark.position } } : null };
        fms.open("USER_WPT");
        return;
      }
      const wpt = index === 1 ? fms.markList.slice(-5)[row - 1] : fms.pilotWaypoints.slice(-5)[row - 1];
      if (side === "L" && wpt && !scratch) fms.setScratch(wpt.ident);
    },
  },

  // The user database (E5; M300 11-23…11-31): USER WPT 1/2 enters a fixed user waypoint (an ident and a position, or
  // the reference a mark on top gave), SAVE? CONFIRM stores it for this user and profile; 2/2 lists what is stored.
  USER_WPT: {
    pages: () => 2,
    render: (fms, index) => {
      const lines: (Line | undefined)[] = [title("USER WPT", `${index + 1}/2`)];
      if (index === 1) {
        lines[1] = caption(" ID", "TYPE ", `FREE=${fms.userWaypointsFree}`);
        fms.userWaypoints.slice(-5).forEach((wpt, i) => { lines[2 + i * 2] = { left: { text: wpt.ident, color: "green" }, right: medium("WAYPOINT") }; });
        if (!fms.userWaypoints.length) lines[2] = { left: medium("NO USER WAYPOINTS") };
        lines[12] = { left: prompt("<WPT DATA") };
        return lines;
      }
      const draft = fms.userWaypointDraft ?? { ident: null, position: null, ref: null };
      lines[1] = caption(" ID/POS", `FREE=${fms.userWaypointsFree} `);
      lines[2] = { left: draft.ident ? { text: draft.ident } : boxes(5), right: draft.position ? medium(formatPosition(draft.position)) : boxes(15) };
      lines[5] = caption(" TYPE");
      lines[6] = { left: medium(">FIXED") };
      lines[7] = caption(" REF WPT ID");
      lines[8] = { left: medium(draft.ref ? draft.ref.ident : "-----") };
      lines[9] = caption(" REF WPT POS");
      lines[10] = { left: medium(draft.ref ? formatPosition(draft.ref.position) : "---°--.-- ----°--.--") };
      const complete = draft.ident !== null && draft.position !== null;
      lines[11] = complete ? caption(undefined, "SAVE? ") : undefined;
      lines[12] = complete ? { left: prompt("<CANCEL"), right: prompt("CONFIRM>") } : { left: prompt("<WPT DATA"), right: prompt("WPT LIST>") };
      return lines;
    },
    lsk: (fms, side, row, scratch, index) => {
      if (index === 1) { if (side === "L" && row === 6) fms.open("NAV_DATA"); return; }
      const draft = fms.userWaypointDraft ?? (fms.userWaypointDraft = { ident: null, position: null, ref: null });
      const complete = draft.ident !== null && draft.position !== null;
      if (row === 1 && side === "L") {
        if (!scratch) return;
        if (!/^[A-Z0-9]{1,5}$/.test(scratch)) return "invalid";
        draft.ident = scratch;
        fms.setScratch("");
        return;
      }
      if (row === 1 && side === "R") {
        if (!scratch) return;
        const at = parsePosition(scratch);
        if (!at) return "invalid";
        draft.position = at;
        fms.setScratch("");
        return;
      }
      if (row === 6 && complete) {
        if (side === "L") { fms.userWaypointDraft = null; return; }
        const refused = fms.createUserWaypoint(draft.ident!, draft.position!);
        if (refused === "invalid") return "invalid";
        if (refused === "in-use") { fms.advisory("DUPLICATE IDENT"); return; }
        if (refused === "full") { fms.advisory("USER DB FULL"); return; }
        if (refused === "not-saved") return;
        fms.advisory(`${draft.ident} STORED`);
        fms.userWaypointDraft = null;
        return;
      }
      if (row === 6) { if (side === "L") fms.open("NAV_DATA"); else fms.open("USER_WPT", 1); }
    },
  },

  VNAV: {
    pages: () => 3,
    render: (fms, index) => {
      if (index === 1) return vnavCruise(fms);
      if (index === 2) return vnavDescent(fms);
      const path = approach(fms);
      // An executed approach that does not end at a runway (a point-in-space approach, LNAV only) has no vertical path
      // to show: it says so, with where it ends ("CRANN (MAP)"), never that there is no approach.
      const endpoint = fms.approachType !== null ? fms.profile().endpoint : null;
      if (!path && endpoint)
        return [title("VNAV", "1/3"), undefined, { center: medium("NO VERTICAL PATH (LNAV)") }, undefined, { center: medium(`TO ${endpoint.label}`) },
          undefined, undefined, undefined, undefined, undefined, undefined, undefined, { left: back("INDEX") }];
      if (!path)
        return [title("VNAV", "1/3"), undefined, { center: medium("NO APPROACH IN ROUTE") }, undefined, undefined, undefined, undefined,
          undefined, undefined, undefined, undefined, undefined, { left: back("INDEX") }];
      const geometry = fms.legGeometry(fms.activeRoute);
      const legs = fms.activeRoute.legs;
      const ident = (i: number) => { const leg = legs[i]; return leg?.kind === "wpt" ? leg.ident : leg?.kind === "cond" ? conditionalLabel(leg) : "-----"; };
      const crsDist = (i: number) => { const leg = geometry[i]; return leg ? `${three(leg.course)}°/${fixed(leg.distance, 1).padStart(5)}NM` : "---°/--.-NM"; };
      const tan = Math.tan((path.vpa * Math.PI) / 180);
      // Vertical deviation is shown once the aircraft is on the final approach: FAF or runway active.
      const toThreshold = distanceNm(fms.position, path.runwayPos);
      const onFinal = ident(0) === path.faf || ident(0) === path.runway;
      const pathAltitude = fms.vnav.runwayElevation + Math.min(toThreshold * 6076.12 * tan, fms.vnav.fafAltitude - fms.vnav.runwayElevation);
      const vdev = Math.round((fms.altitude - pathAltitude) / 10) * 10;
      const targetVs = Math.round((fms.groundSpeed * 101.27 * tan) / 10) * 10;
      const outside = path.vpa < GLIDEPATH_LIMITS.low || path.vpa > GLIDEPATH_LIMITS.high;
      return [
        // The runway without its RW and LNAV/VNAV as L/VNAV, so the longest title (NO APPR) fits beside 1/3 (R19, 3b).
        title(`VNAV ${path.runway.replace(/^RW/, "")} ${fms.approachType === "LNAV/VNAV" ? "L/VNAV" : fms.approachType ?? ""}`.trim(), "1/3", "ACT"),
        caption(" MDA-DA", fms.coldCorrection ? "FAF ALT TEMP COMP " : "FAF ALT "),
        { left: { text: `${fms.vnav.mda}FT` }, right: { text: `${path.faf} ${fms.fafAltitudeCorrected}A`, color: fms.coldCorrection ? "cyan" : "white" } },
        caption(" ACT WPT", "CRS/DIST "),
        { left: { text: pad(ident(0), 5), color: "magenta" }, right: medium(crsDist(0)) },
        caption(" NEXT WPT", "CRS/DIST "),
        { left: { text: pad(ident(1), 5), color: "green" }, right: medium(crsDist(1)) },
        caption(" DEST TEMP", "QNH "),
        {
          left: fms.vnav.destTemp === null ? boxes(3) : { text: `${fms.vnav.destTemp >= 0 ? "+" : ""}${fms.vnav.destTemp}°C` },
          right: fms.vnav.qnh === null ? boxes(4) : { text: fms.vnav.qnh },
        },
        caption(" WIND/GS", "VPA "),
        { left: medium(`${three(fms.wind.direction)}°/${fms.wind.speed}KT ${Math.round(fms.groundSpeed)}KT`), right: medium(`-${fixed(path.vpa, 2)}°`, outside ? "amber" : "white") },
        caption(" VDEV", "TGT VS "),
        { left: medium(onFinal ? `${vdev >= 0 ? "+" : ""}${vdev}FT` : "-----"), right: medium(`-${targetVs}FPM`) },
      ];
    },
    lsk: (fms, side, row, scratch, index) => {
      if (index === 1) return vnavCruiseLsk(fms, side, row, scratch);
      if (index === 2) {
        if (side === "L" && row === 6 && !fms.profile().descending) { fms.vnav.desNow = true; fms.advisory("DES NOW"); }
        return;
      }
      if (!approach(fms) || !scratch) return;
      if (side === "L" && row === 1) {
        const mda = numberIn(scratch, 0, 20000, /^\d{1,5}$/);
        if (mda === null) return "invalid";
        fms.vnav.mda = mda;
        return void fms.setScratch("");
      }
      if (side === "R" && row === 1) {
        const altitude = numberIn(scratch.replace(/A$/, ""), fms.vnav.runwayElevation + 200, 18000, /^\d{3,5}$/);
        if (altitude === null) return "invalid";
        fms.setFafAltitude(altitude);
        fms.setScratch("");
        const vpa = verticalPathAngle(fms);
        if (vpa !== null && vpa < GLIDEPATH_LIMITS.low) fms.alert(alert("LOW GLIDEPATH ANGLE"));
        if (vpa !== null && vpa > GLIDEPATH_LIMITS.high) fms.alert(alert("HIGH GLIDEPATH ANGLE"));
        return;
      }
      if (side === "L" && row === 4) {
        const temp = numberIn(scratch, -60, 60, /^[+-]?\d{1,2}$/);
        if (temp === null) return "invalid";
        fms.vnav.destTemp = temp;
        return void fms.setScratch("");
      }
      if (side === "R" && row === 4) {
        const hpa = numberIn(scratch, 945, 1050, /^\d{3,4}$/), inHg = numberIn(scratch, 28, 31, /^\d{2}\.\d{2}$/);
        if (hpa === null && inHg === null) return "invalid";
        fms.vnav.qnh = scratch;
        return void fms.setScratch("");
      }
    },
  },

  MAINT: {
    pages: () => 1,
    render: fms => {
      const test = fms.selfTestState;
      const lines: (Line | undefined)[] = [
        title("MAINTENANCE", "1/1"),
        caption(" OP PROGRAM", "NAV DATA "),
        { left: medium("AEROLINK SIM 1"), right: medium(fms.activeCycle.id) },
        caption(" SELF TEST", "RESULT "),
        { left: prompt("<START"), right: medium(test.result ?? (test.startedAt === null ? "-----" : "IN PROG"), test.result === "FAIL" ? "amber" : test.result === "PASS" ? "green" : "white") },
        caption(" CROSS-SIDE"),
        { left: medium(fms.hasCondition("independent") ? "INDEPENDENT" : "DUAL SYNC", fms.hasCondition("independent") ? "amber" : "green"), right: medium(fms.crossSideInSync ? "RTE MATCH" : "RTE DIFFER", fms.crossSideInSync ? "white" : "amber") },
        caption(" FAULT LOG"),
      ];
      fms.faultLog.slice(0, 3).forEach((fault, i) => { lines[8 + i] = { left: small(`${hhmm(fault.at).slice(0, 4)}Z ${fault.text}`) }; });
      if (!fms.faultLog.length) lines[8] = { left: small("NO FAULTS") };
      lines[11] = { left: dashes(24) };
      lines[12] = { left: back("INDEX") };
      return lines;
    },
    lsk: (fms, side, row) => {
      if (side === "L" && row === 2) fms.startSelfTest();
      if (side === "L" && row === 6) fms.open("INIT_REF");
    },
  },

  TIMER: {
    pages: () => 1,
    render: fms => {
      const now = fms.now.getTime();
      const { alarmAt, countdownEnd } = fms.timer;
      const remaining = countdownEnd === null ? null : Math.max(0, Math.ceil((countdownEnd - now) / 1000));
      const utc = (ms: number) => { const d = new Date(ms); return `${String(d.getUTCHours()).padStart(2, "0")}${String(d.getUTCMinutes()).padStart(2, "0")}`; };
      const seconds = String(fms.now.getUTCSeconds()).padStart(2, "0");
      return [
        title("TIMER", "1/1"),
        caption(" ALARM TIME", "COUNTDOWN "),
        {
          left: alarmAt === null ? { text: "----Z" } : { text: `${utc(alarmAt)}Z`, color: "green" },
          right: remaining === null ? { text: "--:--" } : { text: `${String(Math.floor(remaining / 60)).padStart(2, "0")}:${String(remaining % 60).padStart(2, "0")}`, color: "green" },
        },
        caption(" UTC"),
        { left: medium(`${utc(now)}:${seconds}Z`) },
        undefined,
        { left: prompt("<ADD 5 MIN") },
        undefined,
        { left: prompt("<ADD 15 MIN") },
        undefined,
        { left: prompt("<ADD 30 MIN") },
        { left: dashes(24) },
        { left: back("CLEAR"), right: prompt("INDEX>") },
      ];
    },
    lsk: (fms, side, row, scratch) => {
      const now = fms.now.getTime();
      if (row === 6) {
        if (side === "R") { fms.open("INIT_REF"); return; }
        fms.timer.alarmAt = null;
        fms.timer.countdownEnd = null;
        return;
      }
      if (side === "L" && row === 1) {
        const shape = /^([01]\d|2[0-3])([0-5]\d)Z?$/.exec(scratch);
        if (!shape) return "invalid";
        const at = new Date(now);
        at.setUTCHours(Number(shape[1]), Number(shape[2]), 0, 0);
        // An alarm time already past today is tomorrow's.
        fms.timer.alarmAt = at.getTime() <= now ? at.getTime() + 86_400_000 : at.getTime();
        return void fms.setScratch("");
      }
      if (side === "R" && row === 1) {
        const minutes = numberIn(scratch, 1, 999, /^\d{1,3}$/);
        if (minutes === null) return "invalid";
        fms.timer.countdownEnd = now + minutes * 60_000;
        return void fms.setScratch("");
      }
      const add = side === "L" ? ({ 3: 5, 4: 15, 5: 30 } as Record<number, number>)[row] : undefined;
      if (add) fms.timer.countdownEnd = Math.max(fms.timer.countdownEnd ?? now, now) + add * 60_000;
    },
  },
};

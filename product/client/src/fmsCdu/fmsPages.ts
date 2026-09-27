import { alert } from "./alerts";
import {
  ICAO, WAYPOINT, boxes, caption, dashes, distanceNm, fixed, formatPosition, hhmm, medium, numberIn, pad, prompt, simulated,
  small, three, title, type CorePageId, type Leg, type LskResult, type Page, type PageId,
} from "./fmsModel";
import type { Line } from "./screen";
import type { ScriptedFms } from "./scriptedFms";

/** The core CMA-9000 pages: index, identification, route, legs, progress, radio, fuel, hold, VNAV and timer. */

const back = (target: string): Line["left"] => prompt(`<${target}`);

/** The final approach fix is the leg before the runway; the vertical path runs from it to the threshold. */
function approach(fms: ScriptedFms) {
  const legs = fms.route.legs;
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

const altitudeText = (leg: Leg) => (leg.kind === "wpt" ? leg.altitude ?? "-----" : "");

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
      ],
    lsk: (fms, side, row, _scratch, index) => {
      const target: Record<string, PageId> = index === 0
        ? { L1: "IDENT", L2: "POS", L3: "FUEL", L4: "RTE", L5: "HOLD", R1: "IDENT", R2: "PREDEF", R3: "MSG_RECALL", R4: "RADIO", R5: "TIMER" }
        : { L1: "TACT", L2: "TACT_APPR", L3: "HOVER", L4: "FIX", R1: "VNAV", R2: "ATC", R3: "FMC_COMM", R4: "ANS" };
      const page = target[`${side}${row}`];
      if (page === "HOLD" && !fms.route.hold) { fms.open("LEGS"); fms.setScratch("/H"); return; }
      if (page) fms.open(page);
    },
  },

  IDENT: {
    pages: () => 1,
    render: () => [
      title("IDENT", "1/1"),
      caption(" MODEL", "OP PROGRAM "),
      { left: medium("CMA-9000"), right: medium("AEROLINK SIM") },
      caption(" NAV DATA", "ACTIVE "),
      { left: medium("NA-2610"), right: medium("01OCT-28OCT") },
      undefined,
      { center: small("SIMULATION - NOT FOR", "amber") },
      { center: small("NAVIGATION", "amber") },
      undefined, undefined, undefined,
      { left: dashes(24) },
      { left: back("INDEX"), right: prompt("POS INIT>") },
    ],
    lsk: (fms, side, row) => {
      if (row === 6) fms.open(side === "L" ? "INIT_REF" : "POS");
    },
  },

  POS: {
    pages: () => 1,
    render: fms => [
      title("POS INIT", "1/1"),
      caption(" FMS POS"),
      { left: medium(formatPosition(fms.position)) },
      caption(" GPS POS"),
      { left: fms.hasCondition("gpsLost") ? dashes(15) : medium(formatPosition(fms.position)) },
      caption(" UTC", "SET POS "),
      { left: medium(hhmm(fms.now)), right: boxes(15) },
      undefined, undefined, undefined, undefined,
      { left: dashes(24) },
      { left: back("INDEX"), right: prompt("RTE>") },
    ],
    lsk: (fms, side, row, scratch) => {
      if (row === 6) { fms.open(side === "L" ? "INIT_REF" : "RTE"); return; }
      if (side === "R" && row === 3) return /^[NS]\d{4}\.\d[EW]\d{5}\.\d$/.test(scratch) ? void fms.setScratch("") : "invalid";
      if (side === "L" && row === 1 && !scratch) fms.setScratch(formatPosition(fms.position));
      if (side === "L" && row === 2 && !scratch && !fms.hasCondition("gpsLost")) fms.setScratch(formatPosition(fms.position));
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

  RTE: {
    pages: () => 2,
    render: (fms, index) => {
      const route = fms.route;
      if (index === 0)
        return [
          title("RTE 1", "1/2", fms.routeStatus),
          caption(" ORIGIN", "DEST "),
          { left: { text: route.origin }, right: { text: route.dest } },
          caption(" CO ROUTE", "FLT NO "),
          { left: { text: route.coRoute }, right: { text: route.flightNo } },
          caption(" RUNWAY"),
          { left: route.runway ? { text: route.runway } : dashes(5) },
          undefined, undefined, undefined, undefined,
          { left: dashes(24) },
          fms.routeStatus === "MOD" ? { left: back("ERASE"), right: prompt("LEGS>") } : { right: prompt("LEGS>") },
        ];
      const lines: (Line | undefined)[] = [title("RTE 1", "2/2", fms.routeStatus), caption(" VIA", "TO ")];
      route.legs.slice(0, 5).forEach((leg, i) => {
        lines[2 + i * 2] = leg.kind === "disco"
          ? { center: small("DISCONTINUITY") }
          : { left: medium("DIRECT"), right: { text: leg.ident } };
      });
      lines[12] = { right: prompt("LEGS>") };
      return lines;
    },
    lsk: (fms, side, row, scratch, index) => {
      if (row === 6) {
        if (side === "R") fms.open("LEGS");
        else if (fms.routeStatus === "MOD") fms.eraseModification();
        return;
      }
      if (index !== 0) return;
      const field = side === "L" ? (row === 1 ? "origin" : row === 2 ? "coRoute" : row === 3 ? "runway" : null)
        : row === 1 ? "dest" : row === 2 ? "flightNo" : null;
      if (!field) return;
      if (!scratch) { fms.setScratch(fms.route[field] ?? ""); return; }
      if (scratch === "DELETE") return "not-allowed";
      if ((field === "origin" || field === "dest") && !ICAO.test(scratch)) return "invalid";
      if (field === "runway" && !/^RW\d{2}[LRC]?$/.test(scratch)) return "invalid";
      if (scratch.length > 10) return "invalid";
      fms.modify(route => { route[field] = scratch; });
      fms.setScratch("");
    },
  },

  DEP_ARR: {
    pages: () => 1,
    render: fms => [
      title("DEP/ARR INDEX", "1/1"),
      undefined,
      { left: prompt("<DEP"), center: { text: fms.route.origin }, right: small("ARR>") },
      undefined,
      { center: { text: fms.route.dest }, right: prompt("ARR>") },
    ],
    lsk: (fms, side, row) => {
      if (side === "L" && row === 1) fms.open("DEPARTURES");
      if (side === "R" && (row === 1 || row === 2)) fms.open("ARRIVALS");
    },
  },

  DEPARTURES: {
    pages: () => 1,
    render: fms => [
      title(`${fms.route.origin} DEPARTURES`, "1/1"),
      caption(" SIDS", "RUNWAYS "),
      ...["07", "14", "25", "32"].flatMap(runway => {
        const selected = fms.route.runway === `RW${runway}`;
        return [{ right: { text: `${selected ? "<SEL> " : ""}RW${runway}`, color: selected ? "green" as const : "white" as const } }, undefined];
      }),
      undefined, undefined, undefined,
      { left: back("INDEX") },
    ],
    lsk: (fms, side, row) => {
      if (side === "L" && row === 6) { fms.open("DEP_ARR"); return; }
      const runway = ["07", "14", "25", "32"][row - 1];
      if (side === "R" && runway) fms.modify(route => { route.runway = `RW${runway}`; });
    },
  },

  ARRIVALS: {
    pages: () => 1,
    render: () => [
      title("ARRIVALS", "1/1"),
      undefined,
      { left: medium("STARS AND APPROACHES") },
      { left: medium("ARE NOT MODELLED IN") },
      { left: medium("THIS SIMULATION") },
      undefined,
      simulated(),
    ],
  },

  LEGS: {
    pages: fms => Math.max(1, Math.ceil(fms.route.legs.length / 5)),
    render: (fms, index) => {
      const route = fms.route;
      const geometry = fms.legGeometry();
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
      return lines;
    },
    lsk: (fms, side, row, scratch, index): LskResult => {
      if (row === 6) {
        if (side === "L" && fms.routeStatus === "MOD") fms.eraseModification();
        if (side === "R") fms.open("RTE", 1);
        return;
      }
      if (side !== "L") return;
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
      if (!WAYPOINT.test(scratch)) return "invalid";
      // Line 1 of the first page is the active waypoint: an entry there is a DIRECT-TO from present position.
      if (at === 0) {
        const result = fms.directTo(scratch);
        if (!result) fms.setScratch("");
        return result;
      }
      // A waypoint further down the route closes the gap: the legs in between are deleted.
      const later = legs.findIndex((next, i) => i >= at && next.kind === "wpt" && next.ident === scratch);
      if (later >= 0) {
        fms.modify(route => { route.legs.splice(at, later - at); });
        fms.setScratch("");
        return;
      }
      if (!fms.coordinates(scratch)) return "not-in-database";
      fms.modify(route => { route.legs.splice(at, leg?.kind === "disco" ? 1 : 0, { kind: "wpt", ident: scratch }); });
      fms.setScratch("");
    },
  },

  PROG: {
    pages: () => 4,
    render: (fms, index) => {
      const legs = fms.route.legs;
      const geometry = fms.legGeometry();
      const [to, next] = legs;
      const toLeg = geometry[0], nextLeg = geometry[1];
      const now = fms.now.getTime();
      const eta = (miles: number) => hhmm(new Date(now + (miles / fms.groundSpeed) * 3_600_000));
      const ident = (leg: Leg | undefined) => (leg?.kind === "wpt" ? leg.ident : "-----");
      const gpsLost = fms.hasCondition("gpsLost");
      if (index === 0) {
        const rnp = fms.hasCondition("npa") ? 0.3 : 1.0;
        const anp = fms.hasCondition("rnpExceeded") ? Math.max(1.35, rnp + 0.35) : gpsLost ? 0.62 : 0.05;
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
          { right: medium(fms.lateralOffset === null ? "L002°/R0.02NM" : `L000°/${fms.lateralOffset < 0 ? "L" : "R"}${fixed(Math.abs(fms.lateralOffset), 2)}NM`) },
          caption("RNP/ANP"),
          { left: medium(`${fixed(rnp, 2)}/${fixed(anp, 2)}NM`, anp > rnp ? "amber" : "white") },
          caption("NAV MODE"),
          { left: gpsLost ? { text: "DR", color: "amber" } : { text: "GPS", color: "cyan" } },
        ];
      }
      if (index === 1)
        return [
          title("PROGRESS", "2/4", "ACT"),
          caption(" FUEL QTY", "FUEL FLOW "),
          { left: medium(`${fms.fuelState.quantity}KG`), right: medium(`${fms.fuelState.flow}KG/H`) },
          caption(" DEST", "EFOB "),
          { left: { text: fms.route.dest, color: "green" }, right: medium(`${Math.max(0, fms.fuelState.quantity - 260)}KG`) },
        ];
      if (index === 2)
        return [
          title("PROGRESS", "3/4", "ACT"),
          caption(" GPS", "HIL "),
          gpsLost ? { left: medium("NO SIGNAL", "amber"), right: dashes(6) } : { left: medium("NAV 9 SAT"), right: medium("0.03NM") },
          caption(" SBAS", "INTEGRITY "),
          { left: medium(gpsLost ? "----" : "WAAS"), right: gpsLost ? medium("LOST", "amber") : medium("OK", "green") },
        ];
      return [
        title("PROGRESS", "4/4", "ACT"),
        caption(" OFFSET"),
        { left: fms.lateralOffset === null ? dashes(5) : { text: `${fms.lateralOffset < 0 ? "L" : "R"}${fixed(Math.abs(fms.lateralOffset), 1)}NM` } },
        caption(" ALT", "VS "),
        { left: medium(`${Math.round(fms.altitude)}FT`), right: medium(`${fms.verticalSpeed >= 0 ? "+" : ""}${Math.round(fms.verticalSpeed / 10) * 10}FPM`) },
      ];
    },
    lsk: (fms, side, row, scratch, index) => {
      if (index !== 3 || side !== "L" || row !== 1) return;
      if (!scratch) return;
      if (scratch === "DELETE") { fms.setOffset(null); fms.setScratch(""); return; }
      // An offset is L or R and 0.1 to 20.0 NM (item: lateral offset, PROGRESS 4/4).
      const shape = /^([LR])(\d{1,2}(\.\d)?)$/.exec(scratch);
      const nm = shape ? numberIn(shape[2], 0.1, 20) : null;
      if (!shape || nm === null) return "invalid";
      fms.setOffset(shape[1] === "L" ? -nm : nm);
      fms.setScratch("");
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

  FUEL: {
    pages: () => 1,
    render: fms => [
      title("FUEL", "1/1"),
      caption(" FUEL QTY", "FLOW "),
      { left: { text: `${fms.fuelState.quantity}KG` }, right: medium(`${fms.fuelState.flow}KG/H`) },
      caption(" RESERVE", "ENDURANCE "),
      { left: { text: `${fms.fuelState.reserve}KG` }, right: medium(`${fixed((fms.fuelState.quantity - fms.fuelState.reserve) / fms.fuelState.flow, 1)}H`) },
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
      const toFix = fms.legGeometry().slice(0, at + 1).reduce((sum, leg) => sum + (leg?.distance ?? 0), 0);
      const eta = hhmm(new Date(fms.now.getTime() + (toFix / fms.groundSpeed) * 3_600_000));
      const entry = fms.holdEntryFor();
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
            fms.changeHold(h => { h.exit = h.exit === "MANUAL" ? "1 TURN" : "MANUAL"; });
            return;
        }
        return;
      }
      if (row === 1) {
        const shape = /^(\d{3})?(?:\/(\d{3,5}[AB]?))?$/.exec(scratch);
        const speed = shape?.[1] ? numberIn(shape[1], 100, 300) : undefined;
        if (!scratch || !shape || (!shape[1] && !shape[2]) || speed === null) return "invalid";
        fms.changeHold(h => { if (speed !== undefined) h.speed = speed; if (shape[2]) h.altitude = shape[2]; });
        return done();
      }
      if (row === 5) {
        if (hold.status === "IN PROGRESS") fms.changeHold(h => { h.status = "EXIT ARMED"; });
        else if (hold.status === "EXIT ARMED") fms.changeHold(h => { h.status = "IN PROGRESS"; });
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
        lines[2] = { left: medium("NO PREDEFINED") };
        lines[3] = { left: medium("WAYPOINTS") };
        return lines;
      }
      lines[1] = caption(" MARK ON TOP");
      fms.markList.slice(-5).forEach((mark, i) => {
        lines[2 + i * 2] = { left: { text: mark.ident, color: "green" }, right: medium(formatPosition(mark.position)) };
      });
      if (!fms.markList.length) lines[2] = { left: medium("NONE") };
      return lines;
    },
    lsk: (fms, side, row, scratch, index) => {
      const mark = index === 1 ? fms.markList.slice(-5)[row - 1] : undefined;
      if (side === "L" && mark && !scratch) fms.setScratch(mark.ident);
    },
  },

  VNAV: {
    pages: () => 1,
    render: fms => {
      const path = approach(fms);
      if (!path)
        return [title("VNAV", "1/1"), undefined, { center: medium("NO APPROACH IN ROUTE") }, undefined, undefined, undefined, undefined,
          undefined, undefined, undefined, undefined, undefined, { left: back("INDEX") }];
      const geometry = fms.legGeometry();
      const legs = fms.route.legs;
      const ident = (i: number) => { const leg = legs[i]; return leg?.kind === "wpt" ? leg.ident : "-----"; };
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
        title(`VNAV ${path.runway}`, "1/1", "ACT"),
        caption(" MDA-DA", "FAF ALT "),
        { left: { text: `${fms.vnav.mda}FT` }, right: { text: `${path.faf} ${fms.vnav.fafAltitude}A` } },
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
    lsk: (fms, side, row, scratch) => {
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

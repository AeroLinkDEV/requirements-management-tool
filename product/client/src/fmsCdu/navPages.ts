import { WAYPOINT, anpExceeds, anpText, caption, dashes, fixed, formatPosition, medium, prompt, small, title, type NavPageId, type Page } from "./fmsModel";
import { MODE_TEXT, shownReceiver, type GpsChoice, type ReceiverAssessment } from "./gpsSensors";
import type { FlightPhase } from "./navigation";
import { navaidComponent, type NavEntry } from "./navData";
import type { Line } from "./screen";
import type { ScriptedFms } from "./scriptedFms";

/**
 * NAV STATUS shows what the FMS navigates with (mode, ANP against RNP, the DMEs and VOR it has tuned, GPS and SBAS);
 * NAV OPTIONS lets the crew exclude navaids from position updating and choose the GPS receiver or select GPS out, as
 * airline FMSs do. GPS STATUS and POS SENSORS show what the two CMA-5024 receivers report on their buses (GPS phase 3a).
 */

/** A tuned DME's or VOR's frequency: the VHF record of its ident, never an NDB of the same ident (PASD's HBT). */
export const vhfFrequency = (entries: readonly NavEntry[]) => navaidComponent(entries, "VHF")?.frequency ?? "";
const frequency = (fms: ScriptedFms, ident: string | null | undefined) => (ident ? vhfFrequency(fms.navdb.find(ident)) : "");

/** Flight phases short enough for a caption beside NAV MODE. */
const PHASE_ABBREVIATION: Record<FlightPhase, string> = { "EN ROUTE": "ENRT", TERMINAL: "TERM", APPROACH: "APPR" };

/** The GPS summary on NAV STATUS and PROGRESS: the satellites used and the RAIM state, or why there is no fix. */
export function gpsSummary(assessment: ReceiverAssessment | undefined) {
  if (!assessment || assessment.reason === "SILENT") return { text: "GPS FAIL", ok: false };
  if (!assessment.fix) return { text: "NO SIGNAL", ok: false };
  const raim = assessment.integrity === "OK";
  return { text: `${assessment.used} SAT ${raim ? "RAIM" : "NO RAIM"}`, ok: raim && assessment.usable };
}

/** The SBAS provider and the approach level the receiver supports (305), or dashes. */
export function sbasSummary(assessment: ReceiverAssessment | undefined) {
  if (!assessment?.provider) return "----";
  return assessment.level === "NONE" ? assessment.provider : `${assessment.provider} ${assessment.level}`;
}

/** The navigation mode as the crew sees it: GPS names the receiver navigated on. */
export function navModeText(fms: ScriptedFms, compact = false) {
  const chosen = fms.navState.gpsSource;
  return fms.navState.mode === "GPS" && chosen !== null ? `GPS${chosen}${fms.navState.uncertain ? compact ? " UNC" : " UNCERTAIN" : ""}` : fms.navState.mode;
}

/**
 * The FMS's current GPS source as NAV OPTIONS shows it under GPS NAV, and the other receiver: available (STBY) or not
 * usable (FAIL). A manual choice that is not usable shows as failed, never replaced by the other.
 */
export function gpsSourceLine(fms: ScriptedFms): Line {
  if (!fms.gpsNavSelected) return { left: medium("DESELECTED", "amber") };
  if (fms.navState.mode === "GPS" && fms.navState.uncertain) return { left: medium(`GPS${fms.navState.gpsSource} UNCERTAIN`, "amber") };
  const { assessed, chosen } = fms.gpsStatus, choice = fms.gpsReceiverChoice;
  const own = choice === "AUTO" ? chosen : choice === "GPS1" ? 0 : 1;
  const other = own === 0 ? 1 : 0;
  const status = (index: number) => (assessed[index]?.usable ? `GPS${index + 1} STBY` : `GPS${index + 1} FAIL`);
  const source = own === null ? medium("AUTO NONE", "amber")
    : chosen === own ? medium(`${choice === "AUTO" ? "AUTO " : ""}GPS${own + 1}`, "green") : medium(`GPS${own + 1} FAIL`, "amber");
  return { left: source, right: medium(status(other), assessed[other]?.usable ? "white" : "amber") };
}

/** GPS NAV on NAV OPTIONS steps through AUTO, GPS1, GPS2 and off. */
const GPS_NAV_CYCLE: (GpsChoice | "OFF")[] = ["AUTO", "GPS1", "GPS2", "OFF"];

const receiverColumn = (assessment: ReceiverAssessment | undefined, value: (a: ReceiverAssessment) => string | null) => {
  const text = assessment && assessment.reason !== "SILENT" ? value(assessment) : null;
  return text === null ? dashes(4) : medium(text);
};

export const NAV_PAGES: Record<NavPageId, Page> = {
  NAV_STATUS: {
    pages: () => 1,
    render: fms => {
      const nav = fms.navState, performance = fms.navPerformance;
      const [dme1, dme2] = nav.dmes;
      const shown = shownReceiver(fms.gpsStatus, fms.gpsReceiverChoice).assessment;
      const gps = fms.gpsNavSelected ? gpsSummary(shown) : { text: "DESELECTED", ok: false };
      const lines: (Line | undefined)[] = [
        title("NAV STATUS", "1/1"),
        // The phase is abbreviated so the caption fits beside NAV MODE on one 24-column row (R19); the values are the same
        // effective RNP and ANP as PROGRESS, and a bench-forced value is labelled TEST (R11).
        caption(" NAV MODE", `ANP/RNP ${performance.forced ? "TEST" : performance.rnpSource === "MANUAL" ? "MAN" : PHASE_ABBREVIATION[fms.flightPhase]} `),
        { left: { text: navModeText(fms), color: nav.mode === "DR" || nav.uncertain ? "amber" : "green" }, right: medium(`${anpText(performance.anp)}/${fixed(performance.rnp, 2)}`, anpExceeds(performance.anp, performance.rnp) ? "amber" : "white") },
        caption(" DME 1", "DME 2 "),
        { left: dme1 ? medium(`${dme1} ${frequency(fms, dme1)}`) : dashes(4), right: dme2 ? medium(`${dme2} ${frequency(fms, dme2)}`) : dashes(4) },
        // LSK3R opens GPS STATUS for both receivers; the GPS line describes the one navigated on (or GPS1).
        caption(" VOR", "GPS STATUS> "),
        { left: nav.vor ? medium(`${nav.vor} ${frequency(fms, nav.vor)}`) : dashes(4), right: medium(gps.text, gps.ok ? "white" : "amber") },
        caption(" DR ESTIMATE", "PRAIM> "),
        { left: medium(nav.mode === "DR" ? nav.airValid ? "HDG/TAS/WIND" : "NO AIR DATA" : "STBY"), right: medium(fms.gpsNavSelected ? sbasSummary(shown) : "----") },
        caption(" INHIBITED"),
        { left: medium(fms.inhibitedNavaids.length ? fms.inhibitedNavaids.join(" ") : "NONE"), right: prompt("DME STATUS>") },
        { left: dashes(24) },
        { left: prompt("<INDEX"), right: prompt("NAV OPTIONS>") },
      ];
      return lines;
    },
    lsk: (fms, side, row) => {
      if (side === "R" && row === 3) { fms.open("GPS_STATUS"); return; }
      if (side === "R" && row === 4) { fms.open("PREDICT_RAIM"); return; }
      if (side === "R" && row === 5) { fms.open("DME_STATUS"); return; }
      if (row !== 6) return;
      fms.open(side === "L" ? "INIT_REF" : "NAV_OPTIONS");
    },
  },

  PREDICT_RAIM: {
    pages: () => 1,
    render: fms => {
      const prediction = fms.predictiveRaim, rows = fms.predictedRaim;
      const time = (at: number) => new Date(at).toISOString().slice(11, 16).replace(":", "");
      const result = (index: number) => rows[index] ? { text: `${time(rows[index].at)} ${rows[index].phase}`,
        size: "medium" as const, inverse: index === 3, color: rows[index].phase === "NONE" || rows[index].phase === "****" ? "amber" as const : "white" as const } : undefined;
      return [title("GPS PREDICT RAIM", "1/1"), caption(" IDENT", "SIM SKY "),
        { left: prediction.ident ? { text: prediction.ident } : dashes(5), right: result(0) },
        { left: small(" ETA"), right: result(1) },
        { left: prediction.eta === null ? dashes(4) : { text: `${time(prediction.eta)} Z` }, right: result(2) },
        { right: result(3) }, { right: result(4) }, { right: result(5) }, { right: result(6) },
        { left: small("MODEL, NOT LIVE ALMANAC") }, undefined, { left: dashes(24) },
        { left: prompt("<NAV STATUS"), right: prompt("SAT DESEL>") }];
    },
    lsk: (fms, side, row, scratch) => {
      if (row === 6) { fms.open(side === "L" ? "NAV_STATUS" : "SAT_DESELECT"); return; }
      if (side !== "L" || !scratch) return;
      if (row === 1) return fms.predictRaimAt(scratch) ? undefined : "not-in-database";
      if (row === 2) return fms.predictRaimEta(scratch) ? undefined : "invalid";
    },
  },

  SAT_DESELECT: {
    pages: () => 1,
    render: fms => {
      const cells = (start: number, end: number) => Array.from({ length: end - start + 1 }, (_, index) => {
        const prn = start + index;
        return { text: `${index ? " " : ""}${String(prn).padStart(2, "0")}`, size: "medium" as const, inverse: fms.raimDeselectedSatellites.includes(prn) };
      });
      return [title("GPS SAT DESELECT", "1/1"), caption(" DESEL", "PRN "), { left: dashes(2), right: cells(1, 5) },
        caption(" RESEL"), { left: dashes(2), right: cells(6, 10) }, { right: cells(11, 15) }, { right: cells(16, 20) },
        { right: cells(21, 25) }, { right: cells(26, 30) }, { right: cells(31, 32) },
        { left: small("PREDICTIVE RAIM ONLY") }, { left: dashes(24) }, { left: prompt("<PREDICT RAIM") }];
    },
    lsk: (fms, side, row, scratch) => {
      if (row === 6 && side === "L") { fms.open("PREDICT_RAIM"); return; }
      if (side !== "L" || (row !== 1 && row !== 2) || !scratch) return;
      if (!/^\d{1,2}$/.test(scratch) || Number(scratch) < 1 || Number(scratch) > 32) return "invalid";
      fms.deselectRaimSatellite(Number(scratch), row === 1);
      fms.setScratch("");
    },
  },

  // Plan F6 (M300 12-17): the stations the FMS scans for DME/DME, with their status (blank used, REJ rejected, N/A no
  // reply), frequency and slant range; whether the FMS controls the scan; and the DME/DME position.
  DME_STATUS: {
    pages: () => 1,
    render: fms => {
      const stations = fms.dmeStatus;
      const row = (index: number): Line | undefined => {
        const entry = stations[index];
        if (!entry) return undefined;
        const distance = entry.slantNm === null ? "" : `${entry.slantNm < 100 ? entry.slantNm.toFixed(1) : Math.round(entry.slantNm)}NM`;
        return { left: medium(`${entry.ident.padEnd(5)}${entry.status.padEnd(4)}${entry.frequency.padStart(6)} ${distance.padStart(7)}`, entry.status === "" ? "white" : "amber") };
      };
      const fix = fms.lastRadioFixes.find(entry => entry.mode === "DME/DME");
      const scanning = (["dme1", "dme2"] as const).some(device => fms.radioPort?.dmeReceiving(device));
      return [
        title("DME STATUS", "1/1"),
        caption(" ID   STAT  FREQ    DIS"),
        row(0), row(1), row(2), row(3), row(4), row(5),
        { left: small(scanning ? "SCANNING CTRL ACTIVE" : "SCANNING CTRL LOST", scanning ? "green" : "amber") },
        caption(" POSITION"),
        { left: fix ? medium(formatPosition(fix.position)) : dashes(18) },
        { left: dashes(24) },
        { left: prompt("<NAV STATUS"), right: prompt("DME DESEL>") },
      ];
    },
    lsk: (fms, side, row) => {
      if (row !== 6) return;
      fms.open(side === "L" ? "NAV_STATUS" : "DME_DESELECT");
    },
  },

  // Plan F6 (M300 12-18): up to 25 deselected DME stations, five a page (NEXT, PREV); an ident on LSK 1-5 inserts it, and
  // CLR (DELETE) with the LSK removes it.
  DME_DESELECT: {
    pages: fms => Math.max(1, Math.ceil(Math.min(fms.dmeDeselectedStations.length + 1, 25) / 5)),
    render: (fms, index) => {
      const list = fms.dmeDeselectedStations, pages = Math.max(1, Math.ceil(Math.min(list.length + 1, 25) / 5));
      const line = (slot: number): Line | undefined => {
        const ident = list[index * 5 + slot];
        if (ident) return { left: medium(`${ident.padEnd(6)}${frequency(fms, ident).padStart(6)} MHZ`) };
        return index * 5 + slot === list.length && list.length < 25 ? { left: dashes(4) } : undefined;
      };
      return [
        title("DME DESELECT", `${index + 1}/${pages}`),
        caption(" IDENT     FREQ"),
        line(0), undefined, line(1), undefined, line(2), undefined, line(3), undefined, line(4),
        { left: dashes(24) },
        { left: prompt("<DME STATUS") },
      ];
    },
    lsk: (fms, side, row, scratch, index) => {
      if (side === "L" && row === 6) { fms.open("DME_STATUS"); return; }
      if (side !== "L" || row > 5 || !scratch) return;
      const list = [...fms.dmeDeselectedStations], slot = index * 5 + row - 1;
      if (scratch === "DELETE") {
        if (slot >= list.length) return "invalid";
        list.splice(slot, 1); fms.setDmeDeselected(list); fms.setScratch(""); return;
      }
      if (!WAYPOINT.test(scratch)) return "invalid";
      const navaid = fms.navdb.find(scratch).find(entry => entry.kind === "navaid" && ["DME", "VORDME", "VORTAC", "TACAN"].includes(entry.type));
      if (!navaid) return "not-in-database";
      if (list.includes(scratch)) { fms.setScratch(""); return; }
      if (list.length >= 25) return "invalid";
      list.splice(Math.min(slot, list.length), 0, scratch);
      fms.setDmeDeselected(list); fms.setScratch("");
    },
  },

  NAV_OPTIONS: {
    pages: () => 1,
    render: fms => {
      const [a, b, c] = fms.inhibitedNavaids;
      return [
        title("NAV OPTIONS", "1/1"),
        caption(" NAVAID INHIBIT"),
        { left: a ? { text: a } : dashes(4), center: b ? { text: b } : dashes(4), right: c ? { text: c } : dashes(4) },
        undefined,
        { center: small("DELETE NAVAID TO RESTORE", "green") },
        caption(" GPS NAV"),
        { left: [{ text: "<", color: "cyan" }, ...GPS_NAV_CYCLE.flatMap((option, i) => {
          const on = option === (fms.gpsNavSelected ? fms.gpsReceiverChoice : "OFF");
          return [...(i ? [{ text: "/", color: "white" as const }] : []), { text: option, color: on ? "green" as const : "white" as const, size: on ? "large" as const : "small" as const }];
        })] },
        // The source the FMS navigates on now, and the note on AUTO: it keeps the current suitable receiver; GPS1 is the
        // initial preference only when both are equally suitable and there is no current one (FMS_TEST_BENCH.md).
        caption(" FMS SOURCE", "OTHER "),
        gpsSourceLine(fms),
        { left: small("AUTO KEEPS SUITABLE RCVR") },
        { left: small("GPS1 INITIAL IF EQUAL") },
        { left: dashes(24) },
        { left: prompt("<NAV STATUS") },
      ];
    },
    lsk: (fms, side, row, scratch) => {
      if (side === "L" && row === 6) { fms.open("NAV_STATUS"); return; }
      if (side === "L" && row === 3) {
        const current = GPS_NAV_CYCLE.indexOf(fms.gpsNavSelected ? fms.gpsReceiverChoice : "OFF");
        fms.selectGpsReceiver(GPS_NAV_CYCLE[(current + 1) % GPS_NAV_CYCLE.length]);
        return;
      }
      if (row !== 1 || !scratch) return;
      const slot = side === "L" ? 0 : 2;
      const inhibited = [...fms.inhibitedNavaids];
      if (scratch === "DELETE") { inhibited.splice(slot, 1); fms.setInhibited(inhibited); fms.setScratch(""); return; }
      if (!WAYPOINT.test(scratch)) return "invalid";
      if (!fms.navdb.find(scratch).some(e => e.kind === "navaid")) return "not-in-database";
      if (!inhibited.includes(scratch)) inhibited.push(scratch);
      fms.setInhibited(inhibited);
      fms.setScratch("");
    },
  },

  GPS_STATUS: {
    pages: () => 1,
    render: fms => {
      const [one, two] = fms.gpsStatus.assessed;
      const chosen = fms.gpsStatus.chosen;
      // The receiver navigated on is marked with an asterisk.
      const heading = (index: number) => `${chosen === index ? "*" : ""}GPS${index + 1}`;
      const nm = (value: number | null) => (value === null ? null : fixed(value, 2));
      return [
        title("GPS STATUS", "1/1"),
        caption(` ${heading(0)}`, `${heading(1)} `, "MODE"),
        { left: receiverColumn(one, a => (a.mode ? MODE_TEXT[a.mode] : null)), right: receiverColumn(two, a => (a.mode ? MODE_TEXT[a.mode] : null)) },
        caption(undefined, undefined, "SAT USED/VIS"),
        { left: receiverColumn(one, a => `${a.used}/${a.visible}`), right: receiverColumn(two, a => `${a.used}/${a.visible}`) },
        caption(undefined, undefined, "HIL NM"),
        { left: receiverColumn(one, a => nm(a.hil)), right: receiverColumn(two, a => nm(a.hil)) },
        caption(undefined, undefined, "HFOM NM"),
        { left: receiverColumn(one, a => nm(a.hfom)), right: receiverColumn(two, a => nm(a.hfom)) },
        caption(undefined, undefined, "RAIM"),
        { left: receiverColumn(one, a => a.integrity), right: receiverColumn(two, a => a.integrity) },
        { left: dashes(24) },
        { left: prompt("<NAV STATUS"), right: prompt("POS SENSORS>") },
      ];
    },
    lsk: (fms, side, row) => {
      if (row === 6) fms.open(side === "L" ? "NAV_STATUS" : "POS_SENSORS");
    },
  },

  POS_SENSORS: {
    pages: () => 1,
    render: fms => {
      const [one, two] = fms.gpsStatus.assessed;
      const sensor = (assessment: ReceiverAssessment | undefined) =>
        assessment?.fix ? medium(formatPosition(assessment.fix), assessment.usable ? "white" : "amber") : dashes(15);
      const state = (assessment: ReceiverAssessment | undefined) => (assessment?.reason === "OK" ? "" : assessment?.reason ?? "SILENT");
      return [
        title("POS SENSORS", "1/1"),
        caption(" FMS", `${navModeText(fms)} `),
        { left: medium(formatPosition(fms.position)) },
        caption(" GPS1", `${state(one)} `),
        { left: sensor(one) },
        caption(" GPS2", `${state(two)} `),
        { left: sensor(two) },
        undefined, undefined, undefined, undefined,
        { left: dashes(24) },
        { left: prompt("<GPS STATUS"), right: prompt("NAV STATUS>") },
      ];
    },
    lsk: (fms, side, row) => {
      if (row === 6) fms.open(side === "L" ? "GPS_STATUS" : "NAV_STATUS");
    },
  },
};

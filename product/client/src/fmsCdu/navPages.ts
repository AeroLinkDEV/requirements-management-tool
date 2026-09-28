import { WAYPOINT, caption, dashes, fixed, formatPosition, medium, prompt, small, title, type NavPageId, type Page } from "./fmsModel";
import { MODE_TEXT, shownReceiver, type GpsChoice, type ReceiverAssessment } from "./gpsSensors";
import type { FlightPhase } from "./navigation";
import type { Line } from "./screen";
import type { ScriptedFms } from "./scriptedFms";

/**
 * NAV STATUS shows what the FMS navigates with (mode, ANP against RNP, the DMEs and VOR it has tuned, GPS and SBAS);
 * NAV OPTIONS lets the crew exclude navaids from position updating and choose the GPS receiver or select GPS out, as
 * airline FMSs do. GPS STATUS and POS SENSORS show what the two CMA-5024 receivers report on their buses (GPS phase 3a).
 */

const frequency = (fms: ScriptedFms, ident: string | null | undefined) => {
  const entry = ident ? fms.navdb.find(ident).find(e => e.kind === "navaid") : undefined;
  return entry?.kind === "navaid" ? entry.frequency : "";
};

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
export function navModeText(fms: ScriptedFms) {
  const chosen = fms.gpsStatus.chosen;
  return fms.navState.mode === "GPS" && chosen !== null ? `GPS${chosen + 1}` : fms.navState.mode;
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
        { left: { text: navModeText(fms), color: nav.mode === "DR" ? "amber" : "green" }, right: medium(`${fixed(performance.anp, 2)}/${fixed(performance.rnp, 2)}`, performance.anp > performance.rnp ? "amber" : "white") },
        caption(" DME 1", "DME 2 "),
        { left: dme1 ? medium(`${dme1} ${frequency(fms, dme1)}`) : dashes(4), right: dme2 ? medium(`${dme2} ${frequency(fms, dme2)}`) : dashes(4) },
        // LSK3R opens GPS STATUS for both receivers; the GPS line describes the one navigated on (or GPS1).
        caption(" VOR", "GPS STATUS> "),
        { left: nav.vor ? medium(`${nav.vor} ${frequency(fms, nav.vor)}`) : dashes(4), right: medium(gps.text, gps.ok ? "white" : "amber") },
        caption(" IRS", "SBAS "),
        { left: medium(nav.mode === "DR" ? "DR ONLY" : "NAV"), right: medium(fms.gpsNavSelected ? sbasSummary(shown) : "----") },
        caption(" INHIBITED"),
        { left: medium(fms.inhibitedNavaids.length ? fms.inhibitedNavaids.join(" ") : "NONE") },
        { left: dashes(24) },
        { left: prompt("<INDEX"), right: prompt("NAV OPTIONS>") },
      ];
      return lines;
    },
    lsk: (fms, side, row) => {
      if (side === "R" && row === 3) { fms.open("GPS_STATUS"); return; }
      if (row !== 6) return;
      fms.open(side === "L" ? "INIT_REF" : "NAV_OPTIONS");
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
        undefined,
        caption(" GPS NAV"),
        { left: [{ text: "<", color: "cyan" }, ...GPS_NAV_CYCLE.flatMap((option, i) => {
          const on = option === (fms.gpsNavSelected ? fms.gpsReceiverChoice : "OFF");
          return [...(i ? [{ text: "/", color: "white" as const }] : []), { text: option, color: on ? "green" as const : "white" as const, size: on ? "large" as const : "small" as const }];
        })] },
        undefined, undefined, undefined,
        { center: small("DELETE NAVAID TO RESTORE", "green") },
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

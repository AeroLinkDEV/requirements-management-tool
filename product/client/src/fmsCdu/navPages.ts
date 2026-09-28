import { WAYPOINT, caption, dashes, fixed, medium, prompt, small, title, type NavPageId, type Page } from "./fmsModel";
import type { Line } from "./screen";
import type { ScriptedFms } from "./scriptedFms";

/**
 * NAV STATUS shows what the FMS navigates with (mode, ANP against RNP, the DMEs and VOR it has tuned, GPS and SBAS);
 * NAV OPTIONS lets the crew exclude navaids from position updating and select GPS out, as airline FMSs do.
 */

const frequency = (fms: ScriptedFms, ident: string | null | undefined) => {
  const entry = ident ? fms.navdb.find(ident).find(e => e.kind === "navaid") : undefined;
  return entry?.kind === "navaid" ? entry.frequency : "";
};

export const NAV_PAGES: Record<NavPageId, Page> = {
  NAV_STATUS: {
    pages: () => 1,
    render: fms => {
      const nav = fms.navState;
      const [dme1, dme2] = nav.dmes;
      const gps = !fms.gpsNavSelected ? "DESELECTED" : fms.hasCondition("gpsLost") ? "NO SIGNAL" : fms.hasCondition("gpsIntegrity") ? "9 SAT NO RAIM" : "9 SAT RAIM";
      const lines: (Line | undefined)[] = [
        title("NAV STATUS", "1/1"),
        caption(" NAV MODE", `ANP/RNP ${fms.flightPhase} `),
        { left: { text: nav.mode, color: nav.mode === "DR" ? "amber" : "green" }, right: medium(`${fixed(nav.anp, 2)}/${fixed(fms.requiredRnp, 2)}`, nav.anp > fms.requiredRnp ? "amber" : "white") },
        caption(" DME 1", "DME 2 "),
        { left: dme1 ? medium(`${dme1} ${frequency(fms, dme1)}`) : dashes(4), right: dme2 ? medium(`${dme2} ${frequency(fms, dme2)}`) : dashes(4) },
        caption(" VOR", "GPS "),
        { left: nav.vor ? medium(`${nav.vor} ${frequency(fms, nav.vor)}`) : dashes(4), right: medium(gps, gps.includes("RAIM") && !gps.includes("NO") ? "white" : "amber") },
        caption(" IRS", "SBAS "),
        { left: medium(nav.mode === "DR" ? "DR ONLY" : "NAV"), right: medium(nav.mode === "GPS" && !fms.hasCondition("gpsIntegrity") ? "WAAS" : "----") },
        caption(" INHIBITED"),
        { left: medium(fms.inhibitedNavaids.length ? fms.inhibitedNavaids.join(" ") : "NONE") },
        { left: dashes(24) },
        { left: prompt("<INDEX"), right: prompt("NAV OPTIONS>") },
      ];
      return lines;
    },
    lsk: (fms, side, row) => {
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
        { left: [{ text: "<", color: "cyan" }, { text: "ON", color: fms.gpsNavSelected ? "green" : "white", size: fms.gpsNavSelected ? "large" : "small" }, { text: "/", color: "white" }, { text: "OFF", color: fms.gpsNavSelected ? "white" : "green", size: fms.gpsNavSelected ? "small" : "large" }] },
        undefined, undefined, undefined,
        { center: small("DELETE A NAVAID TO RESTORE IT", "green") },
        { left: dashes(24) },
        { left: prompt("<NAV STATUS") },
      ];
    },
    lsk: (fms, side, row, scratch) => {
      if (side === "L" && row === 6) { fms.open("NAV_STATUS"); return; }
      if (side === "L" && row === 3) { fms.selectGps(!fms.gpsNavSelected); return; }
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
};

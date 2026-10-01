import { adfFrequency } from "./radioManagement";
import { caption, dashes, prompt, small, three, title, type Page, type RadioPageId } from "./fmsModel";
import type { RadioDevice, TestableDevice } from "./radioManagement";
import type { ScriptedFms } from "./scriptedFms";
import type { Segment } from "./screen";

/**
 * The NAV and ADF radio pages (Stage F plan F8b; M300 13-21 to 13-25), on the shared RMS (#1350, plan F8a). The FMS is
 * the only tuning source (DEC-150). Station libraries are a later step; these pages offer no LIBRARY prompt yet.
 */

const on = (fms: ScriptedFms, key: string) =>
  (fms.aircraftProfile.configuration?.options as Record<string, { configured: boolean }> | undefined)?.[key]?.configured === true;

/**
 * An active frequency as M300 13-3 shows it: inverse while this computer's request is being tuned, small amber when
 * the tuning did not succeed or the radio reports nothing, large white when tuned.
 */
export function activeFrequency(fms: ScriptedFms, device: RadioDevice, text = fms.radioState[device]): Segment {
  const port = fms.radioPort;
  const requests = fms.radioRequests.filter(request => request.device === device);
  const pending = requests.find(request => request.status === "PENDING");
  if (pending) return { text: pending.value, color: "white", inverse: true };
  // Plan C3: amber for a failed tuning (REJECTED or TIMEOUT) or a radio whose words do not reach the FMS (M300 13-3).
  const faults = port?.faults(device);
  const failed = faults !== undefined && (faults.receiver !== "NORMAL" || faults.measurementBus === "LOST");
  if (failed || requests[0]?.status === "REJECTED" || requests[0]?.status === "TIMEOUT") return small(text, "amber");
  return { text, color: "white" };
}

/** VOR/ILS 108.00 to 117.95 MHz at 50 kHz (M300 13-4). */
const navFrequency = (entry: string) => {
  if (!/^1[01]\d\.\d{1,2}$/.test(entry)) return null;
  const value = Number(entry), hundredths = Math.round(value * 100);
  return value >= 108 && value <= 117.95 && hundredths % 5 === 0 ? value.toFixed(2) : null;
};

const testText = (fms: ScriptedFms, device: TestableDevice) => fms.radioPort?.testState(device) ?? "READY";

export const RADIO_PAGES: Record<RadioPageId, Page> = {
  NAV_RADIO: {
    // NAV 2/2 exists only when VOR auto-tuning or radio test is configured (M300 13-22).
    pages: fms => (on(fms, "vorAutoTune") || on(fms, "radioTest") ? 2 : 1),
    render: (fms, index) => {
      const port = fms.radioPort;
      if (index === 0) {
        const ident = (device: "nav1" | "nav2") => fms.navStation(device)?.ident ?? "";
        const radial = (device: "nav1" | "nav2") => { const value = fms.navRadial(device); return value === null ? dashes(3) : small(`RAD:${three(value) === "000" ? "360" : three(value)}°`); };
        const mode = (device: "nav1" | "nav2") => (port?.navMode(device) ?? "MAN") === "AUTO" ? "AUTO" : "MAN";
        const hold = (device: "dme1" | "dme2") => (port?.dmeHold(device) ? "ON" : "OFF");
        const distance = (device: "dme1" | "dme2") => { const text = fms.dmeDistance(device); return text === "" ? undefined : small(`DME:${text}`, text === "****" ? "amber" : "white"); };
        return [
          title("NAV", on(fms, "vorAutoTune") || on(fms, "radioTest") ? "1/2" : "1/1"),
          caption(` NAV1 ${mode("nav1")}`, `${mode("nav2")} NAV2 `),
          { left: [activeFrequency(fms, "nav1"), small(` ${ident("nav1")}`)], right: [small(`${ident("nav2")} `), activeFrequency(fms, "nav2")] },
          undefined,
          { left: radial("nav1"), right: radial("nav2") },
          undefined, undefined,
          caption(" DME1 HOLD", "DME2 HOLD "),
          { left: prompt(`>${hold("dme1")}`), right: prompt(`${hold("dme2")}<`) },
          undefined,
          { left: distance("dme1"), right: distance("dme2") },
          { left: dashes(24) },
          { left: prompt("<RADIO") },
        ];
      }
      const modeText = (device: "nav1" | "nav2") => (port?.navMode(device) === "AUTO" ? "AUTOMATIC" : "MANUAL");
      const test = on(fms, "radioTest");
      return [
        title("NAV", "2/2"),
        caption(" NAV1 MODE", "NAV2 MODE "),
        { left: prompt(`>${modeText("nav1")}`), right: prompt(`${modeText("nav2")}<`) },
        undefined, undefined,
        test ? caption(" NAV1 TEST", "NAV2 TEST ") : undefined,
        test ? { left: prompt(`>${testText(fms, "nav1")}`), right: prompt(`${testText(fms, "nav2")}<`) } : undefined,
        undefined, undefined,
        test ? caption(" DME1 TEST", "DME2 TEST ") : undefined,
        test ? { left: prompt(`>${testText(fms, "dme1")}`), right: prompt(`${testText(fms, "dme2")}<`) } : undefined,
        { left: dashes(24) },
        { left: prompt("<RADIO") },
      ];
    },
    lsk: (fms, side, row, scratch, index) => {
      const port = fms.radioPort;
      if (side === "L" && row === 6) { fms.open("RADIO"); return; }
      if (!port) return;
      const nav = side === "L" ? "nav1" : "nav2", dme = side === "L" ? "dme1" : "dme2";
      if (index === 0) {
        if (row === 1 && scratch) {
          const value = navFrequency(scratch);
          if (value === null) return "invalid";
          fms.setRadio(nav, value);
          fms.setScratch("");
          return;
        }
        if (row === 4) { port.setDmeHold(dme, !port.dmeHold(dme)); return; }
        return;
      }
      if (row === 1 && on(fms, "vorAutoTune")) { port.setNavMode(nav, port.navMode(nav) === "AUTO" ? "MAN" : "AUTO"); return; }
      if (row === 3 && on(fms, "radioTest")) { port.pressTest(nav); return; }
      if (row === 5 && on(fms, "radioTest")) { port.pressTest(dme); return; }
    },
  },

  ADF_RADIO: {
    // ADF 2/2 exists only when radio test is configured (M300 13-25).
    pages: fms => (on(fms, "radioTest") ? 2 : 1),
    render: (fms, index) => {
      const port = fms.radioPort;
      if (index === 0) {
        const settings = (device: "adf" | "adf2") => port?.adf(device) ?? { mode: "ADF" as const, bfo: false, bearing: "REL" as const };
        const label = (device: "adf" | "adf2") => ({ REL: "REL-BRG", MAG: "MAG-BRG", TRUE: "TRUE-BRG" })[settings(device).bearing];
        const bearing = (device: "adf" | "adf2") => { const value = fms.adfBearing(device); return value === null ? dashes(3) : { text: `${three(value) === "000" ? "360" : three(value)}°`, color: "green" as const }; };
        return [
          title("ADF", on(fms, "radioTest") ? "1/2" : "1/1"),
          caption(" ADF1", "ADF2 "),
          { left: activeFrequency(fms, "adf"), right: activeFrequency(fms, "adf2") },
          // The standby frequency, beside the active one (M300 13-23): an entry goes there, LSK 1L/1R swaps it in.
          { left: small(` STBY ${fms.radioState.adfStby}`), right: small(`${fms.radioState.adf2Stby} STBY `) },
          undefined,
          caption(" MODE", "MODE "),
          { left: prompt(`>${settings("adf").mode}`), right: prompt(`${settings("adf2").mode}<`) },
          caption(" BFO", "BFO "),
          { left: prompt(`>${settings("adf").bfo ? "ON" : "OFF"}`), right: prompt(`${settings("adf2").bfo ? "ON" : "OFF"}<`) },
          caption(` ${label("adf")}`, `${label("adf2")} `),
          { left: bearing("adf"), right: bearing("adf2") },
          { left: dashes(24) },
          { left: prompt("<RADIO") },
        ];
      }
      return [
        title("ADF", "2/2"),
        undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined,
        caption(" ADF1 TEST", "ADF2 TEST "),
        { left: prompt(`>${testText(fms, "adf")}`), right: prompt(`${testText(fms, "adf2")}<`) },
        { left: dashes(24) },
        { left: prompt("<RADIO") },
      ];
    },
    lsk: (fms, side, row, scratch, index) => {
      const port = fms.radioPort;
      if (side === "L" && row === 6) { fms.open("RADIO", 1); return; }
      if (!port) return;
      const adf = side === "L" ? "adf" : "adf2";
      if (index === 1) { if (row === 5) port.pressTest(adf); return; }
      // M300 13-23: an entry goes to standby; with the scratchpad empty, the LSK swaps standby and active.
      if (row === 1 && scratch) {
        const value = adfFrequency(scratch);
        if (value === null) return "invalid";
        fms.setRadio(adf === "adf" ? "adfStby" : "adf2Stby", value);
        fms.setScratch("");
        return;
      }
      if (row === 1) { fms.swapRadio(adf); return; }
      const settings = port.adf(adf);
      if (row === 3) port.setAdf(adf, { mode: settings.mode === "ADF" ? "ANT" : "ADF" });
      if (row === 4) port.setAdf(adf, { bfo: !settings.bfo });
      if (row === 5) port.setAdf(adf, { bearing: settings.bearing === "REL" ? "MAG" : settings.bearing === "MAG" ? "TRUE" : "REL" });
    },
  },
};

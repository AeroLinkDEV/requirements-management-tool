import { COPTER_PINS_CIFP_2609 } from "./data/copterPinsCifp2609";
import type { FlightSimulator } from "./flight";
import { courseDeg, offset } from "./fmsModel";
import type { StartStateId } from "./kbtvDemo";
import type { Scenario, ScenarioStep } from "./scenario";
import type { ScriptedFms } from "./scriptedFms";

/**
 * The helicopter acceptance mission's data and start (helicopter-first plan §10, "87N offshore SAR"): the FAA CIFP 2609
 * Copter point-in-space extract, bundled with the client (data/copterPinsCifp2609.ts), with Southampton heliport (87N)
 * and its COPTER RNAV (GPS) 190, over the declared sea south of it (surface.ts OFFSHORE_87N), in a steady 230°T / 20 kt
 * wind.
 */

/** How the bench and the run reports name the bundled data: real, public domain, and not for navigation. */
export const COPTER_PINS_SOURCE = "FAA CIFP 2609, Copter PinS extract (87N and four others): real public-domain data, for demonstration only, not for navigation";

/** The mission wind, a laboratory test condition (plan §10; no envelope claim). */
export const MISSION_WIND = { direction: 230, speed: 20 } as const;
/** The start: this far south of the 87N heliport, at this altitude over the sea (MSL = radio height there), at this IAS. */
export const MISSION_START_SOUTH_NM = 10;
export const MISSION_START_ALTITUDE_FT = 500;
export const MISSION_START_IAS = 100;
/** The SAR datum the crew searches about: a user waypoint this far and on this bearing from the start. */
export const MISSION_DATUM_NM = 2;
export const MISSION_DATUM_BEARING = 230;

/**
 * Loads the bundled Copter PinS extract as the inactive cycle and activates it, as the Nav data tab's file load and
 * Activate do. Loading again when it is already active changes nothing.
 */
export function loadCopterPinsDemonstration(fms: ScriptedFms): { loaded: string; already?: boolean } | { refused: string } {
  if (fms.activeCycle.source === COPTER_PINS_SOURCE) return { loaded: fms.activeCycle.id, already: true };
  const outcome = fms.loadArinc424(COPTER_PINS_CIFP_2609, COPTER_PINS_SOURCE);
  if ("refused" in outcome) return outcome;
  fms.swapCycles();
  return { loaded: outcome.loaded };
}

/**
 * The start state for the 87N offshore SAR mission (plan §10 step 1), labelled synthetic: the Copter PinS data active;
 * 87N the destination; the declared sea surface; the mission wind; the aircraft placed 10 NM south of 87N at 500 ft,
 * tracking 230°T, level (ALT) at 100 KIAS under LNAV; a user waypoint (the datum) 2 NM on 230°T, and the SAR reference
 * set to it, so the crew's first action is to select and activate the sector search. The aircraft is placed once
 * (placeAircraft, recorded in the engineering log); everything after is flown.
 */
export function setUp87nOffshoreSar(fms: ScriptedFms, sim?: FlightSimulator): { ready: true } | { refused: string } {
  if (!sim) return { refused: "the helicopter mission needs the flight simulation (autopilot selections)" };
  if (fms.aircraftProfile.verticalPolicy !== "ADVISORY") return { refused: "the 87N mission flies the helicopter profile" };
  const loaded = loadCopterPinsDemonstration(fms);
  if ("refused" in loaded) return loaded;
  if (!fms.declareSurface("offshore-87n")) return { refused: "the offshore-87n surface is not declared" };
  Object.assign(fms.wind, MISSION_WIND);
  fms.modify(route => { route.dest = "87N"; });
  fms.press("EXEC");
  const heliport = fms.coordinates("87N");
  if (!heliport) return { refused: "87N is not in the active navigation data" };
  const start = offset(heliport, 180, MISSION_START_SOUTH_NM);
  fms.placeAircraft({ position: start, track: MISSION_DATUM_BEARING, altitude: MISSION_START_ALTITUDE_FT }, "87N offshore SAR mission set-up");
  const datum = fms.createPilot("DTM", offset(start, MISSION_DATUM_BEARING, MISSION_DATUM_NM), "SAR DATUM");
  fms.sar.refId = datum;
  sim.selectSpeed(MISSION_START_IAS);
  sim.engageAltitudeHold();
  sim.armLnav();
  return { ready: true };
}

/** Where the final start state places the aircraft: this far before STAYS on the final course, at 1,700 ft and 70 KIAS. */
export const FINAL_START_BEFORE_STAYS_NM = 3;

/**
 * The start state for the mission's approach variants (plan §10, step 8), labelled synthetic: as the mission start, then
 * the 87N COPTER RNAV (GPS) 190 via HTO executed, the aircraft placed 3 NM before STAYS on the final course (TIDUE to
 * STAYS, extended back) at 1,700 ft, level at 70 KIAS, DIRECT STAYS executed (the HF behind it), NAV armed and the
 * approach armed.
 */
export function setUp87nRnav190Final(fms: ScriptedFms, sim?: FlightSimulator): { ready: true } | { refused: string } {
  if (!sim) return { refused: "the helicopter mission needs the flight simulation (autopilot selections)" };
  if (fms.aircraftProfile.verticalPolicy !== "ADVISORY") return { refused: "the 87N mission flies the helicopter profile" };
  const loaded = loadCopterPinsDemonstration(fms);
  if ("refused" in loaded) return loaded;
  if (!fms.declareSurface("offshore-87n")) return { refused: "the offshore-87n surface is not declared" };
  Object.assign(fms.wind, MISSION_WIND);
  fms.modify(route => { route.dest = "87N"; });
  fms.press("EXEC");
  fms.selectProcedure("APPROACH", "R190", "HTO");
  fms.press("EXEC");
  const tidue = fms.coordinates("TIDUE"), stays = fms.coordinates("STAYS");
  if (!tidue || !stays) return { refused: "TIDUE or STAYS is not in the active navigation data" };
  const course = courseDeg(tidue, stays);
  fms.placeAircraft({ position: offset(stays, course + 180, FINAL_START_BEFORE_STAYS_NM), track: course, altitude: 1700 }, "87N RNAV 190 final set-up");
  fms.directTo("STAYS");
  fms.press("EXEC");
  sim.selectSpeed(70);
  sim.engageAltitudeHold();
  sim.armLnav();
  fms.armApproach(true);
  return { ready: true };
}

const at = (seconds: number) => ({ kind: "time" as const, seconds });
const then = { kind: "start" as const };

/**
 * The v1 acceptance mission, nominal run (plan §10), as a scenario. The crew's actions are scripted as a crew would
 * fly them under the helicopter profile, where the FMS advises and the crew selects the vertical modes and speeds:
 * - a sector search about the datum, and the "sighting" at 7 minutes: MARK ON TOP, ACTIVATE, EXEC;
 * - downwind on heading 050, then an intercept heading onto the 230°T final track with NAV armed (Phase 1 to TDN);
 * - the FMS transition to a hover at MRK (TD, gate segment, TD/H, HOV), held for two minutes;
 * - TU-LAB, then a climb to 2,000 ft at 90 KIAS; the 87N COPTER RNAV (GPS) 190 via HTO, DIRECT HTO;
 * - the HF course reversal at TIDUE (EXIT TYPE ONCE), the step-downs (1,800, 1,700, MDA 560) at 70 KIAS;
 * - TOGA at CRANN (the MAP): the missed approach, direct BEADS at 70 KIAS to 2,000 ft, then 90 KIAS;
 * - the BEADS hold, flown once and left (MISSED-HOLD), then END OF ROUTE and NAV giving way to HDG.
 */
export const MISSION_87N_OFFSHORE_SAR: Scenario = {
  id: "87n-offshore-sar",
  title: "87N offshore SAR: search, hover at the mark, the Copter RNAV 190 and its missed approach",
  objective: "The helicopter acceptance mission (plan §10) on real FAA CIFP 2609 data at Southampton (87N), over the declared sea in a steady 230/20 wind: a sector search, MARK ON TOP, the CMA transition down to a 50 ft hover at the mark, TU-LAB, the point-in-space approach via HTO with its HF course reversal, a missed approach at CRANN, and the BEADS missed-approach hold flown once. Synthetic start; real public-domain data, for demonstration only, not for navigation.",
  maxSeconds: 5400,
  start: "87n-offshore-sar",
  startTime: "2026-09-29T15:00:00Z",
  steps: [
    // 1. The sector search about the datum.
    { when: then, action: { kind: "keys", keys: ["TACT", "LSK4L", "LSK6R", "EXEC"] } },
    { when: then, action: { kind: "expectLine", line: 0, pattern: "ACT SECTOR SAR" } },
    // 2-3. The sighting: MARK ON TOP on the HOVER page, with a valid radio height over the sea; ACTIVATE and EXEC.
    { when: at(420), action: { kind: "keys", keys: ["TACT", "LSK1R", "LSK4L"] } },
    { when: then, action: { kind: "expectLine", line: 4, pattern: "^\\s*500FT\\s+50FT\\s*$" } },
    { when: then, action: { kind: "keys", keys: ["LSK6R", "EXEC"] } },
    { when: then, action: { kind: "expectAlert", text: "TRANSITION DOWN" } },
    // 4. Downwind, then onto the final track into the wind with NAV armed.
    { when: then, action: { kind: "autopilot", heading: 50 } },
    { when: at(570), action: { kind: "autopilot", heading: 200, lnav: true } },
    // 5. The transition to the hover at MRK.
    { when: then, action: { kind: "expectAfcs", collective: "RHT", pitch: "HOV", roll: "HOV" }, within: 600 },
    { when: then, action: { kind: "expectAircraft", near: "MRK", nearMetres: 50, radioHeight: 50, heightTolerance: 5, maxGroundSpeed: 1 }, within: 60 },
    // 6. Two minutes in the hover.
    { when: at(1100), action: { kind: "expectAircraft", near: "MRK", nearMetres: 10, radioHeight: 50, heightTolerance: 5, maxGroundSpeed: 1 } },
    // 7. TU-LAB: each axis captures on its own; then the climb to 2,000 at 90 KIAS.
    { when: then, action: { kind: "autopilot", transitionUp: true } },
    { when: then, action: { kind: "expectAfcs", collective: "RHT", pitch: "IAS", roll: "HDG" }, within: 120 },
    { when: then, action: { kind: "autopilot", altitude: 2000, verticalSpeed: 500, speed: 90 } },
    // 8. The approach via HTO, DIRECT HTO, NAV.
    { when: then, action: { kind: "procedure", procedure: "APPROACH", ident: "R190", transition: "HTO" } },
    { when: then, action: { kind: "keys", keys: ["EXEC", "LEGS", "CHAR_H", "CHAR_T", "CHAR_O", "LSK1L", "EXEC"] } },
    { when: then, action: { kind: "autopilot", heading: 30, lnav: true } },
    { when: then, action: { kind: "expectAfcs", roll: "NAV" }, within: 60 },
    // TF TIDUE at or above 1,800 and 70 KIAS; the HF course reversal is flown once.
    { when: { kind: "active", waypoint: "TIDUE" }, action: { kind: "autopilot", altitude: 1800, verticalSpeed: -500, speed: 70 } },
    { when: then, action: { kind: "expectLine", line: 0, pattern: "LEGS" } },
    { when: { kind: "active", waypoint: "STAYS" }, action: { kind: "autopilot", altitude: 1700, verticalSpeed: -500 } },
    { when: then, action: { kind: "armApproach" } },
    // After STAYS, down to the MDA and level to CRANN.
    { when: { kind: "active", waypoint: "CRANN" }, action: { kind: "autopilot", altitude: 560, verticalSpeed: -600 } },
    { when: then, action: { kind: "expectAircraft", altitude: 560, heightTolerance: 60 }, within: 240 },
    // 9. TOGA at CRANN: the missed approach, climbing at the GA rate to 2,000 at 70 KIAS, then 90.
    { when: { kind: "distance", waypoint: "CRANN", nm: 0.1 }, action: { kind: "goAround" } },
    { when: then, action: { kind: "autopilot", altitude: 2000 } },
    { when: then, action: { kind: "expectActive", waypoint: "BEADS" }, within: 60 },
    { when: then, action: { kind: "expectAfcs", collective: "ALT" }, within: 300 },
    { when: then, action: { kind: "autopilot", speed: 90 } },
    // 10. The BEADS hold: flown once, then left at the fix (MISSED-HOLD); the route ends and NAV gives way to HDG.
    { when: then, action: { kind: "expectAlert", text: "END OF ROUTE" }, within: 1200 },
    { when: then, action: { kind: "expectAfcs", roll: "HDG" }, within: 5 },
    // The crew keeps holding: NEW HOLD at BEADS with the charted values (MANUAL, 4 NM legs, right turns, inbound
    // 236°M = 222°T, 90 KIAS), DIRECT BEADS with NAV; then two circuits.
    { when: then, action: { kind: "keys", keys: ["LEGS"] } },
    { when: then, action: { kind: "type", text: "BEADS/H" } },
    { when: then, action: { kind: "keys", keys: ["LSK1L"] } },
    { when: then, action: { kind: "type", text: "222" } },
    { when: then, action: { kind: "keys", keys: ["LSK3L"] } },
    { when: then, action: { kind: "type", text: "/4" } },
    { when: then, action: { kind: "keys", keys: ["LSK4L"] } },
    { when: then, action: { kind: "type", text: "090" } },
    { when: then, action: { kind: "keys", keys: ["LSK1R", "EXEC"] } },
    { when: then, action: { kind: "expectLine", line: 6, pattern: "222°.*ARMED" } },
    { when: then, action: { kind: "autopilot", lnav: true } },
    { when: then, action: { kind: "expectLine", line: 6, pattern: "IN PROGRESS" }, within: 600 },
    { when: then, action: { kind: "expectAfcs", roll: "NAV" } },
    // Two circuits later (about 15 minutes at 90 KIAS), still in the hold under NAV.
    { when: { kind: "time", seconds: 5300 }, action: { kind: "expectLine", line: 6, pattern: "IN PROGRESS" } },
    { when: then, action: { kind: "expectAfcs", roll: "NAV" } },
  ],
};

// ---------------------------------------------------------------------------------------------- checkpoint variants

/**
 * The checkpoint variants of the acceptance mission (plan §10; the rev 3.1 addendum's deterministic named setups):
 * each starts from a named synthetic start state, not a replay of the nominal run. (a1) to (c) start from the mission
 * start and make the sighting at once; (d) to (g) start on the RNAV 190 final (87n-rnav190-final).
 */
const S = then;
const after = (seconds: number) => ({ kind: "after" as const, seconds });
// From the mission start: MARK ON TOP at once (the sighting), ACTIVATE, EXEC, downwind, then onto the final with NAV.
const markAndActivate: ScenarioStep[] = [
  { when: after(1), action: { kind: "keys", keys: ["TACT", "LSK1R", "LSK4L", "LSK6R", "EXEC"] } },
  { when: S, action: { kind: "expectAlert", text: "TRANSITION DOWN" } },
  { when: S, action: { kind: "autopilot", heading: 50 } },
  { when: after(150), action: { kind: "autopilot", heading: 200, lnav: true } },
]
const variant = (id: string, title: string, objective: string, steps: ScenarioStep[], start: StartStateId = "87n-offshore-sar", maxSeconds = 1800): Scenario => ({
  id: `87n-${id}`, title: `87N mission variant ${title}`, objective, maxSeconds, start, startTime: MISSION_87N_OFFSHORE_SAR.startTime, steps,
})
export const MISSION_87N_VARIANTS: readonly Scenario[] = [
  variant("a1-ra-invalid", "(a1) RA invalid initially: no ACTIVATE", "(a1) RA invalid initially: no ACTIVATE. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    { when: S, action: { kind: "condition", condition: "raFail", on: true } },
    { when: after(1), action: { kind: "keys", keys: ["TACT", "LSK1R", "LSK4L"] } },
    { when: S, action: { kind: "expectLine", line: 4, pattern: "^\\s*----FT" } },
    { when: S, action: { kind: "expectLine", line: 12, pattern: "^(?!.*ACTIVATE).*$" } },
  ]),
  variant("a2-ra-lost-before-exec", "(a2) RA valid at ACTIVATE, lost before EXEC: RALT FAILED, EXEC refused, MOD kept", "(a2) RA valid at ACTIVATE, lost before EXEC: RALT FAILED, EXEC refused, MOD kept. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    { when: after(1), action: { kind: "keys", keys: ["TACT", "LSK1R", "LSK4L", "LSK6R"] } },
    { when: S, action: { kind: "expectLine", line: 0, pattern: "MOD.*HOVER" } },
    { when: S, action: { kind: "condition", condition: "raFail", on: true } },
    { when: S, action: { kind: "keys", keys: ["EXEC"] } },
    { when: S, action: { kind: "expectAlert", text: "RALT FAILED" } },
    { when: S, action: { kind: "expectNoAlert", text: "TRANSITION DOWN" } },
    { when: S, action: { kind: "expectLine", line: 0, pattern: "MOD.*HOVER" } },
  ]),
  variant("a3-ra-lost-in-td", "(a3) RA lost during TD at about 350 ft: ALT latched, TD pitch continues, TDN FUNCTION LOST", "(a3) RA lost during TD at about 350 ft: ALT latched, TD pitch continues, TDN FUNCTION LOST. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    ...markAndActivate,
    { when: S, action: { kind: "expectAfcs", collective: "TD", pitch: "TD" }, within: 900 },
    // TD descends from 500 ft at 500 fpm: about 350 ft after 18 s.
    { when: after(18), action: { kind: "condition", condition: "raFail", on: true } },
    { when: S, action: { kind: "expectAfcs", collective: "ALT", pitch: "TD" }, within: 2 },
    { when: S, action: { kind: "expectAlert", text: "TDN FUNCTION LOST" }, within: 2 },
  ]),
  variant("a4-ra-lost-in-hover", "(a4) RA lost in the hover: ALT latched, HOV continues", "(a4) RA lost in the hover: ALT latched, HOV continues. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    ...markAndActivate,
    { when: S, action: { kind: "expectAfcs", collective: "RHT", pitch: "HOV", roll: "HOV" }, within: 900 },
    { when: after(30), action: { kind: "condition", condition: "raFail", on: true } },
    { when: S, action: { kind: "expectAfcs", collective: "ALT", pitch: "HOV", roll: "HOV" }, within: 2 },
    { when: after(60), action: { kind: "expectAircraft", near: "MRK", nearMetres: 10, maxGroundSpeed: 1 } },
  ]),
  variant("b-tdn-off-track", "(b) TDN reached 0.3 NM off the final track: TDN NOT POSSIBLE, NAV gives way to HDG", "(b) TDN reached 0.3 NM off the final track: TDN NOT POSSIBLE, NAV gives way to HDG. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    { when: after(1), action: { kind: "keys", keys: ["TACT", "LSK1R", "LSK4L", "LSK6R", "EXEC"] } },
    { when: S, action: { kind: "autopilot", heading: 50 } },
    { when: after(120), action: { kind: "autopilot", heading: 140 } },
    { when: after(35), action: { kind: "autopilot", heading: 230 } },
    // NAV armed late, within 0.6 NM of TDN while still about 0.4 NM off the final track: it captures and is still correcting.
    { when: { kind: "distance", waypoint: "TDN", nm: 0.6 }, action: { kind: "autopilot", lnav: true } },
    { when: S, action: { kind: "expectAlert", text: "TDN NOT POSSIBLE" }, within: 60 },
    { when: S, action: { kind: "expectAfcs", roll: "HDG" }, within: 2 },
  ]),
  variant("b2-tdn-high", "(b2) TDN reached 400 ft high: TDN DIST SHORT", "(b2) TDN reached 400 ft high: TDN DIST SHORT. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    ...markAndActivate,
    { when: S, action: { kind: "autopilot", altitude: 900, verticalSpeed: 800 } },
    { when: S, action: { kind: "expectAlert", text: "TDN DIST SHORT" }, within: 900 },
    { when: S, action: { kind: "expectAfcs", roll: "HDG" }, within: 2 },
  ]),
  variant("c-hover-feedback-lost", "(c) HOV feedback lost on both receivers, then a 5 kt wind change: ATT holds the air velocity", "(c) HOV feedback lost on both receivers, then a 5 kt wind change: ATT holds the air velocity. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    ...markAndActivate,
    { when: S, action: { kind: "expectAfcs", collective: "RHT", pitch: "HOV", roll: "HOV" }, within: 900 },
    { when: after(30), action: { kind: "gps", receiver: 1, stimulus: { op: "override", label: "166", kind: "FORCE", amount: 0, ssm: "FW" } } },
    { when: S, action: { kind: "gps", receiver: 2, stimulus: { op: "override", label: "166", kind: "FORCE", amount: 0, ssm: "FW" } } },
    { when: S, action: { kind: "expectAfcs", collective: "RHT", pitch: "ATT", roll: "ATT" }, within: 2 },
    { when: S, action: { kind: "wind", direction: 230, speed: 25 } },
    // The truth drifts with the wind change while the modes report the loss.
    { when: after(60), action: { kind: "expectAfcs", pitch: "ATT", roll: "ATT" } },
  ]),
  variant("d-early-toga", "(d) TOGA 1.5 NM before CRANN: the lateral path is kept to CRANN, then the missed approach", "(d) TOGA 1.5 NM before CRANN: the lateral path is kept to CRANN, then the missed approach. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    { when: { kind: "distance", waypoint: "CRANN", nm: 1.5 }, action: { kind: "goAround" } },
    { when: S, action: { kind: "autopilot", altitude: 2000 } },
    { when: after(10), action: { kind: "expectActive", waypoint: "CRANN" } },
    { when: S, action: { kind: "expectAfcs", roll: "NAV" } },
    { when: S, action: { kind: "expectActive", waypoint: "BEADS" }, within: 180 },
  ], "87n-rnav190-final"),
  variant("e-direct-on-final", "(e) Crew direct-to during the final: immediate", "(e) Crew direct-to during the final: immediate. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    { when: { kind: "distance", waypoint: "STAYS", nm: 1 }, action: { kind: "keys", keys: ["LEGS", "CHAR_B", "CHAR_E", "CHAR_A", "CHAR_D", "CHAR_S", "LSK1L", "EXEC"] } },
    { when: S, action: { kind: "expectActive", waypoint: "BEADS" } },
    { when: S, action: { kind: "expectAfcs", roll: "NAV" }, within: 5 },
  ], "87n-rnav190-final"),
  variant("f-proceed-vfr", "(f) Proceed VFR: DIRECT 87N at CRANN", "(f) Proceed VFR: DIRECT 87N at CRANN. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    { when: { kind: "distance", waypoint: "CRANN", nm: 0.2 }, action: { kind: "keys", keys: ["LEGS", "CHAR_8", "CHAR_7", "CHAR_N", "LSK1L", "EXEC"] } },
    { when: S, action: { kind: "expectActive", waypoint: "87N" } },
    { when: S, action: { kind: "expectAfcs", roll: "NAV" }, within: 5 },
  ], "87n-rnav190-final"),
  variant("g-integrity-on-final", "(g) GPS integrity lost on the final: GPS POS UNCERTAIN", "(g) GPS integrity lost on the final: GPS POS UNCERTAIN. A checkpoint variant of the acceptance mission (plan §10), from a named synthetic start.", [
    { when: { kind: "distance", waypoint: "STAYS", nm: 1 }, action: { kind: "gps", receiver: 1, stimulus: { op: "override", label: "130", kind: "FORCE", amount: 2 } } },
    { when: S, action: { kind: "gps", receiver: 2, stimulus: { op: "override", label: "130", kind: "FORCE", amount: 2 } } },
    { when: S, action: { kind: "expectAlert", text: "GPS POS UNCERTAIN" }, within: 10 },
  ], "87n-rnav190-final"),
]

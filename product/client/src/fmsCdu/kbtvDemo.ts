import { KBTV_CIFP_2609 } from "./data/kbtvCifp2609";
import type { FlightSimulator } from "./flight";
import { courseDeg, offset } from "./fmsModel";
import { setUp87nOffshoreSar, setUp87nRnav190Final } from "./heliDemo";
import type { ScriptedFms } from "./scriptedFms";

/**
 * The real-data demonstration: Burlington, Vermont (KBTV) from the FAA's CIFP cycle 2609, bundled with the client
 * (data/kbtvCifp2609.ts), and its RNAV (GPS) RWY 15 approach flown on the published FAS. The invented CYUL demonstration
 * stays the default start; this is an action on the Nav data tab and a start state for scenarios.
 */

/** How the bench and the run reports name the bundled data: real, public domain, and not for navigation. */
export const KBTV_SOURCE = "FAA CIFP 2609, KBTV extract: real public-domain data, for demonstration only, not for navigation";

/**
 * Loads the bundled KBTV extract as the inactive cycle and activates it, as the Nav data tab's file load and Activate
 * do. The loaded cycle is the built-in data with KBTV merged in, so the demonstration route still resolves. Loading
 * again when it is already active changes nothing.
 */
export function loadKbtvDemonstration(fms: ScriptedFms): { loaded: string; already?: boolean } | { refused: string } {
  if (fms.activeCycle.source === KBTV_SOURCE) return { loaded: fms.activeCycle.id, already: true };
  const outcome = fms.loadArinc424(KBTV_CIFP_2609, KBTV_SOURCE);
  if ("refused" in outcome) return outcome;
  fms.swapCycles();
  return { loaded: outcome.loaded };
}

/** How far before STAEV, the intermediate fix, the set-up places the aircraft: on the approach course, NM. */
export const KBTV_START_BEFORE_STAEV_NM = 8;
/** The altitude it starts at: STAEV's published minimum, 3200 ft (the approach's IF altitude). */
export const KBTV_START_ALTITUDE_FT = 3200;

/**
 * The start state for KBTV RNAV (GPS) RWY 15: the KBTV data active; KBTV the destination; the R15 approach via the STAEV
 * transition executed; the aircraft 8 NM before STAEV on the approach course (STAEV to FOVES, extended back) at 3200 ft,
 * tracking toward STAEV, level; DIRECT-TO STAEV executed; the approach armed.
 *
 * Why a start state and not Jumps: Jump sequences the demonstration route waypoint by waypoint and needs an
 * engineering override at the discontinuity before the approach, and it leaves the aircraft at a fix with no
 * established intercept. Here the aircraft is placed once (placeAircraft, recorded in the engineering log) and the
 * route is built with the crew's own controls (destination, approach, DIRECT-TO, EXEC), so the flight from there on is
 * flown and checked like any other: the intercept, the capture at the FAF (FOVES) and the published path.
 */
export function setUpKbtvRnav15(fms: ScriptedFms, sim?: FlightSimulator): { ready: true } | { refused: string } {
  const loaded = loadKbtvDemonstration(fms);
  if ("refused" in loaded) return loaded;
  fms.modify(route => { route.dest = "KBTV"; });
  fms.press("EXEC");
  fms.selectProcedure("APPROACH", "R15", "STAEV");
  fms.press("EXEC");
  const staev = fms.coordinates("STAEV"), foves = fms.coordinates("FOVES");
  if (!staev || !foves) return { refused: "STAEV or FOVES is not in the active navigation data" };
  const course = courseDeg(staev, foves);
  fms.placeAircraft({ position: offset(staev, course + 180, KBTV_START_BEFORE_STAEV_NM), track: course, altitude: KBTV_START_ALTITUDE_FT },
    "KBTV RNAV (GPS) RWY 15 set-up");
  // DIRECT-TO from where the aircraft now is, as a crew cleared direct to the IF would do.
  fms.directTo("STAEV");
  fms.press("EXEC");
  if (fms.aircraftProfile.verticalPolicy === "ADVISORY") {
    // The crew descends it; without the flight simulation there is no autopilot to set, so the start state is refused.
    if (!sim) return { refused: "the helicopter profile start state needs the flight simulation (autopilot selections)" };
    // The helicopter profile: the crew preselects the FAF altitude and descends to it in VS, so the aircraft is level
    // at the FAF altitude. S300 remains LNAV/advisory; LPV capture exists only in the later-SBAS profile.
    sim?.selectAltitude(fms.fafAltitudeCorrected);
    sim?.engageVerticalSpeed(-500);
  } else if (!fms.profile().descending) {
    // The laboratory airline-style VNAV: DES NOW (VNAV page 2), as the crew would at the IF altitude, so the descent
    // starts here rather than after a climb back to the demonstration's cruise altitude and a top of descent.
    fms.vnav.desNow = true;
  }
  fms.armApproach(true);
  return { ready: true };
}

/** Start states a scenario can name (scenario.ts): each sets up a fresh simulation before the first step. */
export const START_STATES = {
  "kbtv-rnav15": { label: "KBTV RNAV (GPS) RWY 15: FAA CIFP 2609, 8 NM before STAEV at 3200 ft, approach armed (S300 LNAV/advisory; later profile LPV)", setUp: setUpKbtvRnav15 },
  "87n-offshore-sar": { label: "87N offshore SAR (synthetic): FAA CIFP 2609 Copter PinS, 10 NM south of 87N at 500 ft, 100 KIAS, wind 230/20, SAR datum set", setUp: setUp87nOffshoreSar },
  "87n-rnav190-final": { label: "87N COPTER RNAV 190 final (synthetic): 3 NM before STAYS at 1700 ft, 70 KIAS, NAV and approach armed", setUp: setUp87nRnav190Final },
} as const satisfies Record<string, { label: string; setUp: (fms: ScriptedFms, sim?: FlightSimulator) => { ready: true } | { refused: string } }>;

export type StartStateId = keyof typeof START_STATES;

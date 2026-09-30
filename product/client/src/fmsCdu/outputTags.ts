import type { AircraftData, FmsOutputs } from "./efis";
import type { FlightSimulator } from "./flight";
import type { ScriptedFms } from "./scriptedFms";

// Plan rev 3 A5 (FMS_APPLICABILITY.md, "Provenance, validity, selection and engagement"): what every display output is,
// kept distinct in place of one advisory/selected/coupled label.
//
// - A data word carries its provenance (which sensor or computation) and its validity (the word's own status, or how
//   the value says it is missing).
// - A target or datum records who selects it: the crew, the FMS, or the procedure the FMS flies.
// - Controller state is reported per autopilot axis: engaged, armed and degraded modes.
// - Coupling is an engaged axis mode consuming an FMS output; advisory is an FMS output that no engaged mode consumes.
//
// The catalogues are typed over every key of the output bus and the aircraft data, so an output added to either does
// not compile until it is tagged here. outputEngagement() derives coupling from the engaged modes, not from the bus's
// own flags, so the owner test can hold the two against each other (fms-output-tags.spec.ts).

/** How an output's validity reaches the display. */
export type Validity =
  /** A Word: NORMAL, NCD or FAIL beside the value. */
  | "word"
  /** The value is null (or a null field inside it) when it is not available; there is no separate status. */
  | "null when unavailable"
  /** Always valid in this bench: a laboratory simplification, declared (rev 3 F10 for baro, heading and attitude). */
  | "always valid (laboratory)";

export type OutputTag =
  | { kind: "data"; provenance: string; validity: Validity }
  | { kind: "target"; selectedBy: "crew" | "FMS" | "FMS or procedure"; validity: Validity }
  /** Per-axis controller state: the modes engaged, armed or degraded, or the approach's arming. */
  | { kind: "controller"; state: "engaged" | "armed" | "degraded" | "approach" }
  /** What the displays annunciate about the data rather than a value: its source, scaling, phase or failure. */
  | { kind: "annunciation"; provenance: string }
  /** The plan's geometry for the navigation display. */
  | { kind: "plan"; provenance: string };

const data = (provenance: string, validity: Validity = "word"): OutputTag => ({ kind: "data", provenance, validity });
const annunciation = (provenance: string): OutputTag => ({ kind: "annunciation", provenance });
const plan = (provenance: string): OutputTag => ({ kind: "plan", provenance });

/** Every word and field of the FMS output bus (efis.ts fmsOutputs). */
export const FMS_OUTPUT_TAGS: { [K in keyof FmsOutputs]: OutputTag } = {
  source: annunciation("the FMS the displays show (FMS1)"),
  failed: annunciation("the FMS's own failure state"),
  angleReference: annunciation("the FMS's angular display reference (MAG or TRUE; the crew selects it, forced TRUE in the polar region)"),
  magneticVariation: data("the FMS's magnetic model (WMM2025) at the aircraft's position"),
  desiredTrack: data("FMS lateral guidance (label 114)"),
  crossTrack: data("FMS lateral guidance, or the selected GPS's 116 on an RNAV final (label 116)"),
  lateralSource: annunciation("which of the route geometry or the selected GPS the lateral deviation comes from"),
  verticalDeviation: data("FMS VNAV path, or the selected GPS's 117 on an RNAV final (label 117)"),
  verticalSource: annunciation("which of VNAV or the approach the vertical deviation comes from"),
  verticalCoupled: annunciation("the bus's own statement that the vertical deviation is being flown"),
  rollCommand: data("FMS lateral guidance: the roll steering command (label 121)"),
  distanceToGo: data("FMS lateral guidance: distance to the active waypoint (label 251)"),
  toWaypoint: data("FMS active leg"),
  eta: data("FMS predictions"),
  targetSpeed: { kind: "target", selectedBy: "FMS or procedure", validity: "word" },
  targetAltitude: { kind: "target", selectedBy: "FMS or procedure", validity: "word" },
  lateralMode: { kind: "controller", state: "engaged" },
  lateralArmed: { kind: "controller", state: "armed" },
  verticalMode: { kind: "controller", state: "engaged" },
  verticalArmed: { kind: "controller", state: "armed" },
  approach: { kind: "controller", state: "approach" },
  lateralFullScaleNm: annunciation("the phase's full scale, or the selected GPS's angular scaling on an RNAV final"),
  verticalFullScaleFt: annunciation("the VNAV or approach full scale, or the selected GPS's angular scaling"),
  phase: annunciation("FMS flight phase"),
  rnp: annunciation("FMS required navigation performance"),
  anp: annunciation("FMS actual navigation performance, from the navigation solution"),
  navMode: annunciation("FMS navigation mode (the sensors in use)"),
  activeRoute: plan("the active flight plan"),
  modifiedRoute: plan("the modification pending EXEC"),
  offsetTrack: plan("the executed parallel offset"),
  holdFix: plan("the hold's fix"),
  topOfDescent: plan("FMS VNAV profile"),
  endOfDescent: plan("FMS VNAV profile"),
};

type HelicopterData = NonNullable<AircraftData["helicopter"]>;

/** Every field the EFIS takes from the aircraft rather than the FMS (efis.ts aircraftData). */
export const AIRCRAFT_DATA_TAGS: { [K in Exclude<keyof AircraftData, "helicopter">]: OutputTag } = {
  pitch: data("the flight-path angle (a point-mass model has no attitude of its own; display only)", "always valid (laboratory)"),
  bank: data("the simulated aircraft's bank", "always valid (laboratory)"),
  heading: data("the simulated aircraft's heading", "always valid (laboratory)"),
  track: data("the simulated aircraft's ground track", "always valid (laboratory)"),
  airspeed: data("air data: true airspeed", "always valid (laboratory)"),
  groundSpeed: data("the simulated aircraft's ground speed", "always valid (laboratory)"),
  altitude: data("air data: the barometric altitude as indicated with the crew's setting (baro.ts)", "always valid (laboratory)"),
  baroSetting: { kind: "target", selectedBy: "crew", validity: "always valid (laboratory)" },
  physicalAltitude: data("truth: the simulated aircraft's physical height (the out-the-window view and synthetic vision)", "always valid (laboratory)"),
  verticalSpeed: data("the simulated aircraft's vertical speed", "always valid (laboratory)"),
  wind: data("the simulated air mass (the bench's declared wind)", "always valid (laboratory)"),
  position: data("the simulated aircraft's true position (for the map's own-ship symbol)", "always valid (laboratory)"),
  selectedAltitude: { kind: "target", selectedBy: "crew", validity: "null when unavailable" },
  selectedSpeed: { kind: "target", selectedBy: "crew", validity: "null when unavailable" },
  selectedHeading: { kind: "target", selectedBy: "crew", validity: "always valid (laboratory)" },
  missedAltitudeConflict: annunciation("the selected altitude against the missed approach's coded altitude"),
  ias: data("air data: indicated airspeed from the true airspeed (ISA)", "null when unavailable"),
};

/** The helicopter profile's autopilot and hover data (efis.ts aircraftData, helicopter). */
export const HELICOPTER_DATA_TAGS: { [K in keyof HelicopterData]: OutputTag } = {
  axes: { kind: "controller", state: "engaged" },
  armed: { kind: "controller", state: "armed" },
  degraded: { kind: "controller", state: "degraded" },
  lowSpeed: annunciation("the autopilot's low-speed regime (TD/H, HOV, GSPD, TU): the ND draws the ground velocity instead of the trend"),
  radioHeight: data("the radio altimeter, over the declared surface", "word"),
  hoverHeight: { kind: "target", selectedBy: "crew", validity: "always valid (laboratory)" },
  lowHeight: annunciation("the autopilot's low-height protection caption"),
  vx: data("the selected GPS receiver's velocity words, in aircraft axes (the hover feedback)", "null when unavailable"),
  vy: data("the selected GPS receiver's velocity words, in aircraft axes (the hover feedback)", "null when unavailable"),
  selectedVelocity: { kind: "target", selectedBy: "crew", validity: "null when unavailable" },
  hoverData: annunciation("whether the low-speed data belongs on the display"),
};

/** The FMS outputs an engaged autopilot mode can consume, and the FMS's transition request (label 121 and the rest). */
export type CouplableOutput = "rollCommand" | "verticalDeviation" | "targetAltitude" | "targetSpeed" | "transitionRequest";
/** Coupled: an engaged mode consumes it. Advisory: valid, and nothing engaged consumes it. No data: not published. */
export type Engagement = "coupled" | "advisory" | "no data";

const VNAV_MODES = new Set(["VNAV PTH", "VNAV CLB", "VNAV DES", "VNAV ALT", "DES NOW"]);

/**
 * Coupling, from the engaged modes: NAV on the roll axis consumes the roll command; APPR or VNAV PTH on the vertical
 * axis, the vertical deviation (DES NOW descends at its own rate until it meets the path, so the deviation is then
 * advisory); a VNAV mode the target altitude; the FMS speed mode the target speed; the
 * transition the autopilot is flying (TD, the gate segment with TD/H to follow, TD/H) the FMS's transition request.
 */
export function outputEngagement(outputs: FmsOutputs, fms: ScriptedFms, sim: FlightSimulator): Record<CouplableOutput, Engagement> {
  const vertical = sim.verticalMode;
  const axes = sim.advisory ? sim.axisModes : null;
  const word = (status: "NORMAL" | "NCD" | "FAIL", consumed: boolean): Engagement => (status !== "NORMAL" ? "no data" : consumed ? "coupled" : "advisory");
  const navEngaged = axes ? axes.roll === "NAV" : sim.lateralMode === "LNAV";
  const request = fms.hover.requestData;
  return {
    rollCommand: word(outputs.rollCommand.status, navEngaged),
    verticalDeviation: word(outputs.verticalDeviation.status, vertical === "APPR" || vertical === "VNAV PTH"),
    targetAltitude: word(outputs.targetAltitude.status, vertical !== null && VNAV_MODES.has(vertical)),
    targetSpeed: word(outputs.targetSpeed.status, !sim.advisory),
    transitionRequest: !request ? "no data" : sim.transitionInProgress === request.id ? "coupled" : "advisory",
  };
}

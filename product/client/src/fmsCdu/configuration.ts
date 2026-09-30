/** Declared civil SAR target, DEC-147. An enabled option is not a claim that its behavior is implemented.
 * References are paraphrased dispositions; no operator-manual text is distributed with the bench. */
export type ConfiguredOption = {
  title: string;
  configured: boolean;
  implementation: "implemented" | "partial" | "pending" | "off";
  source: string;
  reason: string;
};

const on = (title: string, source: string, implementation: ConfiguredOption["implementation"], reason = "civil SAR target"): ConfiguredOption =>
  ({ title, configured: true, implementation, source, reason });
const off = (title: string, source: string, reason: string): ConfiguredOption =>
  ({ title, configured: false, implementation: "off", source, reason });

const options = {
  planData: on("Cruise planning data", "M300 3-19, A-105", "implemented"),
  fuelPlan: on("Fuel planning and manual fuel inputs", "M300 3-20, 14-2", "partial", "current-flow predictions; attributable aircraft performance tables remain later work"),
  efis: on("EFIS outputs and symbol selection", "M300 3-22", "partial", "generic bench EFIS, not an installed display"),
  satelliteDeselection: on("GPS satellite deselection", "M300 3-23, 5-28", "implemented", "predictive RAIM exclusions only; live receiver tracking is independent"),
  fuelKey: on("Fuel function key", "M300 3-20, 14-2", "implemented"),
  magTrue: on("MAG/TRUE reference selection", "M300 3-9", "implemented", "consumed NOAA WMM2025; true internal geometry and wind; simulator table format"),
  backtrack: on("Backtrack of flown history", "M300 11-35/36", "implemented", "actual waypoint passage snapshots, default airborne option 1, explicit live ground input and 50 temporary-waypoint bound"),
  rotorEndpoints: on("Rotorcraft waypoint endpoints", "M300 3-14, A-107", "implemented"),
  fixedWing: off("Fixed-wing configuration", "M300 A-107", "helicopter first; fixed wing deferred"),
  efisPersistence: on("Persistent EFIS symbol selections", "M300 3-22", "pending"),
  timer: on("Crew timer", "M300 5-39", "implemented"),
  qnhReference: on("Computed QNH reference", "M300 5-18", "pending", "requires valid pressure and hybrid altitude inputs"),
  approachDiscrete: on("Approach-enabled discrete output", "M300 7-12", "pending", "timestamped guidance interface; not a physical output today"),
  flightLog: on("Takeoff and landing times", "M300 8-2", "pending"),
  sar: on("Civil search patterns", "M300 11-1", "implemented"),
  mark: on("Mark on top", "M300 11-16", "implemented"),
  hover: on("Transition down to hover", "M300 11-18", "implemented", "declared laboratory join and representative AFCS"),
  predef: on("Predefined waypoint pages", "M300 A-117 to A-122", "partial", "mark/hover functions implemented; external equipment inputs off"),
  centralClear: on("Central user-data clear", "M300 11-34", "pending", "requires coherent per-computer user database and confirmation"),
  movingIntercept: on("Rendezvous with a moving waypoint", "M300 11-37", "implemented", "trajectory intercept, 10-second recompute/one-minute freeze, four 500-NM conditions; ground uses entered CRZ TAS/WIND; straight-course laboratory solver"),
  movingWaypoint: on("Moving waypoint information", "M300 A-242", "partial", "moving positions exist; persistent moving-user entries remain pending"),
  predictiveRaim: on("Predictive RAIM requests", "M300 5-27", "partial", "IDENT/ETA intervals from simulated sky; no actual GPS almanac or installation-specific failure prediction"),
  dualFms: on("Two independent FMS computers", "M300 3-23 to 3-26", "implemented", "two computers/CDUs and measured navigation; modeled cross-talk; installation-specific synchronized interfaces remain absent"),
  rms: on("Radio management system", "M300 13-1, 3-26", "partial", "shared civil tuning devices, acknowledgements, timeout and standby cross-talk; no installed radio bus or RF/channel model"),
  com1: on("VHF COM 1", "M300 13-16", "partial"),
  com2: on("VHF COM 2", "M300 13-16", "partial"),
  nav1: on("NAV 1", "M300 13-16", "partial"),
  nav2: on("NAV 2", "M300 13-16", "partial"),
  atc1: on("ATC transponder 1", "M300 13-16", "partial", "transponder radio control, not an ATC datalink"),
  atc2: on("ATC transponder 2", "M300 13-16", "partial", "shared code tuning and device feedback; no installed transponder mode or RF replies"),
  adf1: on("ADF 1", "M300 13-16", "partial", "shared frequency tuning and feedback; no direction-finding measurement"),
  adf2: on("ADF 2", "M300 13-16", "partial", "shared frequency tuning and feedback; no direction-finding measurement"),
  radioKeyPaging: on("RADIO key page cycling", "M300 13-19", "pending"),
  vorAutoTune: on("VOR AUTO tuning selection", "M300 13-21", "pending"),
  radioVolume: on("Radio volume control", "M300 16-2", "pending"),
  missedPrompt: on("Missed-approach crew prompt", "M300 A-132, A-165, A-238", "implemented"),
  adfControlAlert: on("ADF control-loss annunciation", "M300 E-2", "pending", "RMS feedback with the manual's roll/polar applicability"),
  fuelComputer: off("External fuel computer", "M300 A-54", "manual fuel input selected; no external fuel computer declared"),
  fm1: off("FM radio 1", "M300 13-16", "dedicated FM equipment not selected for this civil configuration"),
  fm2: off("FM radio 2", "M300 13-16", "dedicated FM equipment not selected for this civil configuration"),
  uhf: off("UHF radio", "M300 13-16", "owner decision"),
  vuhf: off("V/UHF radio", "M300 13-16", "owner decision"),
  maritimeVuhf: off("V/UHF maritime mode", "M300 13-36", "its parent V/UHF radio is off"),
  upperUhf: off("Upper UHF band", "M300 13-36", "its parent UHF equipment is off"),
  iff: off("IFF power control", "M300 13-19", "military equipment off"),
  military: off("Military navigation", "M300 1-3", "owner decision"),
  bullsEye: off("Bulls-eye functions", "M300 11-41, A-6", "military functions off"),
  tacticalApproach: off("Military tactical approach and its database", "M300 11-47, A-197 to A-210", "military functions off; civil hover/TDN remain on"),
  carpHarp: off("CARP/HARP", "M300 11-62 to 11-85", "owner decision"),
  cospas: off("COSPAS/SARSAT beacon handling", "M300 11-86", "owner decision"),
  externalTarget: off("ATOS/radar target reception", "M300 11-88", "no external ATOS or radar equipment selected; moving targets use bench input"),
  joystick: off("External joystick position", "M300 11-18, 11-31", "no external radar/AHCAS joystick selected"),
  ess: off("External MCDU subsystem control", "M300 16-1", "no external ARINC 739 subsystem selected"),
  irs: off("EGI/IRS and its maintenance information", "M300 A-88 to A-89", "civil heading/TAS/wind DR selected; no inertial equipment declared"),
  doppler: off("Doppler navigation", "M300 1-3", "no Doppler equipment selected"),
} satisfies Record<string, ConfiguredOption>;

/** All 87 literal 'if configured' occurrences, including contents references and repetitions.
 * Tuple: printed page, local extraction page, extraction line, option. Repeated text maps to shared semantics.
 * The two occurrences at A-107 line 7 refer separately to ROTOR and FIX. */
const references: readonly [string, number, number, keyof typeof options][] = [
  ["3-i", 51, 24, "planData"], ["3-i", 51, 25, "fuelPlan"], ["3-i", 51, 26, "fuelPlan"],
  ["3-i", 51, 27, "efis"], ["3-i", 51, 28, "satelliteDeselection"], ["3-2", 54, 10, "fuelPlan"],
  ["3-5", 57, 11, "tacticalApproach"], ["3-9", 61, 5, "magTrue"], ["3-14", 66, 26, "backtrack"],
  ["3-14", 66, 34, "rotorEndpoints"], ["3-20", 72, 19, "fuelKey"], ["3-22", 74, 39, "efisPersistence"],
  ["5-ii", 90, 10, "timer"], ["5-ii", 90, 11, "timer"], ["5-18", 108, 5, "qnhReference"],
  ["7-12", 152, 7, "approachDiscrete"], ["8-i", 169, 7, "flightLog"], ["8-2", 172, 3, "flightLog"],
  ["11-i", 201, 6, "sar"], ["11-i", 201, 20, "mark"], ["11-i", 201, 21, "hover"],
  ["11-i", 201, 33, "centralClear"], ["11-i", 201, 34, "backtrack"], ["11-i", 201, 35, "movingIntercept"],
  ["11-i", 201, 42, "bullsEye"], ["11-i", 201, 43, "tacticalApproach"], ["11-ii", 202, 9, "carpHarp"],
  ["11-ii", 202, 10, "carpHarp"], ["11-ii", 202, 11, "carpHarp"], ["11-ii", 202, 24, "cospas"],
  ["11-ii", 202, 25, "externalTarget"], ["11-18", 220, 7, "joystick"], ["11-31", 233, 26, "joystick"],
  ["11-49", 251, 29, "planData"], ["11-62", 264, 30, "carpHarp"], ["11-88", 290, 3, "externalTarget"],
  ["12-12", 308, 31, "predef"], ["13-16", 344, 19, "com1"], ["13-16", 344, 20, "nav1"],
  ["13-16", 344, 21, "atc1"], ["13-16", 344, 22, "fm1"], ["13-16", 344, 24, "com2"],
  ["13-16", 344, 25, "nav2"], ["13-16", 344, 26, "adf1"], ["13-16", 344, 27, "fm2"],
  ["13-16", 344, 28, "uhf"], ["13-16", 344, 29, "vuhf"], ["13-16", 344, 31, "atc2"],
  ["13-16", 344, 32, "adf2"], ["13-19", 347, 25, "radioKeyPaging"], ["13-19", 347, 27, "iff"],
  ["13-21", 349, 31, "vorAutoTune"], ["13-36", 364, 30, "maritimeVuhf"], ["13-36", 364, 33, "upperUhf"],
  ["14-2", 418, 5, "fuelKey"], ["16-i", 431, 6, "ess"], ["16-1", 433, 5, "ess"],
  ["16-2", 434, 9, "radioVolume"], ["A-ii", 444, 30, "planData"], ["A-ii", 444, 34, "predef"],
  ["A-ii", 444, 35, "predef"], ["A-ii", 444, 36, "predef"], ["A-iii", 445, 17, "tacticalApproach"],
  ["A-iii", 445, 18, "tacticalApproach"], ["A-iii", 445, 19, "tacticalApproach"], ["A-iii", 445, 20, "tacticalApproach"],
  ["A-6", 452, 6, "bullsEye"], ["A-54", 500, 47, "fuelComputer"], ["A-83", 529, 6, "sar"],
  ["A-83", 529, 21, "sar"], ["A-88", 534, 27, "irs"], ["A-89", 535, 10, "irs"],
  ["A-107", 553, 7, "rotorEndpoints"], ["A-107", 553, 7, "fixedWing"], ["A-132", 578, 8, "missedPrompt"],
  ["A-140", 586, 42, "planData"], ["A-141", 587, 5, "planData"], ["A-142", 588, 23, "planData"],
  ["A-142", 588, 27, "planData"], ["A-147", 593, 41, "tacticalApproach"], ["A-147", 593, 48, "tacticalApproach"],
  ["A-164", 610, 21, "tacticalApproach"], ["A-164", 610, 23, "tacticalApproach"], ["A-165", 611, 12, "missedPrompt"],
  ["A-238", 684, 40, "missedPrompt"], ["A-242", 688, 10, "movingWaypoint"], ["E-2", 752, 15, "adfControlAlert"],
];

export const CIVIL_SAR_CONFIGURATION = {
  id: "civil-sar-s300",
  version: 1,
  options,
  occurrences: references.map(([page, extractionPage, extractionLine, option], index) =>
    ({ id: index + 1, page, extractionPage, extractionLine, option })),
};

export type AircraftConfiguration = typeof CIVIL_SAR_CONFIGURATION;

/** Included in the run identity so configured target capabilities cannot be mistaken for completed behavior. */
export function configurationSummary(configuration: AircraftConfiguration) {
  const enabled = Object.values(configuration.options).filter(option => option.configured);
  const implemented = enabled.filter(option => option.implementation === "implemented").length;
  const partial = enabled.filter(option => option.implementation === "partial").length;
  return `${configuration.id}: ${configuration.occurrences.length} configured references resolved; ${enabled.length} options on (${implemented} implemented, ${partial} partial, ${enabled.length - implemented - partial} pending)`;
}

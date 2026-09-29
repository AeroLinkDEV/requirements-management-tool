/**
 * The aircraft profile the bench simulates: one versioned, declared configuration of the CMA-9000 and the aircraft
 * around it, so every run names what it was a run of.
 *
 * The helicopter-first plan (Stage A) makes the bench a rotorcraft CMA-9000: the helicopter/military operational
 * program S/W 169-614876-300 (Operator's Manual Pub. 9000-GEN-0150, Rev 2) is the behavioural baseline, cited as
 * "M300 <page>". Which behaviour comes from which source, and where the bench deliberately differs, is in
 * product/docs/FMS_APPLICABILITY.md.
 *
 * Stage A declares the profile; it does not yet change behaviour. Each parameter says whether the simulation already
 * flies that value (`inForce`) or whether it is declared for a later stage, so the bench and the run report never
 * claim a value the simulation does not use. A parameter's basis is "sourced" (a CMA manual page or a regulation),
 * "borrowed" (a value from a cited public AW189 description, used as representative and not as an installed
 * controller) or "lab" (a laboratory choice).
 */

export type ParameterBasis = "sourced" | "borrowed" | "lab";

export type ProfileParameter = {
  value: number;
  unit: string;
  basis: ParameterBasis;
  /** The page, document or reason behind the value. */
  source: string;
  /** Whether the simulation uses this value today. False: declared for the stage named in `stage`. */
  inForce: boolean;
  stage?: "B" | "C" | "D" | "E";
};

export type AircraftProfile = {
  id: string;
  version: number;
  title: string;
  aircraftType: "ROTOR" | "FIX";
  operationalProgram: string;
  navigationOption: "CIVIL" | "MILITARY";
  /** How the active error limit is set (M300 1-3: phase of flight or RNP). */
  errorLimit: "RNP" | "PHASE";
  equipment: readonly string[];
  missionFunctions: readonly string[];
  /** The vertical-guidance policy (plan A4). */
  verticalGuidance: {
    enRoute: string;
    approach: string;
    sbasFinals: string;
  };
  /** Configurable CMA functions deliberately off (plan D6). */
  notConfigured: readonly string[];
  parameters: Readonly<Record<string, ProfileParameter>>;
};

const p = (value: number, unit: string, basis: ParameterBasis, source: string, inForce: boolean, stage?: ProfileParameter["stage"]): ProfileParameter =>
  ({ value, unit, basis, source, inForce, ...(stage ? { stage } : {}) });

export const HELICOPTER_PROFILE: AircraftProfile = {
  id: "cma9000-s300-heli-civil",
  version: 1,
  title: "CMA-9000 helicopter, civil navigation (S/W -300 baseline)",
  aircraftType: "ROTOR",
  operationalProgram: "169-614876-300 (M300, Pub. 9000-GEN-0150 Rev 2)",
  navigationOption: "CIVIL",
  errorLimit: "RNP",
  equipment: ["2 × CMA-5024 GPS/SBAS", "1 radio altimeter (declared; Stage B)", "representative rotorcraft AFCS (declared; Stage B)"],
  missionFunctions: ["HOVER", "MARK ON TOP", "SAR SQUARE, LADDER, SECTOR", "moving waypoints", "rendezvous"],
  verticalGuidance: {
    enRoute: "no airline-style en-route VNAV; constraints advisory, flown with AFCS ALT/VS (Stage B)",
    approach: "S300 advisory approach VNAV where it can be constructed (M300 7-22…7-27); none on no-VPA point-in-space approaches",
    sbasFinals: "coupled LPV and LNAV/VNAV finals: bench capability for a modern CMA-5024 SBAS installation, not S300 behaviour",
  },
  notConfigured: ["CARP/HARP", "COSPAS-SARSAT", "EGI/IRS", "DVS (Doppler)", "military navigation option", "tactical approach changes (review first)"],
  parameters: {
    // Speeds and regimes
    cruiseSpeed: p(120, "kt", "lab", "the bench's existing cruise speed (TAS today; IAS from Stage B)", true),
    planningCruiseTas: p(130, "kt TAS", "sourced", "M300 3-19 PLAN DATA CRZ TAS default for ROTOR", false, "E"),
    maximumSpeed: p(150, "KIAS", "lab", "RTA feasibility upper bound", false, "E"),
    climbSpeed: p(80, "KIAS", "borrowed", "AAIB-27532 (AW189 TU target)", false, "B"),
    vmini: p(50, "KIAS", "lab", "procedure limits below it refuse coupled IFR activation", false, "C"),
    unreliableIasBelow: p(30, "KIAS", "lab", "airspeed shown as dashes below it", false, "B"),
    reliableIasAgainAt: p(33, "KIAS", "lab", "3 kt hysteresis", false, "B"),
    coordinatedEnterAt: p(45, "KIAS", "lab", "coordinated-flight regime entry", false, "B"),
    coordinatedLeaveBelow: p(40, "KIAS", "lab", "5 kt hysteresis", false, "B"),
    sidewaysLimit: p(35, "kt", "lab", "low-speed air-relative sideways limit", false, "B"),
    rearwardLimit: p(30, "kt", "lab", "low-speed air-relative rearward limit", false, "B"),
    // Accelerations and rates
    longitudinalAccel: p(2.0, "kt/s", "lab", "longitudinal acceleration and deceleration limit", false, "B"),
    lateralAccel: p(1.5, "kt/s", "lab", "low-speed lateral acceleration limit", false, "B"),
    maxVerticalSpeed: p(1000, "fpm", "lab", "existing MAX_VS", true),
    verticalAccel: p(600, "fpm/s", "lab", "existing VS_RATE", true),
    rollRate: p(10, "deg/s", "lab", "today 5 deg/s", false, "B"),
    afcsBankLimit: p(30, "deg", "lab", "today 25 deg", false, "B"),
    fmsRollSteeringLimit: p(30, "deg", "sourced", "M300 4-3, 7-16: up to 30 deg bank in roll steering", false, "B"),
    lowSpeedYawRate: p(15, "deg/s", "lab", "hover heading rate", false, "B"),
    // Transition and mode values
    tdDescentRate: p(500, "fpm", "lab", "TD descent to the gate", false, "D"),
    tdDeceleration: p(1.0, "kt/s", "lab", "TD deceleration to the gate speed", false, "D"),
    gateHeight: p(200, "ft RA", "borrowed", "AAIB-27585 (AW189 TD)", false, "D"),
    gateSpeed: p(80, "KIAS", "borrowed", "AAIB-27585 (AW189 TD)", false, "D"),
    gateSegmentMinimum: p(0.2, "NM", "lab", "the only slack in the transition trajectory", false, "D"),
    tdhMinHeight: p(30, "ft RA", "borrowed", "AAIB-27532 (AW189 TD/H window)", false, "D"),
    tdhMaxHeight: p(210, "ft RA", "borrowed", "AAIB-27532 (AW189 TD/H window)", false, "D"),
    tdhMaxSpeedBelow: p(85, "KIAS", "borrowed", "AAIB-27532 (AW189 TD/H window, exclusive)", false, "D"),
    tdhDeceleration: p(0.75, "kt/s", "lab", "nominal; closed loop within 0.5-1.25 kt/s", false, "D"),
    tdhDescentRate: p(150, "fpm", "lab", "TD/H descent to hover height", false, "D"),
    hoverHeightDefault: p(50, "ft RA", "borrowed", "AAIB-27532 (AW189 TD/H)", false, "B"),
    hoverHeightMin: p(30, "ft RA", "lab", "selectable range", false, "B"),
    hoverHeightMax: p(200, "ft RA", "lab", "selectable range", false, "B"),
    departureAccel: p(1.0, "kt/s", "lab", "TU-LAB departure from hover", false, "B"),
    departureClimbRate: p(500, "fpm", "lab", "TU-LAB climb to the gate height", false, "B"),
    goAroundClimbRate: p(800, "fpm", "lab", "GA; 400 ft/NM at 120 kt GS (AIM 5-4-21 Copter)", false, "B"),
    minimumUseHeight: p(30, "ft RA", "lab", "derived from the TD/H window", false, "B"),
    lowHeightCruise: p(75, "ft RA", "borrowed", "AAIB-27585 (AW189 low-height protection, cruise)", false, "B"),
    lowHeightHover: p(17, "ft RA", "borrowed", "AAIB-27585 (AW189 low-height protection, hover)", false, "B"),
    radioAltimeterRange: p(2500, "ft", "lab", "NCD above", false, "B"),
    // Holding (M300 Table 10-1, helicopter rows; the rows overlap at 6,000 ft and the bench gives 6,000 to the lower)
    holdingSpeedLow: p(100, "KIAS", "sourced", "M300 10-8 Table 10-1, helicopter, at or below 6,000 ft", false, "D"),
    holdingSpeedHigh: p(170, "KIAS", "sourced", "M300 10-8 Table 10-1, helicopter, above 6,000 to 14,000 ft", false, "D"),
    // Timing and display
    fmaCaptureBox: p(10, "s", "lab", "existing boxed-mode time", true),
    settlingTime: p(20, "s", "lab", "before hover tolerances apply", false, "B"),
    noProgressBelow: p(1, "kt", "lab", "predicted along-path ground speed", false, "B"),
  },
};

/** The profile the bench flies. One default for now; a fixed-wing profile is later work. */
export const ACTIVE_PROFILE = HELICOPTER_PROFILE;

/** A short, stable fingerprint of the whole profile (FNV-1a over its JSON): any changed value changes it. */
export function profileFingerprint(profile: AircraftProfile) {
  let hash = 0x811c9dc5;
  for (const ch of JSON.stringify(profile)) {
    hash ^= ch.codePointAt(0)!;
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `fnv1a-${hash.toString(16).padStart(8, "0")}`;
}

/** The profile as a run report or the bench names it: identity, version, fingerprint and how much of it is in force. */
export function profileSummary(profile: AircraftProfile) {
  const values = Object.values(profile.parameters);
  const inForce = values.filter(parameter => parameter.inForce).length;
  return `${profile.id} v${profile.version} (${profileFingerprint(profile)}): ${profile.title}; ${inForce} of ${values.length} parameters in force, the rest declared for later stages`;
}

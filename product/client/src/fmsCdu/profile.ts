import { CIVIL_SAR_CONFIGURATION, configurationSummary, type AircraftConfiguration } from "./configuration";

/**
 * The aircraft profile the bench simulates: one versioned, declared configuration of the CMA-9000 and the aircraft
 * around it, so every run names what it was a run of.
 *
 * The helicopter-first plan (Stage A) makes the bench a rotorcraft CMA-9000: the helicopter/military operational
 * program S/W 169-614876-300 (Operator's Manual Pub. 9000-GEN-0150, Rev 2) is the behavioural baseline, cited as
 * "M300 <page>". Which behaviour comes from which source, and where the bench deliberately differs, is in
 * product/docs/FMS_APPLICABILITY.md.
 *
 * Each parameter says whether the simulation uses that value (`inForce`) or whether it is declared only, so the
 * bench and run report distinguish operational parameters from declarations. Configured target options separately
 * state whether their behavior is implemented, partial or pending. A parameter's basis is "sourced" (a CMA manual page or a regulation),
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
  /**
   * Who commands the vertical axis and the speed en route. ADVISORY: the crew, through the autopilot's altitude,
   * vertical-speed and speed selections; the FMS constraints are advisories (the helicopter CMA, plan A4). AIRLINE_VNAV:
   * the laboratory airline-style VNAV (top of descent, VNAV PTH, DES NOW, the FMS speed schedule).
   */
  verticalPolicy: "ADVISORY" | "AIRLINE_VNAV";
  approachPolicy: "S300_ADVISORY" | "SBAS_COUPLED";
  temperatureEntry: "OPTIONAL" | "ALERT" | "MANDATORY";
  /** The vertical-guidance policy (plan A4). */
  verticalGuidance: {
    enRoute: string;
    approach: string;
    sbasFinals: string;
  };
  /** Configurable CMA functions deliberately off (plan D6). */
  notConfigured: readonly string[];
  configuration: AircraftConfiguration;
  parameters: Readonly<Record<string, ProfileParameter>>;
};

const p = (value: number, unit: string, basis: ParameterBasis, source: string, inForce: boolean, stage?: ProfileParameter["stage"]): ProfileParameter =>
  ({ value, unit, basis, source, inForce, ...(stage ? { stage } : {}) });

export const HELICOPTER_PROFILE: AircraftProfile = {
  id: "cma9000-s300-heli-civil",
  version: 5,
  title: "CMA-9000 helicopter, civil SAR target (S/W -300 baseline)",
  aircraftType: "ROTOR",
  operationalProgram: "169-614876-300 (M300, Pub. 9000-GEN-0150 Rev 2)",
  navigationOption: "CIVIL",
  errorLimit: "RNP",
  verticalPolicy: "ADVISORY",
  approachPolicy: "S300_ADVISORY",
  temperatureEntry: "ALERT",
  equipment: ["2 × CMA-5024 GPS/SBAS", "1 radio altimeter (height above a declared flat surface)", "representative rotorcraft AFCS (laboratory)"],
  missionFunctions: ["HOVER", "MARK ON TOP", "SAR SQUARE, LADDER, SECTOR", "moving waypoints", "rendezvous"],
  verticalGuidance: {
    enRoute: "no airline-style en-route VNAV; constraints advisory, flown with AFCS ALT/VS (Stage B)",
    approach: "S300 advisory approach VNAV where it can be constructed (M300 7-22…7-27); none on no-VPA point-in-space approaches",
    sbasFinals: "not available in S300; select the separate later-CMA/CMA-5024 SBAS profile for coupled LPV",
  },
  notConfigured: ["CARP/HARP", "COSPAS-SARSAT", "EGI/IRS", "DVS (Doppler)", "military navigation option", "military tactical approach"],
  configuration: CIVIL_SAR_CONFIGURATION,
  parameters: {
    sensorMaxAge: p(2, "s", "lab", "maximum sensor age for the internal simulator port", true),
    radioAcquisition: p(3, "s", "lab", "AUTO facility acquisition after tuning or signal return", true),
    radioRangeBias: p(0.02, "NM", "lab", "deterministic radio slant-range bias", true),
    radioBearingBias: p(0.25, "deg", "lab", "deterministic VOR bearing bias", true),
    radioCrossAngle: p(30, "deg", "lab", "minimum DME crossing angle; maximum is its supplement", true),
    radioResidualLimit: p(0.5, "NM", "lab", "maximum range-circle residual", true),
    radioMinFacilities: p(3, "facilities", "sourced", "M300 1-8: fewer than three DMEs falls back to VOR/DME", true),
    drWindUncertainty: p(2, "kt", "lab", "DR last-wind uncertainty allowance", true),
    drTasUncertainty: p(0.5, "kt", "lab", "DR TAS uncertainty allowance", true),
    drHeadingUncertainty: p(1, "deg", "lab", "DR heading uncertainty allowance", true),
    drNoAirGrowth: p(10, "NM/h", "lab", "position held without air data, with growing uncertainty", true),
    windRadioMaxGap: p(10, "s", "lab", "maximum successive radio-fix interval for computed wind", true),
    idfCrossingTolerance: p(0.1, "NM", "lab", "PinS IDF crossing proximity; no obstacle/protection claim", true),
    approachPredictionAge: p(60, "s", "lab", "maximum age of the simulated FAF/MAP integrity prediction", true),
    advisoryMinimumProgress: p(1, "kt", "lab", "minimum measured progress for advisory VNAV; not an OEM threshold", true),
    qnhAltitudeSlope: p(27, "ft/hPa", "lab", "linear pressure-altitude correction; not an ADC atmosphere model", true),
    temperatureLapseRate: p(0.0019812, "deg C/ft", "lab", "linear ISA ratio for advisory compensation; OEM formula unpublished", true),
    afRadiusTolerance: p(0.15, "NM", "lab", "AF import consistency allowance for 0.1-NM rho coding", true),
    rfRadiusTolerance: p(0.02, "NM", "lab", "RF import consistency allowance for 0.001-NM radius coding", true),
    piSecondLegReserve: p(2.25, "NM", "lab", "short-limit PI construction reserves a full second outbound; OEM reduction formula unpublished", true),
    // Speeds and regimes
    cruiseSpeed: p(120, "kt", "lab", "crew-selected cruise speed (KIAS for the helicopter AFCS)", true),
    planningCruiseTas: p(130, "kt TAS", "sourced", "M300 3-19 PLAN DATA CRZ TAS default for ROTOR (planning data; v1 predicts only in the air)", true),
    maximumSpeed: p(150, "KIAS", "lab", "RTA feasibility upper bound (the required TAS in IAS at each leg, R3-04)", true),
    climbSpeed: p(80, "KIAS", "borrowed", "AAIB-27532 (AW189 TU target)", true),
    vmini: p(50, "KIAS", "lab", "the RTA feasibility lower bound (R3-04); procedure limits below it refuse coupled IFR activation (Stage C)", true),
    unreliableIasBelow: p(30, "KIAS", "lab", "airspeed shown as dashes below it", true),
    reliableIasAgainAt: p(33, "KIAS", "lab", "3 kt hysteresis", true),
    coordinatedEnterAt: p(45, "KIAS", "lab", "coordinated-flight regime entry", true),
    coordinatedLeaveBelow: p(40, "KIAS", "lab", "5 kt hysteresis; the ND turn trend is drawn only at or above it", true),
    sidewaysLimit: p(35, "kt", "lab", "low-speed air-relative sideways limit", true),
    rearwardLimit: p(30, "kt", "lab", "low-speed air-relative rearward limit", true),
    // Accelerations and rates
    longitudinalAccel: p(2.0, "kt/s", "lab", "longitudinal acceleration and deceleration limit", true),
    lateralAccel: p(1.5, "kt/s", "lab", "low-speed lateral acceleration limit", true),
    maxVerticalSpeed: p(1000, "fpm", "lab", "AFCS vertical-speed limit", true),
    verticalAccel: p(600, "fpm/s", "lab", "AFCS vertical-acceleration limit", true),
    rollRate: p(5, "deg/s", "lab", "representative AFCS roll-rate limit", true),
    afcsBankLimit: p(30, "deg", "lab", "final AFCS bank envelope; no extra correction allowance", true),
    fmsRollSteeringLimit: p(30, "deg", "sourced", "M300 4-3, 7-16: up to 30 deg bank in roll steering", true),
    lowSpeedYawRate: p(15, "deg/s", "lab", "hover heading rate", true),
    // Transition and mode values
    tdDescentRate: p(500, "fpm", "lab", "TD descent to the gate", true),
    tdDeceleration: p(1.0, "kt/s", "lab", "TD deceleration to the gate speed", true),
    gateHeight: p(200, "ft RA", "borrowed", "AAIB-27585 (AW189 TD)", true),
    gateSpeed: p(80, "KIAS", "borrowed", "AAIB-27585 (AW189 TD)", true),
    gateSegmentMinimum: p(0.2, "NM", "lab", "the only slack in the transition trajectory", true),
    tdhMinHeight: p(30, "ft RA", "borrowed", "AAIB-27532 (AW189 TD/H window)", true),
    tdhMaxHeight: p(210, "ft RA", "borrowed", "AAIB-27532 (AW189 TD/H window)", true),
    tdhMaxSpeedBelow: p(85, "KIAS", "borrowed", "AAIB-27532 (AW189 TD/H window, exclusive)", true),
    tdhDeceleration: p(0.75, "kt/s", "lab", "nominal; closed loop within 0.5-1.25 kt/s", true),
    tdhDescentRate: p(150, "fpm", "lab", "TD/H descent to hover height", true),
    hoverHeightDefault: p(50, "ft RA", "borrowed", "AAIB-27532 (AW189 TD/H)", true),
    hoverHeightMin: p(30, "ft RA", "lab", "selectable range", true),
    hoverHeightMax: p(200, "ft RA", "lab", "selectable range", true),
    departureAccel: p(1.0, "kt/s", "lab", "TU-LAB departure from hover", true),
    departureClimbRate: p(500, "fpm", "lab", "TU-LAB climb to the gate height", true),
    goAroundClimbRate: p(800, "fpm", "lab", "GA; 400 ft/NM at 120 kt GS (AIM 5-4-21 Copter)", true),
    minimumUseHeight: p(30, "ft RA", "lab", "derived from the TD/H window", true),
    lowHeightCruise: p(75, "ft RA", "borrowed", "AAIB-27585 (AW189 low-height protection, cruise)", true),
    lowHeightHover: p(17, "ft RA", "borrowed", "AAIB-27585 (AW189 low-height protection, hover)", true),
    radioAltimeterRange: p(2500, "ft", "lab", "NCD above", true),
    // Holding (M300 Table 10-1, helicopter rows; the rows overlap at 6,000 ft and the bench gives 6,000 to the lower)
    holdingSpeedLow: p(100, "KIAS", "sourced", "M300 10-8 Table 10-1, helicopter, at or below 6,000 ft", true),
    holdingSpeedHigh: p(170, "KIAS", "sourced", "M300 10-8 Table 10-1, helicopter, above 6,000 to 14,000 ft", true),
    // Timing and display
    hoverTransferTick: p(0.25, "s", "lab", "a receiver change keeps HOV only within one tick of the last sample (Astra rev 3.1)", true),
    hoverTransferPosition: p(10, "m", "lab", "a receiver change keeps HOV only within this of the last sample propagated", true),
    hoverTransferVelocity: p(1, "kt", "lab", "a receiver change keeps HOV only within this velocity step", true),
    fmaCaptureBox: p(10, "s", "lab", "existing boxed-mode time", true),
    settlingTime: p(20, "s", "lab", "acceptance settling allowance; not a controller parameter", false),
    noProgressBelow: p(1, "kt", "lab", "ground speed below which there is no measurable progress", true),
  },
};

/** The profile the bench flies by default. */
export const ACTIVE_PROFILE = HELICOPTER_PROFILE;

/** Representative later installation; the brochure/SIL establish applicability, not an exact qualified OEM build. */
export const LATER_SBAS_PROFILE: AircraftProfile = {
  ...HELICOPTER_PROFILE,
  id: "cma9000-later-sbas-heli",
  version: 1,
  title: "Later CMA software + CMA-5024 SBAS helicopter (representative simulation)",
  operationalProgram: "later SBAS-capable CMA family; exact OEM software baseline unqualified",
  approachPolicy: "SBAS_COUPLED",
  parameters: {
    ...HELICOPTER_PROFILE.parameters,
    advisoryMinimumProgress: { ...HELICOPTER_PROFILE.parameters.advisoryMinimumProgress, inForce: false },
    temperatureLapseRate: { ...HELICOPTER_PROFILE.parameters.temperatureLapseRate, inForce: false },
  },
  verticalGuidance: {
    ...HELICOPTER_PROFILE.verticalGuidance,
    approach: "coupled GPS LPV/LNAV-VNAV from the accepted FAS and receiver guidance; generic simulated AFCS",
    sbasFinals: "CMC CMA-9000 brochure and SIL-25-04A Rev 2 applicability; laboratory receiver/AFCS contracts",
  },
};

/**
 * A laboratory profile: the helicopter profile with the generic airline-style VNAV in place of crew-selected vertical
 * modes. Not a CMA installation; kept as an intentional, selectable profile (the seed of a later fixed-wing profile).
 */
export const LAB_AIRLINE_VNAV_PROFILE: AircraftProfile = {
  ...HELICOPTER_PROFILE,
  id: "lab-airline-vnav",
  title: "Laboratory: generic airline-style VNAV (not a CMA installation)",
  verticalPolicy: "AIRLINE_VNAV",
  approachPolicy: "SBAS_COUPLED",
  parameters: LATER_SBAS_PROFILE.parameters,
  verticalGuidance: {
    ...HELICOPTER_PROFILE.verticalGuidance,
    enRoute: "generic airline-style VNAV: top of descent, VNAV PTH, DES NOW and the FMS speed schedule (laboratory)",
    approach: "generic coupled FMS/receiver approach controller; intentional laboratory behavior",
    sbasFinals: "coupled LPV/LNAV-VNAV receiver simulation; not an S300 or installed OEM implementation",
  },
};

export const PROFILES: readonly AircraftProfile[] = [HELICOPTER_PROFILE, LATER_SBAS_PROFILE, LAB_AIRLINE_VNAV_PROFILE];

export const profileById = (id: string | null | undefined) => PROFILES.find(profile => profile.id === id);

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
  const declared = Object.entries(profile.parameters).filter(([, parameter]) => !parameter.inForce).map(([name]) => name);
  return `${profile.id} v${profile.version} (${profileFingerprint(profile)}): ${profile.title}; ${inForce} of ${values.length} parameters in force${declared.length ? `; declared only: ${declared.join(", ")}` : ""}; ${configurationSummary(profile.configuration)}`;
}

/** Bank geometry and FMS commands share the configured limit inside the AFCS envelope. */
export const fmsBankLimit = (profile: AircraftProfile) => Math.min(profile.parameters.afcsBankLimit.value, profile.parameters.fmsRollSteeringLimit.value);

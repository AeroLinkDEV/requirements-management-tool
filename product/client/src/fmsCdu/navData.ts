import { distanceNm, longitudeDelta, offset, type ConditionalPath, type FixPath, type LatLon } from "./fmsModel";

/**
 * The navigation database: airports with runways, navaids, fixes, airways and terminal procedures (SIDs, STARs and
 * approaches with their transitions and missed approach).
 *
 * The built-in data is a DEMONSTRATION set around Ottawa and Montréal. The two airports are real and roughly placed;
 * every fix, airway, procedure, frequency and runway position is invented for the simulation and must not be used for
 * navigation. Engineers can load their own data in ARINC 424 format (see arinc424.ts): it is merged over the active
 * cycle's data into a new, inactive cycle, which the crew then activates (ScriptedFms.loadNavData).
 */

export type NavaidType = "VOR" | "VORDME" | "VORTAC" | "DME" | "TACAN" | "NDB";
export type Fix = { kind: "fix"; ident: string; position: LatLon };
/**
 * A navaid (Stage F1). `position` is the VOR's (or the NDB's), or for a DME-only or TACAN-only station its DME's.
 * `dmePosition` is the DME antenna's own position when the data gives one: a co-located DME can stand apart from
 * its VOR, and ranges are measured from it. `elevation` is the station's, feet MSL, and says where it came from:
 * `data` (the ARINC 424 record's DME elevation), `terrain` (the ground elevation at an invented demonstration site,
 * the antenna's height above it unknown), or `assumed` (a record whose field is blank), never a silent zero; a range
 * corrected with a terrain or assumed elevation says so in its accuracy (radioNavigation.ts). `channel` is the
 * DME/TACAN channel of the frequency's standard pairing (ICAO Annex 10 Vol I).
 */
export type Navaid = {
  kind: "navaid"; ident: string; type: NavaidType; position: LatLon; frequency: string; name: string;
  elevation: NavaidElevation;
  dmePosition?: LatLon;
  channel?: string;
};
/**
 * A station's elevation (Stage F1 contract; FMS_STAGE_F_PLAN.md F1). Feet above mean sea level. The source, in order
 * of preference: `data`, the ARINC 424 record's DME elevation (columns 80-84, feet MSL); `terrain`, the ground
 * elevation at an invented demonstration site (metres above the geoid, taken as MSL); `assumed`, none available (0 ft,
 * stated). `provenance` says where the figure came from. A terrain height is the ground's, not the antenna's.
 */
export type NavaidElevation = { feet: number; source: "data" | "terrain" | "assumed"; provenance: string };
/** A record's DME elevation. */
export const dataElevation = (feet: number): NavaidElevation => Object.freeze({ feet, source: "data" as const, provenance: "ARINC 424 DME elevation (columns 80-84)" });
/** No elevation from the data (a blank ARINC 424 field, or a VOR-only record): an assumed 0 ft, stated as assumed. */
export const ASSUMED_ELEVATION: NavaidElevation = Object.freeze({ feet: 0, source: "assumed" as const, provenance: "no elevation in the record" });
/**
 * The ground elevation at an invented demonstration site, feet: the Terrarium elevation tiles (AWS open data, from
 * SRTM and national elevation models), zoom 14, read on 30 September 2026. The demonstration navaids are invented, so no
 * navigation data gives their elevation; the ground under them is the honest figure, the antenna's height above it
 * unknown (the profile's terrainNavaidElevationUncertainty).
 */
const terrain = (feet: number): NavaidElevation =>
  Object.freeze({ feet, source: "terrain" as const, provenance: "Terrarium ground elevation, zoom 14, read 30 September 2026 (the ground under the invented site, not the antenna)" });

/**
 * The DME/TACAN channel paired with a VHF frequency (ICAO Annex 10 Vol I, Attachment C, Table A): 108.00 to 112.25 MHz
 * are channels 17 to 59, 112.30 to 117.95 MHz channels 70 to 126; a frequency ending in 0 is an X channel, in 5 a Y.
 * Null for a frequency outside the paired bands or off the 50 kHz raster.
 */
export function pairedChannel(mhz: number): string | null {
  const hundredths = Math.round(mhz * 100);
  if (hundredths % 5 !== 0) return null;
  const suffix = hundredths % 10 === 0 ? "X" : "Y";
  const tenths = Math.floor(hundredths / 10);
  if (tenths >= 1080 && tenths <= 1122) return `${tenths - 1080 + 17}${suffix}`;
  if (tenths >= 1123 && tenths <= 1179) return `${tenths - 1123 + 70}${suffix}`;
  return null;
}
export type Runway = { ident: string; threshold: LatLon; course: number; elevation: number; length: number };
export type Airport = {
  kind: "airport"; ident: string; name: string; position: LatLon; elevation: number; runways: Runway[];
  /** Magnetic variation, degrees, east positive, when the data gives it (ARINC 424 airport record). */
  magneticVariation?: number;
  /**
   * A heliport (ARINC 424 heliport section, HA record) rather than an airport. It is a landing site and a valid
   * destination like an airport, with no runways.
   */
  heliport?: true;
};
export type NavEntry = Fix | Navaid | Airport;

export type Airway = { ident: string; fixes: string[] };

/**
 * A minimum sector altitude (ARINC 424 PS or HS record): the sectors about a centre fix, each with its bearings (from
 * the centre, magnetic unless `magnetic` is false), its altitude and its radius.
 */
export type Msa = {
  airport: string; centre: string; magnetic: boolean;
  sectors: { from: number; to: number; altitude: number; radiusNm: number }[];
};

/** A procedure speed limit (ARINC 424 columns 100-102 and its description at 118), knots indicated. */
export type SpeedLimit = { kt: number; descriptor: "AT" | "AT OR ABOVE" | "AT OR BELOW" };

/**
 * A holding pattern a procedure codes at a fix: HF (a course reversal, left after one circuit), HA (left at the fix
 * once the altitude is reached) or HM (held until the crew exits it). The inbound course is true; a coded leg distance
 * takes the place of leg time. Kept as data: how a hold is armed and flown is the flight's business.
 */
export type ProcedureHold = {
  path: "HF" | "HA" | "HM"; inbound: number; turn: "RIGHT" | "LEFT";
  legDistanceNm: number | null; legTimeMin: number | null; exit: "ONCE" | "AT ALT" | "MANUAL";
  altitude?: string; speedLimit?: SpeedLimit;
};

/**
 * A procedure leg: a fix with its path terminator, or a conditional leg (course or heading to an event). Imported
 * data may add the coded turn direction, speed limit and vertical angle, and the hold coded at the fix.
 */
export type ProcedureLeg =
  | {
    ident: string; altitude?: string; overfly?: boolean; path?: FixPath; course?: number; arc?: { centre: LatLon; turn: "L" | "R" };
    turnDirection?: "LEFT" | "RIGHT"; speedLimit?: SpeedLimit; verticalAngleDeg?: number; hold?: ProcedureHold;
    /** Generated PI outbound waypoints stay inside their procedure, never in the pilot waypoint database. */
    position?: LatLon; procedureTurn?: { reference: string; role: "REFERENCE" | "OUTBOUND" | "INBOUND" };
  }
  | { path: ConditionalPath; course: number; altitude?: number; turnDirection?: "LEFT" | "RIGHT"; speedLimit?: SpeedLimit };

/**
 * Where an approach ends and what follows it, as separate facts (Stage C, C.2). The instrument end is the missed
 * approach point and its altitude (the MDA of a point-in-space approach). The landing site is the runway, heliport or
 * airport the procedure serves. The visual segment is how the crew gets from the MAP to the site: RUNWAY when the MAP
 * is the threshold, or a point-in-space kind with the site's bearing and distance from the MAP. Its kind comes from
 * the chart, which the decoded fields do not carry: UNKNOWN (and unvalidated) unless a chart has been checked for
 * it. PROCEED VISUALLY never inherits PROCEED VFR semantics. Nothing here manufactures a runway threshold or extends
 * a descent below the MDA or past the MAP; the missed approach continuation is the procedure's `missed` legs.
 */
export type ProcedureEndpoint = {
  instrumentEnd: { fix: string; altitude?: string };
  landingSite: { kind: "RUNWAY" | "HELIPORT" | "AIRPORT"; ident: string; airport: string };
  visualSegment: {
    kind: "RUNWAY" | "PROCEED VFR" | "PROCEED VISUALLY" | "UNKNOWN"; validated: boolean; source?: string;
    /** From the MAP to the landing site: magnetic bearing and distance; absent when the MAP is the runway. */
    bearingMag?: number; distanceNm?: number;
  };
  /**
   * The vertical path the data gives the final: a vertical angle, or NONE when the MAP's coded angle is zero (a
   * point-in-space approach flown LNAV with advisory step-downs), or NOT CODED.
   */
  vertical: { kind: "VPA"; angleDeg: number } | { kind: "NONE"; reason: string } | { kind: "NOT CODED" };
  /**
   * Why the MAP is taken for what it is (plan Q8): a coded runway threshold; a heliport-section approach, point-in-space
   * by its section; or an airport-section Copter procedure in the reviewed set (arinc424.ts REVIEWED_COPTER_PINS), with
   * what was reviewed. Never the ident pattern alone.
   */
  identification: { basis: "RUNWAY" | "HELIPORT SECTION" | "REVIEWED COPTER PROCEDURE"; source: string };
};
export type ProcedureKind = "SID" | "STAR" | "APPROACH";
export type ApproachType = "RNAV" | "ILS" | "VOR" | "NDB";
export type Procedure = {
  kind: ProcedureKind; airport: string; ident: string;
  /** Runways the procedure serves; an approach serves exactly one. */
  runways: string[];
  /** SID: runway to common route; STAR: enroute transitions to common route; approach: transitions to the IAF. */
  transitions: Record<string, ProcedureLeg[]>;
  legs: ProcedureLeg[];
  approachType?: ApproachType;
  /** Approach: the leg that is the final approach fix, and the missed approach legs flown after the runway. */
  faf?: string;
  missed?: ProcedureLeg[];
  /** The missed approach hold; imported data adds its coded leg distance and speed limit. */
  missedHold?: { fix: string; inbound: number; turn: "RIGHT" | "LEFT"; altitude: string; legDistanceNm?: number; speedLimit?: SpeedLimit };
  /** Imported approaches: where the procedure ends and what follows (C.2). */
  endpoint?: ProcedureEndpoint;
  /** Instrument entry fixes; preceding visual/VFR portions require separate chart evidence. */
  departure?: {
    entries: Record<string, { fix: string; altitude?: string }>;
    visualSegment: { kind: "PROCEED VISUALLY" | "PROCEED VFR" | "UNKNOWN"; source?: string };
  };
  runwayTransitions?: Record<string, ProcedureLeg[]>;
  /**
   * A point-in-space approach (a Copter procedure whose MAP is not a runway): flown to the MAP, then the visual
   * segment or the missed approach. Its `runways` is empty.
   */
  pointInSpace?: true;
  /** Chart notes the data does not code (restrictions, speed notes, minima), displayed and never enforced. */
  notes?: string[];
  /**
   * An RNAV approach's published final approach segment data (ARINC 424 path point record), when the data has one:
   * the fields of the FAS data block, and the CRC as published. The simulation derives a FAS block only when this is
   * absent (gpsSensors.buildFas).
   */
  publishedFas?: PublishedFas;
  /**
   * An RNAV approach intentionally flown LNAV only, and why the data says so (Astra Q4): no path point record, and no
   * vertical path published (vertical angle 000 at the MAP), as 87N COPTER RNAV 190. Only this makes an approach without
   * a FAS data block LNAV only; a required FAS block that is missing or unreadable does not (gpsSensors approachAuthority).
   */
  lnavOnly?: { source: string };
  /**
   * A path point record the data publishes for this approach but that could not be read, and why: the approach needs a
   * FAS data block and has none usable, so none is derived in its place (gpsSensors buildFas).
   */
  fasInvalid?: string;
};

/** The FAS data block fields as a path point record publishes them, with the published CRC (hexadecimal). */
export type PublishedFas = {
  operationType: number; sbasProvider: number; airport: string; runway: number; designator: "L" | "R" | "C" | "";
  performance: number; routeIndicator: string; referencePathSelector: number; referencePathId: string;
  ltp: { lat: number; lon: number; heightM: number };
  fpapDelta: { lat: number; lon: number };
  tchFt: number; gpaDeg: number; courseWidthM: number; lengthOffsetM: number; halM: number; valM: number;
  publishedCrc: string;
};

export type NavData = {
  /** The cycle's effective dates as the data gives them (YYYY-MM-DD), or "" when the data does not say. */
  cycle: { id: string; from: string; to: string };
  entries: NavEntry[];
  airways: Airway[];
  procedures: Procedure[];
  /** Minimum sector altitudes, when the data gives them. */
  msa?: Msa[];
};

const fix = (ident: string, lat: number, lon: number): Fix => ({ kind: "fix", ident, position: { lat, lon } });

/** Runway thresholds either side of an airport reference point: each end lies half the length out along the axis. */
function runwayPair(ref: LatLon, a: string, b: string, courseA: number, length: number, elevation: number): Runway[] {
  const half = length / 6076.12 / 2;
  return [
    { ident: `RW${a}`, threshold: offset(ref, courseA + 180, half), course: courseA, elevation, length },
    { ident: `RW${b}`, threshold: offset(ref, courseA, half), course: (courseA + 180) % 360, elevation, length },
  ];
}

const CYOW_REF = { lat: 45.3225, lon: -75.6692 };

// The RNAV 06L approach turns onto final on a 3 NM radius-to-fix arc: it ends at the FAF (UL601) on the final
// approach course, 057, having begun 120 degrees earlier at UL603.
const UL601 = { lat: 45.4300, lon: -73.8200 };
const R06L_ARC = { centre: offset(UL601, 147, 3), turn: "R" as const };
const UL603 = offset(R06L_ARC.centre, 207, 3);
const CYUL_REF = { lat: 45.4706, lon: -73.7408 };

// CYUL runway 06L/24R is placed so its 24R threshold matches the demonstration route's RW24R.
const cyul24R: Runway = { ident: "RW24R", threshold: { lat: 45.4790, lon: -73.7180 }, course: 237, elevation: 118, length: 11000 };
const cyul06L: Runway = { ident: "RW06L", threshold: offset(cyul24R.threshold, 237, 11000 / 6076.12), course: 57, elevation: 118, length: 11000 };

export const DEMO_NAV_DATA: NavData = {
  cycle: { id: "DEMO-2610", from: "2026-10-01", to: "2026-10-28" },
  entries: [
    { kind: "airport", ident: "CYOW", name: "OTTAWA INTL", position: CYOW_REF, elevation: 374,
      runways: [...runwayPair(CYOW_REF, "07", "25", 71, 8000, 374), ...runwayPair(CYOW_REF, "14", "32", 136, 10000, 374)] },
    { kind: "airport", ident: "CYUL", name: "MONTREAL TRUDEAU", position: CYUL_REF, elevation: 118,
      runways: [cyul06L, cyul24R, ...runwayPair(offset(CYUL_REF, 200, 0.6), "06R", "24L", 57, 9600, 118), ...runwayPair(CYUL_REF, "10", "28", 98, 7000, 118)] },
    { kind: "airport", ident: "CYYZ", name: "TORONTO PEARSON", position: { lat: 43.6772, lon: -79.6306 }, elevation: 569,
      runways: [...runwayPair({ lat: 43.6772, lon: -79.6306 }, "05", "23", 57, 11120, 569), ...runwayPair({ lat: 43.6700, lon: -79.6100 }, "15L", "33R", 147, 11050, 569)] },
    { kind: "airport", ident: "CYRO", name: "OTTAWA ROCKCLIFFE", position: { lat: 45.4603, lon: -75.6461 }, elevation: 188,
      runways: runwayPair({ lat: 45.4603, lon: -75.6461 }, "09", "27", 90, 3300, 188) },
    { kind: "navaid", ident: "YOW", type: "VORDME", position: { lat: 45.4398, lon: -75.8967 }, frequency: "114.60", name: "OTTAWA DEMO", elevation: terrain(433) },
    { kind: "navaid", ident: "YUL", type: "VORDME", position: { lat: 45.6334, lon: -73.8740 }, frequency: "116.30", name: "MONTREAL DEMO", elevation: terrain(157) },
    // South of the airway, so DME/DME has a usable crossing angle with YOW along the route (YOW and YUL alone
    // lie nearly in line with it).
    { kind: "navaid", ident: "HWK", type: "VORDME", position: { lat: 45.0000, lon: -74.7000 }, frequency: "115.20", name: "HAWKESBURY DEMO", elevation: terrain(153) },
    { kind: "navaid", ident: "RIG", type: "VOR", position: { lat: 45.5600, lon: -74.7000 }, frequency: "112.10", name: "RIGAUD DEMO", elevation: terrain(248) },
    { kind: "navaid", ident: "OW", type: "NDB", position: { lat: 45.3000, lon: -75.5500 }, frequency: "236", name: "OTTAWA NDB DEMO", elevation: terrain(314) },
    { kind: "navaid", ident: "UL", type: "NDB", position: { lat: 45.5050, lon: -73.6500 }, frequency: "371", name: "DORVAL NDB DEMO", elevation: terrain(160) },
    // A second BOBTU far to the south: duplicate idents exist in real data, and the FMS asks which one is meant.
    { kind: "navaid", ident: "BOBTU", type: "NDB", position: { lat: 44.2000, lon: -76.5000 }, frequency: "284", name: "KINGSTON NDB DEMO", elevation: terrain(246) },
    fix("MUN", 45.2150, -75.3900), fix("RDG", 45.4300, -74.9800), fix("TOLGU", 45.5020, -74.5100),
    // FERDI, the FAF, is on the RW24R extended centreline (057/237), 4.40 NM from the threshold.
    fix("FERDI", 45.51889, -73.63027), fix("ELIBA", 45.6500, -75.1000), fix("BOBTU", 45.2100, -74.6500),
    fix("KILLA", 45.3900, -74.3300), fix("AGBEK", 45.4400, -73.9100), fix("YUL01", 45.5200, -73.9800),
    // Terminal fixes of the demonstration procedures.
    fix("OW501", 45.2800, -75.8200), fix("OW511", 45.3700, -75.4900), fix("OW512", 45.4100, -75.2500),
    fix("UL301", 45.5700, -74.2500), fix("UL302", 45.5500, -73.9000), fix("UL401", 45.6800, -74.0500),
    fix("UL402", 45.6000, -73.8200), fix("UL501", 45.4200, -73.8800), fix("UL502", 45.3500, -73.9500),
    fix("UL601", UL601.lat, UL601.lon), fix("UL602", 45.4000, -73.8700), fix("UL603", UL603.lat, UL603.lon),
    // Arriving from the west, the runway 24R approaches fly a downwind, base and final north of the airport, so no turn
    // is sharper than about 90 degrees and the final is straight: DEMEL joins downwind 4 NM from the RW24R threshold on
    // 327, ALNIT ends it 9 NM along 057, and ULIDA is the intermediate fix on the extended centreline, 9 NM out on 057.
    fix("DEMEL", 45.5349, -73.7698), fix("ALNIT", 45.6166, -73.5902), fix("ULIDA", 45.56051, -73.53842),
  ],
  airways: [
    { ident: "V300", fixes: ["YOW", "MUN", "RDG", "TOLGU", "YUL"] },
    { ident: "T613", fixes: ["ELIBA", "RDG", "KILLA", "AGBEK"] },
    { ident: "V312", fixes: ["OW", "BOBTU", "KILLA", "UL"] },
  ],
  procedures: [
    { kind: "SID", airport: "CYOW", ident: "RIDEA3", runways: ["RW25", "RW32"],
      transitions: { MUN: [{ ident: "MUN", altitude: "3000" }], ELIBA: [{ ident: "ELIBA", altitude: "5000" }] },
      // The runway course to 1200 ft, then direct to the first fix.
      legs: [{ path: "CA", course: 251, altitude: 1200 }, { ident: "OW501", altitude: "2500A", path: "DF" }] },
    { kind: "SID", airport: "CYOW", ident: "GATIN2", runways: ["RW07", "RW14"],
      transitions: { RDG: [{ ident: "OW512", altitude: "4000" }, { ident: "RDG", altitude: "4500" }] },
      // A heading to 1000 ft, a heading to intercept, then the published course into the first fix.
      legs: [{ path: "VA", course: 71, altitude: 1000 }, { path: "VI", course: 40 }, { ident: "OW511", altitude: "2500A", path: "CF", course: 90 }] },
    { kind: "STAR", airport: "CYUL", ident: "LACHN3", runways: ["RW24R", "RW24L", "RW28"],
      transitions: { TOLGU: [{ ident: "TOLGU", altitude: "6000" }], RDG: [{ ident: "RDG", altitude: "7000" }, { ident: "TOLGU", altitude: "6000" }] },
      legs: [{ ident: "UL301", altitude: "5000" }, { ident: "UL302", altitude: "4000" }] },
    { kind: "STAR", airport: "CYUL", ident: "BOUCH2", runways: ["RW06L", "RW06R", "RW10"],
      transitions: { ELIBA: [{ ident: "ELIBA", altitude: "7000" }] },
      legs: [{ ident: "UL401", altitude: "5000" }, { ident: "UL402", altitude: "4000" }] },
    { kind: "APPROACH", airport: "CYUL", ident: "R24R", approachType: "RNAV", runways: ["RW24R"],
      transitions: { UL302: [{ ident: "UL302", altitude: "4000" }], AGBEK: [{ ident: "AGBEK", altitude: "3000" }] },
      legs: [{ ident: "DEMEL", altitude: "3000" }, { ident: "ALNIT", altitude: "3000" }, { ident: "ULIDA", altitude: "2500" },
        { ident: "FERDI", altitude: "1500A" }, { ident: "RW24R", altitude: "168", overfly: true }], faf: "FERDI",
      missed: [{ path: "CA", course: 237, altitude: 1000 }, { ident: "UL501", altitude: "3000", path: "DF" }, { ident: "UL502", altitude: "3000" }],
      missedHold: { fix: "UL502", inbound: 57, turn: "RIGHT", altitude: "3000" } },
    { kind: "APPROACH", airport: "CYUL", ident: "I24R", approachType: "ILS", runways: ["RW24R"],
      transitions: { UL302: [{ ident: "UL302", altitude: "4000" }] },
      legs: [{ ident: "DEMEL", altitude: "3000" }, { ident: "ALNIT", altitude: "3000" }, { ident: "ULIDA", altitude: "2500" },
        { ident: "FERDI", altitude: "1500A" }, { ident: "RW24R", altitude: "168", overfly: true }], faf: "FERDI",
      missed: [{ ident: "UL501", altitude: "3000" }], missedHold: { fix: "UL501", inbound: 57, turn: "RIGHT", altitude: "3000" } },
    { kind: "APPROACH", airport: "CYUL", ident: "R06L", approachType: "RNAV", runways: ["RW06L"],
      transitions: { UL402: [{ ident: "UL402", altitude: "4000" }] },
      legs: [{ ident: "UL603", altitude: "3000" }, { ident: "UL601", altitude: "2000A", path: "RF", arc: R06L_ARC }, { ident: "RW06L", altitude: "168", overfly: true }], faf: "UL601",
      missed: [{ ident: "UL602", altitude: "3000" }], missedHold: { fix: "UL602", inbound: 237, turn: "RIGHT", altitude: "3000" } },
  ],
};

/** Company routes the CO ROUTE field can load: stored enroute legs between an origin and destination. */
/**
 * A stored route. A user route (E5) also records where each fix was when it was saved, so a load can report a fix
 * the active database has since moved (E6).
 */
export type StoredRoute = { name: string; origin: string; dest: string; legs: { ident: string; via?: string; altitude?: string; position?: LatLon }[] };

export const DEMO_COMPANY_ROUTES: StoredRoute[] = [
  { name: "OWUL1", origin: "CYOW", dest: "CYUL", legs: [{ ident: "MUN", altitude: "3000" }, { ident: "RDG", altitude: "4500" }, { ident: "TOLGU", altitude: "4500" }] },
  { name: "OWUL2", origin: "CYOW", dest: "CYUL", legs: [{ ident: "ELIBA", altitude: "5000" }, { ident: "RDG", via: "T613" }, { ident: "KILLA", via: "T613" }, { ident: "AGBEK", via: "T613", altitude: "3000" }] },
];

export class NavDatabase {
  private byIdent = new Map<string, NavEntry[]>();
  private airwayByIdent = new Map<string, Airway>();
  readonly procedures: Procedure[];
  readonly msa: Msa[];
  readonly cycle: NavData["cycle"];
  readonly counts: { fixes: number; navaids: number; airports: number; runways: number; airways: number; procedures: number };

  constructor(data: NavData) {
    for (const entry of data.entries) this.byIdent.set(entry.ident, [...(this.byIdent.get(entry.ident) ?? []), entry]);
    for (const airway of data.airways) this.airwayByIdent.set(airway.ident, airway);
    this.procedures = data.procedures;
    this.msa = data.msa ?? [];
    this.cycle = data.cycle;
    this.counts = {
      fixes: data.entries.filter(e => e.kind === "fix").length,
      navaids: data.entries.filter(e => e.kind === "navaid").length,
      airports: data.entries.filter(e => e.kind === "airport").length,
      runways: data.entries.reduce((n, e) => n + (e.kind === "airport" ? e.runways.length : 0), 0),
      airways: data.airways.length,
      procedures: data.procedures.length,
    };
  }

  /** Complete immutable transfer copy, including duplicate entries and procedure geometry. */
  exportData(): NavData { return structuredClone({ cycle: this.cycle, entries: [...this.byIdent.values()].flat(), airways: [...this.airwayByIdent.values()], procedures: this.procedures, msa: this.msa }); }

  /** Every entry with an ident: duplicates are possible, as in real data. */
  find(ident: string): NavEntry[] { return this.byIdent.get(ident) ?? []; }

  airport(ident: string) { return this.find(ident).find((e): e is Airport => e.kind === "airport"); }

  airway(ident: string) { return this.airwayByIdent.get(ident); }

  /** A runway threshold by its route ident, e.g. RW24R, at the given airport (or any airport). */
  runway(ident: string, airport?: string): Runway | undefined {
    for (const entries of this.byIdent.values())
      for (const entry of entries)
        if (entry.kind === "airport" && (!airport || entry.ident === airport)) {
          const runway = entry.runways.find(r => r.ident === ident);
          if (runway) return runway;
        }
    return undefined;
  }

  proceduresFor(airport: string, kind: ProcedureKind) {
    return this.procedures.filter(p => p.airport === airport && p.kind === kind);
  }

  /** The fixes flown along an airway from one fix to another, excluding the first; null if either is not on it. */
  airwaySegment(ident: string, from: string, to: string): string[] | null {
    const airway = this.airway(ident);
    if (!airway) return null;
    const a = airway.fixes.indexOf(from), b = airway.fixes.indexOf(to);
    if (a < 0 || b < 0 || a === b) return null;
    return a < b ? airway.fixes.slice(a + 1, b + 1) : airway.fixes.slice(b, a).reverse();
  }

  /** Entries within a distance of a position, nearest first. */
  nearby(at: LatLon, nm: number): NavEntry[] {
    const close = (p: LatLon) => Math.abs(p.lat - at.lat) * 60 < nm && Math.abs(longitudeDelta(at.lon, p.lon)) * 60 * Math.cos((at.lat * Math.PI) / 180) < nm;
    return [...this.byIdent.values()].flat().filter(e => close(e.position))
      .sort((a, b) => distanceNm(at, a.position) - distanceNm(at, b.position));
  }

  /** Merges another database over this one: entries with the same ident and kind are replaced. */
  merge(other: NavData): NavDatabase {
    const replaced = new Set(other.entries.map(e => `${e.kind}:${e.ident}`));
    const entries = [...[...this.byIdent.values()].flat().filter(e => !replaced.has(`${e.kind}:${e.ident}`)), ...other.entries];
    const airways = [...[...this.airwayByIdent.values()].filter(a => !other.airways.some(o => o.ident === a.ident)), ...other.airways];
    const msa = [...this.msa.filter(m => !(other.msa ?? []).some(o => o.airport === m.airport && o.centre === m.centre)), ...(other.msa ?? [])];
    const procedures = [...this.procedures.filter(p => !other.procedures.some(next => next.kind === p.kind && next.airport === p.airport && next.ident === p.ident)), ...other.procedures];
    return new NavDatabase({ cycle: other.cycle, entries, airways, procedures, msa });
  }
}


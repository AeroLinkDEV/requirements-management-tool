import { distanceNm, offset, type ConditionalPath, type FixPath, type LatLon } from "./fmsModel";

/**
 * The navigation database: airports with runways, navaids, fixes, airways and terminal procedures (SIDs, STARs and
 * approaches with their transitions and missed approach).
 *
 * The built-in data is a DEMONSTRATION set around Ottawa and Montréal. The two airports are real and roughly placed;
 * every fix, airway, procedure, frequency and runway position is invented for the simulation and must not be used for
 * navigation. Engineers can load their own data in ARINC 424 format (see arinc424.ts): it is merged over the active
 * cycle's data into a new, inactive cycle, which the crew then activates (ScriptedFms.loadNavData).
 */

export type NavaidType = "VOR" | "VORDME" | "VORTAC" | "DME" | "NDB";
export type Fix = { kind: "fix"; ident: string; position: LatLon };
export type Navaid = { kind: "navaid"; ident: string; type: NavaidType; position: LatLon; frequency: string; name: string };
export type Runway = { ident: string; threshold: LatLon; course: number; elevation: number; length: number };
export type Airport = { kind: "airport"; ident: string; name: string; position: LatLon; elevation: number; runways: Runway[] };
export type NavEntry = Fix | Navaid | Airport;

export type Airway = { ident: string; fixes: string[] };

/** A procedure leg: a fix with its path terminator, or a conditional leg (course or heading to an event). */
export type ProcedureLeg =
  | { ident: string; altitude?: string; overfly?: boolean; path?: FixPath; course?: number; arc?: { centre: LatLon; turn: "L" | "R" } }
  | { path: ConditionalPath; course: number; altitude?: number };
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
  missedHold?: { fix: string; inbound: number; turn: "RIGHT" | "LEFT"; altitude: string };
};

export type NavData = {
  /** The cycle's effective dates as the data gives them (YYYY-MM-DD), or "" when the data does not say. */
  cycle: { id: string; from: string; to: string };
  entries: NavEntry[];
  airways: Airway[];
  procedures: Procedure[];
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
    { kind: "navaid", ident: "YOW", type: "VORDME", position: { lat: 45.4398, lon: -75.8967 }, frequency: "114.60", name: "OTTAWA DEMO" },
    { kind: "navaid", ident: "YUL", type: "VORDME", position: { lat: 45.6334, lon: -73.8740 }, frequency: "116.30", name: "MONTREAL DEMO" },
    // South of the airway, so DME/DME has a usable crossing angle with YOW along the route (YOW and YUL alone
    // lie nearly in line with it).
    { kind: "navaid", ident: "HWK", type: "VORDME", position: { lat: 45.0000, lon: -74.7000 }, frequency: "115.20", name: "HAWKESBURY DEMO" },
    { kind: "navaid", ident: "RIG", type: "VOR", position: { lat: 45.5600, lon: -74.7000 }, frequency: "112.10", name: "RIGAUD DEMO" },
    { kind: "navaid", ident: "OW", type: "NDB", position: { lat: 45.3000, lon: -75.5500 }, frequency: "236", name: "OTTAWA NDB DEMO" },
    { kind: "navaid", ident: "UL", type: "NDB", position: { lat: 45.5050, lon: -73.6500 }, frequency: "371", name: "DORVAL NDB DEMO" },
    // A second BOBTU far to the south: duplicate idents exist in real data, and the FMS asks which one is meant.
    { kind: "navaid", ident: "BOBTU", type: "NDB", position: { lat: 44.2000, lon: -76.5000 }, frequency: "284", name: "KINGSTON NDB DEMO" },
    fix("MUN", 45.2150, -75.3900), fix("RDG", 45.4300, -74.9800), fix("TOLGU", 45.5020, -74.5100),
    fix("FERDI", 45.5200, -73.6313), fix("ELIBA", 45.6500, -75.1000), fix("BOBTU", 45.2100, -74.6500),
    fix("KILLA", 45.3900, -74.3300), fix("AGBEK", 45.4400, -73.9100), fix("YUL01", 45.5200, -73.9800),
    // Terminal fixes of the demonstration procedures.
    fix("OW501", 45.2800, -75.8200), fix("OW511", 45.3700, -75.4900), fix("OW512", 45.4100, -75.2500),
    fix("UL301", 45.5700, -74.2500), fix("UL302", 45.5500, -73.9000), fix("UL401", 45.6800, -74.0500),
    fix("UL402", 45.6000, -73.8200), fix("UL501", 45.4200, -73.8800), fix("UL502", 45.3500, -73.9500),
    fix("UL601", UL601.lat, UL601.lon), fix("UL602", 45.4000, -73.8700), fix("UL603", UL603.lat, UL603.lon),
    // Arriving from the west, the runway 24R approaches fly a downwind, base and final north of the airport, so no turn
    // is sharper than about 90 degrees and the final is straight: DEMEL joins downwind 4 NM from the RW24R threshold on
    // 327, ALNIT ends it 9 NM along 057, and ULIDA is the intermediate fix on the extended centreline, 9 NM out on 057.
    fix("DEMEL", 45.5349, -73.7698), fix("ALNIT", 45.6166, -73.5902), fix("ULIDA", 45.5607, -73.5386),
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
export type StoredRoute = { name: string; origin: string; dest: string; legs: { ident: string; via?: string; altitude?: string }[] };

export const DEMO_COMPANY_ROUTES: StoredRoute[] = [
  { name: "OWUL1", origin: "CYOW", dest: "CYUL", legs: [{ ident: "MUN", altitude: "3000" }, { ident: "RDG", altitude: "4500" }, { ident: "TOLGU", altitude: "4500" }] },
  { name: "OWUL2", origin: "CYOW", dest: "CYUL", legs: [{ ident: "ELIBA", altitude: "5000" }, { ident: "RDG", via: "T613" }, { ident: "KILLA", via: "T613" }, { ident: "AGBEK", via: "T613", altitude: "3000" }] },
];

export class NavDatabase {
  private byIdent = new Map<string, NavEntry[]>();
  private airwayByIdent = new Map<string, Airway>();
  readonly procedures: Procedure[];
  readonly cycle: NavData["cycle"];
  readonly counts: { fixes: number; navaids: number; airports: number; runways: number; airways: number; procedures: number };

  constructor(data: NavData) {
    for (const entry of data.entries) this.byIdent.set(entry.ident, [...(this.byIdent.get(entry.ident) ?? []), entry]);
    for (const airway of data.airways) this.airwayByIdent.set(airway.ident, airway);
    this.procedures = data.procedures;
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
    const close = (p: LatLon) => Math.abs(p.lat - at.lat) * 60 < nm && Math.abs(p.lon - at.lon) * 60 * Math.cos((at.lat * Math.PI) / 180) < nm;
    return [...this.byIdent.values()].flat().filter(e => close(e.position))
      .sort((a, b) => distanceNm(at, a.position) - distanceNm(at, b.position));
  }

  /** Merges another database over this one: entries with the same ident and kind are replaced. */
  merge(other: NavData): NavDatabase {
    const replaced = new Set(other.entries.map(e => `${e.kind}:${e.ident}`));
    const entries = [...[...this.byIdent.values()].flat().filter(e => !replaced.has(`${e.kind}:${e.ident}`)), ...other.entries];
    const airways = [...[...this.airwayByIdent.values()].filter(a => !other.airways.some(o => o.ident === a.ident)), ...other.airways];
    return new NavDatabase({ cycle: other.cycle, entries, airways, procedures: [...this.procedures, ...other.procedures] });
  }
}


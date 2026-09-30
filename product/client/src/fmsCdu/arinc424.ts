import { bearingDeg, distanceNm } from "./fmsModel";
import type { Airport, Airway, Msa, NavData, NavEntry, NavaidType, Procedure, ProcedureEndpoint, ProcedureLeg, PublishedFas } from "./navData";
import { DEPARTURE_CHARTS, PROCEDURE_CHARTS } from "./procedureCharts";
import { codedVerticalAngle, constraintText, decodeProcedureLegs, type ProcedureRecord } from "./procedureLegs";

/**
 * Reads the parts of an ARINC 424 navigation data file that the simulation uses: enroute and terminal waypoints,
 * VHF and NDB navaids, airports and heliports (with their magnetic variation), runways (their magnetic bearing made
 * true), airways, minimum sector altitudes, RNAV approach procedures and their published FAS data blocks (path point
 * records). The heliport section (HA, HC, HF, HS) is read like the airport section (PA, PC, PF, PS); heliport
 * departures (HD) are also read. Records are the 132-column fixed-width lines of the specification; only primary
 * records are read (continuation records are skipped). Field positions are the ones listed beside each reader, 1-based
 * as the specification numbers columns.
 *
 * The layouts were checked against the FAA's Coded Instrument Flight Procedures (CIFP, ARINC 424-18, public domain).
 * A full CIFP holds some 400,000 records; `airports` limits a load to the named airports and heliports, their
 * procedures and the fixes those procedures use.
 *
 * This is a subset reader for engineering use in the test bench. It has been checked against the specification's
 * field layout and the CIFP, not qualified against a supplier's data file.
 */
export type Arinc424Options = { airports?: string[] };

/**
 * The records read, and what was not. errors are records that could not be used (a DME-only station without a VOR
 * position, a blank field); invalid are records whose values are impossible (a latitude of 100 degrees, 61 minutes),
 * which mean the file itself is damaged and must be refused whole.
 */
export type Arinc424Result = { data: NavData; read: number; skipped: number; errors: string[]; invalid: string[] };

/** Columns a..b inclusive, 1-based. */
const col = (line: string, a: number, b: number) => line.slice(a - 1, b).trim();

/**
 * Degrees, minutes, seconds and hundredths as a signed angle, or null when a part is out of range: minutes and seconds
 * below 60, and the whole no more than the limit (90 for latitude, 180 for longitude).
 */
function angle(m: RegExpExecArray | null, negative: string, limit: number) {
  if (!m) return null;
  const [degrees, minutes, seconds, hundredths] = [m[2], m[3], m[4], m[5]].map(Number);
  if (minutes > 59 || seconds > 59) return null;
  const value = degrees + minutes / 60 + (seconds + hundredths / 100) / 3600;
  if (value > limit) return null;
  return m[1] === negative ? -value : value;
}

/** Latitude "N45183600" and longitude "W075405000" (hemisphere, degrees, minutes, seconds, hundredths). */
export function arincLatitude(text: string) {
  return angle(/^([NS])(\d{2})(\d{2})(\d{2})(\d{2})$/.exec(text), "S", 90);
}

export function arincLongitude(text: string) {
  return angle(/^([EW])(\d{3})(\d{2})(\d{2})(\d{2})$/.exec(text), "W", 180);
}

/**
 * Latitude 33-41 and longitude 42-51, common to the records read here: a position, "missing" when the fields do not
 * hold coordinates, or "impossible" when they have the shape of coordinates but a value out of range.
 */
function position(line: string) {
  const latText = col(line, 33, 41), lonText = col(line, 42, 51);
  const lat = arincLatitude(latText), lon = arincLongitude(lonText);
  if (lat !== null && lon !== null) return { lat, lon };
  return /^[NS]\d{8}$/.test(latText) && /^[EW]\d{9}$/.test(lonText) ? "impossible" as const : "missing" as const;
}

function navaidType(navaidClass: string, ndb: boolean): NavaidType {
  if (ndb) return "NDB";
  const vor = navaidClass[0] === "V", dme = navaidClass[1] === "D", tacan = navaidClass[1] === "T";
  if (vor && tacan) return "VORTAC";
  if (vor && dme) return "VORDME";
  if (vor) return "VOR";
  return "DME";
}

export function parseArinc424(text: string, options: Arinc424Options = {}): Arinc424Result {
  const entries: NavEntry[] = [];
  const airports = new Map<string, Airport>();
  const airwayFixes = new Map<string, { sequence: number; fix: string }[]>();
  const procedureRecords = new Map<string, ProcedureRecord[]>();
  const pathPoints = new Map<string, PublishedFas>();
  // Path point records that were present but unreadable, by procedure: the approach they belong to has no usable FAS.
  const invalidPathPoints = new Map<string, string>();
  const msa: Msa[] = [];
  const errors: string[] = [];
  const invalid: string[] = [];
  let read = 0, skipped = 0;

  const lines = text.split(/\r?\n/);
  // Limited to named airports and heliports: their own records, and only the enroute fixes and navaids their
  // procedures use.
  const wanted = options.airports?.length ? new Set(options.airports.map(ident => ident.toUpperCase())) : null;
  const used = new Set<string>();
  if (wanted) for (const line of lines) if ((line[4] === "P" || line[4] === "H") && ["F", "D"].includes(line[12]) && wanted.has(line.slice(6, 10).trim())) {
    for (const [a, b] of [[30, 34], [51, 54], [107, 111]] as const) { const fix = col(line, a, b); if (fix) used.add(fix); }
  }
  const cycle = /^HDR01.*?(\d{4})\s+\d{2}-[A-Z]{3}-\d{4}/.exec(lines[0] ?? "")?.[1];
  lines.forEach((line, index) => {
    if (line.length < 60 || (line[0] !== "S" && line[0] !== "T")) { if (line.trim() && !(index < 5 && /^HDR0\d/.test(line))) skipped += 1; return; }
    const section = line[4], subsection = line[5];
    const airportSubsection = line[12];
    if (wanted) {
      if (section === "P" || section === "H" ? !wanted.has(line.slice(6, 10).trim()) : section === "E" && subsection === "A" ? !used.has(col(line, 14, 18))
        : section === "D" ? !used.has(col(line, 14, 17)) : true) return;
    }
    const fail = (what: string) => { errors.push(`line ${index + 1}: ${what}`); skipped += 1; };
    // The position a record needs: without one it is skipped; an impossible one condemns the file.
    const located = (what: string) => {
      const at = position(line);
      if (typeof at === "object") return at;
      if (at === "impossible") impossible(what); else fail(what);
      return null;
    };
    // Field validity beyond shape (R16): a value outside its range condemns the file like an impossible position.
    const impossible = (what: string) => { invalid.push(`line ${index + 1}: impossible ${what}`); skipped += 1; };
    /** An integer field within min..max; `blank` when the field is empty (null makes it required); undefined when unusable. */
    const integer = (a: number, b: number, min: number, max: number, what: string, blank: number | null) => {
      const text = col(line, a, b);
      if (!text) { if (blank === null) { fail(what); return undefined; } return blank; }
      const value = Number(text);
      if (!/^[+-]?\d+$/.test(text) || value < min || value > max) { impossible(what); return undefined; }
      return value;
    };
    /** An identifier: letters and digits. A blank one skips the record; any other character condemns the file. */
    const ident = (a: number, b: number, what: string) => {
      const text = col(line, a, b);
      if (/^[A-Z0-9]+$/.test(text)) return text;
      if (text) impossible(what); else fail(what);
      return null;
    };

    // Enroute waypoint (EA): ident 14-18, continuation 22.
    if (section === "E" && subsection === "A") {
      if (!"01".includes(line[21])) { skipped += 1; return; }
      const name = ident(14, 18, "waypoint ident");
      if (!name) return;
      const at = located("waypoint position");
      if (!at) return;
      entries.push({ kind: "fix", ident: name, position: at });
      read += 1;
      return;
    }
    // Airway (ER): route 14-18, sequence 26-29, fix 30-34, continuation 39.
    if (section === "E" && subsection === "R") {
      if (!"01".includes(line[38])) { skipped += 1; return; }
      const route = ident(14, 18, "airway ident");
      if (!route) return;
      const sequence = integer(26, 29, 0, 9999, "airway sequence", null);
      if (sequence === undefined) return;
      const fix = ident(30, 34, "airway fix");
      if (!fix) return;
      airwayFixes.set(route, [...(airwayFixes.get(route) ?? []), { sequence, fix }]);
      read += 1;
      return;
    }
    // VHF navaid (D blank) and NDB (DB): ident 14-17, continuation 22, frequency 23-27, class 28-32, name 94-123.
    if (section === "D" && (subsection === " " || subsection === "B")) {
      if (!"01".includes(line[21])) { skipped += 1; return; }
      const ndb = subsection === "B";
      const name = ident(14, 17, "navaid ident");
      if (!name) return;
      const vhf = located("navaid position");
      // A DME-only station has no VOR position; it gives its DME position at 56-74, which this reader does not use.
      if (!vhf) return;
      // VHF in tens of kHz, 108.00 to 117.95 MHz; NDB in tenths of a kHz, 190 to 1750 kHz.
      const raw = ndb ? integer(23, 27, 1900, 17500, "NDB frequency", null) : integer(23, 27, 10800, 11795, "VHF frequency", null);
      if (raw === undefined) return;
      const frequency = ndb ? String(raw / 10) : (raw / 100).toFixed(2);
      entries.push({ kind: "navaid", ident: name, type: navaidType(col(line, 28, 32), ndb), position: vhf, frequency, name: col(line, 94, 123) });
      read += 1;
      return;
    }
    // Airport records (P) and heliport records (H), which share their layout: airport or heliport 7-10, subsection at
    // 13. Elevations are feet, -1,500 to 30,000; blank reads as 0.
    if (section === "P" || section === "H") {
      const heliport = section === "H";
      if (airportSubsection === "A") {
        // Airport or heliport reference point: continuation 22, elevation 57-61, name 94-123.
        if (!"01".includes(line[21])) { skipped += 1; return; }
        const icao = ident(7, 10, heliport ? "heliport ident" : "airport ident");
        if (!icao) return;
        const at = located(heliport ? "heliport position" : "airport position");
        if (!at) return;
        const elevation = integer(57, 61, -1500, 30000, "airport elevation", 0);
        if (elevation === undefined) return;
        const existing = airports.get(icao);
        // Magnetic variation 52-56: E or W and tenths of a degree (W0150 is 15.0 degrees west); T is true north.
        const variationText = col(line, 52, 56), variation = /^([EW])(\d{4})$/.exec(variationText);
        if (variationText && !variation && variationText[0] !== "T") { impossible("magnetic variation"); return; }
        const magneticVariation = variation ? (variation[1] === "W" ? -1 : 1) * Number(variation[2]) / 10 : undefined;
        if (magneticVariation !== undefined && Math.abs(magneticVariation) > 180) { impossible("magnetic variation"); return; }
        const airport: Airport = {
          kind: "airport", ident: icao, name: col(line, 94, 123), position: at, elevation, runways: existing?.runways ?? [], magneticVariation,
          ...(heliport ? { heliport: true as const } : {}),
        };
        airports.set(icao, airport);
        read += 1;
        return;
      }
      if (airportSubsection === "S") {
        // Minimum sector altitude: centre fix 14-18, continuation 39, then up to seven sectors of 11 columns from 43
        // (bearing from 3, bearing to 3, altitude in hundreds of feet 3, radius in NM 2), magnetic or true at 120.
        if (!"01".includes(line[38])) { skipped += 1; return; }
        const icao = ident(7, 10, heliport ? "heliport ident" : "airport ident"), centre = icao && ident(14, 18, "MSA centre");
        if (!icao || !centre) return;
        const sectors: Msa["sectors"] = [];
        for (let start = 43; start + 10 <= 119; start += 11) {
          const text = line.slice(start - 1, start + 10);
          if (!text.trim()) break;
          const m = /^(\d{3})(\d{3})(\d{3})(\d{2})$/.exec(text);
          if (!m || Number(m[1]) > 360 || Number(m[2]) > 360) { impossible("MSA sector"); return; }
          sectors.push({ from: Number(m[1]), to: Number(m[2]), altitude: Number(m[3]) * 100, radiusNm: Number(m[4]) });
        }
        if (!sectors.length) { fail("MSA without a sector"); return; }
        msa.push({ airport: icao, centre, magnetic: line[119] !== "T", sectors });
        read += 1;
        return;
      }
      if (airportSubsection === "G" && !heliport) {
        // Runway: ident 14-18, continuation 22, length 23-27 (feet), magnetic bearing 28-31 (tenths of a degree, up to
        // 360.0), threshold elevation 67-71.
        if (!"01".includes(line[21])) { skipped += 1; return; }
        const icao = ident(7, 10, "airport ident"), runway = icao && ident(14, 18, "runway ident");
        if (!icao || !runway) return;
        const at = located("runway threshold");
        if (!at) return;
        const length = integer(23, 27, 0, 30000, "runway length", 0);
        const bearing = length === undefined ? undefined : integer(28, 31, 0, 3600, "runway bearing", 0);
        const elevation = bearing === undefined ? undefined : integer(67, 71, -1500, 30000, "runway threshold elevation", 0);
        if (length === undefined || bearing === undefined || elevation === undefined) return;
        const airport = airports.get(icao) ?? { kind: "airport" as const, ident: icao, name: "", position: at, elevation: 0, runways: [] };
        airport.runways.push({ ident: runway, threshold: at, course: bearing / 10, elevation, length });
        airports.set(icao, airport);
        read += 1;
        return;
      }
      if (airportSubsection === "F" || airportSubsection === "D") {
        // Approach procedure leg: procedure 14-19, route type 20, transition 21-25, sequence 27-29, fix 30-34,
        // continuation 39 (primary 0 or 1), waypoint description 40-43, turn 44, path terminator 48-49, magnetic course
        // 71-74 (tenths; T after is true), holding distance or time 75-78 (tenths of a NM, or T and tenths of a
        // minute), altitude description 83, ATC indicator 84, altitudes 85-89 and 90-94, speed limit 100-102 (knots),
        // vertical angle 103-106 (hundredths of a degree, signed), speed limit description 118.
        if (!"01".includes(line[38])) { skipped += 1; return; }
        const icao = ident(7, 10, heliport ? "heliport ident" : "airport ident");
        if (!icao) return;
        const key = `${icao} ${col(line, 14, 19)} ${airportSubsection}`;
        procedureRecords.set(key, [...(procedureRecords.get(key) ?? []), {
          kind: airportSubsection === "D" ? "SID" : "APPROACH",
          recommended: col(line, 51, 54), centre: col(line, 107, 111), radius: col(line, 57, 62), rho: col(line, 67, 70),
          routeType: line[19], transition: col(line, 21, 25), sequence: Number(col(line, 27, 29)), fix: col(line, 30, 34),
          description: line.slice(39, 43), turn: line[43], path: col(line, 48, 49), course: col(line, 71, 74),
          distance: col(line, 75, 78), altitudeDescription: line[82], altitude1: col(line, 85, 89), altitude2: col(line, 90, 94),
          speed: col(line, 100, 102), verticalAngle: col(line, 103, 106), speedDescription: line[117] ?? " ",
        }]);
        read += 1;
        return;
      }
      if (airportSubsection === "P" && !heliport) {
        // Path point (FAS data block), primary record only (continuation number 27 is 1).
        if (line[26] !== "1") { skipped += 1; return; }
        const icao = ident(7, 10, "airport ident");
        if (!icao) return;
        const fas = pathPoint(line, icao);
        if (typeof fas === "string") { impossible(fas); invalidPathPoints.set(`${icao} ${col(line, 14, 19)}`, fas); return; }
        pathPoints.set(`${icao} ${col(line, 14, 19)}`, fas);
        read += 1;
        return;
      }
      if (airportSubsection === "C") {
        // Terminal waypoint: ident 14-18, continuation 22.
        if (!"01".includes(line[21])) { skipped += 1; return; }
        const name = ident(14, 18, heliport ? "heliport terminal waypoint ident" : "terminal waypoint ident");
        if (!name) return;
        const at = located("terminal waypoint position");
        if (!at) return;
        entries.push({ kind: "fix", ident: name, position: at });
        read += 1;
        return;
      }
    }
    skipped += 1;
  });

  const airways: Airway[] = [...airwayFixes].map(([ident, fixes]) => ({ ident, fixes: fixes.sort((a, b) => a.sequence - b.sequence).map(f => f.fix) }));
  // Runway bearings are magnetic: made true with the airport's variation (unchanged where the data gives none).
  for (const airport of airports.values()) {
    const variation = airport.magneticVariation ?? 0;
    for (const runway of airport.runways) runway.course = ((runway.course + variation) % 360 + 360) % 360;
  }
  const procedures: Procedure[] = [];
  const positions = new Map<string, { lat: number; lon: number }>();
  for (const entry of entries) if (!positions.has(entry.ident)) positions.set(entry.ident, entry.position);
  for (const [key, records] of procedureRecords) {
    const [icao, ident] = key.split(" ");
    const site = airports.get(icao), place = (name: string) => positions.get(name) ?? site?.runways.find(r => r.ident === name)?.threshold;
    const built = records[0].kind === "SID" ? buildDeparture(icao, ident, records, site, place)
      : buildApproach(icao, ident, records, site, pathPoints.get(`${icao} ${ident}`), place, invalidPathPoints.get(`${icao} ${ident}`));
    if (typeof built === "string") errors.push(`${icao} ${ident}: ${built}`);
    else if (built) procedures.push(built);
  }
  return {
    data: {
      cycle: { id: cycle ? `CIFP${cycle}` : "LOADED", from: "", to: "" }, entries: [...entries, ...airports.values()], airways, procedures,
      ...(msa.length ? { msa } : {}),
    },
    read, skipped, errors: errors.slice(0, 20), invalid,
  };
}

/**
 * Copter point-in-space approaches in the airport section: the FAA codes a Copter procedure by its final approach
 * course (R027, R250) where a runway approach carries its runway (R15, R33-Y). This is the coding convention the
 * public CIFP 2609 shows (its five Copter point-in-space approaches, and no other procedure, have such an ident), an
 * observation and not a rule of the specification. Heliport-section approaches are point-in-space by their section.
 */
const COPTER_COURSE_IDENT = /^[A-Z]\d{3}$/;

/**
 * The airport-section Copter point-in-space approaches that have been reviewed (plan Q8; Astra's answer 8): the course
 * ident is accepted as identifying one only within this bounded set, each pinned by its airport, ident and MAP as read
 * from the FAA CIFP 2609 Copter extract (#1256). An airport-section approach that matches the ident pattern but is not
 * in the set, or whose MAP differs from the one reviewed, is refused with the reason rather than imported as
 * point-in-space on the pattern alone: taking it for one would make it assert a landing site and a MAP that nobody
 * checked. Adding a procedure here is the review.
 */
const REVIEWED_COPTER_PINS: Readonly<Record<string, { map: string; source: string }>> = {
  "KJFK R027": { map: "HELOG", source: "FAA CIFP 2609 Copter extract: KJFK R027, MAP HELOG (reviewed, #1256)" },
  "KLGA R250": { map: "WITKN", source: "FAA CIFP 2609 Copter extract: KLGA R250, MAP WITKN (reviewed, #1256)" },
  "2P2 R029": { map: "OBIBE", source: "FAA CIFP 2609 Copter extract: 2P2 R029, MAP OBIBE (reviewed, #1256)" },
};

/**
 * An RNAV approach from its leg records: transitions (route type A) to the final (route type R), whose final approach
 * fix is the leg described F and whose missed approach is everything after the missed approach point (described M).
 * The MAP is a runway, or on a point-in-space approach (a heliport's, or a Copter procedure's) a fix. null for other
 * approach types; a reason when the procedure uses an unsupported leg or its end
 * cannot be placed.
 *
 * A hold (HF, HA, HM) is kept on the fix leg it holds at, with its exit and its coded leg distance or time. Each
 * transition keeps all its legs, including the fix the final begins at: the route joins them by the records' roles
 * (procedures.ts), never by collapsing records that share a name.
 */
function buildDeparture(icao: string, ident: string, records: ProcedureRecord[], site: Airport | undefined,
  place: (ident: string) => { lat: number; lon: number } | undefined): Procedure | string {
  if (!site) return "departure without its airport or heliport record";
  const common: ProcedureRecord[] = [], transitions: Record<string, ProcedureLeg[]> = {}, runwayTransitions: Record<string, ProcedureLeg[]> = {};
  const entries: NonNullable<Procedure["departure"]>["entries"] = {};
  for (const r of records) if ("25M".includes(r.routeType)) common.push(r);
  const decode = (list: ProcedureRecord[]) => decodeProcedureLegs(list.sort((a, b) => a.sequence - b.sequence), site.magneticVariation ?? 0, place);
  const legs = decode(common);
  if (typeof legs === "string") return legs;
  const firstCommon = legs[0];
  if (firstCommon && "ident" in firstCommon) entries[""] = { fix: firstCommon.ident, ...(firstCommon.altitude ? { altitude: firstCommon.altitude } : {}) };
  for (const name of new Set(records.filter(r => "36SV".includes(r.routeType)).map(r => r.transition))) {
    if (!name) return "departure without transition identifier";
    const built = decode(records.filter(r => "36SV".includes(r.routeType) && r.transition === name));
    if (typeof built === "string") return built;
    transitions[name] = built;
    const first = [...legs, ...built][0];
    if (first && "ident" in first) entries[name] = { fix: first.ident, ...(first.altitude ? { altitude: first.altitude } : {}) };
  }
  for (const name of new Set(records.filter(r => "14FT".includes(r.routeType)).map(r => r.transition))) {
    const built = decode(records.filter(r => "14FT".includes(r.routeType) && r.transition === name));
    if (typeof built === "string") return built;
    runwayTransitions[name] = built;
  }
  if (records.some(r => !"123456FMSTV".includes(r.routeType))) return "unsupported departure route type";
  if (!legs.length && !Object.keys(transitions).length && !Object.keys(runwayTransitions).length) return "departure without instrument legs";
  const chart = DEPARTURE_CHARTS[`${icao} ${ident}`], entry = chart && entries[chart.transition];
  if (chart && (!entry || entry.fix !== chart.fix || entry.altitude !== chart.altitude)) return "departure IDF does not agree with its reviewed chart";
  for (const leg of [...legs, ...Object.values(transitions).flat(), ...Object.values(runwayTransitions).flat()]) {
    if ("ident" in leg && !leg.position && !place(leg.ident)) return `departure fix ${leg.ident} is missing`;
  }
  return { kind: "SID", airport: icao, ident, legs, transitions, runways: Object.keys(runwayTransitions),
    ...(Object.keys(runwayTransitions).length ? { runwayTransitions } : {}),
    ...(site.heliport ? { departure: { entries, visualSegment: chart ? { kind: chart.visualSegment, source: chart.source } : { kind: "UNKNOWN" } } } : {}) };
}

function buildApproach(icao: string, ident: string, records: ProcedureRecord[], site: Airport | undefined, fas: PublishedFas | undefined,
  place: (ident: string) => { lat: number; lon: number } | undefined, fasInvalid?: string): Procedure | string | null {
  const finalType = records.find(r => "RPDVSNQ".includes(r.routeType))?.routeType;
  if (!finalType) return null;
  const final = [...records.filter(r => r.routeType === finalType).sort((a, b) => a.sequence - b.sequence),
    ...records.filter(r => r.routeType === "Z").sort((a, b) => a.sequence - b.sequence)];
  if (!final.length) return null;
  const variation = site?.magneticVariation ?? 0;
  const legs = (list: ProcedureRecord[]) => decodeProcedureLegs(list, variation, place);
  const mapAt = final.findIndex(r => r.description[3] === "M");
  if (mapAt < 0) return "no missed approach point";
  const approachLegs = legs(final.slice(0, mapAt + 1));
  // The MAP is flown over, never turned short of: the missed approach starts there.
  if (typeof approachLegs !== "string") { const last = approachLegs.at(-1); if (last && "ident" in last) approachLegs[approachLegs.length - 1] = { ...last, overfly: true }; }
  const missed = legs(final.slice(mapAt + 1));
  if (typeof approachLegs === "string") return approachLegs;
  if (typeof missed === "string") return missed;
  const map = final[mapAt];
  const isRunway = /^RW\d{2}[LRC]?$/.test(map.fix);
  // An airport-section procedure with a Copter course ident is point-in-space only when it was reviewed (Q8).
  const copterIdent = !isRunway && site?.heliport !== true && COPTER_COURSE_IDENT.test(ident);
  const reviewed = copterIdent ? REVIEWED_COPTER_PINS[`${icao} ${ident}`] : undefined;
  if (copterIdent && !reviewed) return `Copter point-in-space identification not reviewed: ${ident} has a Copter course ident and its MAP ${map.fix} is not a runway`;
  if (reviewed && reviewed.map !== map.fix) return `Copter point-in-space identification not reviewed: reviewed with MAP ${reviewed.map}, this data codes ${map.fix}`;
  const pointInSpace = !isRunway && (site?.heliport === true || reviewed !== undefined);
  if (!isRunway && !pointInSpace) return "missed approach point is not a runway";
  if (pointInSpace && !site) return "point-in-space approach without its landing site record";
  if (pointInSpace && !place(map.fix)) return `point-in-space approach whose missed approach point ${map.fix} is not in the data`;
  const transitions: Record<string, ProcedureLeg[]> = {};
  for (const name of new Set(records.filter(r => r.routeType === "A").map(r => r.transition))) {
    const built = legs(records.filter(r => r.routeType === "A" && r.transition === name).sort((a, b) => a.sequence - b.sequence));
    if (typeof built === "string") return `transition ${name}: ${built}`;
    transitions[name] = built;
  }
  const missedHoldLeg = missed.find(l => "ident" in l && l.hold?.path === "HM");
  const hold = missedHoldLeg && "ident" in missedHoldLeg ? missedHoldLeg.hold : undefined;
  const chart = PROCEDURE_CHARTS[`${icao} ${ident}`];
  const mapAngle = codedVerticalAngle(map.verticalAngle);
  let visualSegment: ProcedureEndpoint["visualSegment"];
  if (isRunway) visualSegment = { kind: "RUNWAY", validated: true, source: "the MAP is the runway threshold (coded)" };
  else {
    const from = place(map.fix)!, to = site!.position;
    const bearingMag = ((bearingDeg(from, to) - variation) % 360 + 360) % 360;
    visualSegment = chart
      ? { kind: chart.visualSegment, validated: true, source: chart.source, bearingMag, distanceNm: distanceNm(from, to) }
      : { kind: "UNKNOWN", validated: false, bearingMag, distanceNm: distanceNm(from, to) };
  }
  const endpoint: ProcedureEndpoint = {
    instrumentEnd: { fix: map.fix, ...(constraintText(map) ? { altitude: constraintText(map) } : {}) },
    landingSite: isRunway ? { kind: "RUNWAY", ident: map.fix, airport: icao } : { kind: site!.heliport ? "HELIPORT" : "AIRPORT", ident: icao, airport: icao },
    visualSegment,
    vertical: fas ? { kind: "VPA", angleDeg: fas.gpaDeg }
      : mapAngle === 0 ? { kind: "NONE", reason: "vertical angle 000 at the MAP: flown LNAV, step-down altitudes advisory" }
        : mapAngle !== undefined ? { kind: "VPA", angleDeg: Math.abs(mapAngle) } : { kind: "NOT CODED" },
    identification: isRunway ? { basis: "RUNWAY", source: "the MAP is the runway threshold (coded)" }
      : reviewed ? { basis: "REVIEWED COPTER PROCEDURE", source: reviewed.source }
        : { basis: "HELIPORT SECTION", source: `a heliport-section approach (${icao}): point-in-space by its section` },
  };
  return {
    kind: "APPROACH", airport: icao, ident, runways: isRunway ? [map.fix] : [], transitions, legs: approachLegs,
    approachType: "RP".includes(finalType) ? "RNAV" : "NQ".includes(finalType) ? "NDB" : "VOR",
    faf: final.find(r => r.description[3] === "F")?.fix, missed,
    ...(hold ? {
      missedHold: {
        fix: (missedHoldLeg as { ident: string }).ident, inbound: Math.round(hold.inbound), turn: hold.turn, altitude: hold.altitude ?? "",
        ...(hold.legDistanceNm !== null ? { legDistanceNm: hold.legDistanceNm } : {}), ...(hold.speedLimit ? { speedLimit: hold.speedLimit } : {}),
      },
    } : {}),
    ...(fas ? { publishedFas: fas } : {}),
    // Intentionally LNAV only only when the data says there is no vertical path: no path point record and vertical angle
    // 000 at the MAP. A missing FAS with a vertical path coded, or an unreadable one, is not LNAV only (Astra Q4).
    ...(!fas && !fasInvalid && mapAngle === 0 ? { lnavOnly: { source: "no path point record, and vertical angle 000 at the MAP (no vertical path published)" } } : {}),
    ...(fasInvalid ? { fasInvalid } : {}),
    endpoint,
    ...(pointInSpace ? { pointInSpace: true } : {}),
    ...(chart ? { notes: chart.notes } : {}),
  } as Procedure;
}

/** Latitude "N4427519665" and longitude "W07309040730": degrees, minutes, seconds and ten-thousandths of a second. */
function preciseAngle(text: string, latitude: boolean) {
  const m = (latitude ? /^([NS])(\d{2})(\d{2})(\d{2})(\d{4})$/ : /^([EW])(\d{3})(\d{2})(\d{2})(\d{4})$/).exec(text);
  if (!m) return null;
  const [degrees, minutes, seconds, fraction] = [m[2], m[3], m[4], m[5]].map(Number);
  if (minutes > 59 || seconds > 59) return null;
  const value = degrees + minutes / 60 + (seconds + fraction / 10000) / 3600;
  if (value > (latitude ? 90 : 180)) return null;
  return m[1] === "S" || m[1] === "W" ? -value : value;
}

/**
 * The FAS data block of a path point record (ARINC 424-18, as the FAA CIFP publishes it): operation type 25-26, route
 * indicator 28, SBAS provider 29-30, reference path data selector 31-32, reference path identifier 33-36, approach
 * performance designator 37, LTP latitude 38-48 and longitude 49-60, LTP ellipsoid height 61-66 (tenths of a metre),
 * glide path angle 67-70 (hundredths), FPAP latitude 71-81 and longitude 82-93, course width 94-98 (hundredths of a
 * metre), length offset 99-102 (metres), TCH 103-108 (tenths, in the unit at 109), HAL 110-112 and VAL 113-115
 * (tenths of a metre), CRC 116-123. A reason when a field is out of range.
 */
function pathPoint(line: string, airport: string): PublishedFas | string {
  const number = (a: number, b: number) => { const t = col(line, a, b); return /^[+-]?\d+$/.test(t) ? Number(t) : NaN; };
  const ltpLat = preciseAngle(col(line, 38, 48), true), ltpLon = preciseAngle(col(line, 49, 60), false);
  const fpapLat = preciseAngle(col(line, 71, 81), true), fpapLon = preciseAngle(col(line, 82, 93), false);
  if (ltpLat === null || ltpLon === null) return "path point LTP position";
  if (fpapLat === null || fpapLon === null) return "path point FPAP position";
  const height = number(61, 66) / 10, gpa = number(67, 70) / 100, width = number(94, 98) / 100, offset = number(99, 102);
  const tchRaw = number(103, 108) / 10, tch = line[108] === "M" ? tchRaw / 0.3048 : tchRaw;
  const hal = number(110, 112) / 10, val = number(113, 115) / 10;
  if (![height, gpa, width, offset, tch, hal, val].every(Number.isFinite)) return "path point numeric field";
  if (gpa <= 0 || gpa > 10) return "path point glide path angle";
  if (width <= 0 || width > 400 || hal <= 0 || val < 0 || tch < 0 || tch > 200) return "path point limits";
  const runway = /^RW(\d{2})([LRC]?)/.exec(col(line, 20, 24));
  if (!runway) return "path point runway";
  const crc = col(line, 116, 123);
  if (!/^[0-9A-F]{8}$/.test(crc)) return "path point CRC";
  return {
    operationType: number(25, 26) || 0, sbasProvider: number(29, 30) || 0, airport, runway: Number(runway[1]),
    designator: runway[2] as PublishedFas["designator"], performance: number(37, 37) || 0, routeIndicator: col(line, 28, 28),
    referencePathSelector: number(31, 32) || 0, referencePathId: col(line, 33, 36),
    ltp: { lat: ltpLat, lon: ltpLon, heightM: height }, fpapDelta: { lat: fpapLat - ltpLat, lon: fpapLon - ltpLon },
    tchFt: Math.round(tch * 10) / 10, gpaDeg: gpa, courseWidthM: width, lengthOffsetM: offset, halM: hal, valM: val, publishedCrc: crc,
  };
}

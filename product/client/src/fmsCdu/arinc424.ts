import type { Airport, Airway, NavData, NavEntry, NavaidType } from "./navData";

/**
 * Reads the parts of an ARINC 424 navigation data file that the simulation uses: enroute and terminal waypoints,
 * VHF and NDB navaids, airports, runways and airways. Records are the 132-column fixed-width lines of the
 * specification; only primary records are read (continuation records are skipped), and procedure records are not
 * read yet. Field positions are the ones listed beside each reader, 1-based as the specification numbers columns.
 *
 * This is a subset reader for engineering use in the test bench. It has been checked against the specification's
 * field layout, not qualified against a supplier's data file.
 */

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

export function parseArinc424(text: string): Arinc424Result {
  const entries: NavEntry[] = [];
  const airports = new Map<string, Airport>();
  const airwayFixes = new Map<string, { sequence: number; fix: string }[]>();
  const errors: string[] = [];
  const invalid: string[] = [];
  let read = 0, skipped = 0;

  const lines = text.split(/\r?\n/);
  lines.forEach((line, index) => {
    if (line.length < 60 || (line[0] !== "S" && line[0] !== "T")) { if (line.trim()) skipped += 1; return; }
    const section = line[4], subsection = line[5];
    const airportSubsection = line[12];
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
    // Airport records (P): airport 7-10, subsection at 13. Elevations are feet, -1,500 to 30,000; blank reads as 0.
    if (section === "P") {
      if (airportSubsection === "A") {
        // Airport reference point: continuation 22, elevation 57-61, name 94-123.
        if (!"01".includes(line[21])) { skipped += 1; return; }
        const icao = ident(7, 10, "airport ident");
        if (!icao) return;
        const at = located("airport position");
        if (!at) return;
        const elevation = integer(57, 61, -1500, 30000, "airport elevation", 0);
        if (elevation === undefined) return;
        const existing = airports.get(icao);
        const airport: Airport = { kind: "airport", ident: icao, name: col(line, 94, 123), position: at, elevation, runways: existing?.runways ?? [] };
        airports.set(icao, airport);
        read += 1;
        return;
      }
      if (airportSubsection === "G") {
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
      if (airportSubsection === "C") {
        // Terminal waypoint: ident 14-18, continuation 22.
        if (!"01".includes(line[21])) { skipped += 1; return; }
        const name = ident(14, 18, "terminal waypoint ident");
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
  return {
    data: { cycle: { id: "LOADED", from: "", to: "" }, entries: [...entries, ...airports.values()], airways, procedures: [] },
    read, skipped, errors: errors.slice(0, 20), invalid,
  };
}

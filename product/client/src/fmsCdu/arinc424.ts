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

export type Arinc424Result = { data: NavData; read: number; skipped: number; errors: string[] };

/** Columns a..b inclusive, 1-based. */
const col = (line: string, a: number, b: number) => line.slice(a - 1, b).trim();

/** Latitude "N45183600" and longitude "W075405000" (hemisphere, degrees, minutes, seconds, hundredths). */
export function arincLatitude(text: string) {
  const m = /^([NS])(\d{2})(\d{2})(\d{2})(\d{2})$/.exec(text);
  if (!m) return null;
  const value = Number(m[2]) + Number(m[3]) / 60 + (Number(m[4]) + Number(m[5]) / 100) / 3600;
  return m[1] === "S" ? -value : value;
}

export function arincLongitude(text: string) {
  const m = /^([EW])(\d{3})(\d{2})(\d{2})(\d{2})$/.exec(text);
  if (!m) return null;
  const value = Number(m[2]) + Number(m[3]) / 60 + (Number(m[4]) + Number(m[5]) / 100) / 3600;
  return m[1] === "W" ? -value : value;
}

/** Latitude 33-41 and longitude 42-51, common to the records read here. */
function position(line: string) {
  const lat = arincLatitude(col(line, 33, 41)), lon = arincLongitude(col(line, 42, 51));
  return lat === null || lon === null ? null : { lat, lon };
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
  let read = 0, skipped = 0;

  const lines = text.split(/\r?\n/);
  lines.forEach((line, index) => {
    if (line.length < 60 || (line[0] !== "S" && line[0] !== "T")) { if (line.trim()) skipped += 1; return; }
    const section = line[4], subsection = line[5];
    const airportSubsection = line[12];
    const fail = (what: string) => { errors.push(`line ${index + 1}: ${what}`); skipped += 1; };

    // Enroute waypoint (EA): ident 14-18, continuation 22.
    if (section === "E" && subsection === "A") {
      if (!"01".includes(line[21])) { skipped += 1; return; }
      const at = position(line);
      if (!at) return fail("waypoint position");
      entries.push({ kind: "fix", ident: col(line, 14, 18), position: at });
      read += 1;
      return;
    }
    // Airway (ER): route 14-18, sequence 26-29, fix 30-34, continuation 39.
    if (section === "E" && subsection === "R") {
      if (!"01".includes(line[38])) { skipped += 1; return; }
      const route = col(line, 14, 18), sequence = Number(col(line, 26, 29)), fix = col(line, 30, 34);
      if (!route || !fix || Number.isNaN(sequence)) return fail("airway fix");
      airwayFixes.set(route, [...(airwayFixes.get(route) ?? []), { sequence, fix }]);
      read += 1;
      return;
    }
    // VHF navaid (D blank) and NDB (DB): ident 14-17, continuation 22, frequency 23-27, class 28-32, name 94-123.
    if (section === "D" && (subsection === " " || subsection === "B")) {
      if (!"01".includes(line[21])) { skipped += 1; return; }
      const ndb = subsection === "B";
      const vhf = position(line);
      // A DME-only station has no VOR position; it gives its DME position at 56-74, which this reader does not use.
      if (!vhf) return fail("navaid position");
      const raw = col(line, 23, 27);
      const frequency = ndb ? String(Number(raw) / 10) : (Number(raw) / 100).toFixed(2);
      entries.push({ kind: "navaid", ident: col(line, 14, 17), type: navaidType(col(line, 28, 32), ndb), position: vhf, frequency, name: col(line, 94, 123) });
      read += 1;
      return;
    }
    // Airport records (P): airport 7-10, subsection at 13.
    if (section === "P") {
      const icao = col(line, 7, 10);
      if (airportSubsection === "A") {
        // Airport reference point: continuation 22, elevation 57-61, name 94-123.
        if (!"01".includes(line[21])) { skipped += 1; return; }
        const at = position(line);
        if (!at) return fail("airport position");
        const existing = airports.get(icao);
        const airport: Airport = { kind: "airport", ident: icao, name: col(line, 94, 123), position: at, elevation: Number(col(line, 57, 61)) || 0, runways: existing?.runways ?? [] };
        airports.set(icao, airport);
        read += 1;
        return;
      }
      if (airportSubsection === "G") {
        // Runway: ident 14-18, continuation 22, length 23-27, magnetic bearing 28-31 (tenths), threshold elevation 67-71.
        if (!"01".includes(line[21])) { skipped += 1; return; }
        const at = position(line);
        if (!at) return fail("runway threshold");
        const airport = airports.get(icao) ?? { kind: "airport" as const, ident: icao, name: "", position: at, elevation: 0, runways: [] };
        airport.runways.push({ ident: col(line, 14, 18), threshold: at, course: Number(col(line, 28, 31)) / 10, elevation: Number(col(line, 67, 71)) || 0, length: Number(col(line, 23, 27)) || 0 });
        airports.set(icao, airport);
        read += 1;
        return;
      }
      if (airportSubsection === "C") {
        // Terminal waypoint: ident 14-18, continuation 22.
        if (!"01".includes(line[21])) { skipped += 1; return; }
        const at = position(line);
        if (!at) return fail("terminal waypoint position");
        entries.push({ kind: "fix", ident: col(line, 14, 18), position: at });
        read += 1;
        return;
      }
    }
    skipped += 1;
  });

  const airways: Airway[] = [...airwayFixes].map(([ident, fixes]) => ({ ident, fixes: fixes.sort((a, b) => a.sequence - b.sequence).map(f => f.fix) }));
  return {
    data: { cycle: { id: "LOADED", from: "", to: "" }, entries: [...entries, ...airports.values()], airways, procedures: [] },
    read, skipped, errors: errors.slice(0, 20),
  };
}

import { parseArinc424 } from "./arinc424";
import { COPTER_PINS_CIFP_2609, COPTER_PINS_CIFP_2609_SHA256 } from "./data/copterPinsCifp2609";
import { offset, type LatLon } from "./fmsModel";
import { MISSION_87N_OFFSHORE_SAR, MISSION_START_ALTITUDE_FT, MISSION_START_IAS, MISSION_START_SOUTH_NM, MISSION_WIND } from "./heliDemo";
import { pairedChannel, type Navaid, type NavaidElevation, type NavaidType } from "./navData";
import { HELICOPTER_PROFILE, profileFingerprint, type AircraftProfile } from "./profile";

/**
 * The Stage F acceptance mission's reproducible fixture (FMS_STAGE_F_PLAN.md F15; Astra's integration item 7).
 *
 * The real data, FAA CIFP 2609's Copter PinS extract, has two radio stations near 87N, HTO (VORTAC) and CCC
 * (VOR/DME): too few for the coastal DME/DME, VOR/DME and TACAN checks of F6, F7 and F15. The coverage they need is
 * added as **synthetic** stations, declared here, labelled synthetic in their data and in the manifest, and never
 * mixed into the CIFP data: they are a separate set the mission adds alongside it.
 */
export const SYNTHETIC_PROVENANCE = "synthetic, declared for the Stage F mission";

/**
 * The ground elevation under a declared site (F1's `terrain` source): the Terrarium tiles (AWS open data, SRTM and
 * national elevation models), zoom 14, tiles dated 13 November 2017, read on 1 October 2026. The ground's height,
 * not an antenna's: the site is invented. The same reading gives 22 ft at HTO, its ARINC 424 DME elevation.
 */
const declaredGround = (feet: number): NavaidElevation => Object.freeze({
  feet, source: "terrain" as const,
  provenance: `${SYNTHETIC_PROVENANCE}: Terrarium ground elevation, zoom 14 (tiles of 13 November 2017), read 1 October 2026; the ground under the declared site, not an antenna`,
});

/** A synthetic station: an ordinary navaid in shape, flagged so that no consumer can mistake it for data. */
export type SyntheticNavaid = Navaid & { readonly synthetic: { readonly declaredFor: "STAGE F MISSION"; readonly role: string } };

const station = (ident: string, type: NavaidType, lat: number, lon: number, frequency: string, name: string, feet: number, role: string): SyntheticNavaid => {
  const channel = pairedChannel(Number(frequency));
  if (!channel) throw new Error(`${ident}: ${frequency} has no Annex 10 DME pairing`);
  return Object.freeze({
    kind: "navaid" as const, ident, type, position: Object.freeze({ lat, lon }), frequency, name, elevation: declaredGround(feet), channel,
    synthetic: Object.freeze({ declaredFor: "STAGE F MISSION" as const, role }),
  });
};

/**
 * The declared coastal stations, on land around 87N (Southampton, N40 50.8 W072 28.0). Their idents begin with Q,
 * which no US navaid uses, their names say SYNTHETIC, and their frequencies are free of the real stations' (HTO
 * 113.60, CCC 114.55, COL 115.40). With HTO and CCC they give the six-station scan of F6 and its geometry offshore
 * and along the coast, a VOR/DME for F7's AUTO-tuned case, and a TACAN for VOR/DME/TCN.
 */
export const SYNTHETIC_COASTAL_STATIONS: readonly SyntheticNavaid[] = Object.freeze([
  station("QWHM", "VORDME", 40.8437, -72.6318, "108.65", "WESTHAMPTON SYNTHETIC", 54, "VOR/DME west of 87N: F7's AUTO-tuned VOR/DME, and a DME/DME range"),
  station("QFIS", "DME", 40.7343, -72.8650, "109.25", "SMITH POINT SYNTHETIC", 6, "DME on the barrier beach to the west-southwest: DME/DME geometry offshore"),
  station("QORP", "DME", 41.1590, -72.2420, "112.05", "ORIENT POINT SYNTHETIC", 14, "DME to the north-northeast: DME/DME geometry across the bays"),
  station("QMTK", "DME", 41.0700, -71.8600, "111.85", "MONTAUK SYNTHETIC", 41, "DME to the east: the station F15 biases for isolation"),
  station("QBIX", "TACAN", 41.1681, -71.5778, "115.70", "BLOCK ISLAND SYNTHETIC", 107, "TACAN to the east: F7's VOR/DME/TCN, and a DME/DME range (DEC-150)"),
]);

/** The real stations near 87N in the CIFP extract that the coastal segment uses. */
export const REAL_COASTAL_STATIONS = ["HTO", "CCC"] as const;

/** A data file of the mission, named with its SHA-256 (checked against the text by the owner test). */
export type MissionDataFile = { name: string; cycle: string; sha256: string; source: string };

/** A fault and when it is applied: seconds after the segment's start, or after a named event of the run. */
export type MissionFault = { at: number; after?: "FAF"; fault: string; detail: string; item: string };

/**
 * An expected result, computed independently of the code under test (F15). Left as a slot, with the item that will
 * fill it, until that item lands: a slot is never read as a pass.
 */
export type ExpectedSlot = { check: string; item: string; value: "TODO"; tolerance: "TODO" };

export type MissionSegment = {
  id: string; title: string;
  initialState: { position: LatLon; altitudeFt: number; iasKt: number; headingDeg: number; wind: { direction: number; speed: number } };
  facilities: string[];
  faults: MissionFault[];
  expected: ExpectedSlot[];
};

export type StageFMissionManifest = {
  schema: "aerolink.fms.stage-f-mission/1";
  title: string;
  profile: { id: string; version: number; fingerprint: string };
  navData: { cycle: string; files: MissionDataFile[] };
  facilities: { real: string[]; synthetic: { ident: string; type: NavaidType; position: LatLon; frequency: string; channel: string; elevation: NavaidElevation; role: string }[] };
  procedures: { airport: string; ident: string; source: string }[];
  clock: { start: string; tickSeconds: number };
  segments: MissionSegment[];
  /**
   * F15's NDB variants (F16), each starting separately at its own airport on its own CIFP 2609 fixture file. Their
   * start states wait for F16's flight item; the fixture files are named here by SHA-256.
   */
  ndbVariants: { id: string; airport: string; procedure: string; file: string; faults: MissionFault[]; expected: ExpectedSlot[] }[];
};

/**
 * The F16 fixture files (tests/fixtures/cifp, from FAA CIFP_260903.zip, LF line endings by .gitattributes) and their
 * SHA-256, checked against the files by the owner test.
 */
export const NDB_FIXTURE_FILES: readonly MissionDataFile[] = Object.freeze([
  { name: "tests/fixtures/cifp/kiag-2609.pc", cycle: "2609", sha256: "0e374750f220fe560fd95367cbba0e82577dc4b08f94d9a908b7cf8f0b7ffac3", source: "FAA CIFP 2609, KIAG records (F16)" },
  { name: "tests/fixtures/cifp/pasd-2609.pc", cycle: "2609", sha256: "36337597eec6df80006a4038427ef9ebc9c49e006ebe10e9cad48ec435c441aa", source: "FAA CIFP 2609, PASD records (F16)" },
]);

const slot = (check: string, item: string): ExpectedSlot => ({ check, item, value: "TODO", tolerance: "TODO" });

/** Where the 87N heliport is, from the CIFP extract itself. */
function heliport87n(): LatLon {
  const parsed = parseArinc424(COPTER_PINS_CIFP_2609);
  const site = parsed.data.entries.find(entry => entry.kind === "airport" && entry.ident === "87N");
  if (!site) throw new Error("87N is not in the CIFP extract");
  return site.position;
}

/**
 * The mission's manifest, built only from declared inputs: the same inputs give the same manifest, and so the same
 * fixture hash. Nothing here reads the clock, the host or a random source.
 */
export function stageFMissionManifest(profile: AircraftProfile = HELICOPTER_PROFILE): StageFMissionManifest {
  const site = heliport87n();
  const offshore = offset(site, 180, MISSION_START_SOUTH_NM);
  // The coastal segment starts 8 NM south-southwest of 87N, offshore of the barrier beach, inside the stations' geometry.
  const coastal = offset(site, 210, 8);
  return {
    schema: "aerolink.fms.stage-f-mission/1",
    title: "Stage F acceptance mission (F15): offshore from 87N without radio coverage, then the coast with DME and TACAN",
    profile: { id: profile.id, version: profile.version, fingerprint: profileFingerprint(profile) },
    navData: {
      cycle: "2609",
      files: [
        { name: "src/fmsCdu/data/copterPinsCifp2609.ts (COPTER_PINS_CIFP_2609)", cycle: "2609", sha256: COPTER_PINS_CIFP_2609_SHA256, source: "FAA CIFP 2609, Copter PinS extract" },
        ...NDB_FIXTURE_FILES.map(file => ({ ...file })),
      ],
    },
    facilities: {
      real: [...REAL_COASTAL_STATIONS],
      synthetic: SYNTHETIC_COASTAL_STATIONS.map(s => ({ ident: s.ident, type: s.type, position: s.position, frequency: s.frequency, channel: s.channel!, elevation: s.elevation, role: s.synthetic.role })),
    },
    procedures: [
      { airport: "87N", ident: "R190", source: "FAA CIFP 2609: COPTER RNAV (GPS) 190 via HTO, with its missed approach and the BEADS hold" },
      { airport: "KIAG", ident: "N28", source: "FAA CIFP 2609: NDB approach (F16 fixture)" },
      { airport: "PASD", ident: "Q32", source: "FAA CIFP 2609: NDB/DME approach via HBT (F16 fixture)" },
    ],
    clock: { start: MISSION_87N_OFFSHORE_SAR.startTime!, tickSeconds: 0.25 },
    segments: [
      {
        id: "offshore", title: "Offshore from 87N, no radio coverage (F4, F10, F11, F12)",
        initialState: { position: offshore, altitudeFt: MISSION_START_ALTITUDE_FT, iasKt: MISSION_START_IAS, headingDeg: 0, wind: { ...MISSION_WIND } },
        facilities: [],
        faults: [
          { at: 120, fault: "GPS INTEGRITY", detail: "integrity removed on both receivers, the position kept", item: "F4" },
          { at: 300, fault: "GPS POSITION", detail: "the position removed on both receivers", item: "F11" },
          { at: 600, fault: "DVS", detail: "the Doppler velocity sensor fails", item: "F10" },
          { at: 900, fault: "GPS RESTORED", detail: "both receivers restored with integrity", item: "F4" },
        ],
        expected: [
          slot("GPS POS UNCERTAIN and INT, no approach authority, from 120 s", "F4"),
          slot("KALMAN for 2 minutes from the last aiding (300 s), then DVS without integrity; CHECK ANP at the phase timing", "F11"),
          slot("DR and FMS NAV IN DR from 600 s", "F10"),
          slot("recovery from 900 s, with the position step shown", "F4"),
          slot("computed position against bench truth throughout", "F15"),
        ],
      },
      {
        id: "coastal", title: "Along the coast with DME and TACAN coverage, synthetic where declared (F6, F7, F8, F9)",
        initialState: { position: coastal, altitudeFt: 1500, iasKt: MISSION_START_IAS, headingDeg: 70, wind: { ...MISSION_WIND } },
        facilities: [...REAL_COASTAL_STATIONS, ...SYNTHETIC_COASTAL_STATIONS.map(s => s.ident)],
        faults: [
          { at: 120, fault: "DME BIAS", detail: "QMTK ranges biased +0.6 NM, four stations held", item: "F6" },
          { at: 300, fault: "DME STATIONS", detail: "three stations held, QMTK still biased", item: "F6" },
          { at: 480, fault: "DME DESELECT", detail: "the crew deselects QORP on DME DESELECT", item: "F6" },
          { at: 600, fault: "NAV CONTROL", detail: "NAV 1's control path lost, its reception continuing", item: "F8" },
          { at: 780, fault: "AUTO VOR", detail: "QWHM AUTO-tuned under autoVorNavigation", item: "F7" },
          { at: 960, fault: "TACAN", detail: "QBIX tuned for VOR/DME/TCN", item: "F7" },
        ],
        expected: [
          slot("QMTK isolated with four stations, on a unique hypothesis", "F6"),
          slot("three stations inconsistent: DME/DME unavailable, no station named", "F6"),
          slot("QORP never scanned after deselection", "F6"),
          slot("NAV 1 CONTROL LOST with reception continuing", "F8"),
          slot("VOR/DME on the AUTO-tuned QWHM, its tuning source shown", "F7"),
          slot("VOR/DME/TCN with QBIX", "F7"),
          slot("computed position against bench truth throughout", "F15"),
        ],
      },
    ],
    ndbVariants: [
      {
        id: "kiag-n28", airport: "KIAG", procedure: "N28", file: NDB_FIXTURE_FILES[0].name,
        faults: [{ at: 0, after: "FAF", fault: "GPS INTEGRITY", detail: "integrity-only loss after the FAF", item: "F16" }],
        expected: [
          slot("the approach flown on FMS guidance with GPS, the ADF bearing on the RMI throughout", "F16"),
          slot("the integrity-only loss continues for 300 s, then cancels to HDG", "F16"),
          slot("the crew's MISSED APPR restores terminal guidance", "F16"),
        ],
      },
      {
        id: "pasd-q32", airport: "PASD", procedure: "Q32", file: NDB_FIXTURE_FILES[1].name,
        faults: [{ at: 60, after: "FAF", fault: "NDB OFF AIR", detail: "the HBT NDB goes off the air on the final", item: "F16" }],
        expected: [
          slot("the NDB/DME approach flown with the DME distance from HBT shown", "F16"),
          slot("the NDB off the air on the final: its bearing flagged, the approach as the profile requires", "F16"),
        ],
      },
    ],
  };
}

/** The manifest as canonical JSON: object keys sorted at every level, so the text depends only on the values. */
export function canonicalJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (value && typeof value === "object")
    return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonicalJson((value as Record<string, unknown>)[key])}`).join(",")}}`;
  return JSON.stringify(value);
}

/** The fixture hash: SHA-256 of the canonical manifest, `sha256-` and 64 hex digits. */
export async function fixtureHash(manifest: StageFMissionManifest): Promise<string> {
  const digest = await globalThis.crypto.subtle.digest("SHA-256", new TextEncoder().encode(canonicalJson(manifest)));
  return `sha256-${Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("")}`;
}

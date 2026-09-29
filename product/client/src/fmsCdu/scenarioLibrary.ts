import { MISSION_87N_OFFSHORE_SAR } from "./heliDemo";
import type { Scenario } from "./scenario";

// Built-in scenarios for the situations airline and certification test programmes exercise most (see the research in
// product/docs/FMS_TEST_BENCH.md): losing GPS on an RNAV approach, losing integrity, dead reckoning when every sensor
// is gone, a crew RNP entry the navigation cannot meet, and faults scripted on one GPS receiver (FDE, then the FMS
// moving to the other receiver; a spoofer only GPS DISAGREE catches). Each is plain data, like a recorded scenario. The
// KBTV ones fly a real approach from the FAA CIFP (kbtvDemo.ts), from a start state 8 NM before its intermediate fix.
export const SCENARIO_LIBRARY: readonly Scenario[] = [
  {
    id: "gps-lost-before-faf",
    title: "GPS lost 2 NM before the final approach fix",
    objective: "Show that losing GPS on the RNAV (GNSS) approach removes approach capability (the approach does not capture), alerts the crew, and that the missed approach is flown after TOGA. On DME/DME the navigation still meets RNP 0.3 here, so no CHECK ANP is expected.",
    maxSeconds: 3600,
    steps: [
      { when: { kind: "start" }, action: { kind: "procedure", procedure: "APPROACH", ident: "R24R" } },
      { when: { kind: "start" }, action: { kind: "keys", keys: ["EXEC"] } },
      { when: { kind: "start" }, action: { kind: "armApproach" } },
      { when: { kind: "distance", waypoint: "FERDI", nm: 2 }, action: { kind: "condition", condition: "gpsLost", on: true } },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "GPS NAV LOST" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "NO APPR INTEGRITY" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectLamp", lamp: "MSG", lit: true } },
      { when: { kind: "start" }, action: { kind: "goAround" } },
      // TOGA drops the rest of the approach: without it the aircraft would reach UL501 only minutes later, via the runway.
      { when: { kind: "start" }, action: { kind: "expectActive", waypoint: "UL501" }, within: 30 },
      // Armed before the FAF, so the FMS never had to ask for it.
      { when: { kind: "start" }, action: { kind: "expectNoAlert", text: "ARM APPROACH" } },
    ],
  },
  {
    id: "integrity-lost-on-approach",
    title: "GPS integrity lost on the RNAV approach",
    objective: "Show that a loss of GPS integrity (RAIM or SBAS) on the approach gives GPS POS UNCERTAIN and NO APPR INTEGRITY while the FMS keeps navigating.",
    maxSeconds: 3600,
    steps: [
      { when: { kind: "start" }, action: { kind: "procedure", procedure: "APPROACH", ident: "R24R" } },
      { when: { kind: "start" }, action: { kind: "keys", keys: ["EXEC"] } },
      { when: { kind: "start" }, action: { kind: "armApproach" } },
      { when: { kind: "distance", waypoint: "FERDI", nm: 5 }, action: { kind: "condition", condition: "gpsIntegrity", on: true } },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "GPS POS UNCERTAIN" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "NO APPR INTEGRITY" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectLamp", lamp: "POS", lit: false } },
      { when: { kind: "active", waypoint: "RW24R" }, action: { kind: "expectActive", waypoint: "RW24R" } },
    ],
  },
  {
    id: "dead-reckoning",
    title: "Dead reckoning with DME and GPS lost",
    objective: "Show that with the DMEs and then GPS lost the FMS dead reckons on inertial with the POS annunciator lit, and returns to GPS when it recovers.",
    maxSeconds: 900,
    steps: [
      { when: { kind: "time", seconds: 60 }, action: { kind: "condition", condition: "dmeOutage", on: true } },
      { when: { kind: "start" }, action: { kind: "expectLamp", lamp: "POS", lit: false } },
      { when: { kind: "time", seconds: 120 }, action: { kind: "condition", condition: "gpsLost", on: true } },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "GPS NAV LOST" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectLamp", lamp: "POS", lit: true }, within: 5 },
      { when: { kind: "time", seconds: 720 }, action: { kind: "condition", condition: "gpsLost", on: false } },
      { when: { kind: "start" }, action: { kind: "expectLamp", lamp: "POS", lit: false }, within: 5 },
    ],
  },
  {
    id: "manual-rnp",
    title: "Crew RNP the navigation cannot meet",
    objective: "Show that a crew RNP below the actual navigation performance lights RNP at once and raises CHECK ANP only after the phase's time to alert.",
    maxSeconds: 300,
    steps: [
      { when: { kind: "start" }, action: { kind: "keys", keys: ["PROG"] } },
      // 0.01 NM: below the 0.02 NM floor on ANP, so below ANP in every satellite geometry.
      { when: { kind: "start" }, action: { kind: "type", text: ".01" } },
      { when: { kind: "start" }, action: { kind: "keys", keys: ["LSK5L"] } },
      { when: { kind: "start" }, action: { kind: "expectLine", line: 9, pattern: "MANUAL" } },
      { when: { kind: "start" }, action: { kind: "expectLine", line: 10, pattern: "^0\\.01/" } },
      { when: { kind: "start" }, action: { kind: "expectLamp", lamp: "RNP", lit: true } },
      { when: { kind: "time", seconds: 30 }, action: { kind: "expectLine", line: 13, pattern: "^\\s*$" } },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "CHECK ANP" }, within: 120 },
    ],
  },
  {
    id: "kbtv-rnav15-lpv",
    title: "KBTV RNAV (GPS) RWY 15, LPV on the published FAS",
    objective: "Fly the real FAA CIFP 2609 approach at Burlington, Vermont, on its published FAS data block: LPV annunciated and armed, captured at the final approach fix (FOVES), and held on the published glide path to the runway. Real public-domain data, for demonstration only, not for navigation.",
    maxSeconds: 900,
    start: "kbtv-rnav15",
    steps: [
      { when: { kind: "start" }, action: { kind: "expectActive", waypoint: "STAEV" } },
      // Inside the 30 NM approach region with SBAS: the receiver reports LPV, and the approach is armed.
      { when: { kind: "start" }, action: { kind: "expectApproach", type: "LPV", state: "ARMED" }, within: 1 },
      // The final approach segment starts at the FAF: once FOVES is sequenced, the approach captures on the LPV path.
      { when: { kind: "active", waypoint: "JUNEL" }, action: { kind: "expectApproach", type: "LPV", state: "CAPTURED", verticalMode: "APPR" }, within: 5 },
      // On the published path near the threshold, within 30 ft (the LPV vertical full scale there is about 50 ft).
      { when: { kind: "distance", waypoint: "RW15", nm: 0.5 }, action: { kind: "expectApproach", state: "CAPTURED", maxVerticalFt: 30 } },
      { when: { kind: "start" }, action: { kind: "expectNoAlert", text: "NO APPR INTEGRITY" } },
    ],
  },
  {
    id: "kbtv-rnav15-integrity-lost",
    title: "KBTV RNAV (GPS) RWY 15, GPS integrity lost on final",
    objective: "On the real KBTV LPV approach, show that GPS integrity lost after capture ends the approach: GPS POS UNCERTAIN and NO APPR INTEGRITY, the approach lost to a latched altitude hold (it does not descend on without integrity), and TOGA climbing on the missed approach from there.",
    maxSeconds: 900,
    start: "kbtv-rnav15",
    steps: [
      { when: { kind: "active", waypoint: "JUNEL" }, action: { kind: "expectApproach", type: "LPV", state: "CAPTURED" }, within: 5 },
      { when: { kind: "distance", waypoint: "RW15", nm: 3 }, action: { kind: "condition", condition: "gpsIntegrity", on: true } },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "GPS POS UNCERTAIN" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "NO APPR INTEGRITY" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectApproach", type: "NO APPR", state: "OFF", verticalMode: "ALT HOLD" }, within: 5 },
      // TOGA from the latched hold: the missed approach becomes active and the go-around climbs (D02; GA under the
      // helicopter profile, where the crew's preselected altitude is the target).
      { when: { kind: "start" }, action: { kind: "goAround" } },
      { when: { kind: "start" }, action: { kind: "expectApproach", verticalMode: "GA" }, within: 10 },
    ],
  },
  {
    id: "kbtv-rnav15-sbas-lost",
    title: "KBTV RNAV (GPS) RWY 15, SBAS lost on final",
    objective: "On the real KBTV LPV approach, show that SBAS marked do not use after capture (on both receivers, as a GEO broadcast does) drops the level to LNAV: NO APPR INTEGRITY and the approach lost to a latched altitude hold, while GPS integrity holds (no GPS POS UNCERTAIN) and the FMS stays on GPS 1; TOGA then climbs on the missed approach.",
    maxSeconds: 900,
    start: "kbtv-rnav15",
    steps: [
      { when: { kind: "active", waypoint: "JUNEL" }, action: { kind: "expectApproach", type: "LPV", state: "CAPTURED" }, within: 5 },
      // Both in the same tick, GPS 2 first: at no moment is the other receiver still eligible to take the approach over.
      { when: { kind: "distance", waypoint: "RW15", nm: 3 }, action: { kind: "gps", receiver: 2, stimulus: { op: "sbas", doNotUse: true } } },
      { when: { kind: "start" }, action: { kind: "gps", receiver: 1, stimulus: { op: "sbas", doNotUse: true } } },
      { when: { kind: "start" }, action: { kind: "expectApproachLevel", level: "LNAV" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "NO APPR INTEGRITY" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectApproach", type: "LNAV", state: "OFF", verticalMode: "ALT HOLD" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectNoAlert", text: "GPS POS UNCERTAIN" } },
      { when: { kind: "start" }, action: { kind: "expectGpsSource", source: "GPS1" } },
      { when: { kind: "start" }, action: { kind: "goAround" } },
      { when: { kind: "start" }, action: { kind: "expectApproach", verticalMode: "GA" }, within: 10 },
    ],
  },
  {
    id: "kbtv-rnav15-sbas-lost-gps1",
    title: "KBTV RNAV (GPS) RWY 15, SBAS lost on GPS 1 on final, LPV continues on GPS 2",
    objective: "On the real KBTV LPV approach, show the approach-aware AUTO selection (the AeroLink simulator policy): SBAS marked do not use on GPS 1 only after capture transfers the approach to GPS 2, annunciated APPR ON GPS2, with LPV, the capture and the path kept and no NO APPR INTEGRITY; GPS 1 recovering does not take it back.",
    maxSeconds: 900,
    start: "kbtv-rnav15",
    steps: [
      { when: { kind: "active", waypoint: "JUNEL" }, action: { kind: "expectApproach", type: "LPV", state: "CAPTURED" }, within: 5 },
      { when: { kind: "distance", waypoint: "RW15", nm: 3 }, action: { kind: "gps", receiver: 1, stimulus: { op: "sbas", doNotUse: true } } },
      { when: { kind: "start" }, action: { kind: "expectGpsSource", source: "GPS2" } },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "APPR ON GPS2" } },
      { when: { kind: "start" }, action: { kind: "expectApproach", type: "LPV", state: "CAPTURED", verticalMode: "APPR" } },
      { when: { kind: "distance", waypoint: "RW15", nm: 1.5 }, action: { kind: "gps", receiver: 1, stimulus: { op: "sbas", doNotUse: false } } },
      { when: { kind: "distance", waypoint: "RW15", nm: 0.5 }, action: { kind: "expectApproach", type: "LPV", state: "CAPTURED", maxVerticalFt: 30 } },
      { when: { kind: "start" }, action: { kind: "expectGpsSource", source: "GPS2" } },
      { when: { kind: "start" }, action: { kind: "expectNoAlert", text: "NO APPR INTEGRITY" } },
      { when: { kind: "start" }, action: { kind: "expectNoAlert", text: "APPR ON GPS1" } },
    ],
  },
  {
    id: "gps1-fde-then-gps2",
    title: "GPS 1 satellite ramp on the RNAV approach, then GPS 1 fails",
    objective: "Show that GPS 1 excludes a satellite with a growing range error (FDE) and keeps LPV, and that when a second fault then makes GPS 1 unusable the FMS moves to GPS 2 and keeps the approach without GPS NAV LOST.",
    maxSeconds: 3600,
    // The GPS sky moves with the clock: PRN 24 is in use by GPS 1 at 37° when the ramp starts from this start time.
    startTime: "2026-09-27T14:00:00.000Z",
    steps: [
      { when: { kind: "start" }, action: { kind: "procedure", procedure: "APPROACH", ident: "R24R" } },
      { when: { kind: "start" }, action: { kind: "keys", keys: ["EXEC"] } },
      { when: { kind: "start" }, action: { kind: "armApproach" } },
      { when: { kind: "distance", waypoint: "FERDI", nm: 12 }, action: { kind: "expectApproachLevel", level: "LPV" } },
      { when: { kind: "start" }, action: { kind: "gps", receiver: 1, stimulus: { op: "satelliteFault", prn: 24, fault: "RAMP", value: 5 } } },
      // By 8 NM the range error is several hundred metres: GPS 1 is still navigating on the approach only because it excluded PRN 24.
      { when: { kind: "distance", waypoint: "FERDI", nm: 8 }, action: { kind: "expectReceiverMode", receiver: 1, mode: "SBAS_PA" } },
      { when: { kind: "start" }, action: { kind: "expectGpsSource", source: "GPS1" } },
      { when: { kind: "start" }, action: { kind: "expectApproachLevel", level: "LPV" } },
      { when: { kind: "distance", waypoint: "FERDI", nm: 5 }, action: { kind: "gps", receiver: 1, stimulus: { op: "fault", fault: "RECEIVER", on: true } } },
      { when: { kind: "start" }, action: { kind: "expectReceiverMode", receiver: 1, mode: "FAULT" }, within: 2 },
      { when: { kind: "start" }, action: { kind: "expectGpsSource", source: "GPS2" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectApproachLevel", level: "LPV" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectNoAlert", text: "GPS NAV LOST" } },
      { when: { kind: "active", waypoint: "RW24R" }, action: { kind: "expectGpsSource", source: "GPS2" } },
    ],
  },
  {
    id: "gps1-spoof-walks-off",
    title: "A spoofed GPS 1 walks off; only GPS DISAGREE catches it",
    objective: "Show that a spoofer walking GPS 1 away at 2 m/s passes the receiver's own integrity (it stays in SBAS PA, the FMS keeps navigating on it and keeps LPV, with no GPS POS UNCERTAIN), and that only the comparison with GPS 2 raises GPS DISAGREE.",
    maxSeconds: 3600,
    startTime: "2026-09-27T14:00:00.000Z",
    steps: [
      { when: { kind: "start" }, action: { kind: "procedure", procedure: "APPROACH", ident: "R24R" } },
      { when: { kind: "start" }, action: { kind: "keys", keys: ["EXEC"] } },
      { when: { kind: "start" }, action: { kind: "armApproach" } },
      { when: { kind: "distance", waypoint: "FERDI", nm: 12 }, action: { kind: "gps", receiver: 1, stimulus: { op: "spoof", northM: 0, driftEastMps: 2 } } },
      // 0.1 NM apart after about 93 s.
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "GPS DISAGREE" }, within: 120 },
      { when: { kind: "start" }, action: { kind: "expectGpsSource", source: "GPS1" } },
      { when: { kind: "start" }, action: { kind: "expectReceiverMode", receiver: 1, mode: "SBAS_PA" } },
      { when: { kind: "start" }, action: { kind: "expectApproachLevel", level: "LPV" } },
      { when: { kind: "start" }, action: { kind: "expectNoAlert", text: "GPS POS UNCERTAIN" } },
      { when: { kind: "start" }, action: { kind: "expectNoAlert", text: "GPS NAV LOST" } },
      { when: { kind: "distance", waypoint: "FERDI", nm: 5 }, action: { kind: "gps", receiver: 1, stimulus: { op: "clearSpoof" } } },
      { when: { kind: "start" }, action: { kind: "expectGpsSource", source: "GPS1" }, within: 5 },
    ],
  },
  MISSION_87N_OFFSHORE_SAR,
];

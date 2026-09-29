import type { Scenario } from "./scenario";

// Built-in scenarios for the situations airline and certification test programmes exercise most (see the research in
// product/docs/FMS_TEST_BENCH.md): losing GPS on an RNAV approach, losing integrity, dead reckoning when every sensor
// is gone, and a crew RNP entry the navigation cannot meet. Each is plain data, like a recorded scenario. The KBTV ones
// fly a real approach from the FAA CIFP (kbtvDemo.ts), from a start state 8 NM before its intermediate fix.
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
      // TOGA from the latched hold: the missed approach becomes active and VNAV climbs on it (D02).
      { when: { kind: "start" }, action: { kind: "goAround" } },
      { when: { kind: "start" }, action: { kind: "expectApproach", verticalMode: "VNAV CLB" }, within: 10 },
    ],
  },
];

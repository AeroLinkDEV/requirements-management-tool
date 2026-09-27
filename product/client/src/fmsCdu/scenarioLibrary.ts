import type { Scenario } from "./scenario";

// Built-in scenarios for the situations airline and certification test programmes exercise most (see the research in
// product/docs/FMS_TEST_BENCH.md): losing GPS on an RNAV approach, losing integrity, dead reckoning when every sensor
// is gone, and a crew RNP entry the navigation cannot meet. Each is plain data, like a recorded scenario.
export const SCENARIO_LIBRARY: readonly Scenario[] = [
  {
    id: "gps-lost-before-faf",
    title: "GPS lost 2 NM before the final approach fix",
    objective: "Show that losing GPS on the RNAV (GNSS) approach removes approach guidance, alerts the crew, and that the missed approach is flown after TOGA.",
    maxSeconds: 3600,
    steps: [
      { when: { kind: "start" }, action: { kind: "procedure", procedure: "APPROACH", ident: "R24R" } },
      { when: { kind: "start" }, action: { kind: "keys", keys: ["EXEC"] } },
      { when: { kind: "start" }, action: { kind: "armApproach" } },
      { when: { kind: "distance", waypoint: "FERDI", nm: 2 }, action: { kind: "condition", condition: "gpsLost", on: true } },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "GPS NAV LOST" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "NO APPR INTEGRITY" }, within: 5 },
      { when: { kind: "start" }, action: { kind: "expectLamp", lamp: "MSG", lit: true } },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "CHECK ANP" }, within: 60 },
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
      { when: { kind: "start" }, action: { kind: "type", text: ".03" } },
      { when: { kind: "start" }, action: { kind: "keys", keys: ["LSK5L"] } },
      { when: { kind: "start" }, action: { kind: "expectLine", line: 9, pattern: "MANUAL" } },
      { when: { kind: "start" }, action: { kind: "expectLine", line: 10, pattern: "^0\\.03/" } },
      { when: { kind: "start" }, action: { kind: "expectLamp", lamp: "RNP", lit: true } },
      { when: { kind: "time", seconds: 30 }, action: { kind: "expectLine", line: 13, pattern: "^\\s*$" } },
      { when: { kind: "start" }, action: { kind: "expectAlert", text: "CHECK ANP" }, within: 120 },
    ],
  },
];

# What makes a good FMS: research and gap analysis for the AeroLink FMS Test Bench

*27 September 2026. Written for the CMA-9000 simulator roadmap.*

> **Read this first.** Everything above "Current capability register" is the 27 September research baseline: what airline practice and the cited sources suggest, measured against the simulator as it was then. It is not a statement of current bench behaviour, and it is not CMA-9000 authority; generic airline practice (Boeing, Airbus) informs questions, not CMA behaviour. The current state of each capability, with its open review findings, is in the register near the end.

## Sources I used

- **ICAO Doc 9613, Performance-based Navigation (PBN) Manual, 5th edition.** The unedited version is on the PBN Portal. I used the RNAV 1/2, RNP 4, RNP 1 and Advanced RNP functional requirements, and the crew knowledge and procedures that go with them. These are per navigation specification: a capability inventory to check against, not one universal requirement list for every installation.
- **Boeing 737 FCOM, Chapter 11, Flight Management and Navigation (2004–2006 revision).** 348 pages. It describes how a mainstream airline FMS behaves in service: position updating, ANP/RNP, waypoint naming, VNAV, RTA, route modification, offsets, REF NAV DATA and messages.
- **Airbus Flight Operations Briefing Note, "Optimum Use of Automation" (via SKYbrary).** It covers what airlines train crews to expect from the FMS and autoflight: managed versus selected guidance, mode awareness, and cross-checking.
- **American Airlines 965 (Cali) accident material.** From the MIT course pack, the FAA Lessons Learned page and Wikipedia. It shows how a direct-to and a duplicate identifier contributed to a fatal accident.
- **Flight Safety Foundation, "Data-entry errors can lead aircraft off course"**, and the SKYbrary work on FMS data-entry errors. IATA's 2015 *FMS Data Entry Error Prevention* guide reports 309 relevant reports in its 2007–2011 dataset, 80% of them involving navigation data. That is a historical dataset, not a current or CMA-specific error rate.
- **Honeywell, "Understanding FMS navigation procedures" (ARINC 424 path terminators)**, plus Honeywell and Airbus material on FMS upgrades, datalink and RNP AR.
- **FAA/MITRE and IFATCA findings on predictability.** Different manufacturers' FMSs build turns and vertical paths differently. ATC needs repeatable paths, and airlines need the aircraft to do what the crew expects.
- **NASA and Flight Safety Foundation work on VNAV "automation surprise"**, from search summaries only: the FAA and NASA servers refused automated download.

The FAA advisory circulars AC 20-138D and AC 90-105A would not download at the time. They are not interchangeable with ICAO Doc 9613: AC 90-105A is operational guidance for specific RNP and baro-VNAV operations, and AC 20-138D is airworthiness guidance. Both should be cited by section where a behaviour depends on them.

## What airlines look for, in short

1. **Predictability over cleverness.** Pilots ask "what is it doing now, and what will it do next?" Published research and incident reports identify vertical-guidance understanding and mode awareness as recurring concerns. A good FMS makes every mode, transition and target visible before it happens.
2. **Error-tolerant data entry with verification.** Airlines lean on the MOD → review → EXEC pattern, on route discontinuities as deliberate stops, and on cross-checking the map against the CDU. Accidents show the dangers: a direct-to silently removes intermediate reporting points, and a duplicate or chart-mismatched identifier can pick the wrong fix. Good FMSs show SELECT DESIRED WPT lists with facility type and position, so the crew chooses the intended entry deliberately, and offer abeam points on a direct-to.
3. **Certified navigation behaviour (PBN).** This means:
   - automatic leg sequencing;
   - direct-to and intercept course;
   - fly-by and fly-over turns;
   - the ARINC 424 path terminators, not only waypoint-to-waypoint legs;
   - parallel offsets;
   - RNAV holding;
   - loading whole procedures by name;
   - a database the crew cannot modify, with its validity period on display;
   - the active sensor shown, with automatic sensor reversion;
   - integrity alerting (ANP against RNP, with the right time to alert for each phase of flight).
4. **Consistent, repeatable paths.** Turn construction, bypassing a short leg, and when a leg sequences all affect where the aircraft actually goes. ATC and RNP containment depend on it.
5. **Economics.** Accurate fuel and time predictions, a top of descent and descent planning that save fuel, required time of arrival (RTA), datalinked winds and routes, and RNP AR access to difficult airports.
6. **A clear split between long-term and short-term guidance.** The CDU is for the long-term plan (strategic). Heading and altitude selection is for short-term changes (tactical). Pilots must be able to drop from managed to selected guidance at any time and rejoin the route easily, for example following ATC vectors and then intercepting a leg.

## ICAO RNAV 1/2 minimum functions, checked against the simulator

| Required function | Simulator today | Gap |
|---|---|---|
| Desired path and aircraft position against it, for both pilots | Map, cross-track readout | Lateral deviation scale by phase of flight (en route / terminal / approach) |
| Navigation database, AIRAC-updatable, protected from crew change | Demonstration database plus ARINC 424 loader; procedures cannot be edited | AIRAC active and inactive cycles with a changeover date |
| Database validity period displayed | IDENT and REF NAV DATA | DATABASE OUT OF DATE driven by the date |
| Retrieve and display waypoint and navaid data | REF NAV DATA | Runway data view; temporary and supplemental database |
| Load a whole SID or STAR by name | DEP/ARR with transitions | — |
| Active sensor type, active waypoint, groundspeed or time, distance and bearing | Partly (PROGRESS, map) | Active sensor on PROGRESS / NAV STATUS |
| Direct-to | Yes | **Intercept course (INTC CRS)**, **abeam points** |
| Automatic leg sequencing, shown to the pilot | Yes | — |
| Fly-by and fly-over turns | Yes | **Leg bypass** for short legs |
| Path terminators IF, CF, CA, DF, TF (and VA, VM, VI, FM, FA; RF for A-RNP; HA/HF/HM holds) | Waypoint-to-waypoint legs, holds, direct-to | **Most path terminators**: conditional (altitude and intercept) legs, vector legs, RF arcs, course-to-fix legs |
| System and sensor failure indication; automatic sensor reversion | FAIL lamp; GPS-loss condition | **Sensor blending**: GPS, then DME/DME, then VOR/DME, then inertial, with automatic reversion |
| Parallel offset (RNP 4, A-RNP) | Shown on PROGRESS 4/4 only | **Offset actually flown**, with start and end waypoints and invalid-leg rules |
| Integrity alerting (ANP against RNP) | Injected condition | **ANP computed** from the sensors, **default RNP by phase of flight** (oceanic, en route 2.0, terminal 1.0, approach 0.3 NM), time to alert, UNABLE RNP |
| RNAV holding | Yes, with real entries | Hold-to-altitude and hold-to-fix terminations (HA/HF) |
| Follow vectors and rejoin the route from heading mode | No | **Selected versus managed lateral guidance**; LNAV arm and capture |
| Change arrival or alternate airport | Destination entry | Alternate airport, diversion |

## How an airline FMS behaves in service (Boeing 737 FCOM Chapter 11), checked against the simulator

- **Position updating.** The priority is GPS, then two or more DMEs, then a VOR with collocated DME, then a localizer with DME, then a localizer alone, then inertial. DMEs are tuned automatically, and the tuned stations are listed on NAV STATUS. *Missing.*
- **ANP.** A 95% position-error circle is shown on the LEGS and position pages. UNABLE REQD NAV PERF–RNP appears after the time to alert for the phase of flight. *Missing as a computed value.*
- **"On approach" logic.** The FMS is on approach within 2 NM of the first approach waypoint or 2,000 ft above the airport, which tightens the RNP alerting. *Missing.*
- **Waypoint naming.** WPTnn for latitude/longitude entries, place-bearing-distance names, and a SELECT DESIRED WPT list with facility type and latitude/longitude. Conditional waypoints such as (3000), (INTC) and (VECTOR) appear in parentheses. *Naming is done; conditional waypoints are missing.*
- **VNAV:**
  - VNAV PTH and VNAV SPD modes;
  - speed and altitude restrictions, with UNABLE NEXT ALTITUDE;
  - top of descent (including intermediate T/D points) and end of descent (the runway threshold, the missed approach point, or the lowest "at" restriction);
  - DES NOW (an early descent at 1,000 fpm to capture the path);
  - step climb;
  - on-approach path handling.
  
  *We have only a leg-by-leg altitude target and the final approach path.*
- **RTA:** speed control to meet a crossing time, with RTA UNACHIEVABLE. *Missing.*
- **Fuel monitoring:** predicted fuel at the destination, USING RSV FUEL, INSUFFICIENT FUEL, and CHECK FMC FUEL QUANTITY. *Only a fixed fuel page today.*
- **Route modifications:** add, delete and resequence waypoints; leg bypass (three bypasses in a row insert a discontinuity); remove discontinuities; direct-to and intercept course. *Bypass and intercept course are missing.*
- **Lateral offset page:** offset distance with start and end waypoints; the offset carries on until an invalid leg. *Missing as flown guidance.*
- **REF NAV DATA and NAV OPTIONS:** a temporary database of pilot-created entries, and inhibiting specific VORs or DMEs from updating. *Temporary database and navaid inhibit are missing.*
- **Data entry formats:** altitude as three digits, four or five digits, or FLxxx, shown according to the transition altitude. *Partly done.*
- **Datalink:** uplinked routes, winds and performance data are loaded, activated and executed with EXEC. *A representative version exists.*

## Human-factors design rules to build in

1. **Route edits are a MOD until EXEC**, with the dashed route on the map, and a pending edit must not change the active route's geometry, guidance or predictions (it currently does: review findings R01, R05, R12). This is not a rule for every command: radio tuning, brightness and acknowledgements are immediate, and each command needs its own declared commit policy.
2. **Show the next mode, not only the current one.** Add a flight mode annunciator strip showing the engaged and armed lateral mode (LNAV, HDG, HOLD, SAR) and vertical mode (VNAV PTH, VNAV SPD, ALT), and show transitions as they happen.
3. **Make direct-to consequences visible.** Offer ABEAM PTS so the reporting points survive a direct-to; the Cali accident is the lesson. Keep INTC CRS beside it.
4. **Duplicate identifiers:** show every candidate with facility type and position, so the intended one (not necessarily the nearest) is chosen deliberately; nearest-first is a useful ordering, not established CMA behaviour. *Done:* our SELECT DESIRED WPT shows type and position; the chosen entry is not yet pinned to the leg (R05, R06).
5. **Messages** must be specific, amber for alerts, recallable, and able to light MSG. *Done.* Add the in-service messages the new features need: UNABLE NEXT ALTITUDE, RTA UNACHIEVABLE, USING RSV FUEL, INSUFFICIENT FUEL, UNABLE REQD NAV PERF–RNP, POS SHIFT, VERIFY RNP.
6. **Repeatable paths:** document the turn and bypass rules and test them, so engineers can compare the simulator's path with the real CMA-9000's.

## Current capability register

The gap tables above are the 27 September research baseline. This register is the current state, per capability, after the independent review of 27 September (Astra, reviewed at `193bfc3c`; finding IDs R01–R26). A roadmap step being "built" does not mean its capabilities are complete: each row carries its own status.

Status vocabulary: **Demonstrated** (works on the bench and has a behavioural test, within the demonstration envelope); **Partial** (works for the common path; named gaps or defects remain); **Placeholder** (a page or label exists without the behaviour behind it); **Not implemented**. Nothing here is independently verified or qualified for engineering use, and the built-in model is not an oracle for software under test.

| Capability | Status | Notes and open review findings |
|---|---|---|
| CDU panel, variants, keys, scratchpad, lighting | Demonstrated | Held CLR survives focus loss (R18); luminance is a simulated value, not a physical NVIS claim |
| MOD / EXEC / ERASE for route edits | Partial | Pending edits leak into final-path guidance (R01), runway lookup (R05) and PROGRESS (R12) |
| Navigation database, airways, SID/STAR/approach selection | Partial | Pages and new entries look up by ident string (R05); the active plan's fixes are pinned when it becomes active (EXEC, the initial plan), so loading or activating a cycle does not move them or their guidance and predictions (R06) |
| ARINC 424 loader | Partial | Subset reader with degree, minute and second range checks. A file that is empty, not recognised or holds an impossible coordinate is refused whole with the reason, and nothing changes; a valid file becomes the inactive cycle (R16 loader part, R06). Procedures and cycle dates are not read |
| Pilot waypoints, SELECT DESIRED WPT, REF NAV DATA | Demonstrated | Regional geometry only; the date line takes the long way round (R17) |
| Company routes, SEC FPLN | Partial | Session memory only; SAVE keeps the en-route legs, not constraints |
| POS INIT SET POS | Placeholder | Accepts the entry and does nothing (R26) |
| TF, CF, DF, RF legs; fly-by and fly-over | Demonstrated | Regional envelope (R17) |
| CA, FA, VA, VI, VM, FM conditional legs | Partial | Altitude termination is climb-oriented |
| Holds | Partial | Entries and racetrack flown; HA/HF/HM termination is a one-turn exit only |
| Leg bypass | Not implemented | Only a direct-to's bypassed points are kept (for ABEAM PTS) |
| DIRECT TO, INTC CRS, ABEAM PTS, offset | Partial | The pending direct-to leaked into active guidance (R01; fix in progress) |
| HDG SEL and LNAV arm/capture | Partial | The flight mode strip is inferred from motion, not a mode state (R10) |
| Discontinuities | Partial | Jump crosses them silently (R15); predictions bridge them as zero distance (R08) |
| Sensor selection and reversion | Demonstrated | Priority selection with synthetic errors, not blending or estimation; ANP is derived from simulated truth |
| RNP by phase, time to alert, CHECK ANP | Demonstrated | Demonstration parameters, not sourced CMA values; forced RNP/NPA disagree across pages (R11); NAV STATUS captions collide (R19) |
| RAIM / SBAS | Placeholder | Condition-driven flags, no satellite or protection-level model |
| Approach type, ARM APPROACH, go-around | Partial | The type is a GPS-integrity classifier; arming and integrity do not govern the final path (R03) |
| Altitude and speed constraints | Partial | Upper bounds are not checked, so a violated constraint reads as met (R04) |
| T/D, E/D, VNAV path, DES NOW | Partial | Simplified geometric path; no VNAV mode state (R10) |
| VNAV SPD | Not implemented | |
| RTA | Not implemented | |
| ETA and fuel predictions | Partial | Destination can be the missed-approach end (R07); gaps (R08); future speed constraints ignored (R09) |
| Cold temperature correction | Demonstrated | Test uses the production helper; needs an independent worked example |
| FMS failure | Partial | Blanks the CDU but guidance continues, and Pause is disabled (R02) |
| MSG and message recall | Partial | MSG can stay lit after recovery with nothing to acknowledge (R13) |
| ATC / FMC COMM datalink | Placeholder | Representative workflow; STANDBY clears the pending indication (R14) |
| Rendezvous, moving waypoints, tactical descent | Partial | Rendezvous uses the route predictions, so it inherits R08 and R09; moving waypoints have no data age or expiry |
| Database cycles, DATABASE OUT OF DATE | Partial | Each cycle holds its own dataset; the two demonstration cycles hold the same demonstration data, and the bench says so. A loaded file, merged over the active cycle's data, becomes the inactive cycle. Activating it (IDENT, or the bench) is recorded and does not re-resolve the active plan: its fixes stay pinned, the record names those the new cycle places differently, and they take the new positions only when the crew executes a modification, recorded as ROUTE RE-RESOLVED. Dates come from the data or show UNKNOWN; an unknown end never raises DATABASE OUT OF DATE (R06, R16). No changeover-date or ground-only rule (N09) |
| MAINT self test and fault log | Placeholder | A demonstration of the page, not equipment built-in test |
| Dual FMS, independent operation | Placeholder | One model with a copy of the route; not a dual-channel protocol |
| Scenarios, recording, procedure text, run report | Partial | A 0.25 s tick contract shared by the bench and headless runs; validated admission; distinct outcomes (passed, failed, no checks, timed out, stopped, invalid, error); the report's context is fixed at run start (N01–N08). In-process against the built-in model only: no external software-under-test adapter, run manifest or controlled evidence import (R21–R23, R25) |

The review's order is adopted: repair active-plan authority, guidance validity and prediction validity (R01–R09) before adding breadth.

Delivery history, for tracing: #1209 (panel), #1211 (conditions, deeper pages, lighting), #1212 (flight simulation, planning, lateral guidance, sensors, VNAV), #1218 (tactical functions, database cycles, maintenance and dual pages, scenarios).

Honesty rules: the simulator stays labelled as a simulation, and the demonstration navigation data is invented. The CMA-9000 manual governs where it speaks. Where it is silent, the behaviour here is a named engineering assumption that needs a source before any fidelity claim; generic airline practice (Boeing, Airbus) informs questions, not CMA behaviour.

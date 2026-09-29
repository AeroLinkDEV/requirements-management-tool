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
| CDU panel, variants, keys, scratchpad, lighting | Demonstrated | A press the panel loses (focus moves away, the pointer is cancelled, the page is hidden) is abandoned without firing, so a CLR hold never completes later (R18). Luminance is a simulated value, not a physical NVIS claim |
| MOD / EXEC / ERASE for route edits | Partial | Pending edits leak into final-path guidance (R01), runway lookup (R05) and PROGRESS (R12) |
| Navigation database, airways, SID/STAR/approach selection | Partial | Pages and new entries look up by ident string (R05). The active plan's fixes are pinned as resolved when it becomes active (EXEC, the initial plan), including fixes it was executed without, which stay unresolved: loading or activating a cycle neither moves a fix nor gives one a position (R06, third review D01). Pins are keyed by ident, a declared limit: two different entries with one ident in one plan are not told apart. The RNAV approach the GPS flies is pinned too: its FAS block is derived at EXEC from the active cycle and kept with the plan revision, so activating a cycle that defines the approach or runway differently changes nothing flown; activation names the difference and EXEC of a modification accepts it, recorded as ROUTE RE-RESOLVED (Astra GPS-02). Other procedure legs are not pinned. Known limits: the ARINC 424 runway bearing is magnetic but the FAS builder uses it as a true course (the invented demonstration data is consistent; imported real runways would be off by the local variation until magnetic variation is applied); GPS word FREEZE holds a value, it does not model message age, transport delay or per-label update rates |
| ARINC 424 loader | Partial | Subset reader with degree, minute and second range checks. A file that is empty, not recognised or holds an impossible coordinate is refused whole with the reason, and nothing changes; a valid file becomes the inactive cycle (R16 loader part, R06). Fields are range-checked too: identifiers (letters and digits), VHF frequency 108.00 to 117.95 MHz, NDB 190 to 1750 kHz, elevations -1,500 to 30,000 ft, runway length to 30,000 ft and bearing to 360.0°, airway sequence. An impossible value refuses the file; a blank required field skips the record with an error (R16 fields). Referential integrity (airway fixes, runway airports) is not checked. Procedures and cycle dates are not read |
| Pilot waypoints, SELECT DESIRED WPT, REF NAV DATA | Demonstrated | Longitude differences wrap the short way in every local frame, the map and nearby search, so a leg across the date line is its true length (R17). The local frame is flat, so accuracy falls off near the poles and over long legs |
| Company routes, SEC FPLN | Partial | Session memory only; SAVE keeps the en-route legs, not constraints |
| POS INIT SET POS | Partial | The entry is recorded as the position reference and shown in the SET POS field. In dead reckoning it also resets the position estimate to the entry, from where drift continues; with GPS or DME navigating, the sensors keep setting the position. It never moves the aircraft. Minutes of 60 or more, latitude above 90° and longitude above 180° are refused and change nothing (R26). No IRS alignment model |
| TF, CF, DF, RF legs; fly-by and fly-over | Demonstrated | Date-line legs are measured the short way (R17); flat local frame, as above |
| CA, FA, VA, VI, VM, FM conditional legs | Partial | Altitude termination is climb-oriented |
| Holds | Partial | Entries and racetrack flown; HA/HF/HM termination is a one-turn exit only |
| Leg bypass | Not implemented | Only a direct-to's bypassed points are kept (for ABEAM PTS) |
| DIRECT TO, INTC CRS, ABEAM PTS, offset | Partial | The pending direct-to leaked into active guidance (R01; fix in progress) |
| HDG SEL and LNAV arm/capture | Demonstrated | The flight mode strip shows the controller's modes (flight.ts), not a reading of the motion; the approach capability is annunciated armed until captured and engaged after it (R10, third review D03). LNAV lost at a gap reverts to HDG HOLD and is recorded |
| Discontinuities | Demonstrated | Jump refuses at a gap; a separate, logged engineering override crosses it, and its record names the plan revision, fingerprint and legs before and after (R15, Q-A3, third review D04). Predictions past a gap are unknown (R08) |
| Sensor selection and reversion | Demonstrated | Priority selection with synthetic errors, not blending or estimation; ANP is derived from simulated truth |
| RNP by phase, time to alert, CHECK ANP | Demonstrated | Demonstration parameters, not sourced CMA values. One effective RNP and ANP (scriptedFms `navPerformance`) is read by PROGRESS, NAV STATUS, the EFIS, the RNP annunciator and CHECK ANP. The sensor values stay separate, and a bench-forced value (NPA, RNP exceeded) is labelled TEST on the pages (R11). NAV STATUS abbreviates the phase so its captions fit, and a test renders every page in five states and fails on any overlapping or overflowing text (R19) |
| RAIM / SBAS | Partial | From the two simulated receivers (GPS phase 3a): the FMS reads each receiver's HIL (130), RAIM state (273) and SBAS level (305) and judges HIL against the phase's alert limit (2, 1 and 0.3 NM, DO-229D as commonly cited). The GPS integrity and GPS loss conditions are now faults injected into both receivers. The approach level is the selected receiver's 305 (LPV only in SBAS PA inside the approach region and within the FAS block's limits); see "Approach guidance (phase 3b)" below |
| GPS sensor (CMA-5024) | Partial | Phases 1 and 2 (see "GPS sensor simulation" below): constellation geometry, mode machine, RAIM with FDE and protection levels, SBAS NAV and SBAS PA, the FAS data block with its CRC, the GPS-computed LPV deviations and approach level, the ARINC 743A-style bus with word status, and overrides. Phase 3a wires two receivers to the FMS (see "FMS integration" below): automatic and manual receiver selection, the FMS position and ANP from the selected receiver, the integrity alerts and the GPS1/GPS2 compare, and GPS STATUS and POS SENSORS pages. The bench GPS sensors tab (phase 4a) reads and faults the FMS's own receivers. Phase 3b: the FMS sends the FAS block and flies the GPS deviations on an RNAV final (see "Approach guidance (phase 3b)" below) |
| Approach type, ARM APPROACH, go-around | Partial | Laboratory contract (Q-A1): beyond the FAF only a captured approach descends; capture needs APPR armed, vertical capability (an ILS, or an RNAV approach whose GPS reports LPV or LNAV/VNAV with its vertical deviation valid), LNAV and the aircraft on the final course. Vertical capability lost after capture latches ALT HOLD. APPR pressed off after capture, or HDG SEL, cancels the approach to ALT HOLD (third review D03). TOGA, refused while the FMS has failed, makes the missed approach active, ends the approach, releases any hold and climbs in VNAV the same way from every approach state (third review D02). For an RNAV approach the approach type is the selected receiver's reported level (305, GPS phase 3b) |
| Altitude and speed constraints | Partial | Upper bounds are not checked, so a violated constraint reads as met (R04) |
| T/D, E/D, VNAV path, DES NOW | Partial | Simplified geometric path. The VNAV phase (CLIMB, CRUISE, DESCENT) is latched and recorded: past the T/D or on DES NOW the descent holds through level segments at its constraints and never climbs back, and only a cruise altitude entered above the aircraft or the missed approach leaves it. DES NOW descends at 1000 fpm to the active fix's planned altitude. The path meets a constraint at a fly-by fix where the turn begins, and a leg between two constraints at one altitude is level. The demonstration route and the R24R/I24R approaches fly a downwind, base and straight-in final north of CYUL (A23), crossing each constraint within 50 ft. A climb that starts close to the T/D is not capped (seen after Jump), which a real FMS would not climb |
| VNAV SPD | Not implemented | |
| RTA | Not implemented | |
| ETA and fuel predictions | Partial | Destination can be the missed-approach end (R07); gaps (R08); future speed constraints ignored (R09) |
| Cold temperature correction | Demonstrated | Test uses the production helper; needs an independent worked example |
| FMS failure | Demonstrated | Laboratory reversion (Q-A2): managed guidance becomes invalid, heading and altitude are latched and flown as HDG HOLD and ALT HOLD, recovery resumes nothing until LNAV and VNAV are selected, and Pause and HDG SEL stay usable (R02). The commanded target shown is the held altitude while a hold commands (third review D02) |
| MSG and message recall | Partial | MSG follows the latest unacknowledged alert, and only CLR on that alert acknowledges it. Recovery from FMS failure shows it again. An alert that typing displaced comes back on CLR with an empty scratchpad, so MSG always has an acknowledgement path; recall history is kept separately (R13). No message priority or queue beyond the latest alert |
| ATC / FMC COMM datalink | Placeholder | Representative workflow. STANDBY keeps an uplink outstanding (ATC annunciator, ATC page, bench condition) until WILCO or UNABLE (R14) |
| Rendezvous, moving waypoints, tactical descent | Partial | Rendezvous uses the route predictions, so it inherits R08 and R09; moving waypoints have no data age or expiry |
| Database cycles, DATABASE OUT OF DATE | Partial | Each cycle holds its own dataset; the two demonstration cycles hold the same demonstration data, and the bench says so. A loaded file, merged over the active cycle's data, becomes the inactive cycle. Activating it (IDENT, or the bench) is recorded and does not re-resolve the active plan: its fixes stay pinned, the record names those the new cycle places differently, and they take the new positions only when the crew executes a modification, recorded as ROUTE RE-RESOLVED. Dates come from the data or show UNKNOWN; an unknown end never raises DATABASE OUT OF DATE (R06, R16). No changeover-date or ground-only rule (N09) |
| MAINT self test and fault log | Placeholder | A demonstration of the page, not equipment built-in test |
| Dual FMS, independent operation | Placeholder | One model with a copy of the route; not a dual-channel protocol |
| EFIS: primary flight and navigation displays | Partial | Generic displays drawn only from the FMS output bus (efis.ts); conventions from FAA and Boeing sources, not a CMA installation's EFIS; pitch is derived from the flight path (the point-mass model has no attitude) |
| Out-the-window view | Partial | A 3D view from the simulated aircraft over open elevation data, with the active route in magenta: HUD or Panel layout, cockpit, chase or map view (see "Out-the-window view" below). Pitch is the flight-path angle, as on the PFD; the flight model does not know the terrain, so nothing warns of or prevents flying into it |
| Scenarios, recording, procedure text, run report | Partial | A 0.25 s tick contract shared by the bench and headless runs; validated admission; distinct outcomes (passed, failed, no checks, timed out, stopped, invalid, error); the report's context is fixed at run start (N01–N08). In-process against the built-in model only: no external software-under-test adapter, run manifest or controlled evidence import (R21–R23, R25) |

The review's order is adopted: repair active-plan authority, guidance validity and prediction validity (R01–R09) before adding breadth.

## How FMS outputs reach the EFIS (research for the displays)

**The FMS does not draw the flight displays.** An EFIS draws them from data the FMS publishes, plus the aircraft's own sensors. Examples of what the FMS publishes, as ARINC 429 labels:
- 114, desired track;
- 116, cross-track distance;
- 117, vertical deviation;
- 121, roll steering command;
- 251, distance to go.

It also publishes the active waypoint, target speed and altitude, and mode and validity data. Each word carries a status: normal, no computed data, or failure warning. The symbology therefore belongs to the EFIS installation, not the FMS. A CMA-9000 drives whatever EFIS its aircraft has.

The bench follows the same structure. `efis.ts` defines the only data the displays may take from the FMS, and `FmsEfis.tsx` draws a generic primary flight display (PFD) and navigation display (ND) from it. A future FMS under test (Sean's embedded software) would drive the displays by producing the same bus.

### Conventions used

**Colours:**
- **Magenta:** what the FMS commands. This covers target speed and altitude bugs, the active route and waypoint, deviation pointers and flight director bars.
- **Green:** engaged modes.
- **White:** armed modes and inactive route data.
- **Cyan:** crew-selected values.
- **Amber:** flags.

**Route drawing:** active in solid magenta; modifications in dashed white; an executed offset in dashed magenta; inactive routes in dashed cyan.

**Flight mode annunciator:** speed, lateral and vertical columns. Engaged modes are green, with armed modes in white below. A newly engaged mode is boxed for ten seconds.

**Lateral deviation:** full scale is 5 NM en route, 1 NM in the terminal area and 0.3 NM on approach. The navigation source is annunciated beside the scale, and the display switches to approach sensitivity near the final approach fix.

**Vertical deviation:** a scale against the VNAV path or the final approach path. The diamond is filled when the path is being flown, and hollow when the information is advisory only (for example, an approach not armed).

**Navigation display** (Boeing MAP style), track-up:
- a compass arc and a heading pointer;
- a range arc;
- the active waypoint with its distance and ETA;
- ground speed, true airspeed and wind;
- the top and end of descent as green circles;
- the position trend vector;
- the map source, with RNP and ANP.

**Failure:** a failed FMS publishes failure words. The displays remove its data and flag FMS FAIL on the PFD and MAP on the ND, and the mode annunciator shows the basic reversion modes.

### Sources

- FAA-H-8083-6, *Advanced Avionics Handbook*: mode annunciation, CDI sensitivity and navigation source annunciation.
- Boeing 737 FCOM D6-27370-TBC, chapter 11 (Flight Management, Navigation): route colours, active restrictions, approach deviation scaling by RNP.
- Boeing 737 NG flight instruments and displays training notes: the ND symbol inventory and map modes.
- NASA TM-102710, *Description of the Primary Flight Display*: PFD elements and bugs.
- ARINC 429 label assignments (for example 114, 116, 117, 121 and 251), from the published general-aviation subset and label lists.

These are generic airline and general-aviation conventions. They are labelled as such and are not a claim about any CMA installation's EFIS.

Delivery history, for tracing: #1209 (panel), #1211 (conditions, deeper pages, lighting), #1212 (flight simulation, planning, lateral guidance, sensors, VNAV), #1218 (tactical functions, database cycles, maintenance and dual pages, scenarios).

Honesty rules: the simulator stays labelled as a simulation, and the demonstration navigation data is invented. The CMA-9000 manual governs where it speaks. Where it is silent, the behaviour here is a named engineering assumption that needs a source before any fidelity claim; generic airline practice (Boeing, Airbus) informs questions, not CMA behaviour.

## GPS sensor simulation (CMA-5024)

A simulated CMC CMA-5024 GPS/SBAS landing system sensor unit, built in phases (the design is in the 28 September research note). Phase 1 is in `product/client/src/fmsCdu/gnss.ts` and `gps.ts`: one receiver that publishes only a bus of labelled words, as the FMS will read it. It is a simulation, not the certified receiver.

- **Constellation** (`gnss.ts`): 31 satellites on circular orbits in six planes inclined 55°, seeded and deterministic. Each has an elevation, an azimuth, an elevation above the antenna's horizon (which tilts with bank and pitch) and a C/N0 that is a parameter of elevation, not a tracking-loop output. The Earth is a sphere.
- **Modes**: Self-Test (10 s), Initialization, Acquisition until the time to first fix (a parameter, under 75 s per the datasheet), NAV with four or more satellites, SBAS NAV once a geostationary satellite has been tracked long enough for its corrections (a laboratory 30 s), SBAS PA with a valid approach selected and not parked, Altitude Aiding with three and baro altitude, and Fault. Aided (inertial coasting) is not modelled. A reset keeps the ephemeris, so it reacquires without a new time to first fix.
- **SBAS**: two geostationary satellites (PRN 131 and 133, over 117° W and 129° W, like WAAS's), tracked on their own channels and not used for ranging. Corrections scale the range errors down. The SBAS protection levels take the DO-229 form: a weighted position covariance with its K factors (6.18 horizontal for SBAS NAV, 6.0 and 5.33 for precision approach), from per-satellite bounds for the corrections, the ionosphere (mapped to the elevation), the airborne receiver and the troposphere. Those bounds are laboratory values, not broadcast UDRE and GIVE data. The bench can set "do not use" (back to NAV at once), take a geostationary satellite out, or multiply the ionospheric bound for a storm.
- **Approach**: the FMS selects an approach and sends its FAS data block (the DO-229 Appendix D fields, in engineering units) with a CRC-32Q. The CRC here is over this model's serialization of the fields, not the DO-229 bit packing. Label 156 reports selected, available, CRC invalid, mismatch (the block is for another approach), incomplete (no block yet) and parked. From the block and its own fix the GPS computes 116 lateral and 117 vertical rectilinear deviations (ft) and 201 distance to the threshold (NM), and the approach level it can support: LPV in SBAS PA within the block's alert limits, LNAV/VNAV with SBAS within 556 m and 50 m, LNAV within 556 m, otherwise none. The deviations are No Computed Data with no active approach; the vertical one is Failure Warning below LNAV/VNAV, and both are with no level. Label 305 carries the SBAS PA state, the provider and (a model assumption) the level.
- **Fix and integrity**: the position is the truth plus the error that per-satellite range errors (seeded noise plus any injected ramp or step) produce through the satellite geometry by least squares. DOP, the RAIM residual test, fault exclusion and the slope-based HPL and VPL all come from that geometry. The false alert probability and the missed detection margin are laboratory parameters. With two or more degrees of freedom FDE excludes the faulty satellite; with one it detects but cannot exclude, and the integrity words go to Failure Warning; a bias below the test threshold passes undetected.
- **Bus**: 110/120, 111/121, 076, 370, 103, 112, 165, 166, 174, 101, 102, 130, 133, 247, 136, 150, 260, 273, 355, 156, 305, 116, 117, 201 and 060 per satellite (geostationary ones flagged), as engineering values; bit layouts are not modelled. Word status follows the manual: Functional Test in self-test, No Computed Data without a fix, Failure Warning in Fault, and status and maintenance words (273, 355) Normal in every mode. A unit that cannot control its word status stops transmitting.
- **Overrides**: any word's value or status can be forced, frozen, biased or ramped, over what the receiver computes.
- **Approach region, scaling and status overrides** (phase 3b groundwork): SBAS PA is entered only within 30 NM of the landing threshold (a laboratory value after the AC 20-138D terminal area convention, not a CMA-5024 figure); outside it the valid selected approach is armed in SBAS NAV (label 156 `armed`) with no deviations. Beside 116/117 the receiver publishes a deviation scale in engineering units (a model output, not an ARINC label): the lateral full scale splays from the FAS course width at the threshold from the GNSS azimuth reference point 305 m beyond the FPAP; the vertical is ±0.25 × the glide path angle from the path origin, bounded to 15–150 m, as commonly described for LPV (the bounds are assumptions to confirm against DO-229). Status words 273, 355, 156 and 305 can be overridden field by field with typed, validated values; an invalid patch is refused.
- **Jamming and spoofing** (phase 4a): jamming lowers every signal's C/N0 by a number of dB; spoofing adds a consistent position offset or drift that the receiver reports as valid, as RAIM cannot see it.
- **On the bench** (phase 4a, joined to the FMS): the tools under the cockpit are tabs. The GPS sensors tab works on the FMS's own GPS1 and GPS2. Its routing strip shows GPS 1 and GPS 2 into the FMS and the EFIS, the one the FMS navigates on drawn solid, their position difference, and the FMS GPS selection (AUTO, GPS1, GPS2 or OFF, as NAV OPTIONS). Per receiver a card (mode, sky plot, C/N0, DOP, HFOM, HIL and VIL, HPL and VPL against the limits of the flight phase on a logarithmic scale, approach level, SBAS, active faults), the fault controls and a live bus monitor with per-word overrides. After every change there, the FMS re-reads the receivers, so a fault reaches its choice and alerts at once. While the GPS integrity lost condition is on it owns the receivers' satellite selection, so the tab's masking is disabled and shown cleared.

### FMS integration (phase 3a)

The FMS owns two receivers (GPS1 and GPS2: different noise seeds, one constellation) in `scriptedFms.ts`, steps them from the aircraft's true state at every navigation update, and judges them from their buses alone (`gpsSensors.ts`). Both start warm, already navigating.

- **Selection**: a receiver can be used when its bus is transmitting, its position words (110/120, 111/121) are Normal, and its HIL (130) is Normal and within the phase's horizontal alert limit (2 NM en route, 1 NM terminal, 0.3 NM approach). In AUTO the FMS uses GPS1, else GPS2, else DME/DME, VOR/DME or dead reckoning. GPS NAV on NAV OPTIONS steps AUTO, GPS1, GPS2 and OFF; a receiver chosen by hand has no fallback to the other. Every change of source is recorded (`navSourceLog`).
- **Position and ANP**: in GPS mode the FMS position is the selected receiver's fix, and ANP is its HFOM (247) with a laboratory floor of 0.02 NM. `navPerformance` stays the single source every page and alert reads.
- **Alerts**: HIL over the limit or at Failure Warning gives GPS POS UNCERTAIN and the next source; neither receiver usable gives GPS NAV LOST; no approach level (305) gives NO APPR INTEGRITY. **GPS DISAGREE** is a laboratory alert, not in the CMA-9000 list: once per episode when both receivers have a fix, whatever their integrity, and the fixes are more than 0.1 NM apart (a laboratory limit).
- **Spoofing and undetected faults**: a spoofed GPS1 (position words biased with a Normal status) passes every integrity check and walks the FMS position off; only the GPS1/GPS2 compare catches it. A satellite range bias below the RAIM threshold goes undetected: the fix moves by a few metres, well under the compare limit, and nothing alerts. That is deliberate, not a gap: it is the hazardously misleading information risk that integrity monitoring bounds but cannot remove.
- **Conditions**: GPS lost takes the RF input from both receivers. GPS integrity lost leaves each receiver its five highest satellites and puts a 200 m range step on the one RAIM sees best: with one degree of freedom that satellite's share of the residual space is at least 1/5, so the step is always detected and cannot be excluded, and HIL goes to Failure Warning on both. (The lowest satellite, as first proposed, can have a near-zero share in some geometries and then goes undetected.)
- **Pages**: NAV STATUS names the receiver (GPS1 or GPS2) as the nav mode and shows its satellites used with the RAIM state and its SBAS provider and level; LSK3R opens GPS STATUS (both receivers' mode, satellites used and visible, HIL, HFOM and RAIM state, the one navigated on marked with an asterisk), which leads to POS SENSORS (the FMS, GPS1 and GPS2 positions). PROGRESS 3/4 shows the receiver's mode, satellites used, HIL, SBAS and integrity; POS INIT's GPS POS is the selected receiver's fix.
- **Attitude**: the flight simulation reports its bank and flight-path pitch (the point-mass model has no angle of attack) through `setAircraft`, and the FMS feeds them to the receivers, so a steep bank masks satellites on the high wing. Each receiver's baro input can be taken away from the bench (`setGpsBaro`).

### Approach guidance (phase 3b)

- **FAS block**: for an RNAV approach in the active route the FMS builds the FAS data block from its navigation database (`gpsSensors.ts` `buildFas`) and sends it to both receivers, once per change; an ILS or no approach sends no selection. The landing threshold point is the runway threshold, its height above the ellipsoid through the receiver's default geoid; the FPAP is the runway's far end; the TCH is the runway leg's altitude above the runway (50 ft for RW24R); the glide path angle runs through the FAF altitude and the TCH; the path identifier is the procedure ident. The course width (105 m), HAL (40 m) and VAL (50 m) are laboratory values: the demonstration database has no published FAS data.
- **Level and annunciation**: the approach type on the FMA, the EFIS bus and the VNAV page is the level the selected receiver reports (305): LNAV/VNAV in SBAS NAV outside the 30 NM approach region (156 armed), LPV in SBAS PA inside it, LNAV without SBAS, and NO APPR without GPS navigation or a level. The vertical column of the FMA shows the level armed only when it has vertical guidance (ILS, LPV, LNAV/VNAV). The VNAV page title shortens LNAV/VNAV to L/VNAV and drops the runway's RW so the longest title fits (R19).
- **Guidance on final**: captured on an RNAV final, the lateral guidance steers the selected receiver's 116 deviation along the FAS course, and the vertical guidance corrects toward the path from its 117. The EFIS bus shows 116 and 117 as they stand, on the receiver's angular scaling. The demonstration FAF (FERDI) and intermediate fix (ULIDA) are on the RW24R extended centreline (moved there from about 480 ft and 80 ft off it), so the route's final is the FAS course: from capture to the threshold the aircraft stays within 30 ft laterally and 30 ft vertically.
- **Downgrade**: a level without vertical guidance (an ionospheric storm pushing VPL over the VAL, SBAS lost), or 117 withdrawn, after capture is APPR LOST to a latched altitude hold, which its return does not re-capture (the D03 contract); the vertical deviation is flagged (FAIL) on the final leg, and NO APPR INTEGRITY is raised once vertical guidance the approach had in the approach phase is lost. Laterally the GPS still guides. An LNAV level arms the approach but never captures it: this laboratory contract descends beyond the FAF only on vertical guidance, and step-down LNAV to an MDA is not modelled.
- **Known limits**: the aircraft's barometric altitude is compared with the GPS path through 117 (no baro-VNAV blending); a laboratory LPV level is not re-checked against the FAS block's own HAL and VAL on the FMS side (the receiver does it); the FAS geometry is derived, not published.

Sources: CMC Electronics CMA-5024 datasheets ([current](https://cmcelectronics.ca/wp-content/uploads/2022/09/4.1.3-CMC-CMA5024-GPS-19-011.pdf), [earlier](http://jproc.ca/rrp/rrp3/ch148_cma5024.pdf)); the CMA-5024 GLSSU Installation Manual as hosted on [ManualsLib](https://www.manualslib.com/manual/2035147/Cmc-Electronic-Esterline-Cma-5024.html) (operating modes p. 49, outputs and SSM pp. 110–111, output tables pp. 113–122, labels 060 p. 134, 156 p. 139, 273 p. 148, 305 p. 150, 355 p. 154, FAS block p. 196); ARINC 743A label summaries ([GlobalSpec](https://standards.globalspec.com/std/10392102/arinc-743a)); FAA [AC 20-138D](https://www.faa.gov/documentLibrary/media/Advisory_Circular/AC_20-138D_Change_1.pdf). RTCA DO-229D is cited through these, not read directly.

## Out-the-window view

"Show the view" above the cockpit opens a 3D view from the simulated aircraft, drawn with CesiumJS (Apache 2.0, bundled and served by AeroLink like the rest of the client). It is hidden until asked for, because it loads a 3D engine and the terrain around the aircraft; the choice, the layout and the view are remembered.

- **What it shows**: the ground from open elevation data, coloured by height and shaded by slope from the north-west, the way a synthetic vision system draws it, with level water in blue. No photographic imagery is used: the free global imagery sets are non-commercial or need a licence. The active route is drawn in magenta at its altitude constraints (a fix without one takes the altitude before it), each fix labelled. The route fixes are the demonstration database's invented positions over real terrain.
- **Layouts**: **HUD** is a large window with head-up symbology in green: the flight modes (the same annunciator as the Flight card), a bank scale, airspeed and ground speed, altitude and vertical speed, a heading scale, and the flight path marker where the aircraft is going. **Panel** is a shorter window over a glareshield, with the bench's CDU and EFIS directly below it standing in for the instrument panel.
- **Views**: **Cockpit** is the pilot's eye, along the heading, pitched with the flight path and rolled with the bank. **Chase** is 0.12 NM behind and 90 m above, level. **Map** looks straight down from 30,000 ft above the aircraft, heading up.
- **Motion**: the simulation moves in 0.25 s ticks (further per tick at a higher rate); the view draws each frame between the last two ticks, a quarter-second behind, so it moves smoothly at any rate. The eye is kept 3 m above the terrain it would otherwise fly through, since the flight model does not know the terrain.

**Where the terrain comes from.** The browser may only talk to the AeroLink server (the document's Content Security Policy and DEC-047), so it asks the server, and the server relays the tile from the [Terrain Tiles](https://registry.opendata.aws/terrain-tiles/) open data set on AWS (Terrarium PNG encoding; SRTM, GMTED2010, USGS NED and other open sources, attributed in the view). The relay (`FmsBenchTerrainEndpoints.cs`, `GET /api/fms-bench/terrain/{z}/{x}/{y}`) needs a signed-in session, reaches exactly one fixed upstream built from three validated integers (zoom 0 to 15), passes on only a PNG, and is bounded to 10 seconds and 1 MB a tile. After an upstream failure it answers at once for a minute rather than holding each request for the timeout.

The relay is an outbound call from the server, which DEC-047's reasoning leaves to each installation: AeroLink runs on restricted and disconnected networks. It is therefore **off unless `FmsBench:TerrainRelay` is `true`** (environment variable `FmsBench__TerrainRelay`), and the Development environment, which the demonstration launcher uses, turns it on. Off, or with the upstream unreachable, the view still starts and flies over flat ground, and says why.

Known limits: the flight model has no terrain awareness, so the aircraft can fly into a hill with no warning (no TAWS); pitch is the flight-path angle, not an attitude; the chase and map views show a plan-view symbol rather than an aircraft model; the ground is sampled at about 20 m (zoom 13) for the mesh and shaded from up to zoom 15.

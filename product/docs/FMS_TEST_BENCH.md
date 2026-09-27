# What makes a good FMS: research and gap analysis for the AeroLink FMS Test Bench

*27 September 2026. Written for the CMA-9000 simulator roadmap.*

## Sources I used

- **ICAO Doc 9613, Performance-based Navigation (PBN) Manual, 5th edition.** The unedited version is on the PBN Portal. I used the RNAV 1/2, RNP 4, RNP 1 and Advanced RNP functional requirements, and the crew knowledge and procedures that go with them. This is the certification-grade list of what a navigation FMS *must* do.
- **Boeing 737 FCOM, Chapter 11, Flight Management and Navigation (2004–2006 revision).** 348 pages. It describes how a mainstream airline FMS behaves in service: position updating, ANP/RNP, waypoint naming, VNAV, RTA, route modification, offsets, REF NAV DATA and messages.
- **Airbus Flight Operations Briefing Note, "Optimum Use of Automation" (via SKYbrary).** It covers what airlines train crews to expect from the FMS and autoflight: managed versus selected guidance, mode awareness, and cross-checking.
- **American Airlines 965 (Cali) accident material.** From the MIT course pack, the FAA Lessons Learned page and Wikipedia. It shows how a direct-to and a duplicate identifier contributed to a fatal accident.
- **Flight Safety Foundation, "Data-entry errors can lead aircraft off course"**, and the SKYbrary work on FMS data-entry errors. One study cited there found 309 air-safety reports on FMS data-entry errors between 2007 and 2011; 80% involved navigation data.
- **Honeywell, "Understanding FMS navigation procedures" (ARINC 424 path terminators)**, plus Honeywell and Airbus material on FMS upgrades, datalink and RNP AR.
- **FAA/MITRE and IFATCA findings on predictability.** Different manufacturers' FMSs build turns and vertical paths differently. ATC needs repeatable paths, and airlines need the aircraft to do what the crew expects.
- **NASA and Flight Safety Foundation work on VNAV "automation surprise"**, from search summaries only: the FAA and NASA servers refused automated download.

The FAA advisory circulars AC 20-138D and AC 90-105A would not download, so I used ICAO Doc 9613, which covers the same functional requirements.

## What airlines look for, in short

1. **Predictability over cleverness.** Pilots ask "what is it doing now, and what will it do next?" Automation surprise, especially in VNAV, is a recurring cause of incidents, and VNAV is the FMS function pilots most want more training on. A good FMS makes every mode, transition and target visible before it happens.
2. **Error-tolerant data entry with verification.** Airlines lean on the MOD → review → EXEC pattern, on route discontinuities as deliberate stops, and on cross-checking the map against the CDU. Accidents show the dangers: a direct-to silently removes intermediate reporting points, and a duplicate or chart-mismatched identifier can pick the wrong fix. Good FMSs show nearest-first SELECT DESIRED WPT lists with facility type and position, and offer abeam points on a direct-to.
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

1. **Everything is a MOD until EXEC**, with the dashed route on the map. *Done.* Carry the same rule into VNAV and offset changes.
2. **Show the next mode, not only the current one.** Add a flight mode annunciator strip showing the engaged and armed lateral mode (LNAV, HDG, HOLD, SAR) and vertical mode (VNAV PTH, VNAV SPD, ALT), and show transitions as they happen.
3. **Make direct-to consequences visible.** Offer ABEAM PTS so the reporting points survive a direct-to; the Cali accident is the lesson. Keep INTC CRS beside it.
4. **Duplicate identifiers:** show the list nearest first, with facility type and position. *Done:* our SELECT DESIRED WPT shows type and position; nearest-first ordering is next.
5. **Messages** must be specific, amber for alerts, recallable, and able to light MSG. *Done.* Add the in-service messages the new features need: UNABLE NEXT ALTITUDE, RTA UNACHIEVABLE, USING RSV FUEL, INSUFFICIENT FUEL, UNABLE REQD NAV PERF–RNP, POS SHIFT, VERIFY RNP.
6. **Repeatable paths:** document the turn and bypass rules and test them, so engineers can compare the simulator's path with the real CMA-9000's.

## Revised plan for the remaining work

The order is revised by what airlines and certification treat as core. The gap tables above are the 27 September baseline; steps 2 to 7 are built, and `PROJECT_STATE.md` describes what the bench does now. Step 8 remains.

| PR | Scope |
|---|---|
| 1 (merged, #1211) | Conditions, deeper pages, lighting |
| 2 (done) | Flight simulation and navigation map |
| 3 (done) | Navigation database and flight planning: airports, runways, navaids, airways, SIDs, STARs and approaches with transitions, missed approach and hold, VIA/TO airways, pilot waypoints (latitude/longitude, place-bearing-distance, place-bearing/place-bearing, along-track), SELECT DESIRED WPT, REF NAV DATA, company routes, SEC FPLN, ARINC 424 loader |
| 4 (done) | **Lateral guidance fidelity:**<br>• path terminators (CF, DF, CA, VA and FA conditional legs; VI and VM vector legs; FM; RF arcs; HA, HF and HM holds);<br>• INTC CRS and ABEAM PTS;<br>• a parallel offset that is flown, with start and end;<br>• leg bypass;<br>• selected heading versus managed LNAV, with arm and capture;<br>• a flight mode annunciator strip. |
| 5 (done) | **Navigation sensors, RNP and approaches:**<br>• GPS/DME/VOR/inertial blending with priority and reversion;<br>• NAV STATUS and NAV OPTIONS pages, including navaid inhibit;<br>• DME autotune;<br>• computed ANP;<br>• default RNP by phase, time to alert and UNABLE RNP;<br>• on-approach logic;<br>• POS SHIFT;<br>• RAIM and SBAS;<br>• approach types (LNAV, LNAV/VNAV, LPV) and go-around. |
| 6 (done) | **VNAV and performance:**<br>• speed and altitude restrictions;<br>• VNAV PTH and VNAV SPD;<br>• top and end of descent;<br>• DES NOW;<br>• UNABLE NEXT ALTITUDE;<br>• winds;<br>• ETA and fuel predictions at each waypoint;<br>• fuel alerts;<br>• RTA;<br>• cold-temperature correction;<br>• altitude formats and transition altitude. |
| 7 (done) | **Tactical, maintenance and dual FMS:**<br>• rendezvous and moving waypoints;<br>• tactical descent;<br>• AIRAC active and inactive cycles, DATABASE OUT OF DATE, temporary and supplemental databases;<br>• built-in test and maintenance pages;<br>• cross-side synchronisation and independent mode. |
| 8 | **Scenarios and AeroLink integration:**<br>• scripted scenarios (for example, GPS lost 2 NM before the final approach fix);<br>• record and playback;<br>• screen assertions;<br>• links to test procedures and evidence. |

Honesty rule, unchanged: the simulator stays labelled as a simulation. The demonstration navigation data is invented. Where the CMA-9000 manual differs from this generic airline practice, the manual wins; where the manual is silent, the behaviour above is the default.

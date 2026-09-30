# FMS Test Bench helicopter-first v1: acceptance ledger

This ledger replaces the claim that "every Stage A–E exit condition is met" (Claude's summary of 29 September 2026). Astra's implementation review of that summary rejected the claim (finding F5): it asked for a row per requirement, giving:

- the requirement and its source;
- the implementation;
- the owner test;
- the observed evidence;
- any accepted deferral.

Sean kept E5 persistence and Phase 1 joining in v1 (29 September). The plan is the helicopter-first plan, revision 2 with the revision 3 changes and the revision 3.1 addendum (DEC-146), plus Sean's decisions for the night of 29 September.

**How a row is judged:**
- **Met:** an owner test on `main` asserts the behaviour.
- **Partial:** a test covers only part of the requirement, or covers it only inside a larger case. The row says what is missing.
- **Open:** nothing implements or tests it.
- **Deferred:** only where a deferral has been accepted, with who accepted it and where. Nothing is marked Deferred on the strength of an unrecorded acceptance.

Where Sean ruled on the night of 29 September and Astra/Sol is implementing it, the row says so, and its status stays Partial or Open until that work lands.

The rows were assembled by the three sessions that built each stage:
- **Session 2:** Stages A, B and D, D-H, D-S, D-R, C.3 and §10.
- **Session 3:** Stage E, C.11, R3-03, R3-04, B1.7 and F1, F4, Q4.
- **Session 1:** Stage C, E5, E6, and the evidence and deferral rows.

Each was checked against `main` on 30 September 2026 (bab18a38). The test titles are those of the `fms-*.spec.ts` specs on that commit, shortened.

## Summary

140 rows: **74 Met, 52 Partial, 13 Open, 1 Deferred** (Stage F only).

v1 is **not complete**: the §10 completion row stays Open until the Partial and Open rows are closed or explicitly deferred by Sean.

The Open rows:
- the ATA at a fix;
- the aircraft freeze;
- time-to-go asterisks;
- F4 (RA valid again) and F10 (admission refusal);
- the ND ground-velocity vector;
- PROGRESS showing TDN and MRK;
- the D-R recompute, 500 NM rule, restart epoch, expiry and tactical review.

Several Partial rows are being addressed tonight by Astra/Sol under Sean's 29 September night decisions: the profile's in-force flags and bank limit, S300 advisory VNAV and the LPV profile split, the civil navigation core, and backtrack.

## Stage A: profile, applicability and vertical-guidance policy

| ID | Requirement | Source | Implementation | Owner test | Status |
|---|---|---|---|---|---|
| A1 | Applicability matrix in product/docs, referenced from the register | rev2 A1 | FMS_APPLICABILITY.md (#1252; reconciled #1269, #1271) | none (document) | Partial: document only; no guard ties its rows to code |
| A2 | Default profile cma9000-s300-heli-civil v1 as versioned data, fingerprinted, named in every evidence record | rev2 A2; rev3 A2, §3a | profile.ts HELICOPTER_PROFILE, profileSummary; scenario RunContext (#1252) | fms-scenario: "the profile fingerprint changes when any profile value changes"; "a scenario names its aircraft profile" | Partial: all 34 parameters are inForce:false although many are flown, so reports say "0 of 34 in force"; the profile's bank limit is 30 while the flight uses 25 (Astra/Sol's first item, 29 Sep night) |
| A3 | Register corrections C1–C16 | rev2 A3; rev3 | FMS_TEST_BENCH.md (#1252 onward) | none (document) | Partial: document only |
| A4 | Vertical policy: no airline VNAV by default; constraints advisory; the crew flies ALT/VS/preselect; S300 advisory approach VNAV where constructible; airline VNAV behind a lab profile | rev2 A4 | verticalPolicy ADVISORY; LAB_AIRLINE_VNAV_PROFILE; flight.ts crew modes (#1252, #1254) | fms-efis: "under the helicopter profile the FMS commands no altitude or speed"; fms-modes: "under the helicopter profile TOGA climbs in GA" | Partial: S300 advisory approach VNAV not built, and coupled LPV is still in the helicopter profile. Sean (29 Sep night): S300 advisory only, with LPV in a separate profile; Astra/Sol implementing |
| A5 | Provenance, validity, selection and engagement kept distinct | rev3 A5 | profile basis field; bus word SSM; EFIS bus | fms-efis: "the bus says so, and the crew selections are aircraft data" | Partial: not every output is tagged; no systematic owner test |

## Stage B: rotorcraft foundation

### B1 kinematics and B2 height

| ID | Requirement | Source | Implementation | Owner test | Status |
|---|---|---|---|---|---|
| B1.1 | Truth state with physical height; sensors derived from truth, never feeding back | rev3 B1.1; rev3.1 B1.1 wording | flight.ts state (integrates the physical height); baro.ts (injectable error, QNH/STD setting, ISA pressure altitude); ScriptedFms physicalAltitude, altitude, indicatedAltitude; surface.ts radioHeight; GPS from truth (#1254) | fms-tactical-maint: "the radio altimeter reads height above the declared surface"; fms-baro: "changing the setting in flight never moves the aircraft …", "an injected baro error changes nothing physical at once …", "in ALT the autopilot holds the barometric altitude …", "the indicated altitude follows the crew's setting …"; fms-cdu-rendered: "B1.1: the PFD writes the altimeter setting …" | Met (the setting changes what is indicated only; the autopilot and FMS use the barometric altitude referenced to the declared QNH, a declared laboratory simplification) |
| B1.2 | Air data: TAS = \|ground velocity − wind\|; IAS from ISA; a hover in wind has TAS = wind | rev2 B1.2 | flight.ts; kinematics.ts tasFromIas/iasFromTas (#1254) | fms-flight: "HOV holds position in a 20 kt wind: … airspeed about the wind"; fms-efis: "… no IAS below 30 kt (B4)" | Met |
| B1.3 | Heading independent of track; coordinated and low-speed regimes with 40/45 kt hysteresis | rev2 B1.3; rev3 | flight.ts integrateLowSpeed, leaveLowSpeed (#1254, #1257) | fms-flight: "in a crosswind the aircraft crabs"; "in HOV a heading selection yaws … 360 degrees"; "TU from an exact hover …"; fms-out-the-window: "the cameras follow the heading, not the track" | Partial: no test of 10 kt sideways flight (rev2 test list) |
| B1.4 | Wind triangle everywhere; infeasible, never clamped | rev2 B1.4 | kinematics.ts holdTrack/groundVelocity (#1254) | fms-flight: "the wind triangle gives the crab angle … refuses a track the airspeed cannot hold" | Met |
| B1.5 | Dynamics as rate and acceleration limits; attitude as a display derivation | rev2 B1.5 | flight.ts rate limits; pitch (#1254, #1264) | fms-flight: "the airspeed changes at the profile acceleration limit"; "… yaws … at the yaw rate"; fms-87n-mission: "climbing out of a hover the modelled pitch stays within its limit" | Partial: pitch is the air-relative flight-path angle (a laboratory stand-in); the rate-one bank and radius table is not tested |
| B1.6 | Airborne state independent of ground speed; never ground planning in a hover | rev2 B1.6 | no ground state in v1 | none | Partial: airborne only by construction; no ground/air state (C12); no owner test |
| B1.7 (a) | Arrival at the current fix: sequence; the ATA is recorded | rev3 B1.7 | flight.ts sequencing; no ATA | fms-flight: "a fly-by waypoint is sequenced before the aircraft reaches it" | Open: no ATA is recorded or shown |
| B1.7 (b) | Planned zero-speed element: the route ends at MRK with a discontinuity; the ETA at MRK comes from the transition profile, and predictions end there | rev3 B1.7 | hover procedure (JN, TDN, MRK, disco) (#1257, #1278) | fms-hover-join: route `JN TDN MRK disco`; fms-flight: "the HOVER page activates the transition …" | Partial: the route shape is Met; the MRK ETA from the transition profile and predictions ending at MRK are not tested |
| B1.7 (c) | Held stationary off-plan: NO PROGRESS; ETAs NCD, endurance valid, EFOB unknown | rev3 B1.7 | vnav.ts NO PROGRESS status (#1258) | fms-prediction-endpoints: "B1.7: held stationary off the plan, there is NO PROGRESS" | Met (endurance validity not asserted separately) |
| B1.7 (d) | An indefinite MANUAL hold: TO is the next crossing along the remaining pattern; downstream follows HOLD-ETA | rev3 B1.7; R3-03; M300 5-17 | predictions.ts, scriptedFms hold path (#1258, #1263; #1279) | fms-prediction-endpoints: "R3-03: a MANUAL hold makes its fix … CONDITIONAL"; "F1: a manual hold at RDG flown in a 30 kt wind …" (#1279) | Met (#1279: the bus ETA, the HOLD page FIX ETA and PROGRESS 1/4 show the predicted next crossing) |
| B1.7 (e) | An unresolved discontinuity: unknown beyond it | rev3 B1.7 (R08) | vnav.ts status (#1222, #1258) | fms-predictions: "a leg of unknown length makes it and everything after it unknown (R08)" | Met |
| B1.7 (f) | Planned GS ≤ 0 on a leg: infeasible, unknown from that leg, with the reason | rev3 B1.7 | kinematics.ts; vnav.ts (#1258) | fms-prediction-endpoints: "B1.7: a leg the planned airspeed cannot make progress along" | Met |
| B1.7 (g) | A paused run: the clock stops; predictions and fuel frozen | rev3 B1.7 | FmsCduTestBench pause | fms-cdu-rendered: "Fly moves the aircraft … and Pause stops it" (position only) | Partial: frozen predictions and fuel not asserted |
| B1.7 (h) | An aircraft freeze: the clock runs; the aircraft and fuel frozen; moving waypoints move | rev3 B1.7 | none | none | Open: no aircraft-freeze control exists |
| B1.7 (i) | Time-to-go beyond the field range: asterisks | rev3 B1.7; M300 2-18 | none | none | Open |
| B1.7 (general) | ETAs from the remaining planned path, never from instantaneous closure | rev3 B1.7 | profile ETAs (vnav.ts) | as the rows above | Partial: the VNAV CRZ T/D ETA (fmsPages.ts) is still closure-based; left for the Astra/Sol work |
| B2.1 | Radio height from physical height; NORMAL / NCD / FAIL | rev3 B2.1 | surface.ts radioHeight (#1254) | fms-tactical-maint: "the radio altimeter reads height above the declared surface, NCD off it or above its range, FAIL when failed" | Met |
| B2.2 | Declared flat surface per scenario; NCD off it | rev2 B2.2 | surface.ts; scenario.surface (#1254) | fms-scenario: "a scenario declares the radio altimeter surface by id …" | Met |
| B2.3 | NCD above 2,500 ft | rev2 B2.3 | surface.ts | fms-tactical-maint (as B2.1) | Met |
| B2.4 | (i) no ACTIVATE without RA; (ii) RALT FAILED at EXEC, MOD kept; (iii) TDN FUNCTION LOST, request withdrawn, lateral continues | rev3 B2.4 | activateHover, execute, watchHover (#1257) | fms-flight: "ACTIVATE needs a valid radio height …"; "radio height lost during TD/H … (F2)"; fms-87n-mission variants (a1), (a2), (a3) | Met |

### B3 the representative autopilot, and the failure table

| ID | Requirement | Source | Implementation | Owner test | Status |
|---|---|---|---|---|---|
| B3.1 | Axes and modes, ATT; HOV feedback from the selected receiver's words, never truth | rev3 B3.1 | flight.ts axisModes; ScriptedFms.hoverFeedback (#1254, #1257) | fms-flight: "HOV holds position"; "hover feedback lost: HOV gives way to ATT (F5)"; "the hover steers on the measured velocity, not the true wind (B3c)" | Partial: GSPD (ground-speed hold) not implemented |
| B3.2 | TD, the TD/H window, TU-LAB, GA, low-height protection, minimum use height | rev3 B3.2 | flight.ts; transition.ts shared laws (#1254, #1257, #1272) | fms-flight: "TD/H from its window …"; "the TD/H window: refused above 210 ft RA and at 85 kt"; "TU from an exact hover …"; "GA from the hover needs no FMS"; fms-modes: "under the helicopter profile TOGA climbs in GA" | Partial: the low-height protection trip (75/17 ft) and the minimum-use-height refusal have no owner test; GA not tested from each coupled mode |
| B3.3 | FMS to AFCS: label 121; the transition request with MRK, final track, remaining distance and planned trajectory | rev3 B3.3 | hover.requestData; transition.ts shared command laws (#1257, #1272) | fms-efis: "in LNAV the bus carries … roll command"; fms-flight: "the transition is flown to a hover at MRK"; fms-tdn-flight | Partial: the request carries MRK, final track and procedure id, not the planned trajectory (shared command laws instead) |
| B3.4 | AFCS to FMS and displays: engaged, armed and degraded modes; datums; VX/VY | rev2 B3.4 | flight.ts axisModes; fms.afcs (#1257) | fms-efis: "in the hover the displays get the helicopter data …" | Partial: armed and degraded modes not published |
| F1 | RA lost during TD: ALT latched to baro; pitch TD continues; TDN FUNCTION LOST | rev3 B3.5 | flight.ts lowCollectiveSpeed (#1257) | fms-87n-mission variant (a3) (#1267) | Partial: the first-tick VS → 0 transient and LOW HT OFF not asserted |
| F2 | RA invalid in TD/H: ALT; horizontal plan to MRK continues; its named cancellations | rev3 B3.5; R3-02.5 | flight.ts watchHover (#1257) | fms-flight: "radio height lost during TD/H … (F2)"; "a direct-to during the transition … (F2)"; "a heading selection cancels a TD/H plan (R3-02)" | Partial: the cyclic force-trim release is not modelled (declared) |
| F3 | RA invalid in the hover: ALT; HOV continues; LOW HT OFF | rev3 B3.5 | flight.ts (#1254) | fms-flight: "radio height lost in the hover … LOW HT OFF (B3b, F3)"; fms-87n-mission variant (a4) | Met |
| F4 | RA valid again: no automatic re-engagement; LOW HT OFF stays | rev3 B3.5 | flight.ts | none | Open: no owner test |
| F5 | HOV feedback invalid in HOV: ATT holding the latched air velocity | rev3 B3.5; R3-02.1–2 | flight.ts (#1254, #1257) | fms-flight: "hover feedback lost … (F5)"; "integrity lost while … NORMAL"; "velocity words invalid alone"; fms-87n-mission variant (c) (#1275) | Met |
| F6 | Feedback invalid in the TD/H horizontal: ATT; the collective continues | rev3 B3.5 | flight.ts (#1257) | fms-flight: "FMS failure during TD/H … either order with GPS loss ends the same (F9)" | Partial: covered only together with F9 |
| F7 | FMS roll command NCD or FAIL in NAV: HDG latched | rev3 B3.5 | flight.ts watchFailure | fms-modes: "on FMS failure the aircraft flies latched heading and altitude (R02)"; fms-cdu-rendered: "an FMS failure in flight reverts the flight modes" | Partial: FAIL only; roll command NCD alone not tested |
| F8 | FMS withdraws roll steering (TDN NOT POSSIBLE, UNABLE HOLD, end of route): NAV to HDG latched | rev3 B3.5 | flight.ts (#1257, #1259, #1261) | fms-flight: "at TDN, 0.3 NM off the final track … HDG"; fms-heliport-procedures: "UNABLE HOLD at the first fix passage … (F8)"; "MISSED-HOLD … HDG" | Met |
| F9 | FMS failure in TD or TD/H: HDG latched; TD/H decelerates to 0 and HOV holds where it stops | rev3 B3.5; R3-02.4 | flight.ts (#1257) | fms-flight: "FMS failure during TD/H … (F9)" | Met |
| F10 | Baro, heading or attitude invalid: the scenario is refused at admission | rev3 B3.5 | none | none | Open: no admission check or test (no condition can inject these) |

### B4 displays

| ID | Requirement | Source | Implementation | Owner test | Status |
|---|---|---|---|---|---|
| B4.1 | FMA: three columns, captured green boxed, armed white, degraded amber | rev2 B4.1 | FmsEfis.tsx (#1257) | fms-efis: "in the hover the displays get the helicopter data: axes …" | Partial: armed (white) and degraded (amber) not shown |
| B4.2 | The airspeed tape shows IAS; dashes below the minimum reliable IAS | rev2 B4.2 | FmsEfis.tsx (#1257) | fms-efis: "… no IAS below 30 kt (B4)" | Met |
| B4.3 | Height: RA readout with flags, hover height datum, LOW HT caption | rev2 B4.3 | FmsEfis.tsx (#1257) | fms-efis (as B4.1); fms-flight: "… LOW HT OFF (F3)" | Partial: the LOW HT (active) caption not owner-tested |
| B4.4 | Low-speed data: VX/VY, wind, heading and selected heading | rev2 B4.4 | FmsEfis.tsx (#1257) | fms-efis (as B4.1) | Partial: VX/VY pinned; wind and selected heading not asserted |
| B4.5 | ND ground-velocity vector replaces the trend vector at low speed | rev2 B4.5 | none | none | Open: not implemented |
| B4.6 | The CMA HOVER page fields, title states, ACTIVATE only with valid RA | rev2 B4.6 | tacticalPages.ts HOVER (#1257) | fms-tactical-maint: "the HOVER page shows the radio altimeter, dashes …"; fms-flight: "the HOVER page activates the transition"; fms-87n-mission nominal | Met (LAST JSTICK POS not applicable: joystick not configured) |
| B4.7 | Views: cockpit camera on the modelled attitude and heading; chase follows heading; a helicopter shape | rev2 B4.7 | FmsOutTheWindow (#1255) | fms-out-the-window: "the cameras follow the heading, not the track"; "the chase aircraft is a helicopter" | Partial: the cockpit pitch uses the air-relative flight-path stand-in, not a modelled attitude |

## Stage C: helicopter navigation data and procedure endpoints

| ID | Requirement | Source | Implementation | Owner test | Status |
|---|---|---|---|---|---|
| C.1 | Read heliport HA, HC, HF, HS records | rev2 §5 | arinc424.ts heliport section (#1256) | fms-heliport-procedures: "C.1: heliport reference, terminal waypoint and MSA records …" | Met |
| C.2 | Endpoint model: instrument end, landing site, visual segment (UNKNOWN unless a chart that has been read validates it), missed continuation; no manufactured threshold | rev2 §5; rev3 C.2 | navData.ts ProcedureEndpoint; procedureCharts.ts (#1256) | "C.2, C.4, C.9: all five …"; "C.2: 87N proceeds VFR from CRANN … 197 degrees magnetic 0.9 NM" | Met |
| C.3.1 | FMS missed-approach request: terminal phase, RNP 1.0, NO APPR INTEGRITY clears; before the MAP, guidance to the MAP, then the missed approach | rev3 C.3 | scriptedFms goAround (#1264) | fms-navigation: "TOGA before the MAP (helicopter): guidance continues to the MAP …"; fms-87n-mission: "variant (d): from TOGA to the MAP, every tick …" | Partial: MISSED APPR (the request without GA) has no separate control; terminal phase, RNP 1.0 and the NO APPR INTEGRITY clearing on the request not asserted |
| C.3.2 | AFCS GA is vertical and speed only; the bench TOGA is GA plus the FMS request | rev3 C.3 | flight.ts engageGoAround; scenario goAround action (#1257, #1264) | fms-modes: "under the helicopter profile TOGA climbs in GA …"; fms-87n-mission variant (d); nominal GO AROUND event | Met |
| C.3.3 | A crew direct-to during the approach: immediate, terminal phase | rev3 C.3 | directTo | fms-87n-mission variant (e) | Partial: the terminal phase not asserted |
| C.3.4 | With the FMS failed: GA remains; roll HDG; no lateral missed route | rev3 C.3 | flight.ts (#1257) | fms-flight: "GA from the hover needs no FMS …"; fms-modes: "on FMS failure the aircraft flies latched heading …" | Partial: GA on the approach with the FMS failed under the helicopter profile not tested |
| C.4 | No advisory VNAV on the supported zero-VPA point-in-space approaches | rev3 C.4 | endpoint vertical NONE (#1256); VNAV 1/3 NO VERTICAL PATH (#1268) | "C.2, C.4, C.9 …"; fms-prediction-endpoints: VNAV 1/3 on R190 | Met |
| C.5 | Procedure speed limits imported and applied from their fix: 87N 70 kt, 90 kt at valid baro ≥ 2,000 ft; advisory under the helicopter profile; forecast on every leg ahead | rev2/rev3 C.5; R3-04 (1); Astra F2 | arinc424.ts (#1256); procedureSpeed.ts (#1262); per-leg forecast (#1273) | fms-pins-approach: "C.5: a coded limit applies from its fix onward …"; "R3-04: the 90 kt release …"; "Astra F2: the forecast flies each leg ahead …" | Met |
| C.5 (VMINI) | A procedure limit below VMINI refuses coupled activation; the procedure stays plannable | rev3 C.5; Q7 | ScriptedFms.approachRefusal, armApproach (#1262) | fms-pins-approach: "Q7: an approach coding a speed limit below VMINI …" | Met |
| C.5a | The missed approach CA completes at once at the MAP when already at or above 439 ft | rev3 C.5a | the flight's CA leg termination | none that isolates it; the mission flies through it | Partial: an owner test is to be added |
| C.6 | Holds kept with leg distance; the missed hold flown one racetrack, then left; C16 (no 180 kt default) | rev3 C.6 | arinc424.ts holds (#1256); armMissedHold data (#1262); D-H (#1259, #1261) | fms-heliport-procedures: "C.6: a missed approach arms the BEADS hold with its coded 4 NM legs and 90 kt …"; "MISSED-HOLD: the BEADS hold is flown for one racetrack …" | Met |
| C.7 | Transition assembly by record role; a direct-to TIDUE keeps the HF, a direct-to STAYS drops it | rev3 C.7 | procedures.ts joinTransition (#1256) | "C.7: the transition joins the final by record role …"; "C.7: in the FMS the route flies TIDUE once …" | Met |
| C.8 | Unsupported procedures stay unavailable with the reason | rev2 C.8 | arinc424.ts errors (#1256, #1280) | "C.8: what is not supported stays unavailable with the reason" | Met |
| C.9 | The other four point-in-space approaches import with their endpoints; Copter identification only within a reviewed set | rev2 C.9; Astra Q8 | #1256; reviewed set REVIEWED_COPTER_PINS (#1280) | "C.2, C.4, C.9 …"; #1280's reviewed-set tests | Met |
| C.10 | Procedure notes displayed as fixture metadata, never enforced | rev2/rev3 C.10 | procedureCharts.ts notes carried on the procedure (#1256) | "87N R190 imports record by record …" (notes in the data) | Partial: the notes are carried, not yet displayed |
| C.11 | Every destination-type prediction carries an endpoint basis: INSTRUMENT END shown as the MAP ident, never the heliport; a direct-to the site gives an arrival basis; KBTV at the threshold; no positive reserve without a landing | rev3 C.11; R3-03 | predictions.ts; vnav.ts endpoint/reserve; fmsPages.ts destinationPrediction (#1258) | fms-prediction-endpoints: "R3-03: … CRANN as INSTRUMENT END"; "vnav.ts:231: the endpoint is the MAP …"; "R3-03: a direct-to 87N … SITE ARRIVAL"; "R3-03: the KBTV threshold …"; fms-87n-mission: "variant (f) …" | Met |

## Stage D: the transition down to hover, holds, SAR and rendezvous

### D-T and the addendum's R3-01 and R3-02

| ID | Requirement | Source | Implementation | Owner test | Status |
|---|---|---|---|---|---|
| T1 | MRK designation: mark on top, a database or user waypoint, coordinates; moving waypoints rejected | rev2 D-T T1 | designateHoverMark*, HOVER 1L/1R (#1257) | fms-flight: "the HOVER page activates …" (by position); fms-87n-mission nominal (MARK ON TOP) | Partial: the ident and coordinate entry and the moving-waypoint rejection not owner-tested |
| T2 | ACTIVATE needs RA; RALT FAILED at EXEC; JN, TDN, MRK fly-over at the head with a discontinuity; nothing between them | rev2 D-T T2 | activateHover, execute (#1257, #1278) | fms-flight: "ACTIVATE needs a valid radio height"; "no waypoint goes between TDN and MRK"; fms-hover-join: "no waypoint goes between JN and TDN either" | Met |
| T3 | Final track into the wind; direction frozen at the request, speed at TDN; below 5 kt, the bearing to MRK | rev2 D-T T3 | activateHover finalTrack (#1257) | fms-flight: "the HOVER page activates …" (final track 230) | Partial: the 4.9/5.1 kt rule and the freeze timing not owner-tested |
| T4 | Phase 1: a path to TDN arriving on the final track (a direct join, or overfly and loop back) | rev2 D-T T4; Astra Q9; Sean (29 Sep) | joining.ts; flight.ts flyJoin (#1278; a declared laboratory construction) | fms-hover-join: "activated over MRK on a 230/050/140/320 track, the join reaches TDN on the final track …"; "ACTIVATE previews the join in MOD …"; fms-cdu-rendered: "the map draws the FMS joining path to JN" | Met |
| T5 | One shared trajectory: TD, gate ≥ 0.20 NM, TD/H at 0.75 kt/s; DTRA | rev3 D-T T5 | transition.ts (#1257, #1272) | fms-tdn-production: the 270-state grid against the oracle; fms-tdn-flight | Met (later stage boundaries: see R3-01.7) |
| T6 | At TDN: XTK > 0.2 NM or track error > 20° gives TDN NOT POSSIBLE; recompute against the fixed MRK; gate < 0 gives TDN DIST SHORT; no closure refused | rev3 D-T T6 | reachTdn; checkAtTdn (#1257) | fms-flight: "at TDN, 0.3 NM off the final track"; "at TDN 400 ft higher … TDN DIST SHORT"; fms-tdn-production: T6; fms-87n-mission variants (b), (b2) | Partial: the 0.19/0.21 NM and 19°/21° thresholds, the track-error branch and the 105/125 KIAS cases not owner-tested |
| T7 | TRANSITION DOWN from execution until TDN | rev2 D-T T7 | execute (#1257) | fms-flight: "… then EXEC and TRANSITION DOWN" | Partial: raised at EXEC; not asserted to clear at TDN |
| T8 | PROGRESS shows TDN and MRK; XTK blanked in the hover procedure | rev2 D-T T8 | none | none | Open |
| T9 | Exits: a new hover procedure, a direct-to, a route cancel; the TU-LAB departure | rev3 D-T T9 | watchHover; flight.ts (#1257) | fms-flight: "a new mark over an active procedure …"; "a direct-to during the transition ends the procedure"; "TU from an exact hover …" | Partial: the route-cancel exit not separately tested |
| T10 | TDN FUNCTION LOST when all radio altimeters fail | rev2 D-T T10 | watchHover, reachTdn (#1257) | fms-flight: "no valid radio height at TDN is TDN FUNCTION LOST"; "radio height lost during TD/H … (F2)" | Met |
| T11 | Caution: a flat, obstacle-free surface; v1 over the sea | rev2 D-T T11 | surface.ts OFFSHORE_87N | fms-87n-mission: "the 87N start state: … the sea declared" | Met (declared: a flat surface only) |
| R3-01.1 | Full start state and admission: ≥ 80 KIAS, VS within the limit, RA valid; below the gate refused | addendum R3-01 | transition.ts planTransition (#1257, #1272) | fms-tdn-production: "the refusals agree with the oracle"; fms-flight: "a refusal at TDN … below the gate speed …" | Met |
| R3-01.2 | Capture, arrival and completion distinct; distances from the command profile | addendum R3-01; Astra F3 | transition.ts shared command laws (#1272) | fms-tdn-flight: "the flown TD agrees with the planner and the oracle at its end" (7 cases) | Met |
| R3-01.3 | Vertical ramps in the trajectory, including a nonzero initial VS | addendum R3-01 | as R3-01.2 (#1272) | fms-tdn-flight: cases at −800, −500 and +500 fpm | Met |
| R3-01.4 | TD/H closed loop in kt/s, saturated at [0.5, 1.25], never evaluated at d ≤ 0 | addendum R3-01 | flight.ts TD/H closed loop (#1257) | fms-tdn-oracle: "TD/H closed loop …" (oracle only) | Partial: saturation and overshoot at MRK not owner-tested in production |
| R3-01.5 | T6 at full precision, either side of zero slack | addendum R3-01 | transition.ts checkAtTdn (#1257) | fms-tdn-production: "T6 at full precision" | Met |
| R3-01.6 | TU-LAB from the ground-velocity vector | addendum R3-01 | flight.ts startDeparture (#1257) | fms-flight: "TU from an exact hover …"; fms-tdn-oracle (oracle) | Partial: production TU from a sideways drift not tested |
| R3-01.7 | The flown run against the oracle at each stage boundary (±0.005 NM, ±5 ft, ±1 kt) | addendum R3-01; Astra F3 | transition.ts shared laws (#1272) | fms-tdn-flight (the TD end, 7 cases, including Astra's 150 KIAS case; fails on 003be1ef by 0.019 NM) | Partial: the TD boundary only; the gate and TD/H boundaries are not compared on the flown trace |
| R3-02.1–2 | HOV eligibility: a selected usable receiver with both velocity words; integrity or velocity invalid is not eligible | addendum R3-02 | ScriptedFms.hoverFeedback (#1257) | fms-flight: "integrity lost while … NORMAL"; "velocity words invalid alone" | Met |
| R3-02.3 | A receiver takeover keeps HOV only if continuous; earth-fixed target; no switch back | addendum R3-02 | flight.ts noteFeedback (#1257) | fms-flight: "a continuous receiver takeover keeps HOV …"; "a takeover onto a receiver 100 m off is not continuous" | Met |
| R3-02.4 | Per-axis precedence; the order of failures does not change the final state | addendum R3-02 | flight.ts (#1257) | fms-flight: "… either order with GPS loss ends the same (F9)" | Met |
| R3-02.5 | F2 retains the accepted plan; named cancellations | addendum R3-02 | as F2 | as F2 | Partial: as F2 |
| R3-02.6 | GA or TU-LAB from HOV releases the station lock; LVL below 40 KIAS; ATT without feedback | addendum R3-02 | flight.ts startDeparture (#1257) | fms-flight: "TU from an exact hover releases the station lock"; "GA from the hover …" | Met |
| R3-02.7 | ATT latches the last trim; truth drifts with a wind change | addendum R3-02 | flight.ts (#1257) | fms-flight: "hover feedback lost … a wind change drifts it (F5)"; fms-87n-mission variant (c) | Met |

### D-H holds, D-S SAR and mark on top, D-R rendezvous

| ID | Requirement | Source | Implementation | Owner test | Status |
|---|---|---|---|---|---|
| D-H geometry | A ground racetrack, radius at TAS + W at the design bank, rebuilt each passage; chart IAS to TAS | rev3 D-H | holds.ts holdGeometry; flight.ts startHold/flyHold (#1259, #1261) | fms-flight: "the hold is a ground racetrack in any wind, either turn …" | Met (a laboratory construction); the design bank uses the flight's 25° while the profile says 30° (see A2) |
| D-H unable | UNABLE HOLD when W ≥ TAS; guidance invalid; NAV to HDG; the fix not sequenced | rev3 D-H | flight.ts unableHold (#1259, #1261) | fms-heliport-procedures: "UNABLE HOLD at the first fix passage …"; fms-flight: "a wind at or above the true airspeed cannot be held" | Met |
| D-H entries | Teardrop at a 40° ground track, parallel about 2.6 r, direct; entry advisories on timing | rev2 D-H | holds.ts entrySegments (#1259, #1261) | fms-cdu-simulation: "hold entry follows the standard sectors"; fms-heliport-procedures: "an HF (ONCE) after a parallel/teardrop entry …" | Partial: the 40° track and 2.6 r not directly asserted; entry advisory timing not tested |
| D-H speeds | The HOLD-SPD table; a chart speed wins; leg time 1.0/1.5 min at entry initiation | rev2 D-H | defaultHoldSpeed/LegTime; holdFromProcedure (#1259) | fms-flight: "the helicopter hold defaults to its holding speed limit and leg time"; fms-heliport-procedures: "C.6: … 4 NM legs and 90 kt" | Partial: a crew hold's defaults come from the altitude at creation, not at entry (M300 10-9) |
| D-H exits | MANUAL, ONCE, AT TGT ALT; RESUME HOLD converts to MANUAL | rev2/rev3 D-H | holdExitReached; HOLD page (#1259, #1261) | fms-guidance: "a hold with EXIT TYPE ONCE …"; fms-heliport-procedures: "an AT TGT ALT hold (HA) …"; "RESUME HOLD converts the exit to MANUAL" | Met |
| D-H missed | The missed-approach hold: one racetrack, then exit; the route ends; a crew NEW HOLD | rev3 D-H; C.6 | missed flag, circuits (#1259, #1261, #1264) | fms-heliport-procedures: "MISSED-HOLD …"; "… after a non-direct entry still flies one whole racetrack"; fms-87n-mission: "the crew NEW HOLD … two whole circuits" | Met |
| D-H high speed | HIGH HOLDING SPEED heuristic (the pattern at the table maximum and the ICAO wind) | rev2 D-H | a direct speed-against-table check at entry (#1259) | fms-flight: "… warns above the limit" | Partial: the pattern heuristic is not built (a declared simplification) |
| D-H oracles | Maximum inbound XTK after capture; fix crossings; entry side; timing; both turns; 30 kt from several azimuths | rev2 D-H | as D-H geometry | fms-flight: the ground-racetrack matrix | Partial: per-circuit outbound timing and distance not checked; the entry side only in still air |
| D-S patterns | SAR conformance: ranges, the 80-waypoint END OF SEARCH, PPOS in flight only, modify only before engagement, the ladder fly-over entry | rev2 D-S | sarTrack, SAR pages | fms-cdu-simulation: "a search pattern is defined … flown until INTERRUPT"; fms-flight: "search pattern geometry …" | Partial: the 80-waypoint, PPOS-in-flight and ladder fly-over rules have no owner test |
| D-S mark | Mark on top: the ONTOP store and NEW USER WPT from it | rev2 D-S | MARK key; designateHoverMarkOnTop (#1257); USER WPT (#1276) | fms-cdu-engine: "MARK creates a Mark On Top waypoint … PREDEF WPT 2/2"; fms-user-database: "MARK ON TOP, then NEW USER WPT …" (#1276) | Met (#1276); the mission does not yet fly the step (see §10 nominal) |
| D-S hover | Executing a hover procedure interrupts a search in progress | #1264 | execute → interruptSar (#1264) | fms-87n-mission: "executing a hover procedure interrupts a search pattern in progress" | Met |
| D-R epoch | The epoch is the simulation time of entry; propagation on the simulation clock along the track at the GS | rev2 D-R | moving waypoints | fms-tactical-maint: "a moving waypoint advances on its track and the aircraft closes on it" | Partial: epoch semantics not asserted |
| D-R recompute | Recompute every 10 s while time-to-go > 1 min; stop at 1 min | rev2 D-R; M300 11-37 | not verified | none | Open |
| D-R 500 NM | The 500 NM unachievable rule and its four cases; alert and invalid roll command when active | rev2 D-R | a speed-based RENDEZVOUS UNACHIEVABLE only | fms-tactical-maint: "a rendezvous that needs more than the maximum speed …" | Open: the 500 NM rule not implemented |
| D-R clock | Paused: nothing moves; aircraft freeze: waypoints move; the rate never changes the timeline | rev3 D-R | the 0.25 s tick contract | fms-scenario: "the same scenario gives the same timeline however the ticks are grouped" | Partial: rate independence Met; pause and freeze not tested for moving waypoints; no aircraft freeze |
| D-R restart | A restart restores the epoch from the scenario or E5 record, never the wall clock | rev3 D-R | moving user waypoints are not stored in E5 (#1276) | none | Open |
| D-R expiry | No expiry; the propagated age shown as a bench aid | rev2 D-R | not verified | none | Open |
| D-R tactical | Tactical approach and direct-to not in v1; an applicability review against M300 11-39…11-58 before changes | rev2 D-R | the tactical approach exists from before v1 | fms-cdu-simulation: "the tactical approach puts IAF, FAF and MAP ahead of the route" | Open: the review is not done (Sean's 29 Sep configuration decision will set it) |

## Stage E: predictions, fuel, RTA and persistence

| ID | Requirement | Source | Implementation | Owner test | Status |
|---|---|---|---|---|---|
| E1 | Airborne ETAs use the system wind (measured, or a PROGRESS manual entry); on the ground, ETEs use PLAN DATA CRZ TAS and CRZ WIND; the PLAN DATA page and its defaults arrive in v1 | rev2 E1; M300 5-14, 3-19 | fmsPages.ts PLAN DATA; scriptedFms planData, predictionLegs (#1258) | fms-prediction-endpoints: "E1: PLAN DATA shows … CRZ TAS 130 for ROTOR …" | Partial: the PLAN DATA page and its planning-only separation are Met; no owner test shows a manual wind entry changing an ETA; ground ETEs are not implemented (rev2 E1 scopes v1 to airborne) |
| E2 | Forecast waypoint winds are deferred, and labelled so | rev2 E2; M300 5-14, 11-82 | none (a deferral) | none | Partial: deferred by plan rev2 E2, kept in rev3 (Astra supports v1 A–E in rev3.1); the label is missing from FMS_APPLICABILITY.md and FMS_TEST_BENCH.md |
| E3 | FUEL pages to the M300 14-1…14-5 field set with the EST tag; hover burns at the current flow; NOT ENOUGH FUEL excludes the missed approach; each fuel result states its endpoint basis, with no positive reserve unless the landing is modelled | rev2 E3; rev3 E3; R3-03 | fmsPages.ts FUEL; vnav.ts reserve (#1258) | fms-prediction-endpoints: "R3-03: the point-in-space approach predicts to CRANN …"; fms-predictions: "NOT ENOUGH FUEL is judged at the landing, not at the end of the missed approach (R07)"; fms-vnav: "fuel burns as the aircraft flies" | Partial: the basis, the missed-approach exclusion and no positive reserve are Met; the M300 field set (RTE FUEL 1/2 and 2/2 with weights and GWT), the EST tag and a hover-burn owner test are missing |
| E4 | RTA: the required TAS from the wind triangle; RTA WIND; the overdue, infeasible, discontinuity and manual-hold cases; the 130 kt planning default separate; CONDITIONAL in a MANUAL hold; no TAS across NO PROGRESS or UNKNOWN | rev2 E4; rev3 E4; M300 5-17, A-141 | scriptedFms rendezvous, requiredTas; tacticalPages RNDZ (#1258, #1263) | fms-prediction-endpoints: "E4: … 67.08 kt"; "E4: an overdue RTA has no computed speed"; "R3-04: RTA feasibility compares like units"; "R3-03: an RTA to a fix ahead of an UNKNOWN segment"; "D-H/R3-04: an RTA past a hold that leaves by itself" | Partial: every computed case is Met; RTA WIND (a manual RTA wind kept apart from the system wind) is not implemented |
| E5 | The user-waypoint database (NEW USER WPT from a mark on top) and user routes; a storage interface; a per-user and per-profile namespace; a versioned export; an atomic import; collisions reported | rev2 E5; DEC-146 D3; Sean (29 Sep) | userDatabase.ts; ScriptedFms user database; USER WPT page; the bench card (#1276) | fms-user-database: 9 tests (restart, isolation, malformed and collision imports, unreadable store, refusing store) | Met (#1276); moving user waypoints are not stored (stated in the register) |
| E6 | INVERSE load (from BACKTRACK, labelled inferred); references resolved at load, missing reported and never substituted, moved reported; pinning survives a load, an import and a cycle change | rev2 E6 | loadCompanyRoute, SELECT CO ROUTE (#1289) | fms-route-load: 4 tests; pinning: fms-dataset (R06, D01) | Met (#1289); the airborne append (M300 3-11) and BACKTRACK itself are not built (BACKTRACK is in Astra/Sol's plan) |
| R3-03 (endpoint) | INSTRUMENT END / SITE ARRIVAL / LANDING; v1 declares no landing allowance, so there is no LANDING endpoint and the reserve is unavailable | rev3.1 R3-03 §1 | as C.11 (#1258) | as C.11, plus "R3-03: a direct-to 87N, still airborne … landing reserve is unavailable" | Met |
| R3-03 (status) | KNOWN / CONDITIONAL / UNKNOWN, separate from the endpoint; a MANUAL hold CONDITIONAL for ETA, EFOB, RTA and fuel; a discontinuity, NO PROGRESS or GS ≤ 0 UNKNOWN; an RTA scoped to the path to its fix | rev3.1 R3-03 §2–3 | vnav.ts status; scriptedFms rendezvous (#1258, #1263) | fms-prediction-endpoints: "R3-03: a MANUAL hold makes its fix and everything after it CONDITIONAL"; "R3-03: an RTA to a fix ahead of an UNKNOWN segment is computed" | Met |
| R3-03 (landing) | A CONDITIONAL fuel result never gives an unconditional positive reserve; a LANDING test only if an allowance is declared | rev3.1 R3-03 §1–2 | vnav.ts reserve; FUEL and PROG 2/4 (#1258) | fms-prediction-endpoints: "R3-03: a MANUAL hold …" (FUEL: SITE ARR COND); "R3-03: a direct-to 87N …" | Met (no allowance is declared, so no LANDING test is due) |
| R3-04 (1) | The 90 kt release is valid baro altitude ≥ 2,000 ft, not ALT capture; invalid baro keeps 70 | rev3.1 R3-04 §1 | procedureSpeed.ts (#1262) | fms-pins-approach: "R3-04: the 90 kt release …" | Met (at the predicate; the 2,000 ft crossing tick is not flown in the test) |
| R3-04 (2) | RTA feasibility in like units: the required TAS as IAS at each leg's planned altitude against 150 KIAS and 50 KIAS; 152 KTAS at 2,000 ft = 147.587 KIAS | rev3.1 R3-04 §2 | scriptedFms rendezvous (requiredIas); kinematics.ts iasFromTas (#1258) | fms-prediction-endpoints: "R3-04: RTA feasibility compares like units …"; fms-tdn-oracle: "ISA conversions …" | Met |
| R3-04 (3) | The GA climb gradient measured on the flown trace over a declared interval (the MAP or the GA selection, then 1 NM), ramp included | rev3.1 R3-04 §3; AIM 5-4-21 | fms-87n-mission gradientFrom (#1270, #1275) | fms-87n-mission: "MA-GRAD from TOGA …"; "MA-GRAD from the MAP …" | Met (the interval stops at the 2,000 ft capture if that comes sooner, and its length is guarded only as > 0.3 NM) |

## §10 the acceptance mission

| ID | Requirement | Source | Implementation | Owner test | Status |
|---|---|---|---|---|---|
| §10 nominal | The nominal run, steps 1–10 | rev3 §10 | heliDemo MISSION_87N_OFFSHORE_SAR (#1264, #1266, #1267, #1270, #1275, #1278) | fms-87n-mission: "nominal run …"; "the hover is held for two minutes from its capture, every tick"; "the crew NEW HOLD … two whole circuits"; "MA-GRAD …" | Partial: step 2 NEW USER WPT is not flown in the mission; step 5 PFD visuals, step 6 fuel and ENDURANCE, step 8 the INSTRUMENT END basis (tested in fms-prediction-endpoints) and step 9 70/90 kt are not asserted in the mission |
| §10 (a1) | RA invalid initially: no ACTIVATE | rev3 §10 | as nominal | fms-87n-mission variant (a1) | Met |
| §10 (a2) | RA lost between ACTIVATE and EXEC: RALT FAILED, EXEC refused, MOD kept | rev3 §10 | as nominal | fms-87n-mission variant (a2) | Met |
| §10 (a3) | RA lost in TD at about 350 ft: ALT latched, TD pitch continues, TDN FUNCTION LOST | rev3 §10 | as nominal (#1267) | fms-87n-mission variant (a3) | Met |
| §10 (a4) | RA lost in the hover: ALT latched, HOV continues, LOW HT OFF | rev3 §10 | as nominal | fms-87n-mission variant (a4) | Partial: LOW HT OFF not asserted in the variant (it is in fms-flight F3) |
| §10 (b) | TDN with 0.3 NM XTK: TDN NOT POSSIBLE; roll steering disabled; NAV to HDG | rev3 §10 | as nominal (#1267; crew vectors since #1278) | fms-87n-mission variant (b) | Met |
| §10 (b2) | TDN 400 ft high: TDN DIST SHORT | rev3 §10 | as nominal | fms-87n-mission variant (b2) | Met |
| §10 (c) | HOV feedback lost, then a 5 kt wind change: ATT; truth drifts | rev3 §10 | as nominal (#1275) | fms-87n-mission variant (c) | Met |
| §10 (d) | TOGA 1.5 NM before CRANN: the GA climb; the lateral path kept to CRANN; the turn only after the MAP | rev3 §10 | goAround (#1264) | fms-87n-mission variant (d); "variant (d): from TOGA to the MAP, every tick …" (#1275) | Met |
| §10 (e) | A crew direct-to during the final: immediate; terminal phase | rev3 §10 | as nominal | fms-87n-mission variant (e) | Partial: the terminal phase not asserted |
| §10 (f) | Proceed VFR, DIRECT 87N at CRANN: no vertical guidance; the arrival basis at 87N | rev3 §10; R3-03 | as nominal | fms-87n-mission: "variant (f): DIRECT 87N … SITE ARRIVAL 87N …" (#1275) | Met |
| §10 (g) | A GPS integrity event on the final | rev3 §10; R3-02 | as nominal (#1275) | fms-87n-mission variant (g) | Met |
| §10 completion | Every A–E exit condition, every owner test, the nominal mission and every variant | addendum, evidence | — | — | Open: the Partial and Open rows in this ledger |

## Fixes from Astra's implementation review (29 September)

| ID | Requirement | Implementation | Owner test | Status |
|---|---|---|---|---|
| F1 | The manual-hold next-crossing ETA along the hold path still to fly, piece by piece through the wind; EFOB and the RTA follow; the direct distance stays separate | predictions.ts, scriptedFms hold path (#1279) | fms-prediction-endpoints: "F1: a manual hold at RDG flown in a 30 kt wind …"; "F1: a leg into the wind and back takes d/(V−W) + d/(V+W) …" | Met (#1279) |
| F2 | Future legs keep the continuing procedure speed limit | scriptedFms predictionLegs (#1273) | fms-pins-approach: "Astra F2: the forecast flies each leg ahead …" | Met |
| F3 | The flown transition shares the planner's command laws and units, checked on the flight trace | transition.ts (#1272) | fms-tdn-flight (7 cases; fails on 003be1ef by 0.019 NM) | Met at the TD boundary (see R3-01.7) |
| F4 | The missed approach's coded altitude is its own planning target above or below cruise; under ADVISORY a conflicting selection is shown, never taken over | vnav.ts missedTarget; flight.ts missedAltitudeConflict (#1274) | fms-prediction-endpoints: "F4: …" (four tests) | Met |
| F5 | This ledger, E5 persistence and Phase 1 joining in v1 | this document; #1276; #1278 | as the E5 and T4 rows | Met: E5 (#1276), Phase 1 (#1278) and this ledger |
| F6 | The mission's titled checks measured over intervals and crossings, each fault-checked | fms-87n-mission (#1275) | the six measured checks | Met |
| Q4 | LNAV ONLY only where the procedure's data says so; a missing or unreadable FAS is NO APPR, never LNAV or a derived FAS | arinc424.ts lnavOnly, fasInvalid; gpsSensors.ts (#1277) | fms-gps-lnav-only: three "Q4: …" tests | Met |
| Q8 | Copter point-in-space identification only within a reviewed set, refused outside it | arinc424.ts REVIEWED_COPTER_PINS (#1280) | #1280's four tests | Met |
| Q9 | Phase 1 joining path | as T4 (#1278) | as T4 | Met |

## Evidence and deferrals

| ID | Requirement | Status |
|---|---|---|
| §11 | Evidence: failing-before and passing-after owner tests; mutation checks on new code; independent oracles; an immutable review revision | Met for the review fixes (#1272–#1280, each PR body) |
| Stage F | Radio data, sensors and integrity | Deferred: agreed as after v1 in Astra's rev 3 and rev 3.1 reviews, and by Sean |

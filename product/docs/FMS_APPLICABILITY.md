# FMS Test Bench: source applicability matrix

*Stage A of the helicopter-first plan, 29 September 2026. Kept current as behaviour is built.*

The FMS Test Bench simulates a **rotorcraft CMA-9000**. This file records, for each behaviour, where the behaviour comes from, what it applies to, what the bench does, and where the bench deliberately differs. It replaces any single "which source wins" ladder: each row stands on its own source and applicability. The aircraft profile the bench flies is data in `product/client/src/fmsCdu/profile.ts` (`cma9000-s300-heli-civil`, version 1); its parameters, with their basis and whether each is in force yet, are listed there and named in every run report.

The bench is a demonstrator. Nothing here is an OEM conformance claim or qualification evidence.

## Source classes

| Class | What it establishes | How it is cited |
|---|---|---|
| **CMA-9000 Operator's Manual, S/W 169-614876-300** (Pub. 9000-GEN-0150, Rev 2, 1 March 2010; the helicopter/military build) | The **behavioural baseline** for the default profile | `M300 <printed page>` |
| **CMA-9000 Operator's Manual, S/W 169-614876-050-RRJ** (Pub. 9000-GEN-0137, 17 August 2010, draft; a fixed-wing installation) | Only an **explicit extension** where S300 is silent. Each use is its own row marked "RRJ extension"; never an automatic fallback | `M050 <page>` |
| **CMC datasheet** CMC-CMA9000-FMS-RMS-19-003 | That a **capability exists** in the current product (for example SBAS approaches with the CMA-5024). It does not supply S300 operating rules | Datasheet |
| **Operational rules and procedure data**: FAA AIM, 14 CFR, FAA charts, FAA CIFP | **Their own subject**: procedure speed limits, visual segments, holding speeds, missed-approach gradients. Not ranked below a brochure | AIM paragraph, chart, CIFP record |
| **Public helicopter autopilot descriptions**: UK AAIB bulletins on the AW189 (AAIB-27585, AAIB-27532), FAA special conditions for SAR autopilots (AW189, S-92A, AW139) | **Representative** helicopter autopilot values ("borrowed"). Not CMA data, not an installed controller | AAIB report number, page |
| **Public standards**: ICAO Doc 9613, FAA AC 20-138D, ICAO APAC coding course | Error models, PBN functions, coding conventions | Document and section |
| **Laboratory assumptions** | Anything else the bench needs. Always labelled, always a named profile parameter | `lab` |

The manuals are copyrighted. They are cited by page and paraphrased here; no manual text or image is in this repository.

**Borrowed versus laboratory.** For the autopilot, a *borrowed* value is a number taken from a cited AW189 description. The logic that sequences modes is laboratory unless a source gives it. The result is a representative generic rotorcraft autopilot informed by the AW189, not a claim about any installed controller.

## Default configuration

One default profile, not a matrix of every CMA option: aircraft type ROTOR, civil navigation option, error limit by RNP (M300 1-3 allows phase of flight or RNP), two CMA-5024 receivers, one radio altimeter and a representative rotorcraft autopilot (the last two declared for Stage B), with HOVER, MARK ON TOP, SAR SQUARE/LADDER/SECTOR, moving waypoints and rendezvous configured. Not configured: CARP/HARP, COSPAS-SARSAT, EGI/IRS, DVS, the military navigation option; the tactical approach is not changed until its applicability is reviewed.

## Vertical-guidance policy (decision D1)

| Phase | Default profile | Status |
|---|---|---|
| En route | **No airline-style en-route VNAV** (no top of descent, no VNAV PTH or DES NOW coupling). Altitude constraints are advisories, flown with autopilot altitude and vertical-speed modes | Built (Stage B3): ALT SEL, VS, ALT HOLD, SPD and GA; the airline-style VNAV is the selectable `lab-airline-vnav` profile |
| Approach | The S300 advisory approach VNAV (M300 7-22…7-27) where it can be constructed: a database vertical path angle, or a threshold to build one from | Declared |
| Point-in-space approaches without a published vertical path | No advisory path is built; flown LNAV with advisory step-downs | Declared (Stage C) |
| SBAS finals | Coupled LPV and LNAV/VNAV finals under the existing receiver and approach-authority checks | **Deliberate deviation**: a bench capability for a modern CMA-5024 SBAS installation, not S300 behaviour |

The airline-style VNAV stays as an intentional, selectable laboratory profile later (the seed of a fixed-wing profile). Its tests own that profile's behaviour; they are not the reason for keeping it.

## Provenance, validity, selection and engagement

These are kept distinct across the bench, in place of one advisory/selected/coupled label:

- **Data words** (radio height, position, velocities, deviations) carry **provenance** (which sensor or computation) and **validity** (NORMAL, NCD, FAIL).
- **Targets and datums** (selected altitude, hover height, speed, heading) record **who selected them**: crew, FMS or procedure.
- **Controller state** is reported per autopilot axis: engaged, armed and degraded modes. **Coupling** means an engaged axis mode consuming an FMS output.
- **Advisory** describes an FMS output that no engaged mode consumes.

## Behaviour rows

Status: **sourced** (a source states it), **borrowed** (a representative public AW189 value), **inferred** (read from a source that does not state it outright; labelled), **lab**. "Built" says whether the bench does this today.

| ID | Behaviour | Source | Applies to | Bench behaviour | Deviation or note | Status | Built |
|---|---|---|---|---|---|---|---|
| HOLD-SPD | Maximum holding speed, helicopter | M300 10-8, Table 10-1 | S300, ROTOR | 100 KIAS at or below 6,000 ft; 170 KIAS above 6,000 to 14,000 ft | The table rows overlap at 6,000 ft: the bench gives 6,000 to the lower row. No helicopter row above 14,000 ft: the check is shown unavailable there | Sourced + lab boundary | No: today the fixed-wing 230 kt limit applies (Stage D) |
| HOLD-ENTRY | Hold entries | M300 10-2, 10-4, 10-6 | S300 | Parallel leg about 2.6 turn radii; teardrop ground track 40° from the inbound reciprocal; direct | — | Sourced | No (Stage D) |
| HOLD-GEOM | Racetrack construction | M300 10-8 (rate-1 or maximum roll, recomputed each fix passage) | S300 | Ground-referenced racetrack, radius sized at maximum ground speed (TAS + wind) | The radius construction is a conservative laboratory choice, not the OEM algorithm | Sourced + lab | No (Stage D) |
| MISSED-HOLD | Missed-approach hold | M300 7-16 note | S300 | Exits automatically after one racetrack; continuing needs a crew-entered hold | — | Sourced | No: armed MANUAL, with the coded leg distance and speed since Stage C (87N BEADS: 4 NM, 90 kt); the one-racetrack exit is Stage D |
| END-ROUTE | After the last waypoint | M300 1-11 | S300, ROTOR | Guidance toward the last overflown waypoint; LNAV disengaged | — | Sourced | No (Stage B) |
| MA-EARLY | Missed approach selected before the MAP | M300 7-16 | S300 | Guidance continues along the final course to the MAP, then sequences the missed approach | — | Sourced | No: today the approach legs are dropped (Stage C) |
| TOGA-EARLY | TOGA before the MAP | M300 7-16 (LNAV stays valid, terminal phase) and FAA AIM 5-4-21 | S300 | Same lateral behaviour as MA-EARLY | — | Inferred | No (Stage C) |
| VNAV-APP | Advisory approach VNAV | M300 7-22…7-27 | S300 | Advisory, not coupled; only where it can be constructed | — | Sourced | No (Stage C) |
| SBAS-FIN | Coupled LPV and LNAV/VNAV finals | Datasheet (capability); bench GPS phases 3a/3b, #1243, #1251 | Modern CMA-5024 installation | Kept, under the existing authority checks | Deliberate deviation from S300 | Sourced capability; bench contract | Yes |
| GPS-DEGRADED | GPS as the only sensor with integrity exceeded | M300 1-4 | S300, civil | Position kept as selected-but-uncertain; not integrity-qualified or approach-authorized | Replaces the 3a rule by a decision table that preserves #1243 and #1251 (Stage F) | Sourced | No (Stage F) |
| PINS-SPD | Copter procedure speeds | FAA AIM 10-1-2 | US Copter procedures | 90 KIAS; final and missed 70 KIAS unless charted | A chart that says more governs | Sourced | Partly: coded limits are applied (procedureSpeed.ts); the AIM defaults for a Copter procedure with no coded limit are not |
| PINS-VIS | Proceed Visually versus Proceed VFR | FAA AIM 10-1-3 | US point-in-space procedures | Separate endpoint kinds; only Proceed VFR is validated (87N) | Proceed Visually stays unvalidated and does not inherit Proceed VFR semantics | Sourced | Data only: the endpoint kinds are imported and 87N carries a validated PROCEED VFR (Stage C); flight behaviour at the MAP is C.3 |
| MA-SPD-90 | 87N missed-approach speed | FAA chart, 87N COPTER RNAV (GPS) 190 | 87N | 70 kt until valid barometric altitude is at or above 2,000 ft, then 90 kt including the hold | The chart governs where it differs from the coded DF speed | Sourced | Yes: the missed approach hold's 90 kt takes over at valid baro altitude 2,000 ft or above; advisory under the ADVISORY profile |
| MA-GRAD | Copter missed-approach climb gradient | FAA AIM 5-4-21 | US Copter procedures | At least 400 ft/NM, measured over a declared interval on the actual trace | — | Sourced | No (Stage C) |
| HOV-ACT | HOVER ACTIVATE needs valid radio height | M300 A-75…A-76 | S300, HOVER + radio altimeter | ACTIVATE not offered without it | — | Sourced | No (Stage D) |
| RALT-EXEC | Radio height lost between ACTIVATE and EXEC | M300 E-27 | S300 | `!RALT FAILED`; EXEC refused, the modification kept | The refusal is read from the message's timing | Sourced + inferred | No (Stage D) |
| TDN-LOST | All radio altimeters lost | M300 E-16 | S300 | `TDN FUNCTION LOST`; the transition request withdrawn | — | Sourced | No (Stage D) |
| TDN-NP | TDN NOT POSSIBLE | M300 E-17 | S300 | At TDN, cross-track over 0.2 NM or track error over 20° disables roll steering; the procedure must be re-activated | — | Sourced | No (Stage D) |
| TD-SEQ | Coupled transition to hover | AAIB-27585 (gate 200 ft, 80 KIAS); AAIB-27532 (hover 50 ft and 0 kt; TD/H window 30–210 ft, below 85 KIAS) | Representative autopilot | TD, then a gate segment, then TD/H: one trajectory shared by the FMS and the autopilot | The sequencing logic is laboratory | Borrowed values + lab logic | No (Stage D) |
| TU-LAB | Departure from hover | AAIB-27532 targets (200 ft, 80 KIAS, heading hold at 40 KIAS) | Representative autopilot | A laboratory departure sequence, engageable from hover | The AAIB's TU capture conditions do not establish TU from zero ground speed and are not used | Borrowed targets + lab logic | No (Stage B) |
| LOW-HT | Low-height protection | AAIB-27585 | Representative autopilot | 75 ft in cruise with radio-height or TD modes; 17 ft in hover; needs valid radio height | — | Borrowed | No (Stage B) |
| FMA-AXES | Mode annunciation by axis | AAIB-27585, AAIB-27532 | Representative autopilot | Collective, pitch and roll/yaw columns; captured green and boxed, armed white, degraded amber | The aircraft's chime becomes an event-log entry | Borrowed | No (Stage B) |
| SAR-DISPLAY | Height, heading, ground speeds and wind shown for SAR modes | FAA special conditions for SAR autopilots, (b)(3)–(b)(6) | Representative installation | Shown on the PFD in the helicopter profile | — | Sourced | No (Stage B) |
| WIND-TEST | The 20 kt wind in the acceptance mission | — | — | A laboratory test condition only | The special conditions' 25/17 kt figures are minimum certification demonstration conditions, not an operating envelope, and are not claimed | Lab | — |
| HOLD-ETA | Predictions in a hold | M300 5-17 | S300 | TO is the next crossing of the holding fix; downstream predictions assume exit at the next crossing and are marked conditional | The exit assumption is an interpretation | Sourced + inferred | No (Stage E) |
| ASTERISK | Value beyond a field's range | M300 2-18 | S300 | Asterisks, never a clipped or plausible number | — | Sourced | Partly |
| PI-CONST | Procedure-turn construction | M300 7-1 | S300 | Two outbound legs of 60 s and 45 s at 180 kt: 3.00 NM and 2.25 NM in still air, before shortening | 180 kt is a construction reference, not a speed to fly | Sourced | No (later) |
| NO-LOC-UPD | No localizer updating | M300 1-3…1-6 | S300 | Not modelled | An airline (Boeing) feature, not a CMA mode | Sourced absence | — |
| FUEL-FLOW | Fuel predictions from the current flow | M300 5-14, 14-1, 14-5 | S300 | Current flow; operator-entered reserve | — | Sourced | Yes |

## References

- CMA-9000 Operator's Manual, S/W 169-614876-300, Pub. 9000-GEN-0150 Rev 2 (1 March 2010); S/W 169-614876-050-RRJ, Pub. 9000-GEN-0137 (17 August 2010). Esterline CMC Electronics; copyrighted, cited by page only.
- CMC Electronics, *CMA-9000 FMS/RMS* datasheet, CMC-CMA9000-FMS-RMS-19-003.
- UK AAIB Bulletin 11/2022, Leonardo AW189 G-MCGT, AAIB-27585; AAIB Bulletin 3/2024, AW189 G-MCGT, AAIB-27532 (Appendix B, "Automatic Flight Control System modes").
- FAA special conditions for SAR automatic flight control systems: Leonardo AW189 (No. 29-050-SC, 85 FR 48646, 2020); Sikorsky S-92A (No. 29-023-SC, 75 FR 77524, 2010); Agusta AW139/AB139 (2012).
- FAA Aeronautical Information Manual: 5-3-8 (holding), 5-4-21 (missed approach), 10-1-2 and 10-1-3 (helicopter IFR).
- FAA chart 87N COPTER RNAV (GPS) 190° (AL-9013, d-TPP 2609); FAA CIFP cycle 2609 (public domain).
- ICAO Doc 9613 (PBN Manual); FAA AC 20-138D.

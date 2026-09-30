# FMS Test Bench Stage F: radio data, sensors and integrity (plan)

- **Status:** draft for review. This is a plan, not an implementation.
- **Follows:** the helicopter-first plan (rev 3 §8, rev 3.1 addendum). Stage F starts after v1 (Stages A–E).
- **Governing rule (Sean, 29 Sep):** realism and closeness to the CMA-9000 always win. Build full functions, with no laboratory stand-ins where the manual defines the behaviour.
- **Carries forward:**
  - rev 2's F1–F8;
  - Astra's rev 3 follow-ups (F4–F7 amendments);
  - Sean's CMA-5024 GPS decisions: satellites plus overrides, dual GPS, GPS-computed LPV deviations only in the later-SBAS profile.
- **Sources:**
  - CMA-9000 Operator's Manual, helicopter S300 edition (M300), cited by printed page and never quoted at length;
  - the bench as it stands on `main` (`civilNavigation.ts`, `radioNavigation.ts`, `gpsSensors.ts`, `configuration.ts`, `efis.ts`);
  - the FMS_TEST_BENCH.md register rows "Sensor selection and reversion", "RNP by phase", "RAIM / SBAS" and "GPS sensor (CMA-5024)".

---

## 1. What exists and what Stage F adds

**Already on main:**
- **Civil estimator:** `CivilNavigation` consumes measured GPS, DME/DME and VOR/DME, and has no truth input.
- **Dead reckoning (DR):** propagates heading, TAS and the last computed wind, with its own uncertainty growth.
- **Uncertain GPS:** S300 keeps an uncertain GPS when no approved alternative exists, with approach and hover authority withheld.
- **Radio receiver:** `BenchRadioReceiver` acquires and loses stations with a 3 s acquisition and range bias. Its tuning is chosen by the navigation code, not by a modelled radio.
- **GPS:** two modelled CMA-5024 receivers with RAIM/FDE, HIL, SBAS, the ARINC 743A-style bus and fault injection.
- **Integrity:** RNP by phase, CHECK ANP, and predictive RAIM with SAT DESELECT.

**Where the bench falls short of the M300:**

| Area | Gap against the M300 |
|---|---|
| Radios | Pages store frequencies (`rms: partial`). No radio is commanded, tuned or answers. Navigation picks stations directly from the database. |
| Stations | Navaid elevation is zero, and DME-only stations are not read (rev 2 F1). |
| Sensor state | One `anp` number stands in for availability, 95% accuracy, integrity (NP) and eligibility (rev 2 F2). |
| Transitions | There is no 100 m hysteresis rule or immediate integrity reversion (M300 1-3). |
| DME/DME | Up to 6 stations, REJ/N/A status and scanning control (M300 12-17) are missing. The solver uses a best pair with a residual check. |
| VOR/DME | It should use manually tuned stations only, at most two VOR/DME plus one TACAN (M300 12-19). The bench auto-selects instead. |
| Pages | DME STATUS, DME DESELECT, VOR/DME/TCN STATUS, the DESELECT page (TAS, HDG and sensors), the NAV radio pages with AUTO/MAN and TEST, and the ADF pages are missing or partial. |
| Alerts | The Appendix E navigation, radio and sensor messages are partly present. |
| Output bus | There are no tags for navigation mode provenance, the integrity annunciator, tuned stations, or radio health. |

---

## 2. Configuration first (F0)

The helicopter profile already declares radio equipment on or off (`configuration.ts`). Stage F decides the navigation sensors the same way, one M300 option at a time. It never simulates equipment the profile declares absent (Astra's gap assessment, item 1).

| Equipment | M300 | Proposed | Reason |
|---|---|---|---|
| GPS, dual (CMA-5024 ×2) | 1-4, 12-1 | **On** (exists) | Sean, 28 Sep |
| DME ×2 (DME/DME scanning) | 1-4, 12-16, 13-20 | **On** | Needed for the civil priority order |
| NAV ×2 (VOR/ILS receivers) | 1-5, 12-19, 13-21 | **On** (`nav1`, `nav2` exist) | |
| ADF ×2 | 13-23 | **On** (`adf1`, `adf2` exist, pending) | ADF is tuned and displayed. It is **not a navigation mode** in M300 (1-4 list). |
| TACAN | 1-4 note, 12-19 | **Off** | Q1: M300 admits civil TACAN only "when proven accurate" |
| AHRS/APIRS with KALMAN | 1-5, 12-23, 15-4 | **Question Q2** | The helicopter edition documents it. It is not RNP-applicable (15-4). |
| IRS / EGI | 12-25 to 12-28 | **Off** (Sean, 29 Sep: remove IRS from the civil profile) | |
| DVS (Doppler) | 12-20, 1-3 | **Off** (`doppler: off`) | Q3 for SAR over water |
| Military navigation, mGPS/cGPS | 1-6 to 1-8 | **Off** | Sean, 28 Sep |

**Exit condition:**
- every Stage F equipment item appears in `CONFIGURED_OPTIONS` with its M300 page and status;
- a guard test fails if any code path navigates on, displays, or alerts for an item declared off.

**Owner tests:** `fms-stage-f-configuration.spec.ts`:
- "no Stage F sensor mode, page prompt or alert exists for equipment the profile declares off";
- "each declared sensor has a consumer".

---

## 3. Items

Each item has an exit condition and named owner tests. Tests go in new spec files per feature (the 30 Sep rule), never appended to the shared fms-*.spec.ts files.

### F1. Navaid data prerequisite

- Read DME-only stations, taking the DME position from ARINC 424 columns 56–74.
- Read station elevation, and channel/frequency identity (including the TACAN channel pairing kept for data only, with TACAN off).
- Read a co-located DME's own position.
- Missing data gives an explicit **unavailable** or **assumed** state: never a silent zero elevation.

**Exit:**
- the slant-range correction uses station elevation;
- a DME-only facility is available to DME/DME;
- the loader refuses malformed DME records, stating the reason.

**Owner tests:** `fms-navaid-data.spec.ts`: "a DME-only record is read with its own position and elevation"; "slant range uses the station elevation"; "a navaid without elevation is marked assumed and the solution's accuracy says so".

### F2. The sensor state is split

Each sensor solution carries separate fields:
- **availability**;
- **95% accuracy**;
- **integrity bound (NP)** and its validity. In the civil option, NP is unavailable when the data is insufficient (M300 1-3);
- **phase eligibility** (DME/DME and VOR/DME are not available for approach, M300 15-3);
- **selected source**;
- **guidance authority**.

Units are NM. "Below the limit" is strictly less than.

**Exit:** `navPerformance` and every page read the split state. A seeded error is never presented as a 95% or 10⁻⁵/h figure.

**Owner tests:** `fms-sensor-state.spec.ts`: "VOR/DME is available but not approach-eligible"; "HIL exactly equal to the alert limit is not integrity (strict less-than)".

### F3. Mode selection and transitions (M300 1-3 to 1-5, 12-1)

- **Civil priority:** GPS, then DME/DME, then VOR/DME/TACAN, then KALMAN (if equipped), then DR.
- **Integrity-required transitions are immediate.**
- **Accuracy-based transitions** use 95% statistics with **100 m hysteresis**. The exceptions, with no hysteresis, are GPS → INS/GPS (not equipped) and **VOR/DME with integrity → DME/DME with integrity**.
- Hysteresis never delays an integrity reversion.
- With dual FMS, switching to the same sensor type on the other FMS also uses 100 m hysteresis.
- A sensed position change on a mode or station change passes to the coupled autopilot as a cross-track change (the M300 12-16 caution). The bench shows it and does not smooth it.

**Exit:** a transition table covering every pair of equipped modes, with the trigger, the hysteresis, the message and the position continuity. Each row is tested.

**Owner tests:** `fms-sensor-transitions.spec.ts`:
- "DME/DME to VOR/DME waits for 100 m of accuracy advantage; VOR/DME to DME/DME does not";
- "an integrity loss reverts at once whatever the hysteresis";
- "a station gained or lost in DME/DME moves the position, and the coupled roll command follows it".

### F4. GPS decision table (rev 2 F4, amended by Astra)

| Case | Behaviour | M300 |
|---|---|---|
| Two healthy receivers | As today (#1251 selection) | 1-4 |
| One receiver silent, faulted, with invalid coordinates or impossible values | Independent rejection, kept from #1243 | |
| HIL at or over the GPIAL, **no eligible alternate** | GPS stays selected: **uncertain**, GPS INT annunciator, **GPS POS UNCERTAIN**. Not approach- or hover-authorized. | 1-4, E-8 |
| HIL at or over the GPIAL, **alternate eligible** | The NAIM check (F5). When the ANP exceeds the required accuracy: revert to the next IFR mode, GPS INT, **GPS NAV LOST** | 1-4, 12-1 |
| GPS unavailable | The next eligible mode. **DR only when none remains** (Astra). GPS NAV LOST, GPS INT. | 1-4, E-8 |
| GPS–GPS disagreement in independent dual FMS | **GPS-GPS POS DISAGREE** | 3-26 |
| Lateral-only versus vertical approach loss | Kept as the separate #1243 authorities | |

**Exit:** each row is a test, and the approach authority reads only integrity-qualified state.

**Owner tests:** `fms-gps-decision.spec.ts`, one test per row.

### F5. NAIM (M300 1-4, 12-1)

With a backup sensor available, the FMS computes a NAIM HIL from the GPS-to-backup position difference and the backup's accuracy. **The manual gives no formula.** The bench declares one with a laboratory label (for example |GPS − backup| plus the backup's 95% accuracy), backs it with worked cases, and makes **no 10⁻⁵/h claim** (Astra).

**Exit:** the NAIM HIL appears as ANP on PROGRESS when GPS is used with a backup present (12-1). The laboratory formula is named in FMS_APPLICABILITY.md.

**Owner tests:** `fms-naim.spec.ts`: "a 0.4 NM GPS bias against a DME/DME fix of 0.3 NM accuracy exceeds a terminal limit and reverts"; "no backup: the receiver HIL is the ANP".

### F6. DME/DME (M300 1-4, 12-16 to 12-18, 15-3)

- **Station selection:** automatic tuning and scanning of up to **six** DME stations (DME-capable TACANs included when equipped). At least three are required. The selection applies distance and geometry reasonableness checks (15-3).
- **DME STATUS status column:** "REJ" means rejected for geometry, blank means used, and "N/A" means not responding to tuning (12-17). A wider meaning of REJ is its own matrix row (Astra).
- **Third range:** with three ranges, a failed consistency test does **not** identify the faulty station. It makes the mode unavailable, and no station is labelled the culprit without isolation (≥ 4 ranges, with declared rejection and isolation limits). This follows Astra's F6 amendment.
- **DME DESELECT:** up to **25** stations, paged, with CLR to remove (12-18).
- **Accuracy:** a declared model reproducing M300 15-3's typical 95% of 0.5 NM en route and 0.4 NM terminal. Not available for approach.
- **Tuning:** through the DME radios of F8, never directly from the database. A station counts only when its radio reports a valid slant range.

**Exit:**
- DME STATUS shows up to six stations with their status, frequency and distance, plus "SCANNING CTRL ACTIVE" and the position;
- a deselected station is never tuned for navigation;
- 3-station inconsistency makes DME/DME unavailable.

**Owner tests:** `fms-dme-dme.spec.ts`:
- "six stations scanned, a poor-geometry pair marked REJ";
- "a non-responding station shows N/A";
- "three inconsistent ranges: mode unavailable, no culprit named";
- "four ranges isolate a biased station";
- "a deselected station is never used".

### F7. VOR/DME (M300 1-5, 12-19 to 12-20, 15-3)

- **Only manually tuned stations are used:** at least one VOR/DME, and at most two VOR/DME plus one TACAN if equipped (12-19). VOR/DME serves when fewer than three DMEs are available.
- **VOR/DME/TCN STATUS:** source and identifier, frequency, radial and slant range for each receiver, plus the position. The title changes with the equipment (VOR/DME STATUS with no TACAN).
- **Accuracy:** 95% of 0.6–0.8 NM within 7 NM of the station, 1.5 NM beyond (15-3). Not available for approach.
- **Reasonableness:** checks on bearing and distance before use (15-3).

**Exit:**
- no VOR/DME fix is computed from a station the crew has not tuned;
- the accuracy steps at 7 NM;
- a mismatched ident removes the station.

**Owner tests:** `fms-vor-dme.spec.ts`:
- "with NAV1 manually tuned to a VOR/DME, the fix uses it; with NAV1 in AUTO it does not, unless the configuration allows";
- "the 95% accuracy steps from 0.8 to 1.5 NM past 7 NM";
- "an unreasonable radial is rejected".

### F8. Radio management: tuning, feedback and failure (M300 13-1 to 13-26, 3-26)

This makes the RMS real: every radio is a modelled unit that receives a tune command and answers with feedback.

- **Radios:**
  - COM1/2;
  - NAV1/2 with DME1/2;
  - ADF1/2;
  - ATC1/2 (control only).
- **Radio model:**
  - the tune command, the in-progress state, and feedback;
  - the displayed active frequency follows the radio's **feedback**, not the entry.
- **Display:**
  - large white when tuned;
  - large inverse white while tuning;
  - small amber when the tuning failed (13-3).
- **RADIO 1/X top page (13-6):**
  - active and standby for each radio;
  - entry by frequency, identifier or preset;
  - LSK swaps standby and active;
  - out-of-range entries are refused with the manual's ranges (13-4: VOR/ILS/DME 108.00–117.95 at 50 kHz, ADF 190.0–1799.5 kHz for the ADF-462);
  - the RADIO key cycles the pages when configured (13-19).
- **NAV 1/2 (13-21, 13-22):**
  - NAV1/NAV2 with the identifier and the radial or localizer deviation;
  - **AUTO/MAN** VOR tuning, toggled by DELETE to standby or by the swap;
  - **DME HOLD** ON/OFF, which freezes the DME on the NAV HOLD frequency;
  - the DME slant range, blank when there's no reply, and `****` when the receiver fails.
- **NAV 2/2 (13-23):**
  - NAV1/2 MODE AUTOMATIC/MANUAL;
  - NAV and DME TEST: READY → CONFIRM? → STARTED → PASS/FAIL/TIMEOUT.
- **ADF pages (13-23 to 13-26):**
  - ADF1/2 frequencies, mode, test, and the bearing to the RMI;
  - the marine distress band 2181–2183 kHz where the model allows it.
- **Libraries (13-2, 13-44 to 13-47):**
  - NAV, ADF and COM libraries of 99 presets, with an identifier of up to five characters;
  - password-protected edits.
- **Tuning authority:**
  - an external control head or backup controller can take over;
  - **RADIO TUNING DISABLED** is shown on entry while the FMS is inhibited (13-3, 13-20).
- **Dual FMS (3-26):**
  - radio tuning is synchronized by burst tuning and radio feedback, not by FMS cross-talk;
  - the standby frequency is cross-talked;
  - tuning works from either side (Sean, 29 Sep).
- **Alerts (Appendix E):**
  - `NAVx CONTROL LOST`, `DMEx CONTROL LOST`, `VORx CONTROL LOST`, `ADFx CONTROL LOST`, `COMx`, `TPDR`, `XPDR CONTROL LOST`;
  - the advisories `ADFx FAILED` and `COMx FAILED` (E-2, E-6, E-13, E-17, E-18, E-21 to E-23).

**Exit:**
- navigation uses **only** stations the radios report as tuned and receiving;
- a radio that stops answering shows amber and raises its CONTROL LOST alert;
- navigation drops that radio's stations.

**Owner tests:**
- `fms-rms-radios.spec.ts`:
  - "an entry is shown in inverse until the radio confirms, then white; a refusal leaves it amber";
  - "the displayed active frequency follows feedback when the radio reports a different one";
  - "an out-of-range entry is refused with the scratchpad message";
  - "a library preset tunes by number and by ident, the first match winning";
  - "tuning disabled: entries raise RADIO TUNING DISABLED";
  - "either CDU tunes, and both show the same standby".
- `fms-nav-radio.spec.ts`:
  - "VOR AUTO/MAN toggles by DELETE and by swap";
  - "DME HOLD freezes the DME while NAV is retuned";
  - "a DME with no reply shows a blank distance, a failed one shows ****";
  - "NAV TEST runs through CONFIRM? to a result".
- `fms-adf.spec.ts`:
  - "ADF tunes within its range and drives the RMI bearing";
  - "ADF CONTROL LOST on a silent ADF".

### F9. Navigation status and deselection pages (M300 5-26, 12-17 to 12-20, 17-3)

- **NAV STATUS INDEX 1/1:** PREDICT RAIM, GPS, DME, VOR/DME/TCN, and KALMAN if equipped, with DESELECT. There are prompts **only for equipped interfaces** (5-26 note).
- **DESELECT 1/1 (17-3):**
  - TAS, HDG and each sensor or navigation source, each VALID ↔ DESEL (or ACQ);
  - a deselected TAS stops the wind computation, and position-fixing modes are unaffected in the long term.
- **GPS DESELECT** for the dual receivers.
- **POS INIT/REF 2/2** sensor table:
  - each mode with its status (NAV, DSEL, ACQ);
  - the distance and bearing from the FMS position;
  - the accuracy (12-20, 12-27 layout without the inertial rows).

**Exit:**
- every page renders within the CDU grid (the R19 layout test);
- deselection reaches the estimator in the same tick.

**Owner tests:** `fms-nav-status-pages.spec.ts`:
- "DESELECT TAS: the wind is no longer computed, and GPS position is unaffected";
- "deselecting DME forces VOR/DME or DR per F3";
- "the POS INIT 2/2 table lists each equipped mode with its accuracy".

### F10. Dead reckoning (M300 1-5, rev 2 F7, Astra's amendment)

- **Inputs:** the last position, heading, TAS and the **last valid computed wind** (the E1 system wind).
- **Alerts:** FMS NAV IN DR on entry (E-33), then CHECK ANP when the estimated accuracy exceeds the phase requirement (1-5, 15-2).
- **Low-speed regime (IAS below 40 KIAS):** DR from heading and TAS is **degraded or unavailable** unless a declared velocity source exists (Astra). The ANP growth rate must say which.
- **Recovery:** automatic on sensor restoration (1-5), with the position change handled as in F3.

**Exit:** the DR ANP grows from declared input uncertainties and matches worked cases, and a hovering DR case is labelled.

**Owner tests:** `fms-dead-reckoning.spec.ts`:
- "DR carries the last computed wind";
- "in the hover, DR is flagged degraded and the ANP grows at the no-velocity rate";
- "a restored GPS ends DR and the position step is announced".

### F11. KALMAN mode (only if Q2 is answered "equipped"; M300 1-5, 12-23 to 12-24, 15-4)

- **Emulated INS:** built from the AHRS and corrected by GPS. It is available one minute after power-up, and a power interruption over 50 ms re-initializes it.
- **Coast:** it carries navigation for a declared time after GPS loss (the manual says "typically 2 minutes"). Its error growth is independent of the RNP limit (rev 2 F7).
- **KALMAN STATUS 1/1:**
  - OP MODE;
  - KALMAN POSITION and GPS POSITION;
  - 2 SIGMA POS ERR;
  - GPS READY and APIRS READY.
- **RNP:** it is not applicable to RNP operations (15-4). On entry it raises CHECK ANP per the phase.
- **Alerts:** KALMAN NAV LOST, APIRS FAILED and AHRSx FAILED (E-12, E-21, E-22).

**Owner tests:** `fms-kalman.spec.ts`:
- "GPS lost offshore: KALMAN carries position for the coast time with growing 2-sigma";
- "KALMAN is never RNP-eligible".

### F12. RNP, ANP and integrity alerts (M300 15-1 to 15-4, 1-4, Appendix E)

- **ANP:** the 95% radial position error, excluding FTE (15-1). In GPS, ANP is the HIL when it is within the limit (12-1).
- **CHECK ANP:** raised when ANP exceeds RNP for **30 s en route and terminal, and 10 s on approach** (15-2). Today's bench values are demonstration parameters, which this replaces with the sourced ones.
- **INT lamp:** in GPS, when the HIL exceeds the current RNP (15-2).
- **CDI full scale (15-1, Table 15-1):**
  - 5.0 NM en route, or for an RNP entry above 1.01;
  - 1.0 NM terminal, or above 0.31;
  - 0.3 NM approach, or for any RNP entry at or below 0.31.
- **VERIFY RNP VALUE:** checked against the procedure's RNP (5-16, 5-25, 5-32, E-17).
- **Position difference messages:** FMS-D/D POS DIFF, FMS-V/D POS DIFF, POSITION SHIFT and GPS POS DIFF (E-7, E-8, E-14), each with the manual's trigger.
- **DR:** **FMS NAV IN DR** (E-33), raised on entering dead reckoning.
- **EHSI/EFIS annunciations (M300 C-9):** the navigation mode (GPS, VOR/DME/TACAN, DME, DR), **GPS integrity lost** and **Unable RNP** are *display annunciations* driven from the bus (F13). They are not CDU scratchpad messages.
- **Wording:** the S300 CDU wording is CHECK ANP, GPS POS UNCERTAIN, GPS NAV LOST, FMS NAV IN DR and VERIFY RNP VALUE. The bench must not raise a Boeing-style "UNABLE REQD NAV PERF" on the CDU.

**Exit:** every navigation message the bench raises is traced to an Appendix E row. A guard test fails on a navigation or sensor alert that has no M300 source.

**Owner tests:** `fms-integrity-alerts.spec.ts`:
- "CHECK ANP waits 30 s en route and 10 s on approach";
- "the CDI full scale follows Table 15-1";
- "an RNP entry of 0.5 in terminal gives 1.0 NM full scale";
- "every navigation alert has an Appendix E source".

### F13. Output bus tags (the exhaustive `fmsOutputs` table)

The new fields must be declared in the exhaustive output table (session 1's rule 4), each with its ARINC-style provenance and validity:
- navigation mode and source (GPS1/2, DME/DME, VOR/DME, KALMAN, DR);
- the uncertain flag;
- the GPS INT annunciator;
- the EHSI annunciations "GPS integrity lost" and "Unable RNP" (M300 C-9);
- ANP, RNP and phase, as today;
- the CDI full-scale value;
- the tuned NAV, DME and ADF stations, with idents and validity;
- the VOR radial and DME distance for the EFIS/RMI;
- the ADF bearing;
- radio health.

The EFIS reads only the bus: an RMI needle and a DME readout are drawn only from bus words.

**Exit:** `outputTags.ts` lists every new field, and the EFIS draws none of them from anything else.

**Owner tests:** `fms-output-bus-nav.spec.ts`:
- "every Stage F field is in the exhaustive table with provenance";
- "a failed DME shows NCD/FAIL on the bus and dashes on the display".

### F14. Sensor failure scenarios on the bench

The bench's sensors and scenario tabs gain the following faults and stimuli.

**Radios:**
- a NAV or DME receiver fails (answers FAIL) or goes silent (no feedback);
- a DME station stops replying, or its ident mismatches;
- a VOR radial is biased;
- an ADF fails;
- the external control head takes authority.

**Air data:**
- TAS invalid, or heading invalid or biased;
- the AHRS fails (if equipped).

**GPS:** the existing faults, plus "integrity only" and "position gone" on both receivers.

Each fault is a scenario step, validated on admission and listed in the run report with its time and value (rev 3 §10 pattern).

**Exit:** each fault has an owner test through the scenario runner, and a rendered test that the bench control drives it.

### F15. Acceptance mission for Stage F

The mission is offshore from 87N.
- **No radio coverage:**
  1. GPS integrity is removed while position is kept: GPS POS UNCERTAIN, INT, no approach authority.
  2. The position is removed: DR (or KALMAN if equipped), with the ANP growing and CHECK ANP at the phase timing.
  3. GPS is restored: recovery, with the position step shown.
- **Coastal, with DME coverage:**
  1. A biased DME is isolated with four stations, and with three the mode goes unavailable.
  2. The crew deselects a station.
  3. A NAV radio fails and its CONTROL LOST alert appears.
  4. VOR/DME is used with the crew manually tuning.
- **Throughout:** the computed position is checked against bench truth, independently of the displayed confidence.

**Exit:** the nominal run and each variant pass with recorded evidence. There is one integrating mission, in line with Sean's evidence standard.

---

## 4. Ledger (draft rows for FMS_V1_ACCEPTANCE.md's Stage F section; session 1 lands them)

| Row | Requirement | Source | Owner test file | Status |
|---|---|---|---|---|
| F0 | Stage F equipment declared, with nothing simulated for absent equipment | M300 1-4, configuration | fms-stage-f-configuration | Open |
| F1 | DME-only navaids, station elevation, co-located DME position | ARINC 424; rev 2 F1 | fms-navaid-data | Open |
| F2 | Split sensor state; strict less-than | M300 1-3, 15-3 | fms-sensor-state | Open |
| F3 | Priority, immediate integrity reversion, 100 m hysteresis and its two exceptions | M300 1-3 to 1-5 | fms-sensor-transitions | Open |
| F4 | GPS decision table (7 rows) | M300 1-4, 3-26, E-8 | fms-gps-decision | Partial (#1243, #1251) |
| F5 | NAIM (laboratory formula, no 10⁻⁵/h claim) | M300 1-4, 12-1 | fms-naim | Open |
| F6 | DME/DME with 6 stations, REJ/N/A, deselect 25, 3-range unavailability, 4-range isolation | M300 12-16 to 12-18, 15-3 | fms-dme-dme | Partial |
| F7 | VOR/DME on manually tuned stations only, with its accuracy steps | M300 12-19, 15-3 | fms-vor-dme | Partial |
| F8 | RMS: command, feedback, amber failure, libraries, AUTO/MAN, DME HOLD, TEST, ADF, dual-side tuning, CONTROL LOST | M300 13-1 to 13-26, 3-26, App. E | fms-rms-radios, fms-nav-radio, fms-adf | Partial (pages only) |
| F9 | NAV STATUS INDEX, DESELECT (TAS, HDG, sensors), POS INIT 2/2 sensor table | M300 5-26, 12-20, 17-3 | fms-nav-status-pages | Partial |
| F10 | DR from heading, TAS and last wind; degraded in low speed; recovery | M300 1-5; Astra F7 | fms-dead-reckoning | Partial |
| F11 | KALMAN (if equipped) | M300 1-5, 12-23, 15-4 | fms-kalman | Open (Q2) |
| F12 | CHECK ANP 30 s/10 s, INT lamp, CDI FSD Table 15-1, VERIFY RNP, POS DIFF messages, no invented messages | M300 15-1, 15-2, App. E | fms-integrity-alerts | Partial |
| F13 | Output bus navigation, radio and integrity fields, EFIS from the bus only | Bench contract | fms-output-bus-nav | Open |
| F14 | Bench sensor and radio failure stimuli in scenarios | rev 3 §10 pattern | per item | Open |
| F15 | Stage F acceptance mission | Astra gap assessment, item 1 | fms-stage-f-mission | Open |

## 5. Order and size

1. **F0, F1 and F2** (small to medium): the contracts everything else reads.
2. **F8** (large): the radios. F6, F7 and F9 depend on tuned-and-receiving stations.
3. **F3, F4, F5, F6 and F7** (large): the estimator and its transitions.
4. **F9, F10 and F12** (medium): pages, DR and alerts.
5. **F13 and F14** (medium), then **F15**.
6. **F11** only after Q2.

Each step is its own PR with red-first owner tests. **F3 and F8 touch shared state and should not be parallelized with each other.**

---

## 6. Questions for Sean (functional)

1. **TACAN:** off for the civil SAR configuration (M300 1-4 admits it civilly only "when proven accurate")? If on, DME/DME may use TACAN ranges and VOR/DME/TCN may use one TACAN.
2. **AHRS/KALMAN:** equip the helicopter with the APIRS/AHRS KALMAN mode (M300 12-23)? It is the only coast mode after GPS loss offshore without radios, apart from DR, but it is not RNP-eligible (15-4). If yes, what coast time (the manual says "typically 2 minutes")?
3. **Doppler (DVS) over water:** keep it off? Real SAR helicopters often carry one. M300 makes DVS lowest-priority and without integrity in civil (12-20).
4. **The error-limit basis:** Phase of Flight or RNP (M300 1-3 says it is configurable)? This decides the GPIAL.
5. **VOR AUTO tuning:** M300 VOR/DME navigation uses *manually* tuned stations (12-19), while NAV1/NAV2 offer AUTO VOR tuning (13-21). Should an AUTO-tuned VOR be eligible for VOR/DME navigation, or only for display?
6. **ANP presentation:** the 95% figure, or the "ANP/HIL = 1.0" 99.999% configuration (15-2)?
7. **External radio control head:** does the helicopter have one (tuning authority can be taken from the FMS, 13-3)? If yes, the bench models it as a second tuning source.
8. **ADF use:** display and RMI only (M300 has no ADF navigation mode), with NDB approaches out of scope?

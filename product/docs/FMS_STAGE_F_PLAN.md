# FMS Test Bench Stage F: radio data, sensors and integrity (plan)

- **Status:** draft for review, revision 2. This is a plan, not an implementation.
- **Revision 2 (30 Sep, evening):** answers Astra's review of revision 1 (d0e7ef27), items SF-01 to SF-08 (SF-08 drafted by session 4) and the integration list. It also records Sean's clarification of DEC-150 item 2 (KALMAN expiry). The main changes are:
  - §3 now opens with the contracts every item reads (C1 values and consumers, C2 KALMAN and DVS inputs and clocks, C3 radio ownership, C4 the output vocabulary);
  - F3 now carries the transition table itself;
  - F5's worked cases are corrected;
  - F16 names its actual CIFP fixtures.
- **Follows:** the helicopter-first plan (rev 3 §8, rev 3.1 addendum). Stage F starts after v1 (Stages A–E).
- **Governing rule (Sean, 29 Sep):** realism and closeness to the CMA-9000 always win. Build full functions, with no laboratory stand-ins where the manual defines the behaviour. Where the manual gives no model, the bench declares one, labels it laboratory and makes no certification claim.
- **Carries forward:**
  - rev 2's F1–F8;
  - Astra's rev 3 follow-ups (F4–F7 amendments);
  - Sean's CMA-5024 GPS decisions: satellites plus overrides, dual GPS, GPS-computed LPV deviations only in the later-SBAS profile.
- **Sources:**
  - CMA-9000 Operator's Manual, helicopter S300 edition (M300), cited by printed page and never quoted at length;
  - the bench as it stands on `main`;
  - the FMS_TEST_BENCH.md register rows "Sensor selection and reversion", "RNP by phase", "RAIM / SBAS" and "GPS sensor (CMA-5024)";
  - FAA CIFP cycle 2609 and the FAA AIM 1-2-3 for the NDB work (F16).

---

## 1. What exists and what Stage F adds

**Already on main (refreshed at c88529ee):**
- **Civil estimator:** `CivilNavigation` consumes measured GPS, DME/DME and VOR/DME, and has no truth input.
- **GPS measurement values:**
  - a GPS with integrity reports ANP from the receiver's HFOM, falling back to HIL, then 0.3 NM;
  - an uncertain GPS reports the larger of HIL and HFOM;
  - these mixed meanings are what C1 replaces.
- **Dead reckoning (DR):** propagates heading, TAS and the last computed wind, with its own uncertainty growth.
- **Uncertain GPS:** S300 keeps an uncertain GPS when no approved alternative exists, with approach and hover authority withheld.
- **Radio receiver:** `BenchRadioReceiver` acquires and loses stations with a 3 s acquisition and range bias. Its stations are chosen by the navigation code.
- **Shared radio system (#1350):**
  - `RadioManagementSystem` is shared by the two computers, with per-side requests, acknowledgement or failure feedback, and a latency and timeout;
  - radio tuning works from either side with the cross-talk link down (M300 3-26).
- **Dual FMS (#1350):**
  - two independent computers with crossfill;
  - synchronized navigation that adopts the peer's solution of the same type only when it is 100 m better (`dualSensorHysteresis`, M300 3-25).
- **GPS:** two modelled CMA-5024 receivers with RAIM/FDE, HIL, SBAS, the ARINC 743A-style bus and fault injection. Receiver selection has qualified transfer, manual GPS1/GPS2 choice and retention of the current suitable receiver (#1251).
- **Integrity:**
  - RNP by phase;
  - CHECK ANP, once per episode, reading the effective values (R11);
  - predictive RAIM with SAT DESELECT.
- **Approach authority:**
  - `gpsApproachAuthority` and NO APPR INTEGRITY (#1243, C.3.1);
  - in the S300 profile, a cancelled approach leaves the approach phase and stays flown on LNAV until MISSED APPR or TOGA.

**Pending, not on main:** the output-tag catalogue (#1345, open) and its missing FMA fields (#1376, open). Stage F depends on them (C4, F13); it does not duplicate them.

**Where the bench falls short of the M300:**

| Area | Gap against the M300 |
|---|---|
| Radios | #1350 models command and feedback for COM, NAV, ADF and transponder. There are no DME transceivers with scanning channels and no TACAN unit. Navigation still picks stations from the database, not from what a radio reports. |
| Stations | Navaid elevation is zero, and DME-only stations are not read (rev 2 F1). |
| Sensor state | One `anp` number stands in for accuracy, integrity bound, integrity validity and eligibility (C1). |
| Transitions | The 100 m same-type rule exists between computers only. Within one computer there is no accuracy-based choice between radio modes, and no transition table (M300 1-3). |
| DME/DME | Up to 6 stations, REJ/N/A status and scanning control (M300 12-17) are missing. The solver uses a best pair with a residual check. |
| VOR/DME | It should use tuned stations, at most two VOR/DME plus one TACAN (M300 12-19). |
| KALMAN, DVS | Not modelled; the sensor port has no APIRS or Doppler input. |
| Pages | DME STATUS, DME DESELECT, VOR/DME/TCN STATUS, the DESELECT page (TAS, HDG and sensors), the NAV radio pages with AUTO/MAN and TEST, and the ADF pages are missing or partial. |
| Alerts | The Appendix E navigation, radio and sensor messages are partly present. |
| Output bus | There are no tags for navigation mode provenance, separated accuracy and integrity, tuned stations, or radio health (C4). |

---

## 2. Configuration first (F0)

The helicopter profile already declares radio equipment on or off (`configuration.ts`). Stage F declares the navigation sensors the same way, one M300 option at a time. It never simulates equipment the profile declares absent (Astra's gap assessment, item 1).

| Equipment | M300 | Proposed | Reason |
|---|---|---|---|
| GPS, dual (CMA-5024 ×2) | 1-4, 12-1 | **On** (exists) | Sean, 28 Sep |
| DME ×2 (DME/DME scanning) | 1-4, 12-16, 13-20 | **On** | Needed for the civil priority order |
| NAV ×2 (VOR/ILS receivers) | 1-5, 12-19, 13-21 | **On** (`nav1`, `nav2` exist) | |
| ADF ×2 | 13-23 | **On** (`adf1`, `adf2` exist, pending) | ADF is tuned and displayed. It is **not a navigation mode** in M300 (1-4 list). |
| TACAN (one airborne unit) | 1-4 note, 12-19 | **On** (Sean, 30 Sep, DEC-150) | DME/DME may use TACAN ranges; VOR/DME/TCN may use one TACAN. M300 admits civil TACAN "when proven accurate". |
| AHRS/APIRS with KALMAN | 1-5, 12-23, 15-4 | **On, 2-minute coast** (Sean, 30 Sep, DEC-150; expiry clarified at about 7:40 PM ET: see F11) | Not RNP-applicable (15-4) |
| IRS / EGI | 12-25 to 12-28 | **Off** (Sean, 29 Sep: remove IRS from the civil profile) | |
| DVS (Doppler) | 12-20, 1-3 | **On** (Sean, 30 Sep, DEC-150) | Lowest priority, without integrity in the civil option (12-20) |
| Military navigation, mGPS/cGPS | 1-6 to 1-8 | **Off** | Sean, 28 Sep |
| External radio control head | 13-3, 13-20 | **Off** (Sean, DEC-150) | The FMS is the only tuning source |

**Exit condition:**
- every Stage F equipment item appears in `CONFIGURED_OPTIONS` with its M300 page and status;
- a guard test fails if any code path navigates on, displays, or alerts for an item declared off;
- a guard test also fails if an item declared built has no consumer, or one declared pending has any.

**Owner tests:** `fms-stage-f-configuration.spec.ts`:
- "no Stage F sensor mode, page prompt or alert exists for equipment the profile declares off";
- "each declared sensor has a consumer exactly when its status says it is built".

---

## 3. Contracts every item reads (C1 to C4)

The contracts are fixed before the items that read them are accepted. F2 establishes C1 and C4 in code. C2 lands with F11 and C3 with F8. Each contract has **one primary owner test file**. Later items add only the newly exposed failure to it, and add rendered cases only for the key, feedback, colour and flag wiring.

### C1. Accuracy, integrity, eligibility and authority (SF-01)

Every sensor solution carries five distinct values, each with its meaning, units, validity and provenance.

| Value | Meaning | GPS | DME/DME, VOR/DME | KALMAN | DVS | DR |
|---|---|---|---|---|---|---|
| **accuracy95Nm** | Estimated horizontal radial error at 95%, NM | Receiver HFOM (label 247) | Declared model reproducing M300 15-3's typical figures (F6, F7) | 2.448 × max(σx, σy), a conservative bound (below) | Declared growth, laboratory (F11) | Declared growth from input uncertainties (F10) |
| **integrityBoundNm** | A containment bound, NM | Receiver HIL (label 130) | None (M300 15-3 rests on criteria, not an NP) | None | None | None |
| **integrityValid** | The civil integrity criteria are met (M300 1-3) | HIL valid, fresh and strictly below the limit | Accuracy strictly below the limit **and** the reasonableness checks of 15-3 passed | Never (15-4) | Never (12-20) | Never |
| **naimComparisonNm** (laboratory) | GPS-to-backup discrepancy plus the backup's accuracy95 (F5) | Only when retaining an uncertain GPS against a qualifying backup | — | — | — | — |
| **eligibility / authority** | Phase eligibility, then actual guidance permission | En route, terminal, approach | En route, terminal (not approach, 15-3) | None (15-4) | None | None |

**accuracy95Nm carries a basis:** `receiver` when it is the receiver's own figure (the GPS HFOM), and `laboratory` when any contributor is a declared model or allowance. Contributors include the radio accuracy models, an assumed or terrain elevation allowance (F1), and the KALMAN, DVS and DR growth. A laboratory value is a simulator estimate, not a validated 95% bound. The basis travels with the value to the bus (C4).

**The limit** is the active RNP (DEC-150 item 4), whether the phase default or the crew entry. "Strictly below" is `value < limit`.

**KALMAN's 2 σ is not a 95% radial figure.**
- The KALMAN STATUS page shows 2 SIGMA POS ERR as 2 σ of the larger per-axis error.
- accuracy95Nm is **2.448 × max(σx, σy)**. This is a conservative bound, and it is labelled laboratory:
  - 2.448 = √(−2 ln 0.05) is the exact 95% radius only for a circular bivariate normal (equal, uncorrelated axes);
  - for unequal axes the true 95% radius is smaller, so the bound over-states the error, never under-states it.
- F11's model applies the same σ₀ and σₐ on both axes, so in practice σx = σy and the bound is exact.
- **Independent check (owner test):**
  - σx = 1, σy = 1: the true 95% radius is 2.448, and the bound is 2.448;
  - σx = 1, σy = 0: the error is one-dimensional, the true 95% radius is 1.960 (the two-sided 95% normal quantile), and the bound is 2.448.
- No page shows the 2 σ value as the 95% figure, or the reverse.

**The NAIM comparison never manufactures integrity.**
- It is kept apart from the receiver HIL, and has its own validity:
  - the backup must be a fresh (within `sensorMaxAge`), GPS-independent **radio** fix (DME/DME or VOR/DME) with integrity;
  - KALMAN (GPS-aided) and DVS or DR (propagated from GPS positions) never qualify.
- It decides one thing: whether an uncertain GPS may stay selected when a backup exists (F4, F5).
- A retained GPS stays **uncertain**: INT lit, GPS POS UNCERTAIN, no approach or hover authority.

**Consumers.** Each consumer reads exactly one value:

| Consumer | Reads | Rule |
|---|---|---|
| PROGRESS ANP field, NAV STATUS, EFIS ANP | accuracy95Nm of the selected solution (DEC-150 item 6) | Dashes when unavailable |
| GPS STATUS 2/2 HOR INT | The selected receiver's integrityBoundNm (HIL word) | Dashes when the word is invalid or stale. Never the NAIM value. |
| 100 m accuracy comparison (F3, dual FMS) | accuracy95Nm of both candidates | A candidate without accuracy cannot win an accuracy comparison |
| CHECK ANP (F12) | accuracy95Nm of the selected solution against the RNP | Episode semantics in F12 |
| INT annunciator, GPS POS UNCERTAIN | GPS navigated without integrityValid | See the boundary table |
| Mode selection | integrityValid, then accuracy95Nm, then priority (F3) | |
| Approach and hover permission | Selected mode approach-eligible **and** integrityValid, then the existing #1243 and hover guards | Never from the NAIM comparison |

**M300 12-1's HIL-as-ANP presentation** belongs to the configuration where ANP presents the integrity value (the 15-2 "ANP/HIL" option). DEC-150 item 6 chose the 95% figure instead, so the bench shows HFOM as ANP and the HIL on GPS STATUS 2/2. It cites 12-1 as the behaviour that is not configured.

**Boundary rows** (owner: `fms-sensor-state.spec.ts`; expected values computed in the test, not by the code under test). Take RNP 1.0 NM:

| Case | integrityValid | Selection | INT | CHECK ANP |
|---|---|---|---|---|
| GPS HIL 0.99 | Yes | GPS with integrity | Off | Per ANP |
| GPS HIL 1.00 (equal) | **No** (strict) | GPS uncertain, or reversion per F4 | **On** | Per ANP |
| GPS HIL 1.01 | No | As above | On | Per ANP |
| ANP 0.99 | — | — | — | No episode |
| ANP 1.00 (equal) | — | — | — | **No episode** (15-2: "exceeds") |
| ANP 1.01 | — | — | — | Episode starts |
| Radio accuracy 1.00 (equal) | **No** | Not a with-integrity candidate | — | — |

Two different predicates govern these, and the table fixes both:
- integrity validity uses strict less-than (M300 1-3: "less than");
- CHECK ANP uses strictly greater-than (15-2: "exceeds").

At equality the solution therefore has no integrity, and no CHECK ANP episode starts. INT follows integrity validity, so it is lit at equality. The manual leaves equality open for the lamp; the bench resolves it on the side of the lost integrity and says so in FMS_APPLICABILITY.md.

**Unavailable and stale values:**
- **Stale:** a word older than `sensorMaxAge` is unavailable.
- **No accuracy:** the solution can be selected only by priority among candidates without integrity. ANP shows dashes, and a CHECK ANP episode runs as though ANP exceeded the RNP (conservative).
- **No HIL:** GPS has no integrity. GPS STATUS HIL shows dashes.

### C2. KALMAN and DVS measured inputs and clocks (SF-02)

**Sensor port.** `SensorFrame` gains two words. Only the sensor generators read aircraft truth; the estimator reads these words.

| Word | Content | Frame and units | Validity |
|---|---|---|---|
| `apirs` | Accelerations resolved by the APIRS's own attitude | Earth frame, north and east, m/s² | NORMAL/FAIL status, sequence, time; stale after `sensorMaxAge` |
| `dvs` | Velocity over the reflecting surface | Body axes, along and across heading, kt | Same; the FMS rotates it with its own measured true heading (air word) |

This is a bench interface, not ARINC 705 or Doppler framing. The laboratory error sources are named parameters: the APIRS accelerometer bias and 1 σ noise, and the surface drift.

**Aiding.** The emulated INS is aided (reset to the GPS position and velocity) only by an **integrity-qualified** GPS update. That update needs a GPS with integrityValid and valid velocity words 166 and 174. An uncertain GPS, a GPS without velocity words, and radio fixes never aid it.

**Error model (laboratory):**
- σ₀ per axis at aiding = HFOM / 2.448 (the circular case: HFOM is a radial 95% figure);
- the unaided growth adds ½ σₐ t² per axis, where σₐ is the declared accelerometer noise;
- the KALMAN STATUS page shows 2 × max(σx, σy); accuracy95Nm is 2.448 × max(σx, σy) (C1).

**Clocks.** All clocks run on simulation time, never wall time or tick counts.
- **Readiness:** KALMAN is unavailable until one minute after FMS power-up (M300 12-24).
- **Coast:**
  - the coast clock starts at the **last accepted integrity-qualified GPS aiding**;
  - it is not renewed by re-entering the mode, by radio selection changes or by an uncertain GPS;
  - at 2 minutes (DEC-150) KALMAN becomes ineligible.
- **Recovery:** a new integrity-qualified aiding restarts the clock; a restart before it does not.
- **Power interruptions:**
  - `powerInterrupt(ms)` records an interruption's duration in simulation time, independently of the flight tick;
  - over 50 ms re-initializes KALMAN: unaided, and not ready for a minute (12-24);
  - 50 ms or less does not;
  - the owner test runs 49, 50 and 51 ms.

**Expiry (Sean's clarification, 30 Sep, about 7:40 PM ET, answering Astra SF-02):** at expiry KALMAN becomes ineligible, and selection takes the best remaining usable source, DVS included when healthy. DR follows only when nothing else is usable. DEC-150 records this as a dated clarification of its item 2.

**DVS:**
- **Solution:** the DVS velocity plus the crew's water current (DVS STATUS 2/2, 12-23), integrated from the solution position at DVS entry.
- **Accuracy:** grows with the distance flown (laboratory rate, F11).
- **Loss:** a DVS word that is FAIL or stale ends the mode (DVS NAV LOST).
- **Hover:** DVS grants no hover authority. The existing hover guards stay unchanged. A separate contract (validity, continuity, water current, reversion) would be needed for that, and none is proposed.

### C3. Radio ownership (SF-03)

One `RadioManagementSystem` (#1350) owns every radio. Stage F extends it; it does not add a second tuning authority.

**Devices and who supplies what:**

| Device | Supplies | Tuned by |
|---|---|---|
| NAV1, NAV2 | VOR bearing (and the paired DME frequency) | FMS AUTO or crew MAN (13-21) |
| DME1, DME2 | Slant ranges on their channels (below) | Channel 1: the paired NAV frequency, or the HOLD frequency. Channels 2 and 3: the FMS navigation scan |
| TACAN (one) | TACAN bearing and range, by channel (X/Y) | FMS AUTO for VOR/DME/TCN, or crew entry (12-19) |
| ADF1, ADF2 | Relative bearing, raw data only | Crew, or the FMS for a loaded NDB approach (F16) |
| COM1/2, ATC1/2 | No navigation data | Crew |

**DME channels (a declared laboratory model of a three-channel scanning transceiver; the M300 gives the six-station capacity, 12-16, not the channel design):**
- Each DME has three channels:
  - channel 1 follows its paired NAV frequency, or the held frequency under DME HOLD;
  - channels 2 and 3 are the FMS's navigation scan.
- The scan assigns up to six stations across the four scan channels (two per DME) by the F6 selection. Each channel dwells on its stations in turn.
- **Measurement identity:** every range is tagged (receiver, channel, frequency, command sequence, station ident, time).
- **Freshness:**
  - a range is usable for `sensorMaxAge` after its measurement;
  - a retune of that channel invalidates its older ranges at once;
  - nothing is cached beyond that.
- A range on channel 1 counts for navigation when its station is identified.

**HOLD, TEST and manual tuning:**
- **HOLD** fixes channel 1 on the held frequency and leaves the scan channels scanning.
- **TEST** removes that receiver's ranges from navigation for the test's duration; the other receiver continues.
- **MAN tuning of a NAV** moves channel 1 only.

**Three separate states per radio.** Their consequences are defined separately:

| State | Values | Consequence |
|---|---|---|
| Command status | PENDING, ACK, REJECTED, SUPERSEDED, TIMEOUT | Inverse while PENDING. A later request supersedes an earlier one, with no alert. REJECTED shows small amber and raises no CONTROL LOST. TIMEOUT raises XX CONTROL LOST on the requesting side (E-2, E-6, E-13). |
| Receiver health | NORMAL, CONTROL LOST (bus), FAIL, SILENT | A lost control path does not stop reception: the last acknowledged frequency's measurements stay usable. FAIL or SILENT raises the configured XX FAILED advisory once, on both sides, and drops that receiver's measurements. |
| Reception | Station identified, no reply, ident mismatch | An acknowledged healthy radio with no reply shows a blank distance or bearing. It raises **no alert**. |

**An untuned or out-of-coverage healthy ADF** shows no bearing and the RMI flag. It does **not** raise ADF CONTROL LOST. ADF CONTROL LOST stays for an actual control-path failure (E-2).

**Deselection has three named meanings:**
- **Station deselection** (DME DESELECT, 12-18): that station is never scanned or used. It does not affect any VOR/DME.
- **DME sensor deselection** (DESELECT 1/1, 17-3): the DME/DME mode and every DME range are unavailable. VOR/DME needs a DME range, so it goes too.
- **Mode fallback:** no deselection. The mode is simply not available.

**Owner:** `fms-rms-radios.spec.ts` for command, status and health. One bounded integration case runs through command, feedback, timestamped measurement, estimator and EFIS. It covers:
- stale feedback after a newer request;
- acknowledgement with no reception;
- a receiver or bus failure;
- TEST suppression;
- HOLD with scanning;
- tuning from either CDU with the cross-talk link down.

### C4. The output vocabulary (SF-07)

F2 declares the complete vocabulary. F13 completes the tags. It depends on #1345 (the catalogue, open) and #1376 (its FMA fields, open), and neither duplicates the catalogue nor weakens its exhaustive typing.

- **Mode:** GPS, DME/DME, VOR/DME, VOR/DME/TCN, KALMAN, DVS, DR.
- **Source:** GPS1/2, the stations in use (DME idents, VOR ident, TACAN channel), and the owning computer (1 or 2) in dual operation.
- **Performance values:**
  - accuracy95Nm and integrityBoundNm, each with validity (NORMAL, NCD, FAIL);
  - integrityValid and the uncertain flag;
  - the laboratory NAIM comparison, labelled laboratory;
  - RNP and phase.
- **Annunciations:** INT; the EHSI "GPS integrity lost" and "Unable RNP" (M300 C-9); POS (DR).
- **Radios:**
  - per device, the active frequency or channel and its command status and health;
  - per measurement, the station ident, VOR radial, DME distance, TACAN bearing and distance, and ADF bearing, each with validity.
- **CDI full scale** (Table 15-1).

A mode or display is not delivered while a value it needs is absent from the vocabulary or read through a side channel.

---

## 4. Items

Each item has an exit condition and named owner tests. Tests go in new spec files per feature (the 30 Sep rule). The existing GPS-selection and approach-authority owner files keep their contracts; a Stage F item adds only the newly exposed failure to them. A proposed test name or count is not evidence: rows become Met only on executed, recorded runs.

### F1. Navaid data prerequisite (with the elevation contract, SF-08; drafted by session 4)

**Read from the ARINC 424 VHF navaid record (4.1.2).** The class is read positionally: column 28 is the VOR; column 29 is D for DME, T or M for TACAN, I for ILS/DME, and N or P for MLS/DME.
- DME-only stations, placed at their DME position (columns 56–74). This includes an ILS's DME.
- TACAN stations (DEC-150: on). A TACAN-only station is placed at its DME.
- The station's DME elevation (columns 80–84).
- A co-located DME's own position, where it differs from the VOR's.
- The DME/TACAN channel of the frequency's standard pairing (ICAO Annex 10 Vol I, Attachment C, Table A).

**Elevation contract.**

**Datum and units:** feet above mean sea level. Terrain heights are in metres above the geoid and are taken as MSL.

**Sources, in order:**
- **`data`:** the record's DME elevation. Provenance: "ARINC 424 DME elevation (columns 80–84)".
- **`terrain`:** the ground elevation at an invented demonstration site, from the Terrarium tiles (zoom 14, with their date read).
  - It applies only to the invented demonstration navaids, which have no navigation data.
  - Its provenance says it is the ground's height, **not the antenna's**.
- **`assumed`:** used when nothing is available (a blank field, or a VOR-only record, which carries no DME elevation). 0 ft is used and stated as assumed. It is never a silent zero.

**Allowances:**
- 100 ft for `terrain`: the unknown mast height of 10–30 m, plus SRTM's ~16 m vertical accuracy at 90%.
- 1,000 ft for `assumed`.
- `data` has none.

Both allowances are named profile parameters (`terrainNavaidElevationUncertainty` and `assumedNavaidElevationUncertainty`, basis `lab`, with provenance stated).
- They are **engineering allowances for a bounded demonstration**.
- They are not 95% accuracy bounds and not integrity containment bounds, and they cannot bound an unknown station elevation in general.
- The ANP they feed is the simulator's error model, not a validated installation accuracy.

**Propagation through the geometry.**
- The horizontal range is `r = √(s² − Δh²)`, where `s` is the slant range and `Δh` is the aircraft's height above the station.
- With an allowance `a`, the height lies in `[max(0, |Δh| − a), |Δh| + a]`.
- The range therefore lies in `[√(s² − (|Δh| + a)²), √(s² − max(0, |Δh| − a)²)]`, and its allowance is the larger departure from `r`. This is exact, not first-order.
- The same vertical allowance costs more horizontally the nearer the aircraft is to overhead, so a fixed elevation allowance is not a fixed horizontal accuracy.

**Refusals.** A range is refused, not clipped, and the fix names it with its reason:
- `s ≤ |Δh|`: impossible geometry;
- `s ≤ |Δh| + a`: near overhead. The allowance could explain the whole slant range, so the horizontal range is undetermined;
- values that are not finite, and negative slant ranges or allowances.

**Exit:**
- the slant-range correction uses the station elevation and the DME's own position;
- a DME-only or TACAN facility is available to DME/DME;
- the loader refuses malformed DME records, stating the reason;
- a range with a non-data elevation is named in the fix, and its propagated allowance is added to C1's accuracy95Nm as a declared engineering allowance. Where it contributes, accuracy95Nm is a laboratory estimate (its basis says so, C1), not a validated 95% bound;
- impossible and undetermined geometry is refused and reported.

**Owner tests** (`fms-navaid-data.spec.ts`, independent values):
- "a DME-only record is read with its own position and elevation": KBTV IBTV, 342 ft, 40X;
- VOR/DME and VORTAC data: BTV 417 ft 122X, HTO 22 ft 83X, and COL's separate DME;
- "the slant range is measured over the height above the station": a DME on a 3,000 ft summit;
- DME/DME from high-ground stations:
  - correct with elevations, and more than 0.05 NM off at sea level;
  - with separate VOR and DME positions: correct from the DME, and more than 0.1 NM off from the VOR;
- "a range corrected with an assumed elevation is named in the solution", with terrain widening less than assumed;
- the exact propagation:
  - slant 5 NM, height 1 NM, allowance 0.1 NM, against the closed form;
  - near overhead, slant 1.2 NM: the same allowance costs more than eight times as much;
- the refusals, each with its reason, including the exact boundary `s = |Δh| + a`;
- a fix leaving out a refused station and naming it;
- the Annex 10 channel pairing table;
- a TACAN's range serving DME/DME.

**Contract change for other items:** `Navaid.elevation` is required, with the shape `{ feet, source: data | terrain | assumed, provenance }`. Anything that builds a navaid supplies it (F0, F2 and the bench's demonstration navaids).

### F2. The sensor state is split (C1, C4)

Each sensor solution carries C1's five values, with availability, source and freshness. The output vocabulary of C4 is declared at the same time.

**Exit:**
- `navPerformance` and every page read the split state, through the consumers table of C1;
- no page or alert reads a mixed `anp`;
- a seeded error is never presented as a 95% or 10⁻⁵/h figure.

**Owner tests:** `fms-sensor-state.spec.ts`:
- C1's boundary rows;
- "VOR/DME is available but not approach-eligible";
- "the KALMAN 2 σ page value and its 95% accuracy differ by the declared factor";
- "a stale HIL word gives no integrity and dashes on GPS STATUS".

### F3. Mode selection and transitions (M300 1-3 to 1-5, 12-1)

**Three selection layers:**
1. **Receiver selection** (within one FMS): GPS1 or GPS2. #1251's qualified transfer, the crew's manual choice and retention of the current suitable receiver are unchanged. F3 does not add a hysteresis there. There is no new reset to GPS1 on either computer.
2. **Mode selection** (within one FMS): the table below.
3. **Peer selection** (synchronized dual FMS, #1350):
   - the on-side preference;
   - a peer's solution of the same type is adopted only when its accuracy95Nm is 100 m better (M300 1-3, 3-25);
   - different types go by layer 2's order;
   - F3 extends this owner and does not replace it.

**Mode selection rules:**
- **Civil priority (as equipped, DEC-150):** GPS, then DME/DME, then VOR/DME/TCN, then KALMAN, then DVS, then DR.
- **Integrity first:** a candidate with integrityValid outranks one without.
- **GPS integrity:** GPS with integrity is selected on integrity (1-4), not on accuracy.
- **Accuracy-based transitions:**
  - between the radio modes with integrity, the most accurate wins;
  - the mode in use is kept until another is **at least 100 m** more accurate;
  - VOR/DME with integrity to DME/DME with integrity has **no hysteresis**;
  - the manual's other exception, GPS to INS/GPS, needs an EGI, which is not equipped.
- **Immediate reversions:** losing integrity or availability reverts at once. Hysteresis never delays it.
- **No integrity anywhere:** the highest-priority available candidate. An uncertain GPS is retained over KALMAN and DVS when no approved radio backup exists (1-4: the backups the manual names are DME/DME and VOR/DME).
- **Candidates:**
  - both radio modes are candidates whenever each can be solved;
  - M300 1-5's "fewer than three DMEs" describes where VOR/DME is typically used, not a gate;
  - the accuracy models of F6 and F7 make DME/DME the usual winner.

**The transition table** (written before F4–F7 and F11 are accepted; each row is an owner test):

| From | Event | To | Hysteresis | Alert | Position | Authority after |
|---|---|---|---|---|---|---|
| GPS (integrity) | HIL reaches the limit; no approved radio backup | GPS uncertain | — | GPS POS UNCERTAIN; INT | Continuous | No approach or hover |
| GPS (integrity) | HIL reaches the limit; qualifying radio backup; NAIM comparison < RNP | GPS uncertain | — | GPS POS UNCERTAIN; INT | Continuous | No approach or hover |
| GPS (integrity) | As above, NAIM comparison ≥ RNP | Best radio mode | — | GPS NAV LOST; INT | Step to the radio fix | Radio eligibility |
| GPS (any) | Both receivers lose position | Best of radio, KALMAN, DVS, DR | — | GPS NAV LOST; INT | Step (radio) or continuous | Per new mode |
| GPS (integrity) | Velocity words lost, position valid | GPS | — | None | Continuous | Unchanged. The wind is not computed; KALMAN is not aided and its coast clock runs. |
| GPS | One receiver fails | GPS (layer 1 transfer) | — | GPSx NOT USABLE | Step within noise | Unchanged |
| DME/DME | VOR/DME ≥ 100 m more accurate | VOR/DME | 100 m | None | Step | En route, terminal |
| VOR/DME | DME/DME more accurate | DME/DME | None | None | Step | En route, terminal |
| DME/DME, VOR/DME | Integrity lost; the other radio mode has it | The other | None | DME/DME NAV LOST or VOR/DME NAV LOST | Step | En route, terminal |
| DME/DME, VOR/DME | Station gained or lost, same mode | Same | — | POSITION SHIFT over 0.5 NM | Step; the coupled cross-track follows it unsmoothed (12-16) | Unchanged |
| Radio mode | Every radio fix lost, or DME sensor deselected | KALMAN, DVS or DR | — | The mode's NAV LOST | Emulated INS or continuous | None |
| Radio mode | Approach phase entered | KALMAN, DVS or DR (radio not approach-eligible, 15-3) | — | The mode's NAV LOST | As above | None |
| KALMAN | Coast expires, or APIRS fails or goes stale | DVS if usable, else DR (C2) | — | KALMAN NAV LOST | Continuous | None |
| DVS | DVS fails or goes stale | DR | — | DVS NAV LOST; FMS NAV IN DR | Continuous | None |
| KALMAN, DVS, DR | A GPS with integrity, or a radio fix, returns | That mode | — | POSITION SHIFT over 0.5 NM | Step | Per new mode |
| DR | DVS returns | DVS | — | None | Continuous | None |
| Any | A higher mode returns | That mode | — | None besides POSITION SHIFT | Step | Per new mode |
| Any, dual SYNC | Peer's same-type solution 100 m better | Peer's | 100 m | None | Step | Unchanged |

**Alert rule:** leaving a mode for a lower one *because it can no longer be navigated on* raises that mode's NAV LOST. Being outranked on accuracy raises nothing, and nor does moving up.

**Code form:** the table exists as one exported table, and every ordered pair of equipped modes is driven through it. The rows above, with their triggers, are the owner tests.

**Exit:**
- the table is complete for every ordered pair of equipped modes;
- the owner tests cover both receivers failing together, and uncertain GPS alongside KALMAN and DVS candidates without integrity.

**Owner tests:** `fms-sensor-transitions.spec.ts`:
- one test per row;
- "DME/DME to VOR/DME waits for 100 m of accuracy advantage; VOR/DME to DME/DME does not";
- "an integrity loss reverts at once whatever the hysteresis";
- "a station gained or lost in DME/DME moves the position, and the coupled roll command follows it".

The existing layer-1 and layer-3 owners (`fms-gps-authority`, the #1350 dual tests) gain only the newly exposed failures.

### F4. GPS decision table (rev 2 F4, amended by Astra)

| Case | Behaviour | M300 |
|---|---|---|
| Two healthy receivers | As today (#1251 selection) | 1-4 |
| One receiver silent, faulted, with invalid coordinates or impossible values | Independent rejection, kept from #1243 | |
| HIL at or over the limit, **no qualifying backup** | GPS stays selected: **uncertain**, INT, **GPS POS UNCERTAIN**. Not approach- or hover-authorized. | 1-4, E-8 |
| HIL at or over the limit, **qualifying radio backup** | The NAIM comparison (F5). Below the RNP: uncertain GPS retained as above. At or above it: revert to the backup, INT, **GPS NAV LOST**. | 1-4, 12-1 |
| GPS unavailable | The next eligible mode. **DR only when none remains** (Astra). GPS NAV LOST, INT. | 1-4, E-8 |
| GPS–GPS disagreement in independent dual FMS | **GPS-GPS POS DISAGREE** | 3-26 |
| Lateral-only versus vertical approach loss | Kept as the separate #1243 authorities | |

**Exit:** each row is a test, and the approach authority reads only integrity-qualified state (C1).

**Owner tests:** `fms-gps-decision.spec.ts`, one test per row. Rows already protected by `fms-gps-authority` are referenced, not duplicated.

### F5. NAIM (M300 1-4, 12-1)

With a qualifying backup (C1), the FMS compares the GPS with it. **The manual gives no formula.** The bench declares one, labelled laboratory:

> NAIM comparison = |GPS position − backup position| + the backup's accuracy95Nm

A GPS is retained (uncertain) while the comparison is **strictly below** the active RNP. It makes **no 10⁻⁵/h claim** (Astra), is never shown as ANP or HIL, and never sets integrityValid.

**Exit:**
- the comparison appears on the bus and in the sensor state, labelled laboratory;
- the formula is named in FMS_APPLICABILITY.md.

**Owner tests:** `fms-naim.spec.ts`. The crew-entered RNP is 0.5 NM, and each expected value is written in the test, not computed by the helper under test.

| Case | GPS bias | Backup accuracy | Comparison | Expected |
|---|---|---|---|---|
| Revert | 0.4 NM | 0.3 NM | 0.7 NM | ≥ 0.5: revert to DME/DME, GPS NAV LOST |
| Retain | 0.1 NM | 0.3 NM | 0.4 NM | < 0.5: GPS uncertain retained, INT, no approach authority |
| Boundary | 0.2 NM | 0.3 NM | 0.5 NM | Equal is not below: revert |

Two more cases:
- "no backup: the uncertain GPS is retained and GPS STATUS shows the receiver HIL";
- "a KALMAN solution is not a qualifying backup".

### F6. DME/DME (M300 1-4, 12-16 to 12-18, 15-3)

- **Station selection:** automatic scanning of up to **six** DME stations on the four scan channels of C3 (DME-capable TACANs included: TACAN is equipped, DEC-150). At least three are required. The selection applies the distance and geometry reasonableness checks (15-3).
- **DME STATUS status column:**
  - "REJ" means rejected for geometry;
  - blank means used;
  - "N/A" means not responding to tuning (12-17);
  - a wider meaning of REJ is its own matrix row (Astra).
- **Consistency and isolation:**
  - with three ranges, a failed consistency test makes the mode unavailable, and no station is named;
  - with four or more, a station is isolated only on a **unique hypothesis**:
    - excluding it leaves a consistent solution that passes the geometry checks;
    - excluding any other single station leaves an inconsistent one;
  - poor geometry, too few independent ranges, or more than one plausible exclusion give **unavailable**, with no culprit named;
  - the rejection and isolation limits are declared parameters.
- **DME DESELECT:** up to **25** stations, paged, with CLR to remove (12-18). This is C3's station deselection.
- **Accuracy:** a declared model reproducing M300 15-3's typical 95% of 0.5 NM en route and 0.4 NM terminal. Not available for approach.
- **Tuning:** only ranges C3 marks identified and fresh count.

**Exit:**
- DME STATUS shows up to six stations with their status, frequency and distance, plus "SCANNING CTRL ACTIVE" and the position;
- a deselected station is never scanned;
- 3-station inconsistency makes DME/DME unavailable.

**Owner tests:** `fms-dme-dme.spec.ts`:
- "six stations scanned, a poor-geometry pair marked REJ";
- "a non-responding station shows N/A";
- "three inconsistent ranges: mode unavailable, no culprit named";
- "four ranges with one biased station isolate it";
- "four ranges with two plausible exclusions: unavailable, no culprit";
- "a deselected station is never used".

### F7. VOR/DME (M300 1-5, 12-19 to 12-20, 15-3)

- **Tuned stations:** at least one VOR/DME, and at most two VOR/DME plus one TACAN (12-19).
- **AUTO-tuned VORs are eligible (a named profile option, `autoVorNavigation: on`).**
  - M300 12-19's default navigates on manually tuned stations only. Sean's decision (30 Sep, DEC-150) overrides it.
  - The option carries that provenance and cites 12-19 as the manual default it overrides. Turning it off restores the manual behaviour.
  - VOR/DME/TCN STATUS shows each station's tuning source (AUTO or MAN), so the applicability is visible.
  - There is no caution for the option operating normally (Astra).
- **VOR/DME/TCN STATUS:** source and identifier, frequency, radial and slant range for each receiver, plus the TACAN line (channel, bearing/distance), plus the position (12-20).
- **Accuracy:** 95% of 0.6–0.8 NM within 7 NM of the station, 1.5 NM beyond (15-3). Not available for approach.
- **Reasonableness:** checks on bearing and distance before use (15-3).

**Exit:**
- a VOR/DME fix uses only positively identified, fresh measurements from an **acknowledged** tuning (C3);
- an AUTO tuning is eligible only when `autoVorNavigation` is on, otherwise only a MAN tuning is;
- the accuracy steps at 7 NM;
- a mismatched ident removes the station.

**Owner tests:** `fms-vor-dme.spec.ts`:
- "an AUTO-tuned VOR/DME is used under `autoVorNavigation`, and with the option off only a manually tuned one is (M300 12-19)";
- "a TACAN bearing and distance give a VOR/DME/TCN fix";
- "the 95% accuracy steps from 0.8 to 1.5 NM past 7 NM";
- "an unreasonable radial is rejected";
- "a pending, unacknowledged tuning gives no fix".

### F8. Radio management: tuning, feedback and failure (M300 13-1 to 13-26, 3-26; C3)

This extends #1350's shared `RadioManagementSystem` to C3's model.

- **Radios:**
  - COM1/2;
  - NAV1/2;
  - DME1/2 with their channels;
  - one TACAN;
  - ADF1/2;
  - ATC1/2 (control only).
- **Radio model:**
  - the tune command, the in-progress state, and feedback;
  - the displayed active frequency follows the radio's **feedback**, not the entry;
  - the command status, health and reception of C3.
- **Display:**
  - large white when tuned;
  - large inverse white while tuning;
  - small amber when the tuning failed or the radio reports failure (13-3).
- **RADIO 1/X top page (13-6):**
  - active and standby for each radio;
  - entry by frequency, identifier or preset;
  - LSK swaps standby and active;
  - out-of-range entries are refused with the manual's ranges (13-4: VOR/ILS/DME 108.00–117.95 at 50 kHz, ADF 190.0–1799.5 kHz for the ADF-462);
  - the RADIO key cycles the pages when configured (13-19).
- **NAV 1/2 (13-21, 13-22):**
  - NAV1/NAV2 with the identifier and the radial or localizer deviation;
  - **AUTO/MAN** VOR tuning, toggled by DELETE to standby or by the swap;
  - **DME HOLD** ON/OFF (C3 channel 1);
  - the DME slant range, blank when there's no reply, and `****` when the receiver fails.
- **NAV 2/2 (13-23):**
  - NAV1/2 MODE AUTOMATIC/MANUAL;
  - NAV and DME TEST: READY → CONFIRM? → STARTED → PASS/FAIL/TIMEOUT. The receiver under test gives no navigation ranges (C3).
- **ADF pages (13-23 to 13-26):**
  - ADF1/2 frequencies, mode, BFO, test, and the bearing to the RMI;
  - the marine distress band 2181–2183 kHz where the model allows it.
- **TACAN:** channel entry and feedback, with the device in the same command, health and reception model. Its control page is identified from M300 section 13 when this item is built.
- **Libraries (13-2, 13-44 to 13-47):**
  - NAV, ADF and COM libraries of 99 presets, with an identifier of up to five characters;
  - password-protected edits.
- **Tuning authority:** **the FMS is the only tuning source** (DEC-150). The external-head behaviour and RADIO TUNING DISABLED (13-3, 13-20) are not configured, with the guard test of F0.
- **Dual FMS (3-26, #1350):**
  - tuning is synchronized by burst tuning and radio feedback, not by FMS cross-talk;
  - the standby frequency is cross-talked;
  - tuning works from either side, including with the link down.
- **Alerts (Appendix E, per C3's states):**
  - CONTROL LOST (a control-path timeout) for NAV, DME, VOR, ADF, COM and TPDR/XPDR;
  - the configured FAILED advisories for a receiver failure;
  - never an alert for an untuned or out-of-coverage healthy radio.

**Exit:**
- navigation uses **only** measurements C3 marks identified and fresh;
- a radio whose control path is lost raises its CONTROL LOST, and keeps its usable reception;
- a failed receiver shows amber, raises its FAILED advisory, and navigation drops its measurements.

**Owner tests:**
- `fms-rms-radios.spec.ts`:
  - C3's integration case;
  - "an entry is shown in inverse until the radio confirms, then white; a refusal leaves it amber without CONTROL LOST";
  - "the displayed active frequency follows feedback when the radio reports a different one";
  - "a superseded request raises nothing";
  - "an out-of-range entry is refused with the scratchpad message";
  - "a library preset tunes by number and by ident, the first match winning";
  - "either CDU tunes, and both show the same standby".
- `fms-nav-radio.spec.ts`:
  - "VOR AUTO/MAN toggles by DELETE and by swap";
  - "DME HOLD keeps channel 1 on the held station while the scan continues";
  - "a DME with no reply shows a blank distance, a failed one shows ****";
  - "NAV TEST runs through CONFIRM? to a result, and the receiver under test gives no ranges".
- `fms-adf.spec.ts`:
  - "ADF tunes within its range and drives the RMI bearing";
  - "an untuned healthy ADF flags the RMI and raises no alert";
  - "ADF CONTROL LOST on a lost control path".

### F9. Navigation status and deselection pages (M300 5-26, 12-17 to 12-20, 17-3)

- **NAV STATUS INDEX 1/1:** PREDICT RAIM, GPS, DME, VOR/DME/TCN, KALMAN and DVS if equipped, with DESELECT. There are prompts **only for equipped interfaces** (5-26 note).
- **DESELECT 1/1 (17-3):**
  - TAS, HDG and each equipped sensor, each VALID ↔ DESEL (or ACQ);
  - a deselected TAS stops the wind computation, and position-fixing modes are unaffected in the long term;
  - DME here is C3's **DME sensor deselection**.
- **GPS DESELECT** for the dual receivers.
- **POS INIT/REF 2/2** sensor table:
  - each mode with its status (NAV, DSEL, ACQ);
  - the distance and bearing from the FMS position;
  - the accuracy95Nm (12-20, 12-27 layout without the inertial rows).

**Exit:**
- every page renders within the CDU grid (the R19 layout test);
- deselection reaches the estimator in the same tick.

**Owner tests:** `fms-nav-status-pages.spec.ts`:
- "DESELECT TAS: the wind is no longer computed, and GPS position is unaffected";
- "DME sensor deselected: DME/DME and VOR/DME both unavailable, per F3's row";
- "station deselection removes one station and keeps DME/DME";
- "the POS INIT 2/2 table lists each equipped mode with its accuracy".

### F10. Dead reckoning (M300 1-5, rev 2 F7, Astra's amendment)

- **Inputs:** the last position, heading, TAS and the **last valid computed wind** (the E1 system wind).
- **Alerts:** FMS NAV IN DR on entry (E-33), then CHECK ANP per F12 when the estimated accuracy exceeds the RNP.
- **Low-speed regime (IAS below 40 KIAS):** DR from heading and TAS is **degraded**. The accuracy growth uses the declared no-velocity rate. DR is selected only when no velocity-measuring mode (DVS) is usable (C2).
- **Recovery:** automatic on sensor restoration (1-5), with the position step handled as in F3.

**Exit:** the DR accuracy grows from declared input uncertainties and matches worked cases, and a hovering DR case is labelled.

**Owner tests:** `fms-dead-reckoning.spec.ts`:
- "DR carries the last computed wind";
- "in the hover, DR is flagged degraded and the accuracy grows at the no-velocity rate";
- "a restored GPS ends DR and the position step is announced".

### F11. KALMAN and DVS modes (DEC-150 with Sean's clarification; M300 1-5, 12-20 to 12-24, 15-4; C2)

- **Emulated INS:** built from the APIRS words of C2 and aided only by integrity-qualified GPS. The readiness and power-interruption rules of C2 apply.
- **Coast:** **2 minutes** from the last integrity-qualified aiding (Sean, DEC-150; the manual says "typically 2 minutes", 1-5).
- **At expiry:** KALMAN becomes ineligible, and selection takes the best remaining usable source: DVS when healthy, DR only when nothing else is usable (Sean, 30 Sep, about 7:40 PM ET).
- **Accuracy:** grows independently of the RNP limit (rev 2 F7), and is reported as C1 requires.
- **KALMAN STATUS 1/1:**
  - OP MODE;
  - KALMAN POSITION and GPS POSITION;
  - 2 SIGMA POS ERR (2 σ, C1);
  - GPS READY and APIRS READY.
- **RNP:** it is not applicable to RNP operations (15-4). CHECK ANP follows F12.
- **Alerts:** KALMAN NAV LOST, APIRS FAILED and AHRSx FAILED (E-12, E-21, E-22).

**DVS (Doppler), equipped (DEC-150):**
- **Solution:** C2's velocity words with the FMS heading give position, ground speed and status (12-20).
- **Civil option:** the DVS solution is **without integrity and lowest priority** (12-20). It is never RNP- or approach-eligible.
- **Pages:** DVS STATUS 1/2 and 2/2 with the water-current entry (12-22, 12-23). The DVS line appears on POS INIT/REF 2/2.
- **Alerts:** DVS NAV LOST (E-6).
- **Hover:** no hover authority (C2).

**Bench stimuli:** APIRS failed and Doppler (DVS) failed are bench conditions. A test or scenario that needs DR with these sensors equipped fails them explicitly.

**Exit:**
- KALMAN carries position for 2 minutes after its last integrity-qualified aiding, and re-entry does not renew it;
- then DVS if usable, otherwise DR;
- neither is ever RNP-eligible.

**Owner tests:** `fms-kalman-dvs.spec.ts`:
- "GPS lost offshore: KALMAN carries position for 2 minutes with a growing 2 σ, then DVS";
- "with DVS failed too, KALMAN expiry goes to DR with FMS NAV IN DR";
- "only a GPS with integrity aids KALMAN; an uncertain GPS does not";
- "leaving KALMAN for a radio fix and returning does not renew the coast";
- "a power interruption of 51 ms re-initializes it, and one of 50 ms does not";
- "the water current entry corrects the DVS drift over water";
- "DVS NAV LOST on a failed DVS";
- "neither mode is ever RNP- or approach-eligible".

### F12. RNP, ANP and integrity alerts (M300 15-1 to 15-4, 1-4, Appendix E)

- **ANP:** the **95%** radial position error, excluding FTE (15-1): C1's accuracy95Nm, and not the "ANP/HIL = 1.0" 99.999% option (15-2) (Sean, DEC-150). The HIL appears on GPS STATUS 2/2 (C1).
- **Error limit basis: RNP** (M300 1-3's configurable choice; Sean, DEC-150). The GPIAL and each mode's integrity limit follow the active RNP, whether the phase default or the crew entry.
- **CHECK ANP episode semantics:**
  - **Start:** an episode starts when ANP exceeds the RNP, or ANP is unavailable (C1).
  - **Timing:** it raises CHECK ANP once, after the phase's time to alert: **30 s en route and terminal, and 10 s on approach** (15-2). This replaces today's demonstration parameters.
  - **Phase change:** a phase change during an episode re-evaluates the time to alert against the new phase, from the episode's start. If that time has already passed, the alert is raised at once.
  - **End:** the episode ends as soon as ANP is at or below the RNP. A new exceedance starts a new episode.
  - **Acknowledgement:** clearing the message neither ends the episode nor re-raises it.
  - **Time base:** timers run on simulation time, so a paused bench pauses them, and the tick grouping cannot change them.
  - **Guidance:** CHECK ANP's persistence never delays withdrawing guidance that already lacks authority (C1).
- **INT annunciator:** lit while GPS is navigated without integrityValid (C1's boundary table).
- **CDI full scale (15-1, Table 15-1):**
  - 5.0 NM en route, or for an RNP entry above 1.01;
  - 1.0 NM terminal, or above 0.31;
  - 0.3 NM approach, or for any RNP entry at or below 0.31.
- **VERIFY RNP VALUE:** checked against the procedure's RNP (5-16, 5-25, 5-32, E-17).
- **Position difference messages:** FMS-D/D POS DIFF, FMS-V/D POS DIFF, POSITION SHIFT and GPS POS DIFF (E-7, E-8, E-14), each with the manual's trigger.
- **DR:** **FMS NAV IN DR** (E-33), raised on entering dead reckoning.
- **EHSI/EFIS annunciations (M300 C-9):** the navigation mode, **GPS integrity lost** and **Unable RNP** are *display annunciations* driven from the bus (C4). They are not CDU scratchpad messages.
- **Wording:** the S300 CDU wording is CHECK ANP, GPS POS UNCERTAIN, GPS NAV LOST, FMS NAV IN DR and VERIFY RNP VALUE. The bench must not raise a Boeing-style "UNABLE REQD NAV PERF" on the CDU.
- **Messages and inhibits:** each alert is mapped to its Appendix E row, including the row's applicability and inhibit conditions. A list of source names alone is not the proof.

**Exit:** every navigation message the bench raises is traced to an Appendix E row, and its raise, clear and inhibit behaviour is tested.

**Owner tests:** `fms-integrity-alerts.spec.ts`:
- "CHECK ANP waits 30 s en route and 10 s on approach";
- "an episode that ends at 29 s and restarts counts from the restart";
- "a phase change to approach at 20 s raises at once";
- "clearing the message does not re-raise it within the episode";
- "the CDI full scale follows Table 15-1";
- "an RNP entry of 0.5 in terminal gives 1.0 NM full scale";
- "every navigation alert has an Appendix E source".

### F13. Output bus tags (C4)

The C4 vocabulary becomes tags in the exhaustive output table, each with its ARINC-style provenance and validity (session 1's rule 4: the PR body says it adds to an exhaustive table). This item **depends on #1345 and #1376**. It extends that catalogue once it lands, and neither duplicates it nor weakens its exhaustive typing.

The EFIS reads only the bus: an RMI needle, a DME readout or a mode annunciation is drawn only from bus words.

**Exit:**
- every C4 value is a tag with provenance and validity;
- the EFIS draws none of them from anything else.

**Owner tests:** `fms-output-bus-nav.spec.ts`:
- "every Stage F value is in the exhaustive table with provenance";
- "a failed DME shows NCD/FAIL on the bus and dashes on the display";
- "accuracy and integrity bound are separate words".

### F14. Sensor failure scenarios on the bench

The bench's sensors and scenario tabs gain the following faults and stimuli.

**Radios:**
- a NAV, DME or TACAN receiver fails (answers FAIL) or goes silent;
- a control path is lost (timeout, reception kept);
- a DME station stops replying, or its ident mismatches;
- a VOR radial is biased;
- an ADF fails;
- an NDB station goes off the air.

**Air data and attitude:**
- TAS invalid, or heading invalid or biased;
- the APIRS fails;
- the DVS fails;
- a power interruption of a stated duration (C2).

**GPS:** the existing faults, plus "integrity only" and "position gone" on both receivers.

The external control head is **not** a stimulus of the default profile (DEC-150: none equipped). A scenario that injects it is refused at admission for this profile, with the reason, and never counts towards the default mission.

Each fault is a scenario step, validated on admission and listed in the run report with its time and value (rev 3 §10 pattern).

**Exit:** each fault has an owner test through the scenario runner, and a rendered test that the bench control drives it.

### F15. Acceptance mission for Stage F (reproducible)

**Fixture record.** The mission's fixture file names:
- the profile and its fingerprint (the scenario report's configuration fingerprint);
- the navigation-data cycle (CIFP 2609) and the hash of each data file;
- the facilities used. Any coastal DME or TACAN coverage that the real data lacks is added as **synthetic** stations, labelled synthetic in the fixture and the report.
- the procedure fixtures;
- the initial state;
- the pinned simulation start time;
- every fault with its time;
- the expected results, computed independently of the code under test, with their tolerances.

**Offshore, from 87N, no radio coverage:**
1. GPS integrity is removed while the position is kept: GPS POS UNCERTAIN, INT, no approach authority.
2. The position is removed: KALMAN for 2 minutes from the last aiding, then DVS without integrity. CHECK ANP comes at the phase timing.
3. The DVS fails: DR, FMS NAV IN DR.
4. GPS is restored: recovery, with the position step shown.

**Coastal, with DME and TACAN coverage (declared synthetic where needed):**
1. A biased DME is isolated with four stations, and with three the mode goes unavailable.
2. The crew deselects a station.
3. A NAV radio's control path is lost, and its CONTROL LOST appears while its reception continues.
4. VOR/DME is used on an AUTO-tuned VOR (`autoVorNavigation`), and VOR/DME/TCN on a TACAN.

**NDB variant (F16):**
- It starts separately, at KIAG.
- The runway NDB approach is flown on FMS guidance with GPS, and the ADF bearing is on the RMI throughout.
- GPS approach integrity is lost on the final.

**Throughout:**
- the computed position is checked against bench truth, independently of the displayed confidence;
- the nominal and failure variants reach the accepted thresholds;
- the checks are made on the actual guidance outputs.

**Exit:** the nominal run and each variant pass with recorded evidence. There is one integrating mission, in line with Sean's evidence standard.

### F16. NDB approaches (DEC-150; M300 7-1)

M300 7-1 lists the approach types the FMS is approved to fly with their required sensor: **NDB (GPS or NDB)** and **NDB D (GPS or NDB/(DME))**. The M300 has **no ADF navigation mode** (1-3, 1-4). The declared **simulator scope** is therefore:
- a database NDB procedure flown on FMS lateral guidance from GPS;
- the ADF bearing as independent raw data for the crew;
- no position derived from the ADF.

This is the bench's implementation scope. It does not prove every installed CMA NDB rule.

**Conventional versus overlay (FAA AIM 1-2-3(c), notes 4 and 5):**
- Flying RNAV guidance on a conventional NDB final does not remove the NDB's operational and monitoring role.
- The fixtures are conventional NDB approaches. The bench does **not** treat "database NDB approach" as unrestricted GPS substitution.
- Each mission records its assumptions:
  - the NDB is operating;
  - the ADF raw data is monitored and valid throughout.
- An NDB off the air, or an ADF failure, is its own failure case. It is not silently covered by GPS.
- CIFP carries no overlay authorization. The chart title in dTPP 2609 is recorded in the fixture metadata, and no overlay case is claimed unless a title says so.

**Fixtures (selected from CIFP 2609):**
- The cycle codes 23 NDB approaches (final route type N) and **no NDB/DME approach**: no route-type Q record exists.
- **KIAG N28**, Niagara Falls:
  - NDB runway 28, with the runway MAP RW28;
  - the FAF and recommended navaid is the NDB IA;
  - the EHMAN transition has an HF course reversal;
  - the missed approach is CA, DF, HM at IA.
- **KBKT NDB-A**, Blackstone:
  - a circling approach whose MAP is the **non-runway fix CFBNK**;
  - the FAF and recommended navaid is the NDB BKT;
  - the MELIA and NUTTS transitions have PI course reversals;
  - the missed approach is CA, DF, HM.
- **NDB/DME:** DEC-150 item 8 names NDB/DME approaches. With none coded in 2609, the NDB D path is covered by the same loader and page code, and flown when a later cycle codes one. The plan says so rather than inventing a fixture.

**Loader:**
- The importer already maps route types N and Q to NDB. So F16 is not "add route letters".
- The importer rejects an airport approach whose MAP is not a runway unless it meets the helicopter PinS rules. KBKT NDB-A is an ordinary conventional circling approach with a non-runway MAP. The loader accepts it as a **conventional non-runway MAP**:
  - its own endpoint kind;
  - flown to the MAP and into the missed approach;
  - never classified as PinS;
  - never treated as a completed landing.
- The recommended navaid and the N/Q distinction are kept on the procedure.
- A leg type the loader cannot fly is refused with the reason, as today.

**Selection:** the approach page lists NDB and NDB D approaches with their prefix (7-1). Loading one arms the recommended NDB for ADF tuning, as a request on the ADF (C3) that the crew confirms on the ADF page.

**Guidance and authority:**
- The approach phase, RNP 0.3 and the CDI full scale of Table 15-1 apply as for any approach.
- Today NO APPR INTEGRITY applies to RNAV approaches only. F16 extends it to NDB approaches flown on FMS guidance.
- **The AFCS consequence (S300 profile, as the bench does today for an RNAV approach, C.3.1):** when GPS approach integrity is lost:
  - NO APPR INTEGRITY and INT;
  - the approach phase is cancelled to terminal (RNP 1.0);
  - FMS LNAV stays coupled on the route **without approach authority**;
  - the mode event is recorded.
- Nothing restores FMS approach authority automatically. Continuing on the NDB raw data (HDG or ATT on the AFCS) or going around (MISSED APPR, TOGA) is a **crew action**.
- DME/DME and VOR/DME are never approach-eligible (15-3), so they cannot stand in.

**Raw data:**
- ADF1/2 bearing to the tuned NDB on the RMI (C3, C4), with validity;
- an NDB off the air, or an ADF failure, flags the RMI;
- an **untuned or out-of-coverage healthy ADF raises no alert** (C3);
- ADF CONTROL LOST is only for a lost control path.

**EFIS:** NDBs within map range are drawn when the NDB option is on (C-10, A-47).

**Exit:**
- KIAG N28 and KBKT NDB-A load from CIFP 2609 and are flown to their MAPs on FMS guidance with GPS;
- the RMI shows the ADF bearing to the recommended NDB throughout;
- GPS approach integrity lost on the final removes approach authority, with the AFCS consequence above, and no position is derived from the ADF.

**Owner tests:** `fms-ndb-approach.spec.ts`:
- "KIAG N28 loads with its prefix, legs, HF reversal and missed approach";
- "KBKT NDB-A loads with a conventional non-runway MAP, not as PinS";
- "loading it requests the recommended NDB on the ADF";
- "flown on GPS to the MAP with the RMI on the NDB";
- "GPS integrity lost on the final: NO APPR INTEGRITY, approach cancelled, LNAV on route without authority, ADF bearing still shown";
- "the NDB off the air flags the RMI";
- "an ADF failure flags the RMI and raises its FAILED advisory".

---

## 5. Ledger (draft rows for FMS_V1_ACCEPTANCE.md's Stage F section; session 1 lands them)

| Row | Requirement | Source | Owner test file | Status |
|---|---|---|---|---|
| C1 | Accuracy, integrity bound, integrity validity, NAIM (laboratory) and authority kept distinct, with named consumers and boundary rows | M300 1-3, 12-1, 15-1 to 15-4; DEC-150 | fms-sensor-state | Open |
| C2 | KALMAN/DVS measured inputs, aiding, simulation clocks, interruption, expiry | M300 1-5, 12-20 to 12-24; DEC-150 with its clarification | fms-kalman-dvs | Open |
| C3 | Radio ownership: DME channels, TACAN, HOLD/TEST/MAN, command status, health and reception separate | M300 12-16, 12-19, 13-3 to 13-26, App. E | fms-rms-radios | Open |
| C4 | Output vocabulary | Bench contract; #1345, #1376 | fms-output-bus-nav | Open |
| F0 | Stage F equipment declared, with nothing simulated for absent equipment | M300 1-4, configuration | fms-stage-f-configuration | Open |
| F1 | DME-only and TACAN navaids, station elevation with its source and allowance propagated exactly, co-located DME position, Annex 10 pairing, refusals | ARINC 424 4.1.2; ICAO Annex 10; rev 2 F1 | fms-navaid-data | Open |
| F2 | Split sensor state and its consumers | M300 1-3, 15-3 | fms-sensor-state | Open |
| F3 | Three selection layers; the transition table; 100 m hysteresis and its exception; immediate reversion | M300 1-3 to 1-5, 3-25 | fms-sensor-transitions | Open |
| F4 | GPS decision table (7 rows) | M300 1-4, 3-26, E-8 | fms-gps-decision | Partial (#1243, #1251) |
| F5 | NAIM (laboratory formula, corrected worked cases, no 10⁻⁵/h claim) | M300 1-4, 12-1 | fms-naim | Open |
| F6 | DME/DME with 6 stations, REJ/N/A, deselect 25, 3-range unavailability, unique-hypothesis isolation | M300 12-16 to 12-18, 15-3 | fms-dme-dme | Partial |
| F7 | VOR/DME/TCN on acknowledged, identified tunings; AUTO eligible under `autoVorNavigation` (overriding 12-19's default); accuracy steps | M300 12-19, 15-3; DEC-150 | fms-vor-dme | Partial |
| F8 | RMS per C3: libraries, AUTO/MAN, DME HOLD, TEST, ADF, TACAN, dual-side tuning, CONTROL LOST and FAILED kept apart; the FMS as the only tuning source | M300 13-1 to 13-26, 3-26, App. E | fms-rms-radios, fms-nav-radio, fms-adf | Partial (#1350) |
| F9 | NAV STATUS INDEX, DESELECT (TAS, HDG, sensors), POS INIT 2/2 sensor table | M300 5-26, 12-20, 17-3 | fms-nav-status-pages | Partial |
| F10 | DR from heading, TAS and last wind; degraded in low speed; recovery | M300 1-5; Astra F7 | fms-dead-reckoning | Partial |
| F11 | KALMAN (2-minute coast from the last aiding; then the best usable source) and DVS (lowest priority, no civil integrity) | M300 1-5, 12-20 to 12-24, 15-4; DEC-150 | fms-kalman-dvs | Open |
| F12 | 95% ANP, RNP-based limits, CHECK ANP episodes (30 s/10 s), INT, CDI FSD Table 15-1, VERIFY RNP, POS DIFF, FMS NAV IN DR, sourced messages and inhibits | M300 15-1, 15-2, App. E | fms-integrity-alerts | Partial |
| F13 | Output bus tags for C4, EFIS from the bus only | Bench contract; #1345, #1376 | fms-output-bus-nav | Open |
| F14 | Bench sensor and radio failure stimuli in scenarios; no external head in the default profile | rev 3 §10 pattern; DEC-150 | per item | Open |
| F15 | Reproducible Stage F acceptance mission | Astra gap assessment, item 1 | fms-stage-f-mission | Open |
| F16 | KIAG N28 and KBKT NDB-A from CIFP 2609 on FMS guidance; ADF raw data; conventional non-runway MAP; AFCS consequence of lost authority | M300 7-1, 1-3; AIM 1-2-3; DEC-150 | fms-ndb-approach | Open |

## 6. Order and size

1. **F0, F1 and F2 with C1 and C4** (small to medium): the contracts everything else reads.
2. **F3's transition table** (small): written and reviewed before the modes it governs are accepted.
3. **F8 with C3** (large): the radios. F6, F7 and F9 depend on identified, fresh measurements.
4. **F3's implementation, F4, F5, F6 and F7** (large): the estimator and its transitions.
5. **F11 with C2** (medium): KALMAN and DVS.
6. **F9, F10 and F12** (medium): pages, DR and alerts.
7. **F16** (medium): NDB approaches, after F8 (ADF) and F12.
8. **F13** (after #1345 and #1376 land), **F14**, then **F15**.

Each step is its own PR with red-first owner tests. **F3 and F8 touch shared state and are not parallelized with each other.** An all-mode acceptance row is not closed before every mode it covers is integrated.

---

## 7. Sean's answers (30 September 2026, about 4:45 PM ET; recorded as DEC-150)

| # | Question | Answer | Where it lands |
|---|---|---|---|
| 1 | TACAN | **On.** DME/DME may use TACAN ranges; VOR/DME/TCN may use one TACAN. | F0, C3, F6, F7 |
| 2 | AHRS/KALMAN | **Equipped, 2-minute coast**, then DR. **Clarified (about 7:40 PM ET, answering Astra SF-02):** at expiry KALMAN becomes ineligible and selection takes the best remaining usable source, DVS included when healthy; DR only when nothing else is usable. | F0, C2, F11 |
| 3 | Doppler (DVS) | **On:** lowest priority, no civil integrity (M300 12-20) | F0, F3, F11 |
| 4 | Error-limit basis | **RNP** | C1, F12 |
| 5 | AUTO-tuned VOR | **Eligible for VOR/DME navigation.** A named profile option, `autoVorNavigation`, with this decision as provenance; M300 12-19's manually-tuned-only rule is the manual default it overrides. | F7 |
| 6 | ANP presentation | **95% figure** | C1, F12 |
| 7 | External radio head | **None**: the FMS is the only tuning source | F8, F14 |
| 8 | ADF use | **NDB approaches in scope:** database NDB approaches flown on FMS guidance, with the ADF bearing as raw data (M300 7-1; no ADF navigation mode) | F16 |

No functional questions remain open for this plan. Astra reviews it before any Stage F code is proposed for merge.

## 8. Reconciling the locally built pieces

Local, unpushed branches exist for F0, F1 (session 4), F2, F8a (the RMS extension), F8b (the NAV and ADF pages) and F11. Most were built against revision 1. Before any of them is proposed, each changes as follows:

| Piece | What it changes to meet this revision |
|---|---|
| F1 | Built by session 4 to this section's contract. `Navaid.elevation` becomes required, so every navaid builder in the other pieces supplies it on rebase. |
| F0 | The external head is declared off with its pages guarded. The KALMAN row carries the clarification. |
| F2 | The GPS entry's integrity bound stays the receiver HIL. The NAIM comparison moves out of `integrityNm` into its own laboratory field. Every consumer reads per C1's table. The GPS measurement's HFOM-or-HIL fallback for ANP goes. |
| F8a | Radio health separates CONTROL LOST (timeout, reception kept) from FAIL/SILENT. REJECTED and SUPERSEDED command states are added. DME channels and HOLD and TEST per C3. A TACAN device. No ADF alert when untuned. |
| F8b | TEST removes that receiver's ranges from navigation. DME HOLD becomes C3's channel 1. |
| F11 | σ becomes per axis, with the 2 σ page value and a 2.448 × max(σx, σy) accuracy. The coast clock runs from the last integrity-qualified aiding, which also requires the GPS velocity words (already so). `powerInterrupt(ms)` gets the 50 ms boundary. The APIRS and Doppler failure conditions stay. |
| F3 | Being built from §4 F3's table. |

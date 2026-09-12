# #1022 exact-candidate core and visual review packet

Implementation: `codex/1022-astra-implementation`, `C:\Sean Project\RMT-1022-astra-implementation`, draft [PR #1031](https://github.com/AeroLinkDEV/requirements-management-tool/pull/1031). Head `7d1a8c58e01166391f5ea4cd1f59997813d58ab4`; merged base/protected main `858791c6e12b316bb7ded56a2d17dab05e0b872b`. Clean before and after final qualification. This is an evidence-only branch, not the implementation.

Requested gates: CORE_INTERACTION round 4 and CROSS_VIEW_VISUAL round 1, separately completed independent verdicts. Earlier CORE round 3 PASS at `b2069eb1c082ae60e9b489f6340f79ef6538331f` does not approve this changed head. FINAL_HEAD and protected integration remain pending.

## Provenance and inspection

All six original PNGs were opened and inspected, not inferred from OCR. [Original ZIP](https://github.com/user-attachments/files/32116311/digital-thread-review-evidence.zip), SHA-256 `c8738c21a4623732baac2c54ae34fcbc1cf6dce57c4a78a6af61774320d4ab73`; enclosed original DOCX SHA-256 `bfed5645e92c6f86f5bda2d24200129591fbded3c145a1626f03b2753077d405`. [Authoritative audit and repair plan](https://github.com/AeroLinkDEV/requirements-management-tool/issues/1022#issuecomment-5648177507), [immutable audit pixels and measurements](https://github.com/AeroLinkDEV/requirements-management-tool/tree/8522c24385e1691a1d9767e808ea93609dd547ed/docs/archive/1022-astra-review-20260912). Historical DeepSeek evidence remains linked in the parent README; obsolete review-evidence files were removed only from the implementation tree after publication of immutable provenance.

`fixtures/`: 57 current captures plus contact sheets and measured rectangles. All three real adapters, 1280/1440/1920 widths, 1000 height, Bottom/Right/Auto, normal and long content with synthetic text-only 125% sizing. Browser zoom is 100%; this text stress is not native browser zoom. Includes manual exploration. `full-application/`: 21 captures, three contact sheets and the actual 26.76-second journey recording: quiet, truly unselected hover, selected, each dock and manually explored states in Change network, Inside a change and Artifact thread. Full application uses existing disposable SQLite showcase data, not the owner's original dataset or HOME. Capture harnesses are included under `harness/`.

`core/` and `motion/`: current rendered proof and 38 recorded core tests, including actual slower click framing and interruption. Click/interruption recordings were decoded and inspected at 0.1-second intervals; contact sheets preserve that sequence. The automated motion proof measures all six displayed matrix axes, more than 15 intermediate samples over more than 550ms, scale steps below .06, edge/card anchor agreement within two pixels and actual hit targets. Interruption verifies the displayed position and subsequent callbacks, not merely CSS duration.

`supplemental/`: real Network adapter fixture with a linked card entirely left and above the usable area. Selection prepares it vertically while x and camera remain fixed; first horizontal exposure arrives at useful y=143. The synthetic fixture projection and before/after coordinates are explicit in its harness and measurements. It supplements the right/below core case without changing domain data.

## Changes since previous reviewed head

`4ed42a9ab1cc3a2c33daed6da2343ef5838a2f56`: readable actual section labels, spacing and bounded controls; candidate dock constraints; displayed-position clipping/continuation; meaningful motion proof. `95d82f8f0fa6ebaad4cf48f868cc1469ca995616`: preserve visible portions of partial/tall cards, independently gate nested native controls, allow manual access to oversized-card tails, repair StrictMode animation cleanup/restart, retain historical evidence through immutable links. `7d1a8c58e01166391f5ea4cd1f59997813d58ab4`: constrain placement notice and secondary Show strip to usable space outside all inspector docks. No API/database or global architecture expansion.

## Audit dispositions

| Finding | Candidate repair and fresh proof |
|---|---|
| ASTRA-01 | Real Artifact card native pointer and keyboard action passes full page suite. Partial-card clipping and StrictMode settlement preserve access; inspector action is not substituted. |
| ASTRA-02 | Promotion uses actual post-selection toolbar, heading, selected-control and inspector reserves. Core independently measures DOM non-intersection; passes on the same head as ASTRA-01. |
| ASTRA-03 | Scope/subject/content geometry identity is separated from camera-derived windows. Same-tier height changes reconcile collisions while retaining valid offscreen placements and delivered/manual ownership. Pure 36-unit counterexample and real selected/linked growth pass. |
| ASTRA-04 | Actual UPSTREAM/DOWNSTREAM eyebrow selectors are 12px (15px in 125% stress), with spacing and bounded controls. All-view matrix includes long and empty content. |
| ASTRA-05 | All adapters assert zero selected cards and no inspector before quiet-hover; selected-hover inert tests remain separately covered. Full application captures establish the same precondition. |
| ASTRA-06 | One shared hook measures actual candidate dock constraints using candidate layout rather than reusing the current panel rectangle. Bounded recovery remains; matrix covers three widths/docks. |
| ASTRA-07 | Real .8-second automatic framing is sampled on all matrix axes; interruption and reduced-motion geometry pass. Videos/contact sequences accompany measurements. Earlier failure is retained below. |
| ASTRA-08 | Four actual usable-boundary cues, partial clipping, compact secondary Show strip, native control guards, pointer identity/up/cancel/unmount and StrictMode replay are covered. Historical artifacts remain immutable and discoverable. |

## A01–A20 coverage

| Criterion | Fresh evidence/disposition |
|---|---|
| A01 | Quiet-hover all three adapters at tiers 0/1/3; repeated camera/density samples; full-application zero-selection/no-inspector preconditions. |
| A02 | Core source and visible resident rectangles remain stationary; pure partial-visibility exclusion. |
| A03 | Network available-space reveal, hidden-lane preparation, collision tests and effective geometry measurements. |
| A04 | Core right/below hidden preparation plus supplemental left/above preparation with unchanged camera and lane x. |
| A05 | Hover-exit retirement and Artifact manual roll/same-scope rerender. |
| A06 | Post-selection promotion, direct click and mid-transition hover-click, selected growth reconciliation. |
| A07 | Selected-hover inert checks in all three views preserve subject/thread/inspector/camera. |
| A08 | Pointer identity, background click, pan/drag release/cancel and active cleanup tests. |
| A09 | Core first right/below exposure and supplemental first left/above exposure. |
| A10 | Dense branches remain individually reachable by manual exploration; secondary Show controls remain one bounded strip. |
| A11 | Manual vertical exploration survives rerender and horizontal away/back; retained-offscreen pure case. |
| A12 | Selected root may leave viewport while inspector persists; four-direction/manual exploration proof. |
| A13 | Clear retains current user matrix; delayed callbacks cannot restore an earlier camera. |
| A14 | Replacement selection during cleanup and full-page focal/stale exact-response scenarios. |
| A15 | Four-direction cues and actual boundary clipping, including inspector and displayed-motion geometry. |
| A16 | Same-tier selected/linked height growth, 36-unit collision counterexample, dense overflow and oversized tail native action. |
| A17 | 57 fixture and 21 full-app captures, readable 12px headings, long/empty content, three dock modes/widths and 125% text stress. |
| A18 | 38 motion-enabled core recordings; six-axis interpolation/interruption/hit-target proof and reduced motion. |
| A19 | Logic cycle/sibling cases; Artifact zero-link, System/no-Case, Customer N/A and exact kinds; Inside Case/Procedure/execution exact-revision, missing/stale scenarios. |
| A20 | Complete 41 Digital Thread page tests, five smoke journeys, ten production tests; touch and keyboard/native actions, view switching, table/back and refresh retained. |

## Exact-head validation

Dependencies installed with `npm ci` in the owned checkout; lockfile unchanged. Final clean head passes `npm run lint` (existing warnings), `npm run build`, `npx playwright test --config=playwright.logic.config.ts` **89/89**; rendered core plus Artifact **84/84**; additional Network and Inside rendered adapters **39/39**. JSON reports are included. Separate motion-enabled core rerun **38/38**, videos retained.

Full application: unchanged `tests/digital-thread-page.spec.ts` **41/41**, smoke **5/5**, plus external all-view visual journey **1/1**, run together **47/47** using the existing configuration and disposable SQLite setup. External config changes test discovery/report/video/cwd only. `AEROLINK_E2E_RUN_ID=astra-1022-7d1a8c58-full-20260912`, owned API 5106/client 5207. Production suite **10/10**, separate run `astra-1022-7d1a8c58-production-20260912`, API 5108, exact candidate build. Servers shut down normally. No persistent HOME or demo acceptance is claimed.

Final changed-area planner: `powershell.exe -NoProfile -ExecutionPolicy Bypass -File product/scripts/Get-AeroLinkTestPlan.ps1 -Base origin/main -Head HEAD -Explain -DryRun`; no unknown paths, client/browser/smoke/production qualification selected, CI script contracts remain for protected Full CI. `Test-RepositoryLayout.ps1` passes including 22 root launchers. Exact SHA/clean status recorded after long runs. Protected Full CI and queue proof are not yet requested and are not implied by local results.

## Failure history (not discarded or called flaky)

The audit's DeepSeek head had page 40/1 native-action occlusion and core 18/2 promotion/motion-precondition failures. Owned red proof reproduced the normal native-action interception and promotion above the independently measured usable frame. CORE rounds 1/2 required changes: explicit reveal used intrinsic rather than usable bounds, selected height growth bypassed reconciliation, and fixture document scrolling invalidated apparent stationary-card proof. The latter was independently measured as window.scrollY 20→5 with identical canvas matrix; an adequate fixture viewport fixed the test environment, then round 3 passed.

At 4ed42a9a, logic/build/rendered passed but full page was 40/1: Inside second-card access failed. The visual matrix also exposed partial/tall-card invisibility. Intermediate repair passed Inside but exposed Artifact stale clipping under real StrictMode. Independent inspection confirmed cancelled animation refs were not reset on replay. A new tall-card test first used the wrong fixture gesture (wheel zoom); corrected lane drag then proved a real scroll-floor defect: native tail at y681..738 could not fully reach a viewport ending at700. Oversized measured heights now contribute to the scroll limit; fresh tests pass. Partial-card assertions now first prove visible DOM intersection and then retain strict full-clearance assertions after explicit reveal.

An external full-app config attempt at 95d82f8f failed module loading before test collection; it is not counted as a test pass. The corrected harness ran all 47 tests at the final head. An initial video decode attempted a video-disabled run and found no file; the explicit final 38-test video-enabled rerun supplies the actual recordings. No force-clicks, timeout increases or weakened native-action assertions were used.

All earlier result/report/log directories remain locally under `C:\Users\seanm\AppData\Local\Temp\astra-1022-implementation-20260912`; selected historical diagnostics already appear in the parent evidence packet. Raw traces/API logs remain local to avoid publishing session material. There are no unresolved observed browser failures at this candidate; independent gate verdicts and protected CI are still pending.

(Retained copy of the exact Checkpoint D Round 2 packet submitted to Astra via Sean on 2026-09-22.)

ASTRA REVIEW REQUEST — #1040 — CHECKPOINT D — ROUND 2

**Requested decision and next action:**
Review the completed D1040-D01 correction (installed-task realignment) and the D1040-D02 recovery-plan correction. On PASS, the precise next action requested is: authorize queue admission for PR #1066 at the exact head below — remove draft, enter the AeroLink merge queue (auto-merge remains disabled; admission is the authorized manual step) — and after the composed candidate passes the queue's checks, complete the merge. Checkpoint E will then verify the supported HOME deployment before any closure proposal.

**Exact candidate and status:** HEAD `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71` (merge of main `329f0757` — 33 commits over the reviewed baseline `b64301b1`, including main's own #1064–#1083 commits), worktree clean (verified before and after all D01 correction runs), branch pushed. Baseline `b64301b168ed98d4054cb893932978c4d090f745`.

**D1040-D01 — DISPOSITION: installed tasks realigned to the PT135M contract via the supported installer from the dedicated production source; readback and exported definitions retained.**
Executed elevated (one UAC-consented run; script retained at `d01-correction/align-task-limits.ps1`, transcript + readback JSON in the same directory):
1. Modules imported from the DEDICATED production source `C:\Sean Project\AeroLink Production` (clean at `3eafdea3`, the current approved main — which carries `AeroLinkInstalledTaskTimeLimit = 'PT135M'` at module line 135).
2. `Install-AeroLinkReconcileTask -Config $config` and `Install-AeroLinkRemoteDemoTask -Config $config` — the supported configuration installers — re-registered both tasks from the dedicated source's task XML (`schtasks /Create /TN … /XML … /F`), each printing SUCCESS and binding to the dedicated source (`AeroLinkRemoteDemoRecovery  S4U  C:\Sean Project\AeroLink Production`).
3. Dedicated-source binding, principal (`seanm`, S4U, Limited), `MultipleInstances=IgnoreNew`, and the recovery policy are preserved by construction — the installers regenerate from the module's own XML templates, which embed all of these; nothing else about the tasks was touched. Both tasks idle/Ready throughout; no process was killed; no transition was active.

**Readback (from `task-readback.json` + live Get-ScheduledTask):**
- `AeroLinkProductionSourceReconcile`: State=Ready, **Limit=PT2H15M**, LogonType=S4U, RunLevel=Limited, Account=seanm, MultipleInstances=IgnoreNew, Enabled=True, time trigger, action = powershell.exe → dedicated source's AeroLinkRemoteDemo.ps1.
- `AeroLinkRemoteDemoRecovery`: State=Ready, **Limit=PT2H15M**, LogonType=S4U, RunLevel=Limited, Account=seanm, MultipleInstances=IgnoreNew, Enabled=True, boot trigger (MSFT_TaskBootTrigger), action = powershell.exe → dedicated source's AeroLinkRemoteDemo.ps1.
PT2H15M ≡ 135 minutes — the shipped contract value; the qualification fixture itself accepts this spelling (its production-limit check accepts `PT2H15M` or `PT135M`).

**Disposable-twin launch-context qualification (per D finding, completed):** the supported fixture (`Invoke-HostedProcessQualification.ps1`) is a GitHub-hosted-runner acceptance lane that refuses operator machines by design ("Records bind this VM's principal and context; … never copied to another installation as qualifications"). Per the finding's alternative, the qualifying receipts were produced by dispatching `process-qualification.yml` (`workflow_dispatch`, run [35729937556](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/35729937556)): **all three matrix jobs (Recovery, Reconcile, FirstDeployment) succeeded**, receipts retained and downloaded into `ci-qualification-receipts/` — Recovery receipt descriptor hash `47E8C805…`, verdict **Qualified**; Reconcile receipt `EC181683…`, verdict **Qualified**; FirstDeployment receipts `3EC989F1…`/`CD40AADF…`. These receipts bind the runner's principal/context and the candidate source at the qualification-time head — exactly as the fixture designs — and are the CI-side acceptance evidence for the realigned PT135M descriptors.

**HOME health after the correction:** `GET /health/identity` → mode HOME-PRODUCTION, healthy/database-connected, source `3eafdea3` (mainCurrency Current). Both tasks State=Ready/Enabled. Nothing else on the installation was touched.

**D1040-D02 — DISPOSITION: rollout/recovery plan corrected — failure phases distinguished.**
The Round-1 packet's blanket "persistent state untouched" claim is WITHDRAWN. Corrected statement, per phase:
1. **Pre-application refusal** (analyze exits non-10/20, or the isolated-copy upgrade/serving proof fails): persistent state IS untouched — the launcher provably reaches nothing real before the isolated-copy proof succeeds; exit codes 20/30 and the retained analyze/upgrade logs are the evidence surface.
2. **Failure during real application** (the real upgrade runs and fails mid-application): persistent state is NOT guaranteed untouched — the retained artifacts are the failed-phase logs, the verified pre-upgrade backup identity, and the observed database/runtime state. The launcher retains the backup and refuses unsuccessful startup; that is containment, not proof of restoration. Recovery from this phase is the attended DEC-045 production restore (separate elevated switch, exact confirmation phrase) — an operator action, never performed unattended by this task.
3. **Failure after application, during restart/serving**: the new schema IS applied; the launcher refuses to serve an upgrade it cannot prove (startup contract), retaining the backup and logs; recovery to the pre-upgrade state is again the attended DEC-045 restore from the verified pre-upgrade backup.
Across all phases: the verified backup is the RECOVERY POINT — never evidence that a rollback completed — and every phase retains its logs, backup identity, and observed schema/runtime state for Astra's inspection at Checkpoint E.

**Post-correction HOME/PR/queue state (fresh):**
- Both tasks: State=Ready, Enabled, PT2H15M, S4U/seanm/Limited, IgnoreNew — realigned, coordinator-informed (the correction ran while both tasks were idle; no transition was active; no process was killed).
- HOME-PRODUCTION healthy at source `20242739…` — main advanced and auto-deployed again during this round (three deployments observed today, all automatic, all through the supported path); #1040's migration is NOT on the HOME database (PR #1066 unmerged).
- PR #1066: head `027c985e`, draft, MERGEABLE/CLEAN against main `329f0757`, auto-merge off, no queue entries.
- Qualification receipts: run 35729937556, all three definitions Qualified, retained as CI artifacts and downloaded (`ci-qualification-receipts/`).

**R01–R12:** R01–R10 complete (as accepted at C Round 3). R11: Full Product gate green at the exact head (run 35718088602), trusted binding published, **D01 installed-configuration correction now complete with realignment + receipt + readback evidence**, supported upgrade/rollback posture documented with failure-phase distinctions — the remaining R11 item is the queue admission this packet requests. R12 — not started (E).

**Remaining limits:** pre-existing showcase spec not repetition-safe (independent, demonstrated; CI single-shot unaffected); no clean exact-final-head full local API-suite rerun (the just-completed Full Product run at this head is the authoritative broad evidence); HOME deployment behavior after merge is the supported automatic path (documented above), with E verifying the result.

**Recommended verdict and why:** PASS for Checkpoint D — the D01 blocker is corrected on the installation through the supported installer with the PT135M contract, preserved bindings/principal/policy, readback and exported definitions retained; the recovery plan now distinguishes failure phases with the backup as recovery point; the queue/HOME state is fresh and clean; and nothing outside the bounded operational correction was changed.

Please independently return PASS, CHANGES REQUIRED, or NOT REVIEWABLE, identify the exact SHA/design and scope reviewed, list actionable findings, and state which next phase may proceed. I will wait for Sean to relay your actual response.

CHECKPOINT D — AWAITING ASTRA REVIEW. Queue admission, merge, rollout and issue closure remain gated.

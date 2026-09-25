# Issue #1040: cloud takeover package

This is an Astra-prepared, historical handoff published at Sean's request for Claude running in the cloud. It provides repository-accessible evidence and instructions. It does not change either implementation PR, grant a merge exception, or constitute deployment acceptance. Refresh live GitHub state before acting.

## Read in this order

1. [Claude's cloud takeover instructions](CLAUDE-CLOUD-PROMPT.md).
2. [Astra's corrections to GLM's handoff](ASTRA-CORRECTIONS.md).
3. [Code and acceptance map](IMPLEMENTATION-AND-ACCEPTANCE.md).
4. [Exact CI runs and artifact access](CI-RUNS.md).
5. [Windows/HOME hand-back boundary](LOCAL-OPERATOR-HANDBACK.md).
6. [Publication and redaction policy](PUBLICATION.md).

## Code is already on GitHub

| Workstream | PR and exact starting head | Remote branch |
| --- | --- | --- |
| Release picker, issue #1040 | [#1066](https://github.com/AeroLinkDEV/requirements-management-tool/pull/1066), `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71` | `glm/1040-release-picker-snapshot` |
| Separate trusted-requester maintenance | [#1098](https://github.com/AeroLinkDEV/requirements-management-tool/pull/1098), `23bb25d987639ada3356a7003fa0380ba08e5042` | `ci/request-full-ci-same-head-retry` |

Protected main at publication preparation: `0dddb0291420ee3630314bba8b9a5a3e6d367e95`. Both PRs were draft, auto-merge disabled, with their historical `ready-for-full-ci` labels still present. Their cited exact-head Product gates were failed. [Current issue](https://github.com/AeroLinkDEV/requirements-management-tool/issues/1040) remains the acceptance authority; its full body and PR/run records are copied in [github-snapshot](github-snapshot/).

## Finding evidence without Windows access

- [TEST-RESULTS.json](TEST-RESULTS.json) indexes 29 retained TRX files, including test names, outcomes and counters. Each entry links its published relative path. A retained run is historical evidence; its presence does not prove current-head qualification.
- [PUBLICATION-MANIFEST.json](PUBLICATION-MANIFEST.json) maps every inventoried GLM source path and the supplementary Astra evidence to its published path, original hash, derived-copy hash, or omission reason. Some long Windows paths were shortened deterministically.
- [evidence](evidence/) contains the historical packets, provider tests/logs, SQL plans, caller-model sources, local qualification receipts, failed CI evidence, proposals and preserved incident material.
- [astra-evidence](astra-evidence/) adds the independent requester-test logs, fixture audits, failed-gate analysis, corrected geometry and rehearsal audit.
- [diagnostic-summaries](diagnostic-summaries/) contains safe error contexts and action summaries derived from browser archives. Raw network/session payloads are excluded from public Git; original CI artifact retrieval is documented separately.
- [visual-evidence](visual-evidence/) contains three pixel-reviewed screenshots: the #1098 CI failure, corrected local geometry, and #1066 authentication wait.
- [SHA256SUMS](SHA256SUMS) verifies the published package. The sums file excludes itself.

All archived scripts have `.txt` appended. They are evidence to inspect, not launchers to execute. In particular, the rehearsal scripts caused a real local-checkout incident and are not approved tooling.

## Critical cloud distinction

The damaged canonical checkout is on Sean's Windows machine. It is not Claude's cloud checkout. Cloud Claude can inspect GitHub code, implement within approved scope, qualify disposable state, and prepare review packets immediately. It cannot truthfully repair that Windows checkout or verify HOME without a local operator. Prepare a concrete hand-back for Astra/Sean; do not block independent cloud work waiting for access that the cloud does not possess.

This handoff branch is evidence-only. Do not merge it into either implementation branch as a delivery shortcut.

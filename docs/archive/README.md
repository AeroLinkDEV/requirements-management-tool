# AeroLink historical archive

This directory preserves dated handoffs, completed audits, superseded status snapshots, agent work logs, and completed delivery/review reports that remain useful as historical evidence but are **not current product authority**.

For current product truth, use [`../../PROJECT_STATE.md`](../../PROJECT_STATE.md). For accepted decisions, use [`../../DECISIONS_AND_OPEN_QUESTIONS.md`](../../DECISIONS_AND_OPEN_QUESTIONS.md). For active work and current status, use GitHub Issues and Pull Requests.

The files below were moved from repository root by issue #781 after the canonical front door was established by #780 / PR #784. Their historical contents are intentionally preserved. Keeping the archive flat avoids gratuitous folder depth and preserves links among historical records that already referred to one another by filename.

Three small compatibility-pointer files — `PROJECT_STATE.md`, `DECISIONS_AND_OPEN_QUESTIONS.md`, and `FEATURE_CATALOG.md` — are **not archived snapshots**. They exist only so historical documents that originally linked to those living root files by filename still resolve after the move. Each pointer redirects explicitly to the maintained root authority.

| Document | Historical checkpoint / original role | Status now | Current authority | Still useful for |
| --- | --- | --- | --- | --- |
| [`PRODUCT_REVIEW_2026_07_26.md`](PRODUCT_REVIEW_2026_07_26.md) | 2026-07-26 product review | Historical review | `PROJECT_STATE.md`, decisions, GitHub | Early product assessment and gaps |
| [`AUTONOMOUS_BACKLOG_HANDOFF_2026-07-28.md`](AUTONOMOUS_BACKLOG_HANDOFF_2026-07-28.md) | 2026-07-28 autonomous-work handoff | Historical handoff | GitHub Issues | Restart context and sequencing rationale |
| [`CURRENT_PRODUCT_HANDOFF_2026-07-29.md`](CURRENT_PRODUCT_HANDOFF_2026-07-29.md) | 2026-07-29 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CURRENT_PRODUCT_HANDOFF_2026-07-31.md`](CURRENT_PRODUCT_HANDOFF_2026-07-31.md) | 2026-07-31 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CURRENT_PRODUCT_HANDOFF_2026-08-01.md`](CURRENT_PRODUCT_HANDOFF_2026-08-01.md) | 2026-08-01 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CURRENT_PRODUCT_HANDOFF_2026-08-02.md`](CURRENT_PRODUCT_HANDOFF_2026-08-02.md) | 2026-08-02 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CURRENT_PRODUCT_HANDOFF_2026-08-03.md`](CURRENT_PRODUCT_HANDOFF_2026-08-03.md) | 2026-08-03 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CURRENT_PRODUCT_HANDOFF_2026-08-04.md`](CURRENT_PRODUCT_HANDOFF_2026-08-04.md) | 2026-08-04 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CURRENT_PRODUCT_HANDOFF_2026-08-05.md`](CURRENT_PRODUCT_HANDOFF_2026-08-05.md) | 2026-08-05 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CURRENT_PRODUCT_HANDOFF_2026-08-06.md`](CURRENT_PRODUCT_HANDOFF_2026-08-06.md) | 2026-08-06 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CODEX_AUG_7_8_AUDIT_REMEDIATION_HANDOFF.md`](CODEX_AUG_7_8_AUDIT_REMEDIATION_HANDOFF.md) | 2026-08-07/08 audit/remediation handoff | Historical audit handoff | Current code/tests, decisions, GitHub | Root-cause and remediation context |
| [`DEEPSEEK_WORK_LOG.md`](DEEPSEEK_WORK_LOG.md) | Agent work log beginning 2026-08-07 | Historical agent log | GitHub Issues/PRs | Session-level implementation provenance |
| [`CURRENT_PRODUCT_HANDOFF_2026-08-08.md`](CURRENT_PRODUCT_HANDOFF_2026-08-08.md) | 2026-08-08 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CURRENT_PRODUCT_HANDOFF_2026-08-09.md`](CURRENT_PRODUCT_HANDOFF_2026-08-09.md) | 2026-08-09 current-state checkpoint | Historical handoff | `PROJECT_STATE.md` | Exact state/restart context at that checkpoint |
| [`CURRENT_PRODUCT_HANDOFF_2026-08-10.md`](CURRENT_PRODUCT_HANDOFF_2026-08-10.md) | 2026-08-10 former current restart snapshot | Historical handoff | `PROJECT_STATE.md` | #364/#365/#367-era qualification and restart context |
| [`AEROLINK_3_IMPLEMENTATION_STATUS.md`](AEROLINK_3_IMPLEMENTATION_STATUS.md) | 2026-08-10 AeroLink 3 implementation scorecard | Superseded status snapshot | `PROJECT_STATE.md`, GitHub Issues | Enterprise-program checkpoint and evidence links |
| [`MASSIVE_ENTERPRISE_UPDATE_REPORT.md`](MASSIVE_ENTERPRISE_UPDATE_REPORT.md) | Completed enterprise delivery report | Historical delivery report | Current code/tests, `PROJECT_STATE.md` | Delivered-increment provenance and original validation record |

## Archive rules

- Do not use these files to decide whether an issue is open, a PR is merged, or a route/state exists today.
- Do not rewrite historical claims to make them match today's architecture. If a historical statement is now stale, that staleness is part of the record.
- Minimal link/path fixes are acceptable when repository reorganization would otherwise make a historical reference unusable; semantic rewriting is not.
- New active findings belong in GitHub Issues. New current product truth belongs in `PROJECT_STATE.md`. Durable lessons belong in `../ENGINEERING_LESSONS.md`.

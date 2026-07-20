# Universal Controlled Editing

Issue #31 extends AeroLink's renewable exclusive edit-session contract beyond SCR/SWCR drafts.

## Shared lease API

The `/api/controlled-editing` endpoints provide one policy-aware implementation for:

- supported-family discovery;
- authoritative project and lifecycle-state resolution;
- read-only lock status;
- exclusive checkout with database-enforced lock keys;
- resumable server snapshots;
- version-checked autosave;
- heartbeat and expiry;
- discard without controlled-content mutation; and
- administrator/configuration-manager forced unlock with audit evidence.

## Connected adapters

This increment resolves and snapshots the controlled families that already have authoritative records:

- change requests;
- requirement proposals inside Draft SCR/SWCR records;
- requirement specification structures;
- test procedures and exact procedure revisions;
- requirement trace links; and
- candidate-baseline release planning.

## Remaining check-in adapters

A lease never changes controlled content by itself. Atomic check-in remains family-specific because each aggregate owns its validation and immutable-history rules. The next increments connect check-in adapters for the existing six families, then add problem-report, configuration-change-set, and controlled-template adapters when those first-class lifecycle models are introduced by issues #32, #33, and #36.

Issue #31 must remain open until every family passes two-user contention, recovery, stale-version, forced-unlock, expiry, and immutable-history acceptance journeys.
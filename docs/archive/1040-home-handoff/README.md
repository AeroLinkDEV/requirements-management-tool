# Issue #1040: HOME-PC hand-off (evidence-only branch; never merge)

Cloud Claude's hand-off for the final local steps of #1040. The feature (#1066) is merged as `92ffbb6c`.

Start here:

1. **[HANDOFF-PROMPT.md](HANDOFF-PROMPT.md):** the instructions for the local Claude session.
2. **[STATE.md](STATE.md):** verified identities, runs and incident facts at hand-off.
3. **[LOCAL-RECOVERY-HANDBACK.md](LOCAL-RECOVERY-HANDBACK.md):** the canonical-checkout recovery procedure in detail, with its rehearsal evidence.
4. **[HOME-ROLLOUT-AND-ACCEPTANCE.md](HOME-ROLLOUT-AND-ACCEPTANCE.md):** how HOME applies the migration, and the read-only acceptance.
5. **`scripts/`:**
   - `Audit-1040-CanonicalCheckout.ps1`: read-only.
   - `Recover-1040-CanonicalCheckout.ps1`: Plan mode is read-only; Recover and Rollback make guarded changes.
   - `Test-1040-HomeAcceptance.ps1`: read-only.
6. **`evidence/`:** cloud qualification logs and TRX files, recovery rehearsal logs, the acceptance-script test, and the historical Checkpoint 1 packet.

`SHA256SUMS` covers every file here except itself.

# Claude instructions for AeroLink

Read these before changing the repository:

1. [`AGENTS.md`](AGENTS.md) — canonical cross-agent repository operating/safety contract.
2. [`PROJECT_STATE.md`](PROJECT_STATE.md) — current product architecture and supported behavior.
3. [`DECISIONS_AND_OPEN_QUESTIONS.md`](DECISIONS_AND_OPEN_QUESTIONS.md) — authoritative accepted product decisions.
4. The current GitHub issue/PR being implemented and the relevant technical docs under [`product/docs/`](product/docs/).

Do not treat this file as an independent product-architecture authority. Mutable product facts belong in `PROJECT_STATE.md`; durable repository rules belong in `AGENTS.md`. This file intentionally stays small so Claude-specific instructions cannot drift into a second stale description of AeroLink.

## Claude-specific working notes

- Use an isolated worktree for non-trivial work when another agent may be active.
- Capture exact `HEAD` and dirty status before and after long tests; discard evidence that spans an unexpected checkout/SHA change.
- Do not touch/reset the persistent AeroLink PostgreSQL database or persistent evidence to make qualification convenient.
- Do not move stable root Windows launcher paths without an explicit external-dependency audit.
- Regenerate generated test-contract artifacts from source; never hand-merge them.
- Refresh GitHub immediately before merge/rebase decisions rather than relying on old handoffs or branch counts.
- Keep PRs focused. Put unrelated findings into their own GitHub issues.

For current product behavior, accepted decisions, repository taxonomy, launcher compatibility, migration guidance,
controlled-history invariants, documentation guardrails, and the never-do list, use [`PROJECT_STATE.md`](PROJECT_STATE.md),
[`DECISIONS_AND_OPEN_QUESTIONS.md`](DECISIONS_AND_OPEN_QUESTIONS.md), and [`AGENTS.md`](AGENTS.md). Do not copy mutable
product architecture into this model-specific adapter.

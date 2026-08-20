# Requirements Management Tool Codex working agreement

## Repository safety

- Before editing, confirm the repository root, `origin`, current branch, `git status`, and the relationship to `origin/main`.
- Treat GitHub as the source of truth. Use a focused `codex/` branch and never push directly to `main`.
- Preserve unrelated tracked and untracked work. Do not reset, clean, overwrite, or absorb it into the task.
- Never reset, reseed, delete, or otherwise mutate the persistent AeroLink PostgreSQL demo database on `127.0.0.1:54329`. Use disposable SQLite or PostgreSQL infrastructure for automated tests unless the user explicitly authorizes a specific persistent-data operation.
- External writes, merges, issue closures, destructive operations, and material scope expansion require authority from the user request. The root agent retains final control of these actions.

## Multi-agent routing

- The root Sol agent is the lead architect and orchestrator. It owns requirements understanding, architecture, task decomposition, the implementation plan, success criteria, delegation, difficult decisions, independent verification, and the final response.
- For non-trivial implementation, fix, refactor, or test work, the default route is `Sol -> one coder -> Sol`.
- Sol should inspect enough context to make the plan, then delegate the complete local execution lane to the project `coder` agent. The coder owns repository research, implementation, tests, debugging, and local verification.
- When the spawn tool supports it, pass `fork_turns = "none"`. The delegation message must contain the goal, Sol's plan, constraints, relevant decisions, authority boundaries, success criteria, and required evidence. The coder gathers additional repository context itself.
- Use `researcher` for research or analysis without implementation. Use `browser_debugger` only when browser or runtime evidence is needed. Use `reviewer` only when an independent second opinion materially improves confidence.
- Keep specialist and review agents read-only. Use multiple agents only for genuinely independent workstreams; avoid parallel write-heavy work in the same worktree.
- Never spawn a Sol subagent without explicit user approval for that specific task. Unspecified or ad-hoc subagents use the configured Luna default.
- Sol may complete a genuinely trivial task directly when delegation overhead exceeds the work.

## Lead verification and delivery

- Sol must inspect the final repository status and diff, check that unrelated files remain untouched, and evaluate the actual validation evidence before accepting delegated work.
- For pull requests or integration decisions, verify the exact immutable head SHA, acceptance criteria, relevant review findings, and required checks. Green CI or a subagent summary alone is not sufficient.
- Stop the live demo API before backend builds if it is locking build outputs. Do not run API and Infrastructure .NET builds concurrently in the same worktree when they share output paths.
- Report changed files, validation commands and outcomes, remaining risks, branch and commit state, and any external action taken.

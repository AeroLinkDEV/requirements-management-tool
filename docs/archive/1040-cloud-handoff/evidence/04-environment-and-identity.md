# RMT-1040 evidence directory — environment and identity record

Task: AeroLinkDEV/requirements-management-tool issue #1040 — "Freeze Release relationship-picker candidates across cursor pages"
Agent: GLM (implementing agent). Reviewer: Astra (independent). Relay: Sean.
Record created: 2026-09-21 (local, Windows 10.0.26200 x64, Git Bash).

## Start boundary (verified via GitHub API, 2026-09-21)

- #1048: CLOSED, state_reason=COMPLETED, closedAt 2026-09-21T02:28:54Z
- #1054: CLOSED, state_reason=COMPLETED, closedAt 2026-09-21T02:28:56Z
- PR #1062 (glm/1048-1054-portal-header): MERGED 2026-09-21T01:58:45Z, merge commit f5e687534dbd1cfd3437e67082d2375b4f43271f
- PR #1063 (glm/1048-1054-manifest-fix): MERGED 2026-09-21T09:09:03Z, merge commit b64301b168ed98d4054cb893932978c4d090f745
- Both predecessor issues closed as completed; implementation merged through the protected queue. Start boundary satisfied.

## Repository identity

- Remote origin: https://github.com/AeroLinkDEV/requirements-management-tool.git
- Canonical checkout: C:\Sean Project\Requirements Management Tool — branch main @ c2602d9360af27a333d789b379d379d66d08ff42, clean (left untouched; 2 commits behind origin/main)
- Refreshed protected main (origin/main) @ b64301b168ed98d4054cb893932978c4d090f745 (2026-09-21 08:43:10 +0000)
- Owned worktree: C:\Sean Project\RMT-1040-worktree — branch glm/1040-release-picker-snapshot @ b64301b168ed98d4054cb893932978c4d090f745, clean at creation
  (branch name follows the glm/<issue>-<slug> convention used by the merged #1048/#1054 PRs; the handoff's
  "codex/1040-release-picker-snapshot" name was offered as an alternative to the agreed convention)
- No pre-existing #1040 branch or worktree existed; nothing owned by another agent was moved or cleaned.

## Owned disposable resources (this checkpoint)

- Evidence directory: C:\Sean Project\RMT-1040-glm-evidence (outside product/.local and outside the repository)
- Disposable PostgreSQL: docker container `rmt1040-baseline-pg`, image postgres:17, published 127.0.0.1:54331
  (NOT the persistent 54329/aerolink database; container is removed after the probe)
- Probe throwaway databases: `aerolink_1040_probe_<guid>` created on and dropped from the container server by the probe itself

## Protected state NOT used for destructive or mutation testing

- Persistent PostgreSQL 127.0.0.1:54329/aerolink (untouched)
- product/.local evidence (untouched)
- Other agents' worktrees/branches (untouched)

## Tooling observed

- docker 29.6.1 (Linux containers); dotnet SDK 10.0.301; gh CLI authenticated

## Files in this directory

- 01-issue-1040-state.json / 01-issue-1040-body.md — live issue body and state as read 2026-09-21
- 02-issue-1040-comments.json — issue comments (one comment, 2026-09-15, historical advice per revised body)
- 03-baseline-probe-run.log — disposable-PostgreSQL baseline reproduction console output
- 04-environment-and-identity.md — this record

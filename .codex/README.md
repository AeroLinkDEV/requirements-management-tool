# AeroLink Codex project configuration

This directory contains project-scoped Codex routing only. The root `AGENTS.md` remains the canonical repository, product-safety, testing, and merge contract; these files intentionally do not duplicate it.

## Defaults

- The primary/root agent remains whatever model the user selects or their user-level Codex configuration selects.
- Unspecified spawned subagents default to `gpt-5.6-luna` at `xhigh` reasoning.
- At most three spawned-agent threads run concurrently in one session.
- `coder`: Luna xHigh, workspace write, bounded implementation and verification.
- `researcher`: Luna xHigh, read-only research and repository analysis.
- `browser_debugger`: Luna xHigh, read-only browser/runtime evidence collection.
- `reviewer`: Terra High, read-only independent review.

This keeps the normal lead-agent workflow lightweight while making the implementation and review lanes reproducible across trusted checkouts. It does not pin the root model, model provider, approval policy, global sandbox, MCP servers, or credentials.

## Typical use

- `Implement issue #123; delegate the bounded implementation to coder and verify its result.`
- `Use researcher to investigate this without changing code.`
- `Use browser_debugger to reproduce this UI failure and collect evidence.`
- `Have reviewer independently review the exact candidate diff.`
- `Do this directly without subagents.` for a one-task override.

Codex loads project `.codex/` configuration only for trusted projects, and project settings override user-level settings for the keys defined here. Start a fresh Codex task after changing these files so the new project configuration and custom-agent definitions are loaded.

No local Chrome DevTools MCP endpoint is configured here; browser debugging uses capabilities already available in the active Codex session.

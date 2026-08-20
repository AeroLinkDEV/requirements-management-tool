# Requirements Management Tool Codex team

This repository carries a project-scoped Codex team. Codex discovers the configuration and agent roles whenever a new task starts with this repository as its primary folder.

## Default workflow

For a non-trivial implementation request, the root Sol agent plans the work, delegates the complete local execution lane to one Luna `coder`, independently checks the result, and reports back. Research, browser debugging, and independent review are optional specialist lanes rather than mandatory stages.

You can ask naturally:

- `Implement GitHub issue #123 using our normal Sol -> coder -> Sol workflow.`
- `Use the researcher to analyze this behavior without changing code.`
- `Have the browser debugger reproduce this UI failure and collect evidence.`
- `Use the reviewer for an independent review of the exact diff.`
- `Do this directly without subagents.` when you want to override the default for one task.

## Agent roster

| Agent | Model | Effort | Default access | Purpose |
| --- | --- | --- | --- | --- |
| `coder` | `gpt-5.6-luna` | `xhigh` | workspace write | End-to-end local implementation, tests, debugging, and verification |
| `researcher` | `gpt-5.6-luna` | `xhigh` | read-only | Research and analysis without implementation |
| `browser_debugger` | `gpt-5.6-luna` | `xhigh` | read-only | Browser reproduction and runtime evidence |
| `reviewer` | `gpt-5.6-terra` | `high` | read-only | Independent risk and regression review |

The root agent remains on the model selected by the user or global Codex configuration. Project defaults ensure that unspecified subagents use Luna rather than inheriting Sol.

## Loading changes

Codex loads `AGENTS.md` and project agent configuration when a task starts. After changing these files, start a new task in this repository before testing the updated routing.

The browser debugger uses the Browser or Chrome capabilities available in the active Codex session. This repository intentionally does not configure a `localhost:3000` Chrome DevTools MCP endpoint because no such server is part of the project setup.

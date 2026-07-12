# AeroLink FMS 3.3 Interactive Showcase

This is a local, fictional-data prototype for demonstrating the planned AeroLink aerospace development-assurance experience. It is a Phase 0.5 design-validation artifact, not a production application.

## Included Story

- Manager and System Engineer dashboards
- FMS Software 3.2 to 3.3 release context
- Round Robin SCR with system requirements and allocated HLRs
- Sequential, author-selected approval workflow
- Future-approver replacement and cancelled/restarted review cycle
- Four-PR corrective SCR
- Verification coverage gap
- Blocked execution, corrected procedure, retest evidence, and human-approved Pass
- Candidate-baseline readiness
- Interactive end-to-end traceability

All displayed records and values are fictional.

## Run Locally

Requirements: Node.js 22.13 or newer.

```powershell
cd "C:\Sean Project\Requirements Management Tool\showcase"
npm install
npm run dev
```

Open `http://127.0.0.1:3000/` or the local URL printed by the development server.

## Validate

```powershell
npm test
```

The test command creates a production build and checks the server-rendered AeroLink shell and removal of starter artifacts.

## Prototype Boundaries

This showcase intentionally has no production database, persistent multi-user state, authentication, authorization, electronic signatures, controlled document generation, external integrations, deployment configuration, or compliance claims.

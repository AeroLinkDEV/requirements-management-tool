# Public export policy and integrity

Sean explicitly requested GitHub publication so cloud Claude can reach the handoff. The destination repository is public. This evidence-only branch is based on verified protected main and was prepared in a new independent clone; neither implementation head nor canonical local main was pushed or repaired.

## Included

- Corrected cloud instructions, code/acceptance map, authority boundaries and local operator hand-back.
- Current GitHub issue/PR/run snapshots and stable source/CI links.
- Derived text copies of the GLM evidence inventory: packets, logs, SQL plans, TRXs, receipts, proposals and offline probe sources.
- Supplementary top-level Astra independent review logs, fixture audits, corrected geometry and rehearsal evidence.
- A byte-identical bounded incident preservation bundle, retained as data rather than executable branch history.
- Safe error/action summaries derived from diagnostic archives, plus three pixel-reviewed synthetic-test screenshots.

## Deliberately not blindly republished

Credentials, session/browser storage, private-key material, identifiable machine SIDs and detected sensitive fields are removed from text copies. Raw browser archives/network payloads, unreviewed images, generated HTML reports, cache binaries and transient ownership/lock files remain outside public Git. The manifest records omissions and original hashes; original CI artifact access is documented in CI-RUNS.md. No database or persistent evidence store was accessed for this export.

Redaction is a public-copy transformation, not a correction to historical results. Original local artifacts remain unchanged. Some JSON-sensitive values and fixture tokens are redacted, so archived probe copies must not be mistaken for executable qualification inputs. Use the reviewed source commit for tests. Scripts are additionally renamed with `.txt` and remain historical only.

## Verification

PUBLICATION-MANIFEST.json maps source paths to published paths and separately records source/published SHA-256 values. Some long paths are mapped into `evidence/long-paths/`; this is a portability measure, not evidence deletion. TEST-RESULTS.json indexes the 29 retained TRX files. REDACTION-SUMMARY.json records transformations by category without exposing removed values.

SHA256SUMS covers every published package file except itself. From this directory in Linux, `sha256sum --check SHA256SUMS` verifies the export. These hashes establish the published copy's integrity; they do not turn a failed run, an old candidate, or an unapproved proposal into current acceptance.

The archived GLM MANIFEST.csv is retained as historical evidence and is known to be incomplete/malformed. It is not the integrity manifest for this publication.

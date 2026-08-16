# Fast CI phase 1 — advisory measurement

This phase measures a bounded Fast pull-request lane without changing merge authority or the existing Product quality gate.

- The existing `.github/workflows/ci.yml` remains unchanged and continues to run the Full gate.
- `.github/workflows/fast-pr-feedback.yml` is advisory and runs in independent `fast-pr-<number>` concurrency.
- `fast-ci-manifest.json` is the versioned source of the selected Fast smoke surface.
- Persistent PostgreSQL and `product/.local` evidence are forbidden in Fast.
- Full Domain tests are retained because their execution cost is negligible once the solution is built.
- The Infrastructure smoke is deliberately limited to reviewed persistence/concurrency/migration-model sentinels that use temp/in-memory SQLite or a non-connecting model comparison.
- The hosted API smoke is `SharedHostIsolationTests`, which uses a disposable shared SQLite host with unique logical data and fresh clients.
- Client Fast runs `npm ci`, lint, and type-check only.
- Browser, PostgreSQL, complete API/Infrastructure, and operator/recovery evidence remain Full-only during phase 1.

Phase 1 is successful only if the real Windows Actions run demonstrates useful early feedback and the Fast aggregate normally lands within the 240-second target. No branch-protection or cadence change should be made from static estimates alone.

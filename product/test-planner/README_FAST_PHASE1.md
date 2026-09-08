# Fast CI phase 1 — advisory measurement

This phase measures a bounded Fast pull-request lane without changing merge authority or the existing Product quality gate.

- The existing `.github/workflows/ci.yml` remains unchanged and continues to run the Full gate.
- `.github/workflows/fast-pr-feedback.yml` is advisory and runs in independent `fast-pr-<number>` concurrency.
- `fast-ci-manifest.json` is the versioned source of the selected Fast smoke surface.
- Persistent PostgreSQL and `product/.local` evidence are forbidden in Fast.
- Full Domain tests are retained because their execution cost is negligible once the solution is built.
- The Infrastructure smoke is deliberately limited to reviewed persistence/concurrency/migration-model sentinels that use temp/in-memory SQLite or a non-connecting model comparison.
- The hosted API smoke is `SharedHostIsolationTests`, which uses a disposable shared SQLite host with unique logical data and fresh clients.
- Client Fast runs `npm ci`, lint, type-check, route/identity parity, the explicit logic tier, Chromium installation,
  a subprocess isolation meta-test, and the explicit rendered-fixture tier selected by
  `product/client/fast-client-tests.json`.
- The rendered fixtures use Vite and Chromium without an API or showcase seed. Integrated browser journeys,
  PostgreSQL, complete API/Infrastructure, and operator/recovery evidence remain Full-only during phase 1.
- Every selected identity remains in Full. The routing check rejects missing, duplicate or substituted Fast
  identities and records every unselected file as Full-only.

Phase 1 is successful only if the real Windows Actions run demonstrates useful early feedback and the Fast aggregate normally lands within the 240-second target. No branch-protection or cadence change should be made from static estimates alone.

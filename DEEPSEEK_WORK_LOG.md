# DEEPSEEK Work Log

Persistent status record for DeepSeek-session work on AeroLink. Entries are appended, never overwritten.

---

## 2026-08-07 15:57 EDT — Confirmed production-routing defect; fix in progress (issue #378)

**Branch:** `codex/production-routing-405` @ `b930189` (based on `origin/main` @ `67614d5`)

**Diagnosis (accepted by the owner):** In the production-served shape (API serving the built client), an
authenticated `POST /api/test-procedures` with a valid CSRF token returned `404 {"error":"No such endpoint.",
"code":"endpoint_not_found"}` instead of the `405 Method Not Allowed` DEC-103 requires. The API test host
passed because it never registers the client fallback.

**Root cause:** `ClientHosting.UseAeroLinkClient` registered `MapFallback`, whose endpoint carries no
HTTP-method constraint and therefore swallows the framework's automatic 405 for wrong-method requests to
existing API paths. Verified live against the running production build (`67614d5`) and reproduced in a new
integration test that enables `Client:StaticFiles` (the production-served shape).

**Fix (committed, not yet pushed):**
- Replaced the all-method `MapFallback` endpoint with terminal middleware that only answers when no endpoint
  answered: framework automatic 405 passes through, unknown API paths keep the JSON `endpoint_not_found`
  404 contract, and non-API deep links still receive `index.html` (explicit 200, because the endpoint
  execution middleware leaves a headerless 404 behind when nothing matches).
- No fake `POST /api/test-procedures` endpoint was added; DEC-103 is unchanged.

**Regression coverage:** `ProductionRoutingTests` (new) exercises the served shape with the client fallback
active: CSRF boundary (400 without token), POST collection → 405 with Allow GET, GET collection functional,
unknown API path → 404 `endpoint_not_found`, former approval route absent (404), deep link → 200 `index.html`.
Failed on `67614d5` with 404; passes with the fix.

**GitHub issue:** https://github.com/seanmccarthyns/requirements-management-tool/issues/378

**Gates so far:**
- Focused regression + existing DEC-103 test: PASS (2/2).
- Full backend suite: PASS — Domain 275 / Infrastructure 194 / API 232 (701 total).
- Client type-check: PASS. Client lint: PASS (only the known pre-existing `ChangeRequestEditor` warning).
- Production-build journeys: pending (running from a disposable worktree so the running production Release
  binaries are not touched).

**Safety:** The sole persistent PostgreSQL database was not accessed by any test or build. Tests used
throwaway SQLite files; the production gate will run on a disposable worktree with its own temp SQLite and
port 5086. No merge has been performed or requested.

---

## 2026-08-07 16:05 EDT — Defect fix complete: draft GitHub pull request open, awaiting owner authorization

**Issue:** https://github.com/seanmccarthyns/requirements-management-tool/issues/378

**Branch:** `codex/production-routing-405`

**Commit (pushed):** `b93018937a9386ca8c8af18078e617f2a158c98b` — "Let the served production shape answer
405 for disallowed methods (#378)"

**GitHub pull request (DRAFT):** https://github.com/seanmccarthyns/requirements-management-tool/pull/379

**Files changed:**
- `product/src/AeroLink.Api/ClientHosting.cs` — replaced the all-method `MapFallback` endpoint with terminal
  middleware that answers only when no endpoint (including the framework's automatic 405) answered.
- `product/tests/AeroLink.Api.Tests/ProductionRoutingTests.cs` — new production-served-shape regression.
- `product/tests/AeroLink.Api.Tests/SecurityBoundaryTests.cs` — test factory gains optional `staticFilesRoot`.

**Exact root cause:** `ClientHosting.UseAeroLinkClient` registered `app.MapFallback(...)`. A fallback endpoint
carries no HTTP-method constraint, so routing sent wrong-method requests to existing API paths to the fallback
instead of producing the framework's automatic 405. The API test host saw 405 because it never registers the
client fallback; the deployed shape answered 404 `endpoint_not_found`.

**Exact fix:** Terminal middleware after the pipeline:
- framework automatic 405 passes through (checked via `GetEndpoint()` because the 405 response sets status and
  Allow without writing a body);
- unknown API paths keep JSON `404 endpoint_not_found`;
- non-API deep links receive `index.html` with an explicit 200 (the endpoint execution middleware leaves a
  headerless 404 status behind when nothing matches);
- static files and health paths unchanged.
No fake `POST /api/test-procedures` endpoint was added; DEC-103 is unchanged.

**Regression proof:** The new test failed on current main (`67614d5`) with 404 and passes with the fix. It
asserts: CSRF boundary (400 without token), POST collection → 405 with `Allow: GET`, GET collection functional,
unknown API path → 404 `endpoint_not_found`, former approval route absent (404), deep link → 200 `index.html`.

**Tests run / results (all on disposable infrastructure; the persistent database was not touched):**
- Focused: new regression + existing DEC-103 405 test — PASS.
- Full backend suite — PASS: Domain 275 / Infrastructure 194 / API 232 (701 total).
- Client type-check — PASS. Client lint — PASS (only known pre-existing `ChangeRequestEditor` warning).
- Production-build journeys (served shape, temp SQLite, port 5086) — 10/10 PASS.
- Browser journeys, two shards — 142 passed, 1 intentionally skipped, 0 failed.
- PostgreSQL migrations: none; no EF/migration change.

**Remaining risks / notes:**
- CI for the draft GitHub pull request is still pending; the branch-protection gate (`Report what this run
  validated`) will confirm the four CI jobs.
- The live production API on port 5080 still runs the pre-fix `main` build and was intentionally not restarted
  or rebuilt with this unmerged branch. Live requalification belongs after an authorized merge.
- One failed local attempt at the two-shard browser suite in the main worktree was an environment collision:
  the suite's in-place Release build cannot overwrite DLLs locked by the running production API. The gate was
  rerun cleanly from a disposable worktree and passed.

**Status:** STOPPED for owner review. No merge, no auto-merge, no squash. The draft GitHub pull request will
remain open until the owner explicitly authorizes this exact pull request to be merged.

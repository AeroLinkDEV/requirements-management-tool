(Retained amendment to 42-f01-diagnostic-findings.md — supersedes the geometry and main-evidence sections. Per Astra's correction; no browser repetition performed.)

# F01 AMENDMENT — coordinate correction withdrawn and corrected

## Withdrawn claims

The following claims from 42-f01-diagnostic-findings.md are WITHDRAWN:
- "bottom overflow 236.0px on every run; the card sits wholly to the right of and below the containment box"
- "the journey passes locally because its in-page expect.poll succeeds the moment containment holds transiently during the dock-recovery animation, while the final resting placement violates it"

Cause of the error: the probe compared the card's viewport-absolute `getBoundingClientRect()` coordinates against a box expressed in canvas-content offsets (panel/toolbar insets subtracted, canvas origin omitted). The comparison mixed coordinate spaces.

## Corrected geometry (Astra's verified viewport bounds)

Correct viewport containment bounds: **left 280, top 340, right 1920, bottom 584**.
Card (`proc-4`) final rect: left 1674.15, top 362.15, right 1892, bottom 568.

Containment against the corrected bounds, recomputed for all five probe runs:
- top: 362.15 ≥ 340 ✓ (22.15px inset)
- bottom: 568 ≤ 584 ✓ (**16px clearance**)
- left: 1674.15 ≥ 280 ✓
- right: 1892 ≤ 1920 ✓ (**28px clearance**)

**Corrected result: containment HELD on every run.** The local 6/6 journey passes were genuine, deterministic containment — not transient passes. The retained per-run JSON files are unchanged; only the interpretation is corrected (the probe's `box` remains the canvas-content box used by the product code; the erroneous comparison against viewport coordinates is what is withdrawn).

## Corrected main-branch evidence (as directed)

- Merge-group run **36049357299** on main: **FAILED this journey initially and PASSED on retry** (I previously recorded it only as "success").
- Push run **36052413361** on main: **SKIPPED browser journeys** (I previously counted it as exercising the family).

**Corrected classification: an existing intermittent failure of this journey, present on main independent of the candidate; the underlying cause remains unresolved.** The candidate (client code byte-identical to base and main) neither introduces nor fixes it. The single CI failure at the candidate (gate 36064279956, two attempts) is consistent with this known intermittent behavior.

## Unchanged conclusions

- The candidate's two files touch no client code; the journey failure is independent of the change.
- No assertion weakened, no timeout increased, no product code changed.
- The failed gate (36064279956) and requester refusal (36064258664) remain preserved as the failure evidence; per direction, no retry or label action was taken.

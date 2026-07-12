# Product Principles

These principles are behavioral constraints for future product and technical decisions. A proposed feature or design that conflicts with them requires an explicit recorded decision.

## 1. Controlled Artifact Data Is Authoritative

Requirements, changes, procedures, executions, links, reviews, approvals, and baselines are authoritative structured records. SYSRDs, test documents, traceability reports, and other files are generated views of named controlled inputs.

## 2. Identity and Revision Are Different

An artifact keeps a stable identity throughout its life. Each approved change creates a distinct revision. A human-readable display number may expose revision information, but it must not make a changed requirement appear to be an unrelated requirement.

## 3. Approved History Is Immutable

Draft content may be edited under controlled permissions. Approved or baselined content must never be silently overwritten. Corrections produce a new revision, amendment, execution, baseline, or superseding record while preserving the original.

## 4. Retirement Is Not Erasure

“Delete” means an approved change makes an artifact no longer effective in future selected baselines. Its identity, revisions, relationships, reviews, approvals, rationale, and audit history remain retrievable.

## 5. Every Material Action Is Attributable

Creation, modification, review, comment disposition, approval, rejection, link change, workflow transition, baseline creation, document generation, administration, and access-control change must record the responsible identity and time.

## 6. Baselines Are Exact and Immutable

A baseline identifies exact artifact revisions, link revisions, applicable configurations, and relevant approvals. A released baseline is immutable; a correction creates a successor baseline with an explicit relationship to the superseded one.

## 7. Approval and Inclusion Are Separate

An approved artifact revision is eligible for controlled use. It becomes part of a release or generated approved document only when explicitly included in the applicable baseline.

## 8. Trace Links Have Semantics and History

Links must be typed, directional where appropriate, version-aware, attributable, and reviewable. A generic “related to” link may supplement but cannot replace a meaningful verification, allocation, change, or inclusion relationship.

## 9. Change Propagates Visible Impact

When an artifact changes, affected links and downstream evidence must become suspect or otherwise require review according to defined rules. The platform must not imply continued validity without reassessment.

## 10. Procedures and Executions Are Separate

A test procedure is a reusable, versioned definition. Each test execution records the exact procedure revision, environment, configuration, performer, time, outcomes, evidence, review, and anomalies. A retest never erases a prior failure.

## 11. Generated Documents Are Reproducible

Every controlled output identifies its source baseline, template revision, generator version, generation time, approval state, and file hash. The product must preserve enough controlled input to regenerate or explain an output years later.

## 12. Human Authority Is Explicit

Qualified humans author, review, approve, and disposition lifecycle data. Future AI may suggest draft content but must never silently alter approved data, approve an artifact, or become the authoritative traceability engine.

## 13. Security Is Program- and Action-Aware

Access is least-privilege and considers program, artifact type, workflow state, role, and requested action. Administrative capability does not include silent alteration or erasure of controlled history.

## 14. Reliability Means Controlled Failure and Recovery

The platform cannot promise never to make mistakes. It must make errors difficult to introduce, quick to detect, visible, attributable, recoverable, and testable.

## 15. Expand by Complete Vertical Slices

The project will prove coherent workflows end to end before broadening across the V lifecycle. The first proof is the system-level chain defined in [SYSTEM_LEVEL_WORKFLOW.md](SYSTEM_LEVEL_WORKFLOW.md).

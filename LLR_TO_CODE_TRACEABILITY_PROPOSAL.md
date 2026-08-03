# LLR-to-Code Traceability MVP and Direction

## Delivery status

The first increment is delivered. AeroLink records one immutable build-scoped mapping for each required exact
approved LLR revision: a GitLab MR URL/reference plus merge commit SHA and merge time, or a justified
`No code change required` disposition. Build 1.5 is historical/read-only; Build 1.6 is active. The Code center
shows the release-gate count and labels seeded FMS examples as demonstration data.

The gate is authoritative, not presentational: the Code center and release-readiness service share both the
required-LLR projection and the campaign baseline it is computed from, the mappings participate in the signed
review manifest, and the create endpoint independently refuses released targets and builds with no
materialized requirement population. Any build with no changed LLR revisions passes this gate at 0/0.

The release rule below applies to every Project. The FMS demonstration seeds a small labelled sample of
mappings; it does not narrow the set of changes that owe evidence. Build 1.5 introduced 700 LLR revisions and
carries five sample mappings, so its gate reads 5 of 700 — which is what adopting AeroLink after a build has
already shipped actually looks like.

The broader synchronized external-code-change model below remains direction, not claimed behavior. AeroLink
does not yet call GitLab, synchronize MR state/author/branches/CI, prove commit inclusion in a compiled binary,
or normalize one MR shared by several LLR revisions.

## Recommendation

Treat GitLab as the authority for source code, commits, branches, merge requests (MRs), reviews, and CI. Keep
AeroLink authoritative for the exact approved LLR revisions a build requires and for the release decision.
AeroLink should record immutable links between those two authorities; it should not store or edit source code.

Use **MR** consistently for a GitLab merge request so **PR** continues to mean Problem Report in AeroLink.

## Delivered minimum record

The delivered record stores:

- GitLab repository path, MR reference, URL, and title;
- merge commit SHA and merged time once merged;
- one **exact LLR artifact and revision ID**;
- target AeroLink Project and build.

The MR reference is human-friendly; the repository identity plus merge commit SHA is immutable evidence. Never
link only to `LLR-000123` because that loses which wording was implemented. Many-to-many MR/LLR mapping remains
a later normalization if real usage requires it.

## Delivered manual workflow and later synchronization

1. An SWCR is approved and its LLR revisions are selected into Build 1.6.
2. A developer creates a GitLab branch and MR. The MR template contains an `AeroLink LLRs` field with controlled
   identifiers such as `LLR-000123.02`.
3. In the delivered MVP, an authorized engineer records the merged GitLab evidence or a justified no-code
   decision. A later read-only GitLab integration may receive a webhook or periodic synchronization event.
4. AeroLink rejects unknown, cross-Project, obsolete, or wrong-build LLR revisions. Draft LLRs may be shown as
   unresolved references but do not satisfy traceability.
5. When GitLab reports the MR merged, AeroLink fixes the merge commit SHA and merged metadata as evidence. Later
   GitLab updates cannot rewrite that captured merge fact; corrections create a new evidence revision.
6. The build view shows, for every changed LLR, the linked MR, merge SHA, target branch, CI state, and whether the
   merge is included in the software build.

## Release rule

The delivered MVP uses one release gate:

> Every LLR introduced or modified by a non-deferred SWCR in this build has at least one merged code change at
> an exact commit included in the build, or an approved `No code change required` disposition with rationale.

This avoids falsely requiring code for documentation-only or data-only LLR changes while making the exception
explicit. Existing mandatory-test and evidence gates remain separate: merged code proves implementation;
passing tests with evidence prove verification.

## Integration boundary

Start with GitLab because that is the expected code host, but keep the provider adapter small:

- one read-only service account/token stored through the existing secret-handling mechanism;
- webhook validation plus an idempotent resynchronization endpoint;
- GitLab project allow-list per AeroLink Project;
- no repository cloning, source browsing, branch creation, merge approval, or code review inside AeroLink;
- manual MR URL entry as a fallback, followed by server-side validation and synchronization.

If GitHub is later selected, its pull request maps to the same external code-change contract; only the provider
adapter and terminology change.

## Deliberately deferred

- source-file or line-level traceability;
- bidirectional editing of GitLab issues or MRs;
- automated code-quality scoring;
- enforcement inside GitLab before merge;
- mapping HLRs directly to code when an approved LLR exists.

Those can evolve after exact LLR-revision-to-merge evidence is proven useful. The first increment should solve
one question reliably: **which merged code implements this exact approved LLR revision in this build?**

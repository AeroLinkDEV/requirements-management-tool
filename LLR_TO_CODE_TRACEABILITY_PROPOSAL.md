# LLR-to-Code Traceability Proposal

## Recommendation

Treat GitLab as the authority for source code, commits, branches, merge requests (MRs), reviews, and CI. Keep
AeroLink authoritative for the exact approved LLR revisions a build requires and for the release decision.
AeroLink should record immutable links between those two authorities; it should not store or edit source code.

Use **MR** consistently for a GitLab merge request so **PR** continues to mean Problem Report in AeroLink.

## Minimum viable record

Add one external code-change record for each GitLab MR:

- provider (`GitLab` initially), GitLab project ID, repository URL, and MR IID;
- MR URL, title, author, source branch, target branch, and current state;
- merge commit SHA and merged time once merged;
- last synchronized time and a hash of the synchronized metadata;
- links to one or more **exact LLR revision IDs**, with relationship `Implements`, `Modifies`, or `Retires`;
- target AeroLink Project and build.

The MR IID is human-friendly; the repository identity plus merge commit SHA is immutable evidence. Never link
only to `LLR-000123` because that loses which wording was implemented. One MR may implement several LLR
revisions, and one LLR revision may require several MRs.

## Proposed workflow

1. An SWCR is approved and its LLR revisions are selected into Build 1.6.
2. A developer creates a GitLab branch and MR. The MR template contains an `AeroLink LLRs` field with controlled
   identifiers such as `LLR-000123.02`.
3. A read-only GitLab integration receives a webhook or periodic synchronization event, resolves each identifier
   to the exact approved revision in the target build, and creates or refreshes the external code-change record.
4. AeroLink rejects unknown, cross-Project, obsolete, or wrong-build LLR revisions. Draft LLRs may be shown as
   unresolved references but do not satisfy traceability.
5. When GitLab reports the MR merged, AeroLink fixes the merge commit SHA and merged metadata as evidence. Later
   GitLab updates cannot rewrite that captured merge fact; corrections create a new evidence revision.
6. The build view shows, for every changed LLR, the linked MR, merge SHA, target branch, CI state, and whether the
   merge is included in the software build.

## Release rule

For an MVP, add one release gate:

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

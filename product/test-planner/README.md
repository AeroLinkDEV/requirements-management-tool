# Pull-request overlap advisory

`tools/check-overlap.mjs` is an API-only advisory checker for issue #569. The
`pull_request_target` workflow runs the checker from the trusted default branch;
it never checks out or executes pull-request-head code. The signal is not a
merge gate.

The checker compares eligible open pull requests by canonical changed paths
and by the repository hotspots in `lib/overlap.mjs`. Rename records include
both `filename` and `previous_filename`. Every PR identity and every returned
file record must be complete, including valid head/base SHAs, branch fields,
file status, and rename source where applicable. An absent, malformed, or
incomplete API record is `Unknown`, never `Clear`.

Analysis is deliberately bounded: at most 100 open pull requests, 30 eligible
pull requests, 1,000 files per pull request, 4,096 characters per path, 1,000
comments per target, 100,000 characters per comment, 435 pair comparisons,
and 30,000 analyzed file paths. The checker fails closed to `Unknown` when a
bound is exceeded; it does not truncate the input and claim that the remaining
evidence is clean. The JSON artifact reports `analysisComplete` and the limits
used for that run.

Only marker comments authored by the exact `github-actions[bot]` account with
GitHub's `Bot` type are managed. A human comment containing the marker text is
left untouched. PR-controlled titles, branches, authors, paths, reasons, and
timestamps are bounded and escaped before entering Markdown comments.

The workflow has a trusted-base presence guard. If the checker is absent from
the checked-out default branch, the guard writes a bounded schema-compatible
`Unknown` JSON artifact and a warning instead of silently skipping the
analysis. The `#569` rollout remains open until the post-`#597` integrated head
has been reviewed and the lifecycle behavior is proven there.

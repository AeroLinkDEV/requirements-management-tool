# CI evidence and original artifacts

| Workstream | Product run | Requester run | Starting head / disposition |
| --- | --- | --- | --- |
| #1066 | [35845728143](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/35845728143) | [35845707428](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/35845707428) | `027c985e82d1d1b764a2f24eac6e6c2ed4ec2c71`; Product failed, trusted success refused. |
| #1098 | [36064279956](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36064279956) | [36064258664](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36064258664) | `23bb25d987639ada3356a7003fa0380ba08e5042`; Product failed, trusted success refused. |
| Main comparison | [36049357299](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36049357299) | merge-group | Digital-thread journey failed initially and passed retry. This does not establish its cause. |
| Main comparison | [36052413361](https://github.com/AeroLinkDEV/requirements-management-tool/actions/runs/36052413361) | push | Browser journeys skipped; not browser acceptance. |

The failed #1066 API test was `ProjectPersonnelApiTests.The_holder_cannot_be_their_own_backup`, with a DataProtection key-file IOException during login. Its audit-value-containment browser failure involved authentication/session readiness; retain the actual trace/context rather than assuming a cause from the screenshot. The failed #1098 journey was `digital-thread-1046-interaction.spec.ts`, recovered strip-space containment, both initial and retry.

Run records and job lists are in [github-snapshot](github-snapshot/). Safe assertion/action extracts are in [diagnostic-summaries](diagnostic-summaries/). Original raw browser archives were not republished to public Git because they can contain cookies, tokens and request bodies. GitHub-authenticated artifact access remains the correct route when full traces are needed:

```bash
gh api repos/AeroLinkDEV/requirements-management-tool/actions/runs/36064279956/artifacts
gh run download 36064279956 --repo AeroLinkDEV/requirements-management-tool --name playwright-diagnostics-1-1 --dir /tmp/aerolink-1098-ci-evidence
gh api repos/AeroLinkDEV/requirements-management-tool/actions/runs/35845728143/artifacts
```

List first and use the currently returned name; artifact names/retention may change. Authentication is intentionally not included in this package. If Claude lacks Actions artifact permission or a retained artifact has expired, use the published extracts and request the exact missing original through Sean/Astra. Do not treat denied artifact access as a reason to broaden credentials or disclose session data.

The nine copied incident files were verified against local originals. All public text is a derived copy; source SHA and published SHA are separate. No CI retry, label mutation or workflow dispatch was performed to publish this package.

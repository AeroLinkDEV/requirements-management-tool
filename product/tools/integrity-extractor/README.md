# Integrity source extractor

This Java 17 command-line tool implements the extraction half of
[#1186](https://github.com/AeroLinkDEV/requirements-management-tool/issues/1186).
Read [the package and operator contract](../../docs/INTEGRITY_IMPORT.md) before a capture.
Live server validation is pending. The checked-in fixtures exercise the real packaging/checkpoint code,
but cannot prove a PTC response profile, permissions, source history completeness or SDK compatibility.

## Build and fixture rehearsal

Use PowerShell 7 and a JDK 17 or later. Set `JAVA_HOME` if Java is not on PATH.
From this directory:

```powershell
$built = ./Build-Extractor.ps1 -OutputDirectory C:\Temp\integrity-build
./Build-Fixture.ps1 -OutputDirectory C:\Temp\integrity-fixture-new
```

Use new, owned output directories. The fixture command produces `source.zip` and
`source.zip.manifest.sha256`. An AeroLink disposable host must explicitly set
`IntegrityImport__AllowFixtures=true` to import it. Leave that setting absent on ordinary hosts.

The build downloads and checks pinned Gson 2.13.2 and PTC SDK 4.16.2671 artifacts from Maven Central.
No jars are committed or bundled into AeroLink. For a customer's supported SDK, pass
`-MksApiJar C:\Path\To\CustomerSdk\mksapi.jar`, rebuild and qualify it against that server.
PTC's SDK is subject to its Freeware Agreement; obtain the customer's authorized SDK and retain the
applicable license/notices when redistributing it. This build is not a claim of PTC compatibility.

## Live capture configuration

Start with [fixtures/config.json](fixtures/config.json) for the package identity/scope fields; replace
every synthetic value and omit `fixtureDirectory`. Supply a stable source-instance UUID, exact server
version, query definition, projects/types, an administrator-accepted list of live decimal item IDs,
inventory acceptance and freeze references, and an explicit-offset capture time. Scope is bounded to
5,000 IDs by the extractor; split a larger, independently inventoried scope into separately reconciled
packages. A live query count by itself never proves that hidden records were captured.

Add connection settings `hostname`, `port`, `username`, `apiMajor`, `apiMinor` and a `profile`:

```json
{
  "projectField": "customer-qualified project response field",
  "typeField": "customer-qualified type response field",
  "historyField": "customer-qualified full history response field",
  "annotationsField": "customer-qualified annotation response field",
  "relationshipFields": ["each in-scope relationship response field"],
  "attachmentFields": ["each in-scope attachment response field"],
  "richTextFields": ["each entity-protected XHTML response field"],
  "attachmentNameField": "customer-qualified filename metadata field",
  "attachmentMimeField": "customer-qualified MIME metadata field",
  "attachmentAuthorField": "customer-qualified author metadata field",
  "attachmentDateField": "customer-qualified date metadata field",
  "attachmentUriField": "customer-qualified mks URI metadata field"
}
```

These are placeholders, not guessed PTC field names. Qualification must compare the full source field
inventory and representative raw responses to that profile, including custom fields and relationships,
history/signatures, annotations, source links, branches/time entries, users and attachment metadata.
The adapter requests the documented detail flags and refuses unsupported responses. It does not
fall back to Excel or silently drop commands/fields the server refuses.

Set the password only in the extractor process's `AEROLINK_INTEGRITY_PASSWORD` environment variable
using the operator's approved secret-entry mechanism. Never put it in configuration, CLI arguments,
source control or a pasted transcript. Remote TLS is required; there is no insecure fallback.

```powershell
$built = ./Build-Extractor.ps1 -OutputDirectory C:\Temp\integrity-build -MksApiJar C:\Sdk\mksapi.jar
& $built.Java -cp $built.ClassPath $built.MainClass extract C:\Migration\capture.json C:\Migration\checkpoint-new C:\Migration\source-new.zip
if ($LASTEXITCODE -ne 0) { throw 'Capture is incomplete' }
```

The same configuration and checkpoint directory may be resumed with a new output filename. Completed
items and attachment hashes are verified, and all source items/bytes are read again before sealing.
Any source/configuration change refuses the capture. An interrupted partial attachment that the SDK
left in its capture path requires a fresh checkpoint directory. Existing acquired packages are never
overwritten. Checkpoints contain sensitive source information and need the same custody as the archive.

## Qualification and custody

Without a `qualification` object, a real capture is marked `unproven` and AeroLink refuses it.
Qualification must name `serverVersion`, `sourceInstanceId`, `evidenceReference`, `acceptedBy`,
`capabilityMatrixReference`, `profileSha256`, `sdkSha256`, and `historicalAttachmentsInventoriedAbsent: true`. The last assertion is
permitted only when the administrator inventory and retrieval evidence establish that no historical
attachment bytes are missing. The current adapter cannot extract replaced historical bytes: if they
exist, extend and qualify extraction first. Do not use that boolean to waive a loss.

The qualification evidence must cover #1186 section 6 and the exact response profile/SDK configuration.
The unproven capture's capabilities member records the profile and SDK hashes; a qualification for
different bytes cannot promote a capture to qualified.
There is no self-certification and no target-server evidence shipped in this repository.
Retain the qualification reference, accepted inventory, freeze evidence, source-to-package reconciliation,
independently recorded manifest hash and human review with each migration.

The fixtures independently specify two reports: one open with a microsecond explicit-offset source date,
and one closed-in-source with a date-only value. They include bold XHTML, an external reference that must
never be fetched, a package image, Japanese/custom identifiers, a source signature in history, an
annotation, a directed exact-revision reference and an original text attachment. They create no native
source signatures or relationships.

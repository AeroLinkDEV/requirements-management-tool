$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)

function Replace-ExactlyOnce([string]$Path, [string]$Old, [string]$New, [string]$Label) {
    $text = [IO.File]::ReadAllText($Path)
    if (($text.Split($Old).Count - 1) -ne 1) { throw "$Label anchor mismatch in $Path." }
    [IO.File]::WriteAllText($Path, $text.Replace($Old, $New), $utf8)
}

$packagePath = 'product/client/package.json'
$packageAnchor = '    "test:smoke": "playwright test tests/application-smoke.spec.ts tests/showcase-usability.spec.ts",'
Replace-ExactlyOnce $packagePath $packageAnchor ('    "test:smoke:core": "playwright test tests/application-smoke.spec.ts",' + "`n" + $packageAnchor) 'package smoke'

$setupPath = 'product/client/tests/global-setup.ts'
$setupAnchor = 'export default async function globalSetup(){'
Replace-ExactlyOnce $setupPath $setupAnchor ($setupAnchor + "`n" + "  if(process.env.AEROLINK_E2E_SKIP_SHOWCASE_SEED==='true')return") 'global setup'

$smokePath = 'product/client/tests/application-smoke.spec.ts'
$loginAnchor = '  await login(page)'
$loginReplacement = @(
  "  const seedless = process.env.AEROLINK_E2E_SKIP_SHOWCASE_SEED === 'true'",
  "  await login(page,'admin',{openProject:!seedless})"
) -join "`n"
Replace-ExactlyOnce $smokePath $loginAnchor $loginReplacement 'application smoke login'
$headingAnchor = "  await expect(page.getByRole('heading', { name: /Create your first program|Command Center/ })).toBeVisible()"
$headingReplacement = "  await expect(page.getByRole('heading', { name: seedless ? 'Create your first program' : 'Command Center' })).toBeVisible()"
Replace-ExactlyOnce $smokePath $headingAnchor $headingReplacement 'application smoke entry-state'

$classifyPath = 'product/test-planner/lib/classify.mjs'
$browserOld = @(
  "    steps.push({",
  "      label: 'Browser smoke journeys',",
  "      command: 'npm --prefix product/client run test:smoke',",
  "      why: 'A bounded subset; the full journey set belongs in CI, not on a laptop.',",
  "    })"
) -join "`n"
$browserNew = @(
  "    steps.push({",
  "      label: 'Browser smoke journeys',",
  "      command: classification.client",
  "        ? 'npm --prefix product/client run test:smoke'",
  "        : 'npm --prefix product/client run test:smoke:core',",
  "      why: classification.client",
  "        ? 'Client changes keep the showcase usability smoke; the full journey set still belongs in CI.'",
  "        : 'Backend-only Fast uses the three first-install/application smoke checks without purchasing the unrelated full showcase seed; Full CI retains showcase usability coverage.',",
  "    })"
) -join "`n"
Replace-ExactlyOnce $classifyPath $browserOld $browserNew 'classifier browser plan'

$scriptPath = 'product/scripts/Get-AeroLinkTestPlan.ps1'
$scriptOld = "        'Browser smoke journeys' { Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'test:smoke') }"
$scriptNew = @(
  "        'Browser smoke journeys' {",
  "            if (`$plan.classification.client) {",
  "                Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'test:smoke')",
  "            }",
  "            else {",
  "                `$hadSkipShowcaseSeed = Test-Path Env:AEROLINK_E2E_SKIP_SHOWCASE_SEED",
  "                `$previousSkipShowcaseSeed = `$env:AEROLINK_E2E_SKIP_SHOWCASE_SEED",
  "                try {",
  "                    `$env:AEROLINK_E2E_SKIP_SHOWCASE_SEED = 'true'",
  "                    Invoke-CheckedProcess 'npm.cmd' @('--prefix', 'product/client', 'run', 'test:smoke:core')",
  "                }",
  "                finally {",
  "                    if (`$hadSkipShowcaseSeed) { `$env:AEROLINK_E2E_SKIP_SHOWCASE_SEED = `$previousSkipShowcaseSeed }",
  "                    else { Remove-Item Env:AEROLINK_E2E_SKIP_SHOWCASE_SEED -ErrorAction SilentlyContinue }",
  "                }",
  "            }",
  "        }"
) -join "`n"
Replace-ExactlyOnce $scriptPath $scriptOld $scriptNew 'Fast browser execution'

git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed after core-smoke v3 patch.' }

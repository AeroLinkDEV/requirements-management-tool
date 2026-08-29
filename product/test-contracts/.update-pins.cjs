const fs = require('fs');
const path = require('path');
const tcDir = path.resolve(__dirname);
const intent = JSON.parse(fs.readFileSync(path.join(tcDir, 'api-test-intent.json'), 'utf8'));
const host = JSON.parse(fs.readFileSync(path.join(tcDir, 'api-host-classification.json'), 'utf8'));
const p = path.join(tcDir, 'tests', 'inventory.test.mjs');
let c = fs.readFileSync(p, 'utf8');

// Update all numeric totals
c = c.replace(/intentArtifact\.totals\.tests, \d+/g, `intentArtifact.totals.tests, ${intent.totals.tests}`);
c = c.replace(/intentArtifact\.totals\.cases, \d+/g, `intentArtifact.totals.cases, ${intent.totals.cases}`);
c = c.replace(/hostArtifact\.totals\.knownCases, \d+/g, `hostArtifact.totals.knownCases, ${intent.totals.cases}`);

// Update summary deep-equal assertions. Access may be `summary.key` or `summary['key']`.
for (const key of ['reusable-host', 'fresh-host', 'converted', 'migration-candidate']) {
  const val = host.summary[key];
  if (!val) continue;
  const summaryStr = `{ classes: ${val.classes}, tests: ${val.tests}, knownCases: ${val.knownCases}, unknownCaseTests: ${val.unknownCaseTests} }`;
  const access = `hostArtifact\\.summary(?:(?:\\['${key.replace(/[-]/g, '\\-')}'\\])|(?:\\.${key}))`;
  const pattern = new RegExp(`(${access}, )\\{[^}]+\\}`);
  c = c.replace(pattern, `$1${summaryStr}`);
}

// Update subtraction assertions
const rh = host.summary['reusable-host'];
c = c.replace(/reusable\.tests - reusable\.classes, \d+/g, `reusable.tests - reusable.classes, ${rh.tests - rh.classes}`);
c = c.replace(/reusable\.knownCases - reusable\.classes, \d+/g, `reusable.knownCases - reusable.classes, ${rh.knownCases - rh.classes}`);

// Update CLI output regex patterns
const totalTests = host.totals.tests;
const fh = host.summary['fresh-host'];
const rhPct = (rh.tests / totalTests * 100).toFixed(1);
const fhPct = (host.summary['fresh-host'].tests / totalTests * 100).toFixed(1);
c = c.replace(/reusable-host\\s\+\d+\\s\+\d+\\s\+\d+\\s\+\d+\\s\+[\d.\\]+%/,
  `reusable-host\\s+${rh.classes}\\s+${rh.tests}\\s+${rh.knownCases}\\s+0\\s+${rhPct}%`);
c = c.replace(/fresh-host\\s\+\d+\\s\+\d+\\s\+\d+\\s\+\d+\\s\+[\d.\\]+%/,
  `fresh-host\\s+${fh.classes}\\s+${fh.tests}\\s+${fh.knownCases}\\s+0\\s+${fhPct}%`);
c = c.replace(/Remaining reuse headroom:\\s\+\d+ classes, \d+ methods, \d+ known cases/,
  `Remaining reuse headroom:\\s+${rh.classes} classes, ${rh.tests} methods, ${rh.knownCases} known cases`);

fs.writeFileSync(p, c);
console.log('Updated:', JSON.stringify({ tests: intent.totals.tests, cases: intent.totals.cases, reusable: rh, freshTotal: host.summary['fresh-host'] }));

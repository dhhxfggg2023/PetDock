// coverage-gate.mjs - machine gate D-10a (docs/41) coverage threshold check.
// Zero npm dependencies, Node built-ins only. Run: node tools/coverage-gate.mjs
//
// Usage:
//   node tools/coverage-gate.mjs            # measure (coverlet.console) + judge
//   node tools/coverage-gate.mjs --report   # judge an existing report only
//   node tools/coverage-gate.mjs --json     # machine-readable verdict
//
// D-10a thresholds (docs/41): packages/core line >= 80%, branch >= 70%.
// The thresholds are data at the top of this file and MUST NOT be changed to
// make a run green - changing them requires an RFC (docs/43).

import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)));
const OUT_DIR = join(ROOT, 'artifacts');
const OUT_FILE = join(OUT_DIR, 'coverage.cobertura.xml');
const REGISTRY_FILE = join(ROOT, 'tools', 'coverage-baseline.json');

const TEST_DLL = 'tests/unit/PetDock.Runtime.Tests/bin/Release/net8.0-windows/PetDock.Runtime.Tests.dll';
const INCLUDE = '[PetDock.Core]*';

// D-10a - frozen thresholds. Change only via RFC (docs/43).
const THRESHOLDS = { line: 0.8, branch: 0.7 };
const LINE_MIN = THRESHOLDS.line * 100;
const BRANCH_MIN = THRESHOLDS.branch * 100;

const args = process.argv.slice(2);
const reportOnly = args.includes('--report');
const asJson = args.includes('--json');
const verbose = args.includes('--verbose');

function log(line) {
  if (!asJson) console.log(line);
}

function fail(line) {
  console.error(line);
  process.exitCode = 2;
}

// --- cobertura parsing (regex-based: the file is a single coverage root) ---
function attr(text, name) {
  const m = text.match(new RegExp(`${name}="([^"]*)"`));
  return m ? m[1] : null;
}

function parseReport(xmlPath) {
  const xml = readFileSync(xmlPath, 'utf8');
  const rootMatch = xml.match(/<coverage\b[^>]*>/);
  if (!rootMatch) throw new Error(`not a cobertura report: ${xmlPath}`);
  const linesValid = Number(attr(rootMatch[0], 'lines-valid'));
  const linesCovered = Number(attr(rootMatch[0], 'lines-covered'));
  const branchesValid = Number(attr(rootMatch[0], 'branches-valid'));
  const branchesCovered = Number(attr(rootMatch[0], 'branches-covered'));
  if (!Number.isFinite(linesValid) || linesValid === 0) {
    throw new Error('report has zero valid lines - the target probably never ran');
  }
  const pct = (c, v) => (v > 0 ? (c / v) * 100 : 0);
  // coverlet's cobertura writer emits neither methods-valid on <coverage> nor
  // method-valid on <class>; count the <method> elements and the ones with a
  // covered line instead of inventing a column.
  const methodEls = xml.match(/<method\b[^>]*>/g) || [];
  const methodsValid = methodEls.length;
  let methodsCovered = 0;
  for (const m of xml.match(/<method\b[\s\S]*?<\/method>/g) || []) {
    if (/<line\b[^>]*hits="[1-9]/.test(m)) methodsCovered += 1;
  }
  return {
    file: xmlPath,
    line: pct(linesCovered, linesValid),
    branch: pct(branchesCovered, branchesValid),
    method: pct(methodsCovered, methodsValid),
    linesCovered,
    linesValid,
    branchesCovered,
    branchesValid,
  };
}

// --- measure step: coverlet.console drives the in-process xUnit runner ---
// The repo's own test entry (tests/unit/.../Program.cs) is used because
// `dotnet test` cannot run on this host: testhost dies in
// SetParentProcessExitCallback -> OpenProcess (Win32 5). See mistakes.md.
function measure() {
  const dll = join(ROOT, ...TEST_DLL.split('/'));
  if (!existsSync(dll)) {
    fail(`MISSING ${TEST_DLL} - build the solution first (dotnet build PetDock.sln -c Release)`);
    return false;
  }
  if (!existsSync(OUT_DIR)) mkdirSync(OUT_DIR, { recursive: true });
  // stdio inherit: capture-through-pipe is denied in the confined channels.
  const res = spawnSync(
    'coverlet',
    [
      dll,
      '--target', 'dotnet',
      '--targetargs', `"${dll}"`,
      '--include', INCLUDE,
      '--format', 'cobertura',
      '--output', OUT_FILE,
    ],
    { cwd: ROOT, stdio: verbose ? 'inherit' : 'ignore' }
  );
  if (res.error) {
    fail(`COVERLET-SPAWN-FAILED ${res.error.message}`);
    return false;
  }
  if (res.status !== 0) {
    fail(`COVERLET-FAILED exit=${res.status} (coverlet.console 10.1.0 must be on PATH; install with dotnet tool install --global coverlet.console)`);
    return false;
  }
  return true;
}

// --- judge step ---
function judge(rep) {
  const lineOk = rep.line >= LINE_MIN;
  const branchOk = rep.branch >= BRANCH_MIN;
  const ok = lineOk && branchOk;
  log(`STAT coverage-module PetDock.Core`);
  log(`STAT coverage-line ${rep.line.toFixed(2)}% (${rep.linesCovered}/${rep.linesValid}) threshold >= ${LINE_MIN.toFixed(0)}%`);
  log(`STAT coverage-branch ${rep.branch.toFixed(2)}% (${rep.branchesCovered}/${rep.branchesValid}) threshold >= ${BRANCH_MIN.toFixed(0)}%`);
  log(`STAT coverage-method ${rep.method.toFixed(2)}%`);
  if (ok) {
    log('COVERAGE-GATE-PASS');
  } else {
    log('COVERAGE-GATE-FAIL');
    if (!lineOk) log(`  line ${rep.line.toFixed(2)}% < ${LINE_MIN.toFixed(0)}% (docs/41 D-10a)`);
    if (!branchOk) log(`  branch ${rep.branch.toFixed(2)}% < ${BRANCH_MIN.toFixed(0)}% (docs/41 D-10a)`);
  }
  return ok;
}

// --- registry: the honest, committed record of the measured scale ---
function readRegistry() {
  if (!existsSync(REGISTRY_FILE)) return null;
  try {
    return JSON.parse(readFileSync(REGISTRY_FILE, 'utf8'));
  } catch {
    return null;
  }
}

// The registry file (tools/coverage-baseline.json) is the committed record of
// what the gate actually measured. It is written ONCE by hand from a real run
// and never auto-refreshed, so it stays a historical record instead of a
// moving target that silently follows whatever the last run printed.
function checkRegistry(rep) {
  const reg = readRegistry();
  if (!reg) {
    log('STAT coverage-registry MISSING tools/coverage-baseline.json');
    return;
  }
  const recorded = reg.measured;
  if (!recorded || typeof recorded.line !== 'number') {
    log('STAT coverage-registry no measured entry (fill tools/coverage-baseline.json from a real run)');
    return;
  }
  const drift = Math.abs(recorded.line - rep.line);
  const driftBranch = Math.abs(recorded.branch - rep.branch);
  log(`STAT coverage-registry recorded line=${recorded.line}% branch=${recorded.branch}% (${reg.measuredAt || 'no date'})`);
  if (drift > 1 || driftBranch > 1) {
    log(`NOTE coverage drifted ${drift.toFixed(2)}pp line / ${driftBranch.toFixed(2)}pp branch from the registered value - re-measure and update tools/coverage-baseline.json from the real output; never edit the registered number to match a failing run`);
  }
}

let ok = true;
if (reportOnly) {
  if (!existsSync(OUT_FILE)) {
    fail(`MISSING report ${OUT_FILE} - run without --report first`);
    process.exit(2);
  }
} else if (!measure()) {
  process.exit(2);
}

if (existsSync(OUT_FILE)) {
  let rep;
  try {
    rep = parseReport(OUT_FILE);
  } catch (err) {
    fail(`REPORT-PARSE-FAILED ${err.message}`);
    process.exit(2);
  }
  checkRegistry(rep);
  ok = judge(rep);
} else {
  fail(`NO REPORT produced at ${OUT_FILE}`);
  ok = false;
}

if (asJson) {
  const rep = existsSync(OUT_FILE) ? parseReport(OUT_FILE) : null;
  console.log(JSON.stringify({
    gate: 'D-10a',
    module: 'PetDock.Core',
    thresholds: { line: LINE_MIN, branch: BRANCH_MIN },
    measured: rep ? { line: Number(rep.line.toFixed(2)), branch: Number(rep.branch.toFixed(2)), method: Number(rep.method.toFixed(2)) } : null,
    passed: ok,
  }));
}

process.exit(ok ? 0 : 1);

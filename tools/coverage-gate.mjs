// coverage-gate.mjs - machine gate D-10a (docs/41) coverage ratchet check.
// Zero npm dependencies, Node built-ins only. Run: node tools/coverage-gate.mjs
//
// Usage:
//   node tools/coverage-gate.mjs            # measure (coverlet.console) + judge
//   node tools/coverage-gate.mjs --report   # judge an existing report only
//   node tools/coverage-gate.mjs --json     # machine-readable verdict
//
// D-10a is a RATCHET (docs/rfcs/RFC-04.md): coverage must not fall below the
// measured floor registered in tools/coverage-baseline.json. The absolute
// target (line 80% / branch 70%) is kept as an M4/M8 milestone goal and is
// reported on every run as COVERAGE-PENDING until it is reached; reaching it
// flips the same run to COVERAGE-GATE-PASS with no further edit.
//
// Verdicts:
//   COVERAGE-GATE-PASS            target reached (line >= 80 and branch >= 70)   exit 0
//   COVERAGE-PENDING ...          above the floor, below the target              exit 0
//   COVERAGE-REGRESSION ...       below the floor                               exit 1
//
// The floor is data in tools/coverage-baseline.json and MUST NOT be lowered to
// make a run green - lowering it requires an RFC (docs/43). Raising it is a
// human edit from a real run (RFC-04 section 4.3); there is deliberately no
// --update-baseline switch, because a floor that follows the last run is not a
// floor.

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

// D-10a ratchet (docs/rfcs/RFC-04.md).
// The FLOOR comes from tools/coverage-baseline.json (current + covered counts);
// the TARGET is the original 80%/70% goal, kept as an M4/M8 milestone.
// EPSILON_PP only absorbs the instrumented-denominator wobble: the same 190
// covered lines read 5.09% over 3734 valid lines and 5.08% over 3740. It is a
// 0.1pp budget for that, NOT an authorisation to drift downward.
const EPSILON_PP = 0.1;
const DEFAULT_TARGET = { line: 80, branch: 70 };

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

// --- judge step: ratchet (RFC-04) ---
// Three verdicts, in priority order:
//   regression (below the registered floor)  -> COVERAGE-REGRESSION + exit 1
//   target reached (>= 80% line, >= 70% branch) -> COVERAGE-GATE-PASS + exit 0
//   otherwise                                -> COVERAGE-PENDING + exit 0
// Only line and branch are judged. Method coverage is printed and never judged:
// coverlet's cobertura writer emits no method counters, so the script derives
// them by counting <method> elements - a derived number is not a floor.
function judge(rep) {
  const reg = readRegistry();
  const floor = reg && reg.current && typeof reg.current.line === 'number' ? reg.current : null;
  if (!floor) {
    fail('COVERAGE-FLOOR-MISSING tools/coverage-baseline.json has no "current" entry with a numeric line - the ratchet has nothing to hold; fill it from a real run (RFC-04 section 4.3)');
    return null;
  }
  const target = reg.target && typeof reg.target.line === 'number' ? reg.target : DEFAULT_TARGET;

  log('STAT coverage-module PetDock.Core');
  log(`STAT coverage-line ${rep.line.toFixed(2)}% (${rep.linesCovered}/${rep.linesValid}) floor >= ${floor.line}% (-${EPSILON_PP}pp eps), ${floor.linesCovered ?? '?'} covered lines, target >= ${target.line}%`);
  log(`STAT coverage-branch ${rep.branch.toFixed(2)}% (${rep.branchesCovered}/${rep.branchesValid}) floor >= ${floor.branch}% (-${EPSILON_PP}pp eps), ${floor.branchesCovered ?? '?'} covered branches, target >= ${target.branch}%`);
  log(`STAT coverage-method ${rep.method.toFixed(2)}% (reported only, not judged)`);

  // Rule 1 (strict, no tolerance): the raw covered counts must not shrink.
  // A shrinking count has exactly one cause - assertions or covered paths went away.
  const regressions = [];
  if (typeof floor.linesCovered === 'number' && rep.linesCovered < floor.linesCovered) {
    regressions.push(`covered lines ${rep.linesCovered} < floor ${floor.linesCovered}`);
  }
  if (typeof floor.branchesCovered === 'number' && rep.branchesCovered < floor.branchesCovered) {
    regressions.push(`covered branches ${rep.branchesCovered} < floor ${floor.branchesCovered}`);
  }
  // Rule 2: the percentages may not fall below the floor by more than the
  // denominator-wobble budget.
  if (rep.line < floor.line - EPSILON_PP) {
    regressions.push(`line ${rep.line.toFixed(2)}% < floor ${floor.line}% - ${EPSILON_PP}pp eps`);
  }
  if (rep.branch < floor.branch - EPSILON_PP) {
    regressions.push(`branch ${rep.branch.toFixed(2)}% < floor ${floor.branch}% - ${EPSILON_PP}pp eps`);
  }

  if (regressions.length > 0) {
    for (const r of regressions) log(`COVERAGE-REGRESSION ${r}`);
    log(`COVERAGE-REGRESSION line=${rep.line.toFixed(2)}% (baseline=${floor.line}%, target=${target.line}%)`);
    log('COVERAGE-GATE-FAIL');
    return false;
  }

  if (rep.line >= target.line && rep.branch >= target.branch) {
    log(`COVERAGE-GATE-PASS (line ${rep.line.toFixed(2)}% >= ${target.line}%, branch ${rep.branch.toFixed(2)}% >= ${target.branch}% - M4/M8 milestone reached)`);
    return true;
  }

  log(`COVERAGE-PENDING line=${rep.line.toFixed(2)}% (baseline=${floor.line}%, target=${target.line}%)`);
  log(`COVERAGE-PENDING branch=${rep.branch.toFixed(2)}% (baseline=${floor.branch}%, target=${target.branch}%)`);
  return true;
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
  const reg = readRegistry() || {};
  const floor = reg.current && typeof reg.current.line === 'number' ? reg.current : null;
  const target = reg.target && typeof reg.target.line === 'number' ? reg.target : DEFAULT_TARGET;
  const rep = existsSync(OUT_FILE) ? parseReport(OUT_FILE) : null;
  const belowFloor = rep && floor
    ? (rep.line < floor.line - EPSILON_PP || rep.branch < floor.branch - EPSILON_PP
      || (typeof floor.linesCovered === 'number' && rep.linesCovered < floor.linesCovered)
      || (typeof floor.branchesCovered === 'number' && rep.branchesCovered < floor.branchesCovered))
    : false;
  const reachedTarget = rep ? (rep.line >= target.line && rep.branch >= target.branch) : false;
  console.log(JSON.stringify({
    gate: 'D-10a',
    mode: 'ratchet',
    rfc: 'RFC-04',
    module: 'PetDock.Core',
    floor: floor ? { line: floor.line, branch: floor.branch, linesCovered: floor.linesCovered ?? null, branchesCovered: floor.branchesCovered ?? null, epsilonPp: EPSILON_PP } : null,
    target: { line: target.line, branch: target.branch },
    verdict: belowFloor ? 'COVERAGE-REGRESSION' : (reachedTarget ? 'COVERAGE-GATE-PASS' : 'COVERAGE-PENDING'),
    measured: rep ? { line: Number(rep.line.toFixed(2)), branch: Number(rep.branch.toFixed(2)), method: Number(rep.method.toFixed(2)), linesCovered: rep.linesCovered, branchesCovered: rep.branchesCovered } : null,
    passed: ok === true,
  }));
}

process.exit(ok === true ? 0 : 1);

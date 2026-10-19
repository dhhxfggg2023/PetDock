// lint-core.mjs - machine gates D-03a / D-03b / D-03c / D-04a
// Spec: docs/40-development-paradigm.md sections 3-4 (rules D-03, D-04a).
// Zero npm dependencies, Node built-ins only. Run: node tools/lint-core.mjs
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { extname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)));
const PACKAGES_DIR = join(ROOT, 'packages');
const CORE_DIR = join(PACKAGES_DIR, 'core');

// D-03a budget comes from tools/core-loc-baseline.json (RFC-11 single source of truth).
// The file is maintained by hand and NEVER auto-written: a floor that follows the last
// run is not a floor (same discipline as RFC-04 section 4.3 for coverage).
const LOC_BASELINE_FILE = join(ROOT, 'tools', 'core-loc-baseline.json');
const MAX_FILE_LINES = 400; // D-03c (unchanged by RFC-11; adjusting it still needs an RFC)
const TEMP_MARKERS = ['TODO', 'FIXME', 'HACK', 'XXX']; // D-04a

// D-03b word list: docs/01-principles.md P-02 plus docs/02-glossary.md section 2.
// These entries are the gate input data, never a reference to any ecosystem.
const BLACKLIST = [
  'DyberPet', 'BongoCat', 'Shimeji', 'Ukagaka', 'SSP', 'Live2D', 'VRM',
  'Codex Pets', 'VPet', 'MMD', 'Spine',
  '.moc3', '.model3.json', '.ase', '.vrm', '.pmx', '.nar',
  'actions.xml', 'descript.txt', 'surfaces.txt', 'pet.json',
  'balloon', 'shell', 'ghost', 'mascot', 'sprite', 'presenter', 'avatar',
  'action', 'behavior',
  '帧动画', '精灵图集', '骨骼', '物理参数', '行为树', '好感度', '饥饿度',
  '气球', '对话泡', '桌面精灵', '精灵图', '宠物商店', '兼容所有桌宠',
];

const SOURCE_EXTS = new Set([
  '.cs', '.ts', '.js', '.mjs', '.cjs', '.tsx', '.jsx', '.mts', '.cts',
  '.py', '.ps1', '.psm1', '.sh', '.bat', '.cmd',
]);
const CONFIG_EXTS = new Set([
  '.csproj', '.props', '.targets', '.json', '.xml', '.config', '.resx',
  '.resw', '.txt', '.sln', '.editorconfig', '.yaml', '.yml', '.ini', '.cfg',
]);
const TEXT_EXTS = new Set([...SOURCE_EXTS, ...CONFIG_EXTS]);
const SKIP_DIRS = new Set(['node_modules', 'obj', 'bin']);
const SKIP_TEST_DIRS = new Set(['test', 'tests']);

const violations = [];

function walk(dir) {
  const out = [];
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const entry of entries) {
    if (entry.name.startsWith('.') || SKIP_DIRS.has(entry.name)) continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...walk(full));
    else if (entry.isFile()) out.push(full);
  }
  return out;
}

function relPath(p) {
  return relative(ROOT, p).split(sep).join('/');
}

function readText(file) {
  try {
    return readFileSync(file, 'utf8');
  } catch {
    return null;
  }
}

function hasSegment(absPath, names) {
  const rel = relative(PACKAGES_DIR, absPath).split(sep);
  return rel.some((s) => names.has(s.toLowerCase()));
}

function countLines(src) {
  if (src.length === 0) return 0;
  return src.replace(/\r?\n$/, '').split(/\r?\n/).length;
}

function escapeRe(s) {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

// D-03a baseline (RFC-11): read the single source of truth, then validate the declaration
// chain. Four things are machine-checkable; the honesty of the attribution is not (D-08d).
function readLocBaseline() {
  const text = readText(LOC_BASELINE_FILE);
  if (text === null) {
    return { error: `LOC-BASELINE-MISSING tools/core-loc-baseline.json (RFC-11 single source of truth)` };
  }
  let data;
  try {
    data = JSON.parse(text);
  } catch (err) {
    return { error: `LOC-BASELINE-BAD-JSON tools/core-loc-baseline.json ${err.message}` };
  }
  const budget = data.budget;
  if (typeof budget !== 'number' || !Number.isFinite(budget) || budget <= 0) {
    return { error: 'LOC-BASELINE-BAD-BUDGET tools/core-loc-baseline.json "budget" must be a positive number' };
  }
  const history = Array.isArray(data.history) ? data.history : [];
  if (history.length === 0) {
    return { error: 'LOC-BASELINE-NO-HISTORY tools/core-loc-baseline.json "history" must record at least one measurement' };
  }
  const last = history[history.length - 1];
  if (last.budget !== budget) {
    return {
      error: `LOC-BASELINE-BUDGET-MISMATCH top-level budget ${budget} != last history entry ${last.budget} (declare the raise in history, do not only bump the top number)`,
    };
  }
  // Entries must be ordered by date so "previous entry" is unambiguous.
  for (let i = 1; i < history.length; i += 1) {
    if (String(history[i].date) < String(history[i - 1].date)) {
      return { error: `LOC-BASELINE-DATE-ORDER history[${i}] date ${history[i].date} is earlier than history[${i - 1}] ${history[i - 1].date}` };
    }
  }
  // Arithmetic closure: declared attribution must add up to the measured delta.
  for (let i = 1; i < history.length; i += 1) {
    const cur = history[i];
    const prev = history[i - 1];
    const delta = cur.measured - prev.measured;
    const declared = (cur.attribution ?? []).reduce((sum, a) => sum + (Number(a.lines) || 0), 0);
    if (delta !== declared) {
      return {
        error: `LOC-BASELINE-ATTRIBUTION-GAP history[${i}] ${cur.date}: measured delta ${delta} but attribution declares ${declared} (difference ${delta - declared})`,
      };
    }
    for (const entry of cur.attribution ?? []) {
      for (const rel of entry.files ?? []) {
        if (!existsSync(join(ROOT, rel))) {
          return { error: `LOC-BASELINE-ATTRIBUTION-MISSING-FILE history[${i}] ${cur.date}: ${rel} does not exist` };
        }
      }
    }
  }
  const genesis = history[0];
  const lastDeclared = history.length > 1 ? history[history.length - 1].measured - history[history.length - 2].measured : 0;
  return { budget, genesis, last, lastDeclared, entries: history.length };
}

const baseline = readLocBaseline();
const CORE_LOC_BUDGET = baseline.error ? null : baseline.budget;
if (baseline.error) violations.push(`VIOLATION D-03a ${baseline.error}`);

let coreLoc = 0;
let coreFiles = 0;
if (existsSync(CORE_DIR)) {
  for (const file of walk(CORE_DIR)) {
    if (extname(file).toLowerCase() !== '.cs') continue;
    const src = readText(file);
    if (src === null) continue;
    const lines = countLines(src);
    coreFiles += 1;
    if (!hasSegment(file, SKIP_TEST_DIRS)) coreLoc += lines;
    if (lines > MAX_FILE_LINES) {
      violations.push(
        `VIOLATION D-03c ${relPath(file)} ${lines} lines exceeds MAX_FILE_LINES ${MAX_FILE_LINES}`
      );
    }
  }
}
if (CORE_LOC_BUDGET !== null && coreLoc > CORE_LOC_BUDGET) {
  violations.push(
    `VIOLATION D-03a packages/core ${coreLoc} lines exceeds budget ${CORE_LOC_BUDGET} (declare the raise in tools/core-loc-baseline.json history, RFC-11)`
  );
}

// D-03b: concept blacklist, case-insensitive, per line
const blacklistRes = BLACKLIST.map((word) => ({ word, re: new RegExp(escapeRe(word), 'gi') }));
if (existsSync(CORE_DIR)) {
  for (const file of walk(CORE_DIR)) {
    if (!TEXT_EXTS.has(extname(file).toLowerCase())) continue;
    const src = readText(file);
    if (src === null) continue;
    const lines = src.split(/\r?\n/);
    for (let i = 0; i < lines.length; i += 1) {
      for (const { re } of blacklistRes) {
        re.lastIndex = 0;
        const m = re.exec(lines[i]);
        if (m) violations.push(`VIOLATION D-03b ${relPath(file)}:${i + 1} ${m[0]}`);
      }
    }
  }
}

// D-04a: temporary markers in source, all layers under packages/
const markerRe = new RegExp(`\\b(${TEMP_MARKERS.join('|')})\\b`, 'i');
if (existsSync(PACKAGES_DIR)) {
  for (const file of walk(PACKAGES_DIR)) {
    if (!SOURCE_EXTS.has(extname(file).toLowerCase())) continue;
    const src = readText(file);
    if (src === null) continue;
    const lines = src.split(/\r?\n/);
    for (let i = 0; i < lines.length; i += 1) {
      const m = markerRe.exec(lines[i]);
      if (m) violations.push(`VIOLATION D-04a ${relPath(file)}:${i + 1} ${m[0]}`);
    }
  }
}

console.log(`STAT core-loc ${coreLoc}/${CORE_LOC_BUDGET === null ? '?' : CORE_LOC_BUDGET}`);
console.log(`STAT core-files ${coreFiles}`);
if (!baseline.error) {
  // Trend line for AM-03: growth since the genesis measurement, plus what the last
  // declaration accounted for. This is the "observation" side of the anti-metric.
  const sinceGenesis = coreLoc - baseline.genesis.measured;
  const last = baseline.lastDeclared >= 0 ? `+${baseline.lastDeclared}` : `${baseline.lastDeclared}`;
  console.log(
    `STAT core-loc-trend ${sinceGenesis >= 0 ? '+' : ''}${sinceGenesis} since ${baseline.genesis.measured} (${baseline.genesis.date}); last declared ${last} in history[${baseline.entries - 1}]`
  );
}
for (const v of violations) console.log(v);
if (violations.length > 0) {
  console.log('LINT-CORE-FAIL');
  process.exit(1);
}
console.log('LINT-CORE-PASS');

// lint-core.mjs - machine gates D-03a / D-03b / D-03c / D-04a
// Spec: docs/40-development-paradigm.md sections 3-4 (rules D-03, D-04a).
// Zero npm dependencies, Node built-ins only. Run: node tools/lint-core.mjs
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { extname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)));
const PACKAGES_DIR = join(ROOT, 'packages');
const CORE_DIR = join(PACKAGES_DIR, 'core');

const CORE_LOC_BUDGET = 9500; // D-03a
const MAX_FILE_LINES = 400; // D-03c
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

// D-03a + D-03c: kernel source size (D-03a excludes test dirs, D-03c covers them)
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
if (coreLoc > CORE_LOC_BUDGET) {
  violations.push(
    `VIOLATION D-03a packages/core ${coreLoc} lines exceeds CORE_LOC_BUDGET ${CORE_LOC_BUDGET}`
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

console.log(`STAT core-loc ${coreLoc}/${CORE_LOC_BUDGET}`);
console.log(`STAT core-files ${coreFiles}`);
for (const v of violations) console.log(v);
if (violations.length > 0) {
  console.log('LINT-CORE-FAIL');
  process.exit(1);
}
console.log('LINT-CORE-PASS');

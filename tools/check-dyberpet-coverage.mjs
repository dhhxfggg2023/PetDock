#!/usr/bin/env node
// tools/check-dyberpet-coverage.mjs — D-13c (docs/41 §5.3 / AM-01) coverage gate.
//
// Authority model (per t4 task §2): docs/15-asset-format-layer.md §3 '## 3. 已批准的格式（v1）'
// is the SINGLE authoritative declaration of supported formats — this script reads it DIRECTLY
// (it parses the §3 markdown table). The DyberPet adapter's concrete layout split (flat + nested)
// is NOT enumerated by docs/15 §3 on its own, so it is carried as a derived EXTRACTION in
// tools/dyberpet-layouts.json, whose `authority` field states: 'docs/15 §3 is authoritative;
// this file is an extraction' and carries a consistency self-proof (below) that is verified
// against docs/15 §3 at runtime — if docs/15 §3 ever drops DyberPet, this gate fails instead of
// silently going stale.
//
// Rule (AM-01 / docs/41 D-13c, evidence source per captain ruling t8): a declared layout is
// covered iff the GENERATOR can actually produce a fixture tree for it — the evidence is the
// freshly generated temp trees, NOT the working tree (tests/fixtures/dyberpet-min/** is a
// regenerable artifact, gitignored; a fresh clone has none and lint must still be green).
//   declared > generator-evidence  -> FAIL (phantom layout with no producible fixture).
// Drift: committed trees are checked against the generator output ONLY where they exist
// (absent is fine — regenerable). Negative-test note: renaming the fixture tree in place
// (dyberpet-min -> dyberpet-min-moved) is NOT a negative case — findTreeDir() recursion
// still finds it; a real absence test must move the tree out of the fixtures scan root.
//
// Usage: node tools/check-dyberpet-coverage.mjs [--root <repo-root>] [--fixtures-dir <rel-to-root>]

import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, resolve, relative } from 'node:path';
import { tmpdir } from 'node:os';
import { mkdtempSync, rmSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
const ROOT = resolve(fileURLToPath(new URL('.', import.meta.url)), '..');

function docs15Formats() {
  const p = join(ROOT, 'docs', '15-asset-format-layer.md');
  if (!existsSync(p)) throw new Error('authority missing: docs/15-asset-format-layer.md §3');
  const md = readFileSync(p, 'utf8');
  const i = md.indexOf('## 3.');
  const j = md.search(/\n## 4/);
  const sec3 = i >= 0 ? md.slice(i, j < 0 ? md.length : j) : '';
  const rows = [];
  for (const m of sec3.matchAll(/\| \*\*(.+?)\*\* \|/g)) rows.push(m[1].trim());
  return rows;
}

function loadManifest() {
  const p = join(ROOT, 'tools', 'dyberpet-layouts.json');
  if (!existsSync(p)) throw new Error('extraction missing: tools/dyberpet-layouts.json');
  const m = JSON.parse(readFileSync(p, 'utf8'));
  if (!/docs\/15-asset-format-layer\.md \u00a73/.test(m.authority || '')) {
    throw new Error('manifest authority header missing/stale: tools/dyberpet-layouts.json must credit docs/15 \u00a73 as the single source of truth');
  }
  return m;
}

// t8 t7-6 自足性：先自调生成器产临时目录，再逐树对账。
// 哈希对账用生成器自己的 --hash 子命令（canonical 算法唯一所有权在生成器，不在本脚本重写）。
function hashViaGenerator(treeDir) {
  return execFileSync(
    process.execPath,
    [join(ROOT, 'tools', 'gen-dyberpet-fixture.mjs'), '--hash', treeDir],
    { encoding: 'utf8' },
  ).trim();
}

// AM-01 evidence source per captain ruling (t8): the GENERATOR, not the working tree.
// tmp is the freshly generated fixture set; a declared layout is covered iff the generator
// actually produced a tree for it. Returns number of produced trees (generator evidence).
function generatorEvidence(tmp, fixtureRoots) {
  let produced = 0;
  for (const r of fixtureRoots) {
    const fresh = join(tmp, r);
    const ok = existsSync(fresh) && existsSync(join(fresh, 'pet_conf.json'));
    console.log('  generator produces ' + r + (ok ? '  YES' : '  NO'));
    if (ok) produced += 1;
  }
  return produced;
}

// t8 t7-6 自足门禁：自调生成器产临时树 → drift 对账（仅对存在的已提交树）→ 返回生成器证据数。
function runCoverageGate(fixturesDir, fixtureRoots) {
  const tmp = mkdtempSync(join(tmpdir(), 'petdock-coverage-'));
  try {
    execFileSync(
      process.execPath,
      [join(ROOT, 'tools', 'gen-dyberpet-fixture.mjs'), '--out', tmp],
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
    );
    // drift 对账：tests/fixtures/dyberpet-min/** 是可再生生成物（gitignore），存在时必须
    // 与生成器产出逐树一致；不存在（全新 clone/被移走）不是失败——AM-01 的证据是生成器，
    // 不是工作树。
    let reconciled = 0;
    for (const r of fixtureRoots) {
      const committed = findTreeDir(fixturesDir, r);
      const fresh = join(tmp, r);
      if (committed === null) {
        console.log('  reconcile ' + r + ': ABSENT-from-worktree (skipped; evidence = generator)');
        continue;
      }
      const a = hashViaGenerator(committed);
      const b = hashViaGenerator(fresh);
      if (a !== b) {
        console.error('COVERAGE-FAIL: fixture drift ' + r +
          ' committed=' + a + ' regenerated=' + b +
          ' \u2014 re-run node tools/gen-dyberpet-fixture.mjs --out tests/fixtures/dyberpet-min');
        process.exit(1);
      }
      reconciled += 1;
      console.log('  reconcile ' + r + ': drift=0 sha256=' + a.slice(0, 12) + '\u2026');
    }
    console.log('reconcile: ' + reconciled + '/' + fixtureRoots.length + ' committed trees match generator output');
    return generatorEvidence(tmp, fixtureRoots);
  } finally {
    rmSync(tmp, { recursive: true, force: true });
  }
}

// 在 base 下递归找名为 name 的目录（夹具真实位置是 tests/fixtures/dyberpet-min/<root>，
// 不是 tests/fixtures/<root> 直接子目录）；找到第一个即返回绝对路径，找不到返回 null。
function findTreeDir(base, name) {
  if (!existsSync(base)) return null;
  for (const e of readdirSync(base, { withFileTypes: true })) {
    if (!e.isDirectory()) continue;
    if (e.name === name) return join(base, e.name);
    const nested = findTreeDir(join(base, e.name), name);
    if (nested !== null) return nested;
  }
  return null;
}

function main() {
  const argv = process.argv.slice(2);
  let root = ROOT, fixturesDir;
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--root') root = resolve(argv[++i]);
    else if (argv[i] === '--fixtures-dir') fixturesDir = join(root, argv[++i]);
  }
  if (!fixturesDir) fixturesDir = join(root, 'tests', 'fixtures');

  const declaredEcosystems = docs15Formats();
  const hasDyberPet = declaredEcosystems.some(r => /dyberpet/i.test(r));
  console.log('docs/15 \u00a73 declared formats (' + declaredEcosystems.length + '): ' + declaredEcosystems.join(' | '));
  if (!hasDyberPet) {
    console.error('COVERAGE-FAIL: docs/15 \u00a73 no longer declares DyberPet \u2014 manifest is stale.');
    process.exit(1);
  } else {
    console.log('self-proof: docs/15 \u00a73 declares DyberPet -> manifest authority confirmed');
  }

  const manifest = loadManifest();
  const fmt = manifest.formats.DyberPet;
  if (!fmt) { console.error('COVERAGE-FAIL: manifest declares no DyberPet entry'); process.exit(1); }
  const declared = fmt.layouts.length;

  // t8 t7-6：门禁自足 —— 自调生成器产临时树，drift 对账（仅已存在树），AM-01 以生成器产出为证据。
  const evidence = runCoverageGate(fixturesDir, fmt.fixture_roots);

  console.log('DyberPet declared layouts: ' + declared + ' -> [' + fmt.layouts.join(', ') + ']');
  // AM-01（证据源=生成器，t8 裁决）：declared 必须不超过生成器实际能产出的树数。
  // 工作树里有没有夹具（可再生生成物，gitignore，新 clone 没有）不影响本判定。
  const ok = declared <= evidence;
  console.log('COVERAGE-' + (ok ? 'PASS' : 'FAIL') + ' (declared=' + declared + ', evidence=' + evidence + ')');
  if (!ok) {
    console.log('AM-01/violation: declared(' + declared + ') > generator-evidence(' + evidence + ') -> FAIL');
    process.exit(1);
  }
  process.exit(0);
}

main();

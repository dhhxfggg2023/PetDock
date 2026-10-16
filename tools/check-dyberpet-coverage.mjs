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
// Rule (AM-01 / docs/41 D-13c): declared-layout count must NOT exceed fixture count.
//   declared > fixtures  -> FAIL.  (deleting a fixture must turn this red; regen -> green)
//
// Usage: node tools/check-dyberpet-coverage.mjs [--root <repo-root>] [--fixtures-dir <rel-to-root>]

import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
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

function findFixtureRoots(fixturesDir, roots) {
  const want = new Set(roots);
  const found = new Set();
  function walk(p) {
    if (!existsSync(p)) return;
    for (const e of readdirSync(p, { withFileTypes: true })) {
      if (e.isDirectory()) {
        if (want.has(e.name)) found.add(e.name);
        walk(join(p, e.name));
      }
    }
  }
  walk(fixturesDir);
  return found;
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
  const found = findFixtureRoots(fixturesDir, fmt.fixture_roots);

  console.log('DyberPet declared layouts: ' + declared + ' -> [' + fmt.layouts.join(', ') + ']');
  console.log('fixtures root scanned: ' + (root === ROOT ? 'tests/fixtures (default)' : relative(ROOT, fixturesDir)));
  for (const r of fmt.fixture_roots) {
    console.log('  ' + r + (found.has(r) ? '  PRESENT' : '  MISSING'));
  }
  const nf = found.size;
  const ok = declared <= nf;                          // AM-01: declared must not exceed fixtures
  console.log('COVERAGE-' + (ok ? 'PASS' : 'FAIL') + ' (declared=' + declared + ', fixtures=' + nf + ')');
  if (!ok) {
    console.log('AM-01/violation: declared(' + declared + ') > fixtures(' + nf + ') -> FAIL');
    process.exit(1);
  }
  process.exit(0);
}

main();

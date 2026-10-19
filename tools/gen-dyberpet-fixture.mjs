#!/usr/bin/env node
// tools/gen-dyberpet-fixture.mjs
// D-13b deterministic DyberPet synthetic-fixture constructor (zero npm deps, Node ESM, engines node>=20).
//
// Spec (binding): docs/handoff/M3-contract-frozen.md
//   §1.2 layout detection  §2.2-§2.7 frame/fps/loop/clip/naturalSize  §9 t4-note (act_num boundary + 0-frame action)
// Modeled on the real package assets/user/dyberpet/守岸人 (gitignored, read-only, NEVER copied in here —
// every pixel below is procedurally generated geometry, no third-party bytes).
//
// Usage:
//   node tools/gen-dyberpet-fixture.mjs --out <dir>          # emit BOTH dyberpet-flat + dyberpet-nested
//   node tools/gen-dyberpet-fixture.mjs --parse <fixture-dir> # observe: actions / count / fps / loop / provenance
//   node tools/gen-dyberpet-fixture.mjs --hash <fixture-dir>  # canonical sha256 of a fixture tree (idempotency)
//
// Exit codes: 0 on success, 1 on usage/detection error.

import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs';
import { join, relative, sep, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { deflateSync } from 'node:zlib';

const ROOT = resolve(fileURLToPath(new URL('.', import.meta.url)), '..');
const DEFAULT_OUT = join(ROOT, 'tests', 'fixtures', 'dyberpet-min');

// ---------------------------------------------------------------------------
// PNG encoder (CRC32 / IHDR / IDAT-deflateSync / IEND) — same proven pattern
// as tools/gen-sample-pet.mjs (CC0 program output).
const CRC = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1); t[n] = c; }
  return t;
})();
function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = (c >>> 8) ^ CRC[(c ^ buf[i]) & 0xff];
  return (c ^ 0xffffffff) >>> 0;
}
function pngChunk(type, data) {
  const tbuf = Buffer.from(type, 'ascii');
  const body = Buffer.concat([tbuf, data]);
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length, 0);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([len, body, crc]);
}
function encodePng(w, h, rgba) {
  const sig = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0; // 8-bit RGBA
  const stride = w * 4;
  const raw = Buffer.alloc((stride + 1) * h);
  for (let y = 0; y < h; y++) { raw[y * (stride + 1)] = 0; raw.set(rgba.subarray(y * stride, (y + 1) * stride), y * (stride + 1) + 1); }
  const idat = deflateSync(raw, { level: 9 });
  return Buffer.concat([sig, pngChunk('IHDR', ihdr), pngChunk('IDAT', idat), pngChunk('IEND', Buffer.alloc(0))]);
}

// ---------------------------------------------------------------------------
// Deterministic synthetic geometry (a pet-disk + eyes per frame).
const CW = 64, CH = 64;
const COLORS = { default: [60, 120, 220], walk: [60, 180, 90], sleep: [230, 170, 50], seeview: [150, 90, 210], ghost: [210, 120, 120] };
function colorOf(images) { return COLORS[images] || [130, 130, 130]; }
function fresh() { return new Uint8Array(CW * CH * 4); } // transparent RGBA
function px(pxa, x, y, r, g, b) { if (x < 0 || y < 0 || x >= CW || y >= CH) return; const i = (y * CW + x) * 4; pxa[i] = r; pxa[i + 1] = g; pxa[i + 2] = b; pxa[i + 3] = 255; }
function disk(pxa, cx, cy, rad, rgb) { const r2 = rad * rad; for (let y = Math.max(0, cy - rad); y <= Math.min(CH - 1, cy + rad); y++) for (let x = Math.max(0, cx - rad); x <= Math.min(CW - 1, cx + rad); x++) { const dx = x - cx, dy = y - cy; if (dx * dx + dy * dy <= r2) px(pxa, x, y, rgb[0], rgb[1], rgb[2]); } }
function hline(pxa, x0, x1, y, rgb) { for (let x = Math.max(0, x0); x <= Math.min(CW - 1, x1); x++) px(pxa, x, y, rgb[0], rgb[1], rgb[2]); }
function makeFrame(images, i, n) {
  const pxa = fresh(); const rgb = colorOf(images);
  // bob: phase over i/n (not i/(n-1)) so adjacent samples never coincide —
  // with i/(n-1) and n=3, sin(0)=sin(π)=sin(2π)=0 produced three identical frames (t7 review).
  const bob = n > 1 ? Math.round(Math.sin((i / n) * Math.PI * 2) * 5) : 0;
  const cy = 36 + bob;
  disk(pxa, 32, cy, 20, rgb);                      // body
  disk(pxa, 25, cy - 6, 6, [250, 250, 255]);       // left eye white
  disk(pxa, 39, cy - 6, 6, [250, 250, 255]);       // right eye white
  const closed = images === 'sleep' ? (i % 2 === 1) : (n === 2 && i === 1);
  if (!closed) { disk(pxa, 25, cy - 6, 3, [25, 25, 50]); disk(pxa, 39, cy - 6, 3, [25, 25, 50]); }
  else { hline(pxa, 19, 31, cy - 6, [45, 45, 70]); hline(pxa, 33, 45, cy - 6, [45, 45, 70]); }
  const png = encodePng(CW, CH, pxa);
  if (i > 0 && png.equals(lastPng)) {
    // Self-check: frames inside one action must not be pixel-identical, or any
    // consumer asserting per-frame motion (e.g. --animtest C1) silently degrades.
    console.error(`FIXTURE-FAIL: ${images} frame ${i} is pixel-identical to frame ${i - 1}`);
    process.exit(1);
  }
  lastPng = png;
  return { png, w: CW, h: CH };
}
let lastPng = null;

// ---------------------------------------------------------------------------
// Fixture spec. act_num is intentionally set to DELIBERATELY mismatch the file
// count (§2.3: act_num is provenance-only, not consumed; frame count = files).
//   default act_num=10 > 3 files      (boundary)
//   left_walk act_num=4 > 2 files     (boundary; images='walk' != name -> tests prefix rule)
//   sleep   act_num=20 > 2 files      (越界 case — reproduces real sleep)
//   seeview act_num=1 < 2 files       (boundary; frame_refresh=0.080 -> 12.5 fps special case)
//   ghost   frames=0                 (triggers dyberpet-clip-skipped no-frames)
const ACTIONS = [
  { name: 'default',   images: 'default', act_num: 10, refresh: 0.120, frames: 3 },
  { name: 'left_walk', images: 'walk',    act_num:  4, refresh: 0.120, frames: 2, need_move: true, direction: 'left', frame_move: 10 },
  { name: 'sleep',     images: 'sleep',   act_num: 20, refresh: 0.120, frames: 2 },
  { name: 'seeview',   images: 'seeview', act_num:  1, refresh: 0.080, frames: 2 },
  { name: 'ghost',     images: 'ghost',   act_num:  5, refresh: 0.120, frames: 0 },
];
// pet_conf minimal-but-real-shaped (LoopSet keys: default/left/right/up/down -> action names).
const PET_CONF = {
  width: 360, height: 360, scale: 1, refresh: 5, interact_speed: 0.02,
  default: 'default', up: 'up', down: 'down', left: 'left_walk', right: 'right_walk',
  drag: 'drag', fall: 'fall', on_floor: 'onfloor', focus: 'seeview',
  random_act: [
    { name: 'idle',  act_list: ['default'],             act_prob: 9 },
    { name: 'move',  act_list: ['left_walk', 'right_walk', 'default'], act_prob: 7 },
  ],
};

function ensureDir(p) { mkdirSync(p, { recursive: true }); }
function writeJson(p, o) { writeFileSync(p, JSON.stringify(o, null, 2) + '\n', 'utf8'); }
function actionConf(a) {
  const o = { images: a.images, act_num: a.act_num, frame_refresh: a.refresh };
  if (a.need_move) { o.need_move = a.need_move; o.direction = a.direction; o.frame_move = a.frame_move; }
  return o;
}
function frameName(a, n) { return `${a.images}_${n}.png`; }

function writeFlat(out) {
  ensureDir(out); ensureDir(join(out, 'action'));
  writeJson(join(out, 'pet_conf.json'), PET_CONF);
  const conf = {};
  for (const a of ACTIONS) conf[a.name] = actionConf(a);
  writeJson(join(out, 'act_conf.json'), conf);
  for (const a of ACTIONS) for (let n = 0; n < a.frames; n++)
    writeFileSync(join(out, 'action', frameName(a, n)), makeFrame(a.images, n, a.frames).png);
  return listFiles(out);
}
function writeNested(out) {
  ensureDir(out); const ad = join(out, 'actions'); ensureDir(ad);
  writeJson(join(out, 'pet_conf.json'), PET_CONF);
  writeJson(join(ad, 'actions_conf.json'), { version: 1, actions: ACTIONS.map(a => a.name) });
  for (const a of ACTIONS) {
    const d = join(ad, a.name); ensureDir(d);
    const files = [];
    for (let n = 0; n < a.frames; n++) { files.push(frameName(a, n)); writeFileSync(join(d, frameName(a, n)), makeFrame(a.images, n, a.frames).png); }
    writeJson(join(ad, `${a.name}_conf.json`), { ...actionConf(a), frames: files });
  }
  return listFiles(out);
}

function listFiles(dir) {
  let all = [];
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const fp = join(dir, e.name);
    if (e.isDirectory()) all = all.concat(listFiles(fp));
    else all.push(fp);
  }
  return all.sort();
}
function hashTree(files, root) {
  const h = createHash('sha256');
  for (const f of files) {
    h.update(relative(root, f).split(sep).join('/') + '\n');
    h.update(createHash('sha256').update(readFileSync(f)).digest('hex') + '\n');
  }
  return h.digest('hex');
}

// ---------------------------------------------------------------------------
// Observation (reference parser for t3/t5): count actual frame FILES, derive
// fps=1/frame_refresh, loop via LoopSet (§2.5), flag act_num provenance + skips.
function probeLayout(dir) {
  const hasPet = existsSync(join(dir, 'pet_conf.json'));
  const hasFlat = existsSync(join(dir, 'act_conf.json'));
  const hasNested = existsSync(join(dir, 'actions', 'actions_conf.json'));
  if (!hasPet) return { layout: 'None', confidence: 0.0, evidence: ['pet_conf.json missing'], audit: [] };
  const ev = ['pet_conf.json'];
  if (hasFlat && !hasNested) return { layout: 'DyberPetFlat',  confidence: 0.9, evidence: ev.concat(['act_conf.json']), audit: [] };
  if (hasNested && !hasFlat) return { layout: 'DyberPetNested', confidence: 0.9, evidence: ev.concat(['actions/actions_conf.json']), audit: [] };
  if (hasFlat && hasNested)  return { layout: 'DyberPetNested', confidence: 0.9, evidence: ev.concat(['act_conf.json', 'actions/actions_conf.json']), audit: ['dyberpet-layout-ambiguous chosen=nested'] };
  return { layout: 'None', confidence: 0.4, evidence: ['pet_conf.json only (no action config)'], audit: ['dyberpet-layout-ambiguous rejected: no action configuration'] };
}
function loopSet(pet) { return new Set([pet.default, pet.left, pet.right, pet.up, pet.down].filter(Boolean)); }
function fpsOf(refresh) {
  const r = (typeof refresh === 'number' && refresh > 0) ? refresh : 0.120;
  return Math.round((1 / r) * 1000) / 1000; // 0.120 -> 8.333
}
function enumFiles(dir, prefix) { // count <prefix>_<n>.png, n from 0 contiguous
  let n = 0;
  while (existsSync(join(dir, `${prefix}_${n}.png`))) n++;
  return n;
}
function provNote(act_num, fn) {
  if (act_num == null) return '';
  if (fn === 0) return 'SKIPPED no-frames';
  if (act_num > fn) return `act_num=${act_num} > frames=${fn} (越界)`;
  if (act_num < fn) return `act_num=${act_num} < frames=${fn} (boundary)`;
  return `act_num=${act_num} == frames=${fn}`;
}
function parseFixture(dir) {
  const abs = resolve(dir);
  if (!existsSync(join(abs, 'pet_conf.json'))) { console.error(`PARSE-FAIL: ${abs} is not a fixture (no pet_conf.json)`); return 1; }
  const probe = probeLayout(abs);
  for (const a of probe.audit) console.log('AUDIT ' + a);
  if (probe.layout === 'None') { console.error('PARSE-FAIL: layout not detected/rejected'); return 1; }
  console.log(`layout=${probe.layout} confidence=${probe.confidence} source=${relative(ROOT, abs)}`);
  const pet = JSON.parse(readFileSync(join(abs, 'pet_conf.json'), 'utf8'));
  const loops = loopSet(pet);
  const rows = []; let declared = 0, clips = 0, skipped = 0;
  if (probe.layout === 'DyberPetFlat') {
    const conf = JSON.parse(readFileSync(join(abs, 'act_conf.json'), 'utf8'));
    for (const [name, c] of Object.entries(conf)) {
      declared++; const fn = enumFiles(join(abs, 'action'), c.images);
      const isLoop = loops.has(name);
      if (fn === 0) { skipped++; rows.push({ name, images: c.images, act_num: c.act_num, frames: 0, fps: fpsOf(c.frame_refresh), loop: isLoop ? 'loop' : 'once', prov: 'SKIPPED no-frames' }); }
      else { clips++; rows.push({ name, images: c.images, act_num: c.act_num, frames: fn, fps: fpsOf(c.frame_refresh), loop: isLoop ? 'loop' : 'once', prov: provNote(c.act_num, fn) }); }
    }
  } else { // nested
    const acl = JSON.parse(readFileSync(join(abs, 'actions', 'actions_conf.json'), 'utf8'));
    const list = Array.isArray(acl) ? acl : (acl.actions || Object.keys(acl));
    for (const name of list) {
      declared++; const c = JSON.parse(readFileSync(join(abs, 'actions', `${name}_conf.json`), 'utf8'));
      const fn = enumFiles(join(abs, 'actions', name), c.images);
      const isLoop = loops.has(name);
      if (fn === 0) { skipped++; rows.push({ name, images: c.images, act_num: c.act_num, frames: 0, fps: fpsOf(c.frame_refresh), loop: isLoop ? 'loop' : 'once', prov: 'SKIPPED no-frames' }); }
      else { clips++; rows.push({ name, images: c.images, act_num: c.act_num, frames: fn, fps: fpsOf(c.frame_refresh), loop: isLoop ? 'loop' : 'once', prov: provNote(c.act_num, fn) }); }
    }
  }
  console.log(`actions_declared=${declared} clips=${clips} skipped=${skipped} loopSet=[${[...loops].join(',')}]`);
  for (const r of rows) {
    console.log(`  ${r.name.padEnd(10)} images=${String(r.images).padEnd(8)} frames=${r.frames} fps=${r.fps.toFixed(3)} loop=${r.loop.padEnd(5)} act_num=${String(r.act_num == null ? '-' : r.act_num).padEnd(3)} | ${r.prov}`);
  }
  console.log('PARSE-OK');
  return 0;
}

// ---------------------------------------------------------------------------
function main() {
  const argv = process.argv.slice(2);
  if (argv[0] === '--parse') { const d = argv[1]; if (!d) { console.error('--parse <dir> required'); process.exit(1); } process.exit(parseFixture(d)); }
  if (argv[0] === '--hash') { const d = argv[1]; if (!d) { console.error('--hash <dir> required'); process.exit(1); } const files = listFiles(resolve(d)); process.stdout.write(hashTree(files, resolve(d)) + '\n'); process.exit(0); }
  let out = DEFAULT_OUT;
  for (let i = 0; i < argv.length; i++) { if (argv[i] === '--out') out = argv[++i]; }
  if (!out) { console.error('--out <dir> required'); process.exit(1); }
  const abs = resolve(out);
  const flatFiles = writeFlat(join(abs, 'dyberpet-flat'));
  const nestedFiles = writeNested(join(abs, 'dyberpet-nested'));
  const all = listFiles(abs);
  const h = hashTree(all, abs);
  console.log(`WROTE flat=${flatFiles.length} nested=${nestedFiles.length} total=${all.length} to ${relative(ROOT, abs)}`);
  console.log('SHA256 ' + h);
  console.log('FIXTURE-ID dyberpet-min sha256=' + h);
  process.exit(0);
}
main();

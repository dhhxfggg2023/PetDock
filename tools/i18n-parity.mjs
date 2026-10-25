#!/usr/bin/env node
// RFC-42 §4.2 `ui-i18n-key-parity`：两份资源表的键集合必须完全相等，且每条键的占位符形状一致。
//
// 为什么是它、不是什么：
//   - 它只比**键**与**占位符形状**（`{0}` `{1}` 的下标与个数），不比文字内容 —— 翻译地不地道是人眼的事
//     （RFC-42 §5 第 6 条），机器判据只该判「翻漏了」与「占位符少了一个会抛 FormatException」。
//   - 它**不**检查「每条键都被代码用到」：反向检查会产生死键告警噪声，RFC-42 §4.3 已否决。
//
// 退出码：绿 0（末行 I18N-PARITY-PASS），红 1（末行 I18N-PARITY-FAIL，逐条列出差异）。
// 用法：node tools/i18n-parity.mjs [--dir <目录>]
import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

const DEFAULT_DIR = 'assets/i18n';
const PLACEHOLDER = /\{(\d+)(?:[^}]*)\}/g;

function parseArgs(argv) {
  const out = { dir: DEFAULT_DIR };
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--dir') {
      out.dir = argv[++i] ?? DEFAULT_DIR;
    } else {
      console.error(`unknown argument: ${argv[i]}`);
      process.exit(2);
    }
  }
  return out;
}

function loadTable(path) {
  let raw;
  try {
    raw = readFileSync(path, 'utf8');
  } catch (err) {
    return { path, error: `cannot read: ${err.message}` };
  }
  let parsed;
  try {
    parsed = JSON.parse(raw);
  } catch (err) {
    return { path, error: `invalid json: ${err.message}` };
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    return { path, error: 'root must be an object of key -> string' };
  }
  const table = new Map();
  const bad = [];
  for (const [key, value] of Object.entries(parsed)) {
    if (typeof value !== 'string') {
      bad.push(`${key}: value is ${typeof value}, expected string`);
    } else if (key.includes(' ') || key.includes('/')) {
      bad.push(`${key}: key contains a space or slash`);
    } else {
      table.set(key, value);
    }
  }
  if (bad.length > 0) {
    return { path, error: bad.join('; ') };
  }
  return { path, table };
}

function placeholders(text) {
  const found = [];
  for (const m of text.matchAll(PLACEHOLDER)) {
    found.push(Number(m[1]));
  }
  return found.sort((a, b) => a - b).join(',');
}

function main() {
  const { dir } = parseArgs(process.argv.slice(2));
  const root = resolve(dir);

  let files;
  try {
    files = readdirSync(root).filter((n) => n.endsWith('.json')).sort();
  } catch (err) {
    console.log(`I18N-PARITY-FAIL cannot list ${dir}: ${err.message}`);
    return 1;
  }
  if (files.length < 2) {
    console.log(`I18N-PARITY-FAIL need at least two tables under ${dir}, found ${files.length}`);
    return 1;
  }

  const loaded = files.map((name) => loadTable(join(root, name)));
  const broken = loaded.filter((t) => t.error);
  if (broken.length > 0) {
    for (const t of broken) {
      console.log(`I18N-PARITY-FAIL ${t.path}: ${t.error}`);
    }
    return 1;
  }

  const base = loaded[0];
  const problems = [];
  for (const other of loaded.slice(1)) {
    for (const key of base.table.keys()) {
      if (!other.table.has(key)) {
        problems.push(`${other.path.split(/[\\/]/).pop()}: missing key "${key}"`);
      }
    }
    for (const key of other.table.keys()) {
      if (!base.table.has(key)) {
        problems.push(`${other.path.split(/[\\/]/).pop()}: extra key "${key}"`);
      }
    }
    for (const [key, value] of other.table) {
      if (!base.table.has(key)) {
        continue;
      }
      const a = placeholders(base.table.get(key));
      const b = placeholders(value);
      if (a !== b) {
        problems.push(`${key}: placeholder shape differs ({${a}} vs {${b}})`);
      }
    }
  }

  if (problems.length > 0) {
    for (const p of problems) {
      console.log(`I18N-PARITY-DIFF ${p}`);
    }
    console.log(`I18N-PARITY-FAIL problems=${problems.length} tables=${loaded.length} keys=${base.table.size}`);
    return 1;
  }

  const summary = loaded
    .map((t) => `${t.path.split(/[\\/]/).pop().replace(/\.json$/, '')}=${t.table.size}`)
    .join(' ');
  console.log(`I18N-PARITY-PASS keys=${base.table.size} ${summary} placeholders=ok`);
  return 0;
}

process.exit(main());

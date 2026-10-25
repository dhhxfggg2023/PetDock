#!/usr/bin/env node
// RFC-42 §4.2 `ui-i18n-key-parity`：每门语言的资源表（`<lang>.json` + `<lang>/*.json` 分片合并后）
// 键集合必须完全相等，且每条键的占位符形状一致。
//
// 为什么是它、不是什么：
//   - 只比**键**与**占位符形状**（`{0}` `{1}` 的下标与个数），不比文字内容 —— 翻译地不地道是人眼的事
//     （RFC-42 §5 第 6 条），机器判据只该判「翻漏了」与「占位符少一个会抛 FormatException」。
//   - **不**检查「每条键都被代码用到」：反向检查会产生死键告警噪声，RFC-42 §4.3 已否决。
//
// 退出码：绿 0（末行 I18N-PARITY-PASS），红 1（末行 I18N-PARITY-FAIL）。
// 用法：node tools/i18n-parity.mjs [--dir <目录>]
import { readFileSync, readdirSync, statSync } from 'node:fs';
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

function readTableFile(path, into, problems) {
  let parsed;
  try {
    parsed = JSON.parse(readFileSync(path, 'utf8'));
  } catch (err) {
    problems.push(`${path}: invalid json: ${err.message}`);
    return;
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
    problems.push(`${path}: root must be an object of key -> string`);
    return;
  }
  for (const [key, value] of Object.entries(parsed)) {
    if (typeof value !== 'string') {
      problems.push(`${path}: ${key}: value is ${typeof value}, expected string`);
      continue;
    }
    if (key.includes(' ') || key.includes('/')) {
      problems.push(`${path}: ${key}: key contains a space or slash`);
      continue;
    }
    if (into.has(key)) {
      problems.push(`${key}: defined twice (${into.get(key).from} and ${path}) - first wins at runtime, so one of them is dead`);
      continue;
    }
    into.set(key, { value, from: path });
  }
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

  let entries;
  try {
    entries = readdirSync(root).sort();
  } catch (err) {
    console.log(`I18N-PARITY-FAIL cannot list ${dir}: ${err.message}`);
    return 1;
  }

  const languages = [];
  const shards = new Map();
  for (const name of entries) {
    const full = join(root, name);
    const isDir = statSync(full).isDirectory();
    if (isDir) {
      languages.push(name);
      shards.set(
        name,
        readdirSync(full)
          .filter((n) => n.endsWith('.json'))
          .sort()
          .map((n) => join(full, n)),
      );
    } else if (name.endsWith('.json')) {
      const lang = name.replace(/\.json$/, '');
      // `<lang>/` and `<lang>.json` both exist: the file branch must NOT clear the shard list read
      // above, and must not push the language twice (duplicates made this gate report a fake
      // `keys=11 en-US=11 en-US=11` green while never reading a single shard).
      if (!shards.has(lang)) {
        languages.push(lang);
        shards.set(lang, []);
      }
    }
  }

  if (languages.length < 2) {
    console.log(`I18N-PARITY-FAIL need at least two languages under ${dir}, found ${languages.length}`);
    return 1;
  }

  const problems = [];
  const tables = new Map();
  for (const lang of languages) {
    const table = new Map();
    const single = join(root, `${lang}.json`);
    if (entries.includes(`${lang}.json`)) {
      readTableFile(single, table, problems);
    }
    for (const shard of shards.get(lang) ?? []) {
      readTableFile(shard, table, problems);
    }
    tables.set(lang, table);
  }

  if (problems.length > 0) {
    for (const p of problems) {
      console.log(`I18N-PARITY-DIFF ${p}`);
    }
    console.log(`I18N-PARITY-FAIL problems=${problems.length} tables=${languages.length}`);
    return 1;
  }

  const base = tables.get(languages[0]);
  for (const lang of languages.slice(1)) {
    const other = tables.get(lang);
    for (const key of base.keys()) {
      if (!other.has(key)) problems.push(`${lang}: missing key "${key}"`);
    }
    for (const key of other.keys()) {
      if (!base.has(key)) problems.push(`${lang}: extra key "${key}"`);
    }
    for (const [key, entry] of other) {
      if (!base.has(key)) continue;
      const a = placeholders(base.get(key).value);
      const b = placeholders(entry.value);
      if (a !== b) problems.push(`${key}: placeholder shape differs ({${a}} vs {${b}}) in ${lang}`);
    }
  }

  if (problems.length > 0) {
    for (const p of problems) {
      console.log(`I18N-PARITY-DIFF ${p}`);
    }
    console.log(`I18N-PARITY-FAIL problems=${problems.length} tables=${languages.length}`);
    return 1;
  }

  const summary = languages.map((lang) => `${lang}=${tables.get(lang).size}`).join(' ');
  console.log(`I18N-PARITY-PASS keys=${base.size} ${summary} placeholders=ok`);
  return 0;
}

process.exit(main());

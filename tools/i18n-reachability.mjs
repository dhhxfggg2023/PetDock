#!/usr/bin/env node
// tools/i18n-reachability.mjs —— 静态层第 9 步（2026-10-31）：「每一条文案键都必须有一条路能走到」。
//
// 为什么非有不可（HANDOVER §4.1）：八道静态门与 282 条 UI 判据覆盖的全是**内部自洽** ——
// i18n-parity 只问「两种语言的键一样吗」，两边都在它就绿。于是插件市场的 15 条文案、
// 后端与判据全绿，而界面上根本没有那个入口：整块功能不可达，没有一条门禁说过一句话。
//
// 三件判定（第 2、3 条是这套机制的关键：**豁免会烂**）
//   1. 表里每条键：要么被产品代码引用（字面量精确命中，或落在已登记的动态前缀下），
//      要么在 tools/i18n-keys-manifest.json 里带着理由被显式豁免；
//   2. 登记的动态前缀必须真的出现在代码里，且至少覆盖一条现存键 —— 否则它是过期的；
//   3. 豁免必须仍然成立（键还在、且仍然没人引用）—— 否则它也是过期的。
// 功能补齐之后忘了撤豁免，下一次「不可达」就会被旧豁免掩盖，所以「豁免不再需要」本身是失败。
//
// 扫描面**只算产品 C# 源码**（内核 src、plugins、sdk）。为什么不是「顺手也扫 tools」：
//   2026-10-31 实测，把 tools/ 一起扫时，本仓库自己的注释里写了一句 "cp.market.title"
//   （就在 tools/lib/cs-source.mjs 的说明里），于是那条死键被自己的文档判成「已引用」。
//   开发工具不是用户走得到的那条路：工具引用了某条键，恰恰说明它是给开发者看的。
//   产品路径只有 C#（内核 + 插件）；将来若出现声明式页面（清单里引用键），再按那时的形态扩面。
//
// 不判什么：不判文案好不好、翻译地不地道（那是人眼的事），也不判界面上真的画得出来 ——
// 它只证明「有代码引用这条键」；点得到点不到归 --uitest 与人眼。
//
// 退出码：0 = 绿（末行 I18N-REACHABILITY-PASS）；1 = 红（末行 I18N-REACHABILITY-FAIL）。
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep, extname } from 'node:path';
import { stringLiterals, looksLikeKey } from './lib/cs-source.mjs';

const ROOT = process.cwd();
const I18N_DIR = join(ROOT, 'assets', 'i18n');
const MANIFEST_PATH = join(ROOT, 'tools', 'i18n-keys-manifest.json');

const CORE = join(ROOT, 'packages', 'core', 'PetDock.Core');
// 产品代码面 = 内核源码 + 插件（页面插件、大脑、陪伴层…）+ SDK。
const PRODUCT_ROOTS = [join(CORE, 'src'), join(ROOT, 'packages', 'plugins'), join(ROOT, 'packages', 'sdk')];
// test/ 单独统计：「只有判据引用」是一个陷阱 —— 键看起来有人用，用户在界面上永远看不到它
// （HANDOVER §3.4 那 18 条就是这么找出来的）。
const TEST_ROOTS = [join(CORE, 'test')];
const SKIP_DIRS = new Set(['obj', 'bin', 'node_modules', '.git', 'artifacts']);

function walk(dir, out) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const entry of entries) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue;
      walk(join(dir, entry.name), out);
    } else if (extname(entry.name) === '.cs') {
      out.push(join(dir, entry.name));
    }
  }
  return out;
}

const rel = (file) => relative(ROOT, file).split(sep).join('/');

/** 读文案表：与 i18n-parity 同一套发现规则（<lang>/ 分片与 <lang>.json 单文件都算）。 */
function loadTables(problems) {
  const keys = new Map();
  const languages = [];
  for (const name of readdirSync(I18N_DIR).sort()) {
    const full = join(I18N_DIR, name);
    const isDir = statSync(full).isDirectory();
    if (!isDir && !name.endsWith('.json')) continue;
    const lang = isDir ? name : name.replace(/\.json$/, '');
    if (!languages.includes(lang)) languages.push(lang);
    const files = isDir
      ? readdirSync(full).filter((n) => n.endsWith('.json')).sort().map((n) => join(full, n))
      : [full];
    for (const file of files) {
      let parsed;
      try {
        parsed = JSON.parse(readFileSync(file, 'utf8'));
      } catch (err) {
        problems.push(`${rel(file)}: invalid json: ${err.message}`);
        continue;
      }
      for (const key of Object.keys(parsed)) {
        if (!keys.has(key)) keys.set(key, rel(file));
      }
    }
  }
  return { keys, languages };
}

/** 一组 C# 文件里的字面量（精确引用）与动态拼键候选。 */
function collect(files) {
  const exact = new Set();
  const dynamic = [];
  for (const file of files) {
    let text;
    try {
      text = readFileSync(file, 'utf8');
    } catch {
      continue;
    }
    for (const lit of stringLiterals(text)) {
      exact.add(lit.value);
      // 动态拼键：右边紧跟 + 的字面量，或内插串里 {} 之前那一段。
      const head = lit.value.includes('{') ? lit.value.slice(0, lit.value.indexOf('{')) : lit.value;
      if ((lit.concat || lit.interpolated) && looksLikeKey(head)) {
        dynamic.push({ value: head, file, line: lit.line });
      }
    }
  }
  return { exact, dynamic };
}

function main() {
  const problems = [];
  const { keys, languages } = loadTables(problems);
  let manifest;
  try {
    manifest = JSON.parse(readFileSync(MANIFEST_PATH, 'utf8'));
  } catch (err) {
    console.log(`I18N-REACHABILITY-FAIL 读不到 ${rel(MANIFEST_PATH)}: ${err.message}`);
    return 1;
  }

  const product = collect(PRODUCT_ROOTS.flatMap((root) => walk(root, [])));
  const tests = collect(TEST_ROOTS.flatMap((root) => walk(root, [])));

  const registered = new Map();
  for (const entry of manifest.dynamicPrefixes ?? []) {
    if (!entry?.prefix || !entry?.reason?.trim()) {
      problems.push(`manifest: dynamicPrefixes 每项都要有 prefix 与 reason: ${JSON.stringify(entry)}`);
      continue;
    }
    registered.set(entry.prefix, entry);
  }

  const observedPrefixes = new Set(product.dynamic.map((d) => d.value));
  const covers = (prefix) => [...keys.keys()].filter((key) => key !== prefix && key.startsWith(prefix));
  const referencedBy = (key) => {
    if (product.exact.has(key)) return 'product';
    for (const prefix of registered.keys()) {
      if (key !== prefix && key.startsWith(prefix)) return `dynamic:${prefix}`;
    }
    return null;
  };
  const testOnly = (key) =>
    tests.exact.has(key) || [...tests.dynamic].some((site) => key !== site.value && key.startsWith(site.value));

  // 1) 表里的键：引用或豁免，二选一。
  const exemptions = new Map();
  for (const entry of manifest.exempt ?? []) {
    if (!entry?.key || !entry?.reason?.trim() || !entry?.status) {
      problems.push(`manifest: exempt 每项都要有 key/status/reason: ${JSON.stringify(entry)}`);
      continue;
    }
    exemptions.set(entry.key, entry);
  }
  const unreachable = [];
  for (const key of keys.keys()) {
    if (referencedBy(key)) continue;
    unreachable.push(key);
    if (!exemptions.has(key)) {
      problems.push(
        `UNREACHABLE ${key} 没有产品代码引用（${keys.get(key)}）`
          + (testOnly(key) ? '；只有判据引用它，用户在界面上看不到' : '')
          + ' —— 要么把入口接上，要么在 tools/i18n-keys-manifest.json 里带理由登记',
      );
    }
  }

  // 2) 登记的动态前缀：必须在代码里真的出现，且至少覆盖一条键。
  for (const [prefix, entry] of registered) {
    if (!observedPrefixes.has(prefix)) {
      problems.push(`STALE-DYNAMIC ${prefix} 登记的动态前缀在代码里找不到（${entry.reason}）`);
      continue;
    }
    if (covers(prefix).length === 0) {
      problems.push(`STALE-DYNAMIC ${prefix} 不再覆盖任何现存键（${entry.reason}）`);
    }
  }

  // 3) 代码里真的在拼、却没登记的动态前缀 —— 动态拼键必须是一次有理由的决定。
  for (const site of product.dynamic) {
    if (registered.has(site.value)) continue;
    const covered = covers(site.value);
    if (covered.length === 0) continue;
    problems.push(
      `UNREGISTERED-DYNAMIC ${site.value} 覆盖 ${covered.length} 条键（${rel(site.file)}:${site.line}）`
        + ' —— 动态拼键必须在 tools/i18n-keys-manifest.json 里登记理由',
    );
  }

  // 4) 豁免必须仍然成立：键还在，且仍然没人引用。
  for (const [key, entry] of exemptions) {
    if (!keys.has(key)) {
      problems.push(`STALE-EXEMPT ${key} 表里已经没有这条键了（${entry.status}: ${entry.reason}）`);
      continue;
    }
    const by = referencedBy(key);
    if (by) {
      problems.push(`STALE-EXEMPT ${key} 已经被引用（${by}），这条豁免该撤了（${entry.status}: ${entry.reason}）`);
    }
  }

  const stats = `keys=${keys.size} langs=${languages.length} product=${keys.size - unreachable.length}`
    + ` dynamic=${registered.size} exempt=${exemptions.size} unreachable=${unreachable.length}`;
  if (problems.length === 0) {
    console.log(`STAT i18n-reachability ${stats}`);
    console.log('I18N-REACHABILITY-PASS');
    return 0;
  }
  for (const problem of problems) console.log(`I18N-REACHABILITY-DIFF ${problem}`);
  console.log(`I18N-REACHABILITY-FAIL ${stats} problems=${problems.length}`);
  return 1;
}

process.exit(main());

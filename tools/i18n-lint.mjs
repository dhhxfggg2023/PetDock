#!/usr/bin/env node
// D-16 静态层：用户看得见的文案必须来自资源表，不许写死在源码里（RFC-42 §4.1）。
// 扫「内核 src/Ui + src/Host」与「随包页面插件」里**字符串字面量中的中文**——
// 注释不是文案（注释解释这条键为什么存在是好事），字面量才是用户会读到的那一份。
//
// 扫描器在 tools/lib/cs-source.mjs，与 tools/i18n-reachability.mjs 共用同一份：
// 两处各写一份就会各自漂移，同一条文字在一个门禁里是「写死的文案」、在另一个里是「已引用」，
// 而没人知道该信哪一个。
//
// 为什么把页面插件也扫进来（2026-10-31）：壳（内核）的文案早已全走表，但页面插件
// 同样在用户眼前画字 —— 在那里写死一句中文，另一门语言的用户就看到中文，而原来的
// 扫描面只到内核，这个洞没有任何门禁看着。
//
// 为什么**不**扫其他插件（petdock.care / petdock.agent / sprite-seq…）：它们是另一件事 ——
// 插件自带的文案表（CareTexts.cs / AgentTexts.cs）与面向开发者的诊断输出，属于「插件本地化」
// 这个还没设计的题目（见 HANDOVER 的已知缺口）。把它混进这一步只会让这一步红着、并且没人修，
// 而红着的门禁等于没有门禁。
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { stringLiterals } from './lib/cs-source.mjs';

const ROOT = process.cwd();
const CORE = join(ROOT, 'packages', 'core', 'PetDock.Core');
const SCAN = [join(CORE, 'src', 'Ui'), join(CORE, 'src', 'Host')];
const SKIP_FILE = /^Program[^/\\]*\.cs$/; // 命令行 stdout 是诊断面，不是界面
const CJK = /[\u3000-\u303f\u3400-\u4dbf\u4e00-\u9fff\uff00-\uffef]/u;

/** 随包页面插件：目录名里带 ui 的插件（今天的形态是 packages/plugins/petdock.ui.default）。 */
function uiPluginRoots() {
  const plugins = join(ROOT, 'packages', 'plugins');
  let entries;
  try {
    entries = readdirSync(plugins, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((entry) => entry.isDirectory() && entry.name.includes('ui'))
    .map((entry) => join(plugins, entry.name));
}

function walk(dir, out) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const entry of entries) {
    if (entry.isDirectory()) {
      if (entry.name === 'bin' || entry.name === 'obj') continue;
      walk(join(dir, entry.name), out);
    } else if (entry.name.endsWith('.cs')) {
      out.push(join(dir, entry.name));
    }
  }
  return out;
}

const hits = [];
for (const dir of [...SCAN, ...uiPluginRoots()]) {
  for (const file of walk(dir, [])) {
    if (SKIP_FILE.test(file.split(sep).pop())) continue;
    for (const literal of stringLiterals(readFileSync(file, 'utf8'))) {
      if (!CJK.test(literal.value)) continue;
      hits.push({
        file: relative(ROOT, file).replaceAll(sep, '/'),
        line: literal.line,
        text: literal.value.replace(/\s+/g, ' ').slice(0, 120),
      });
    }
  }
}

if (hits.length === 0) {
  console.log('STAT i18n-lint files=0 literals=0');
  console.log('I18N-LINT-PASS');
  process.exit(0);
}

const byFile = new Map();
for (const hit of hits) byFile.set(hit.file, (byFile.get(hit.file) ?? 0) + 1);
for (const [file, count] of [...byFile].sort((a, b) => b[1] - a[1])) {
  console.log(`VIOLATION ${file} literals=${count}`);
  for (const hit of hits.filter((h) => h.file === file)) {
    console.log(`  ${file}:${hit.line}  ${hit.text}`);
  }
}
console.log(`STAT i18n-lint files=${byFile.size} literals=${hits.length}`);
console.log('I18N-LINT-FAIL move the text into assets/i18n/<lang>.json and read it by key (RFC-42 3.1)');
process.exit(1);

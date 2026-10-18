#!/usr/bin/env node
// tools/validate-manifest.mjs —— docs/21 §2「校验规则（机器可执行）」的实现。
//
// 权威来源：docs/21-manifest-schema.md §1 的 JSON Schema 是唯一事实源，本脚本把它从
// markdown 里抽出来就地编译，不复制第二份 schema（复制就会漂移）。
//
// 为什么放在 tools/：docs/40 §1.1 把仓库内开发工具（lint、依赖图扫描、规模预算）定位在
// tools/，仓库根只留 README / sln / package.json 这类项目级文件。
//
// 为什么需要它：docs/21 §2 的三条规则里，`additionalProperties: false`、`capabilities`
// 白名单、`license.spdx` 必填，都只能靠 schema 校验判定；手写 manifest 的作者看不出
// id 里多了一个连字符（schema 的 pattern 不允许 `-`）。
//
// 用法：
//   node tools/validate-manifest.mjs                       # 校验 packages/plugins/**/petdock-plugin.json
//   node tools/validate-manifest.mjs path/to/petdock-plugin.json
//
// 退出码：0 = 全部通过；1 = 有 manifest 不合规或找不到任何 manifest。

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv from 'ajv';
import addFormats from 'ajv-formats';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const SCHEMA_DOC = path.join(REPO_ROOT, 'docs', '21-manifest-schema.md');
const MANIFEST_NAME = 'petdock-plugin.json';

/** 从 docs/21 的 markdown 中抽出第一个 ```json 代码块作为权威 schema。 */
function loadSchema() {
  const markdown = fs.readFileSync(SCHEMA_DOC, 'utf8');
  const match = markdown.match(/```json\s*(\{[\s\S]*?\})\s*```/);
  if (!match) {
    throw new Error(`在 ${path.relative(REPO_ROOT, SCHEMA_DOC)} 中找不到 JSON Schema 代码块`);
  }

  const schema = JSON.parse(match[1]);
  // schema 自带的 `$schema` 指向 https://petdock.dev/schema/plugin/v1.json（一个线上 URI），
  // ajv 会试图把它当作待解析的 meta-schema 并抛 "no schema with key or ref"。
  // 我们只需要它的校验规则，不需要它去联网解析 meta-schema，故摘掉该键。
  delete schema.$schema;
  return schema;
}

/** 递归找出插件目录下的全部 manifest。 */
function discoverManifests(dir, found = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return found;
  }

  for (const entry of entries) {
    if (entry.name === 'node_modules' || entry.name === 'bin' || entry.name === 'obj') {
      continue;
    }
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      discoverManifests(full, found);
    } else if (entry.name === MANIFEST_NAME) {
      found.push(full);
    }
  }
  return found;
}

/**
 * 把一条 ajv 错误定位到「人能用 grep 找过去的坐标」。
 *
 * 为什么不是 `error.instancePath`：本仓库装的是 ajv **6**.15.0，它的字段叫 `dataPath`
 * （`instancePath` 是 ajv 8 才改的名）。写 `error.instancePath` 会永远是 undefined，
 * 于是每条错误都打印 `(root)` —— 冷启动走查的陌生作者原话是「§7 说 lint 会告诉你具体字段，
 * 但它只说 (root)」。实测 ajv 6.15.0 的错误形状：`{keyword, dataPath, params, message}`。
 *
 * 光有 dataPath 还不够：`additionalProperties` 与 `required` 的 dataPath 本来就是空串
 * （它们是**在对象上**判的），真正缺的字段名在 `params` 里。所以这里把 params 也拼进去。
 */
function errorWhere(error) {
  const where = error.dataPath || error.instancePath || '';
  const at = where === '' ? '(root)' : where;
  const params = error.params ?? {};
  if (typeof params.additionalProperty === 'string') {
    return `${at} 多余字段 '${params.additionalProperty}'`;
  }
  if (typeof params.missingProperty === 'string') {
    return `${at} 缺字段 '${params.missingProperty}'`;
  }
  if (Array.isArray(params.allowedValues)) {
    return `${at}（允许值 ${params.allowedValues.join(' | ')}）`;
  }
  return at;
}

function main() {
  const explicit = process.argv.slice(2);
  const targets = explicit.length > 0
    ? explicit.map((p) => path.resolve(p))
    : discoverManifests(path.join(REPO_ROOT, 'packages', 'plugins'));

  if (targets.length === 0) {
    console.log(`VIOLATION no ${MANIFEST_NAME} found under packages/plugins`);
    console.log('MANIFEST-FAIL');
    process.exit(1);
  }

  const ajv = new Ajv({ strict: false, allErrors: true });
  addFormats(ajv);
  const validate = ajv.compile(loadSchema());

  let failed = 0;
  for (const target of targets) {
    const rel = path.relative(REPO_ROOT, target).replaceAll('\\', '/');
    let manifest;
    try {
      manifest = JSON.parse(fs.readFileSync(target, 'utf8'));
    } catch (error) {
      console.log(`VIOLATION ${rel} 无法解析为 JSON: ${error.message}`);
      failed += 1;
      continue;
    }

    if (validate(manifest)) {
      console.log(`OK ${rel} id=${manifest.id} tier=${manifest.tier} kind=${manifest.kind}`);
      continue;
    }

    failed += 1;
    for (const error of validate.errors ?? []) {
      console.log(`VIOLATION ${rel} ${errorWhere(error)} ${error.message}`);
    }
  }

  console.log(`STAT manifests ${targets.length} failed ${failed}`);
  if (failed > 0) {
    console.log('MANIFEST-FAIL');
    process.exit(1);
  }

  console.log('MANIFEST-PASS');
}

// 供同进程调用者（tools/petdock-cli/petdock.mjs 的 lint）复用的单文件校验入口。
// 为什么导出而不是 spawn 本脚本：受限通道里管道捕获子进程输出会静默失败
// （code_verify 通道实测 spawnSync 返回 status=null / out=undefined，pwsh 通道正常），
// 而 docs/21 的 schema 只能有一份。返回 { ok, messages }，不打印、不退出。
export function validateManifestFile(target) {
  const ajv = new Ajv({ strict: false, allErrors: true });
  addFormats(ajv);
  const validate = ajv.compile(loadSchema());
  const messages = [];

  let manifest;
  try {
    manifest = JSON.parse(fs.readFileSync(target, 'utf8'));
  } catch (error) {
    messages.push(`VIOLATION 无法解析为 JSON: ${error.message}`);
    return { ok: false, messages };
  }

  if (validate(manifest)) {
    messages.push(`OK id=${manifest.id} tier=${manifest.tier} kind=${manifest.kind}`);
    return { ok: true, messages };
  }

  for (const error of validate.errors ?? []) {
    messages.push(`VIOLATION ${errorWhere(error)} ${error.message}`);
  }
  return { ok: false, messages };
}

// 只有直接执行时才跑 CLI 形态；被 import 时不得有副作用。
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}

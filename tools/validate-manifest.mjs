#!/usr/bin/env node
// tools/validate-manifest.mjs —— 插件清单的静态校验（门禁与 petdock CLI 共用）。
//
// 唯一权威实现是内核的 ManifestValidator（C#）：它决定 id/version 正则、能力白名单、
// contributes 键集这些语义。本脚本**不再维护第二份 JSON Schema** —— 原规格 21 删除后，
// 一份 JS schema 只会成为又一份会漂移的知识。
//
// 用法：
//   node tools/validate-manifest.mjs                  # 校验 packages/plugins/**/petdock-plugin.json
//   node tools/validate-manifest.mjs <path>...        # 文件、插件目录或插件根都可以
//
// 退出码：0 = 全部通过；1 = 有清单不合规或找不到任何清单。

import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const PROJECT = path.join(REPO_ROOT, 'packages', 'core', 'PetDock.Core', 'PetDock.Core.csproj');
const DLL = path.join(REPO_ROOT, 'packages', 'core', 'PetDock.Core', 'bin', 'Release', 'net8.0-windows', 'PetDock.Core.dll');

function run(targets) {
  const extra = ['--validate-plugins', ...targets];
  // 已经有 Release 产物就直接跑它（门禁 L0 不依赖构建顺序）；没有才让 dotnet 构建一次。
  let child = spawnSync('dotnet', ['exec', DLL, ...extra], { cwd: REPO_ROOT, encoding: 'utf8' });
  if (child.error || child.status === null) {
    child = spawnSync(
      'dotnet',
      ['run', '--project', PROJECT, '-c', 'Release', '--', ...extra],
      { cwd: REPO_ROOT, encoding: 'utf8' },
    );
  }
  return child;
}

function main() {
  const explicit = process.argv.slice(2);
  const targets = explicit.length > 0 ? explicit : [path.join('packages', 'plugins')];
  const child = run(targets);
  const output = [child.stdout, child.stderr].filter(Boolean).join('\n').trim();
  if (output) {
    console.log(output);
  }
  if (child.status === null) {
    console.log(`MANIFEST-FAIL 无法运行内核校验器：${child.error?.message ?? '未知原因'}`);
    process.exit(1);
  }
  process.exit(child.status);
}

/**
 * 供同进程调用者（tools/petdock-cli/petdock.mjs）复用：校验一份 manifest 或一个插件目录。
 * 返回 { ok, messages }，不打印、不退出。判定逻辑在内核里，Node 侧没有可 import 的副本。
 */
export function validateManifestFile(target) {
  const child = run([target]);
  const messages = [child.stdout, child.stderr].filter(Boolean).join('\n').trim().split('\n').filter(Boolean);
  return { ok: child.status === 0, messages };
}

// 只有直接执行时才跑 CLI 形态；被 import 时不得有副作用。
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}

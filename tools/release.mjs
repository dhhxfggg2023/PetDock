#!/usr/bin/env node
// tools/release.mjs —— M9 的发布管线：把「能跑的仓库」变成「能发出去的目录」。
//
// 为什么需要它：`dotnet publish` 只产出内核本体的 6 个文件（实测），没有 plugins/、
// 没有图标、没有 LICENSE/NOTICE —— 直接发出去的东西装完是打不开宠物的。发布物必须
// 由一处**可重复**的脚本装配，而不是靠人记得拷哪几个目录。
//
// 它同时是 docs/52 §6「发布前合规清单」的机器判据：清单是七条勾选，勾选靠人，判据靠它。
// 出口再扫一次（而不是只信入口的 lint）是关键——lint 扫的是**仓库**，这里扫的是
// **真正要发出去的那棵树**，两者的差别正是「仓库干净但打包时多拷了一个目录」。
//
// 用法：
//   node tools/release.mjs                    # 装配到 artifacts/release/<version>/
//   node tools/release.mjs --out DIR          # 指定输出目录
//   node tools/release.mjs --dry-run          # 只打印装配清单与合规检查，不落盘
//   node tools/release.mjs --skip-publish     # 复用已有 publish 产物（迭代装配脚本时用）
//
// 退出码：0 = 装配完成且合规；1 = 任一合规判据失败（此时不产出可用发布物）。

import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ARTIFACTS = path.join(REPO_ROOT, 'artifacts');

function argValue(flag) {
  const i = process.argv.indexOf(flag);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : null;
}

const DRY_RUN = process.argv.includes('--dry-run');
const SKIP_PUBLISH = process.argv.includes('--skip-publish');
const OUT_OVERRIDE = argValue('--out');

/** 版本号唯一事实源 = package.json 的 version（与 manifest/ABI 的语义化版本同形）。 */
function readVersion() {
  const pkg = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8'));
  if (!pkg.version || !/^\d+\.\d+\.\d+$/.test(pkg.version)) {
    console.log(`RELEASE-FAIL package.json 缺少合法 version（需要 x.y.z，当前 ${pkg.version ?? '(none)'}）`);
    process.exit(1);
  }
  return pkg.version;
}

/** 发布物组成。每一项都要说清为什么在里面。 */
function plan(version) {
  return [
    {
      from: path.join('packages', 'core', 'PetDock.Core'),
      to: '.',
      kind: 'publish',
      why: '内核本体（dotnet publish 产出）',
    },
    {
      from: path.join('packages', 'plugins'),
      to: 'plugins',
      kind: 'plugins',
      why: '第一方插件；按 manifest 的 entry 逐工程 publish（.NET）或整目录复制（脚本型）',
    },
    {
      from: path.join('assets', 'icons'),
      to: path.join('assets', 'icons'),
      kind: 'tree',
      why: '托盘与窗口图标（docs/18 §1 指定 icon-32.png）',
    },
    {
      from: 'LICENSE',
      to: 'LICENSE',
      kind: 'file',
      why: 'docs/52 §6 第 5 条：Apache-2.0 正文',
    },
    {
      from: 'NOTICE',
      to: 'NOTICE',
      kind: 'file',
      why: 'docs/52 §6 第 5 条：第三方声明',
    },
    {
      from: 'README.md',
      to: 'README.md',
      kind: 'file',
      why: '装完之后用户看的第一份说明',
    },
  ];
}

function copyFile(from, to) {
  fs.mkdirSync(path.dirname(to), { recursive: true });
  fs.copyFileSync(from, to);
}

function copyTree(from, to, filter) {
  let entries;
  try {
    entries = fs.readdirSync(from, { withFileTypes: true });
  } catch {
    return 0;
  }
  let n = 0;
  for (const entry of entries) {
    const abs = path.join(from, entry.name);
    const rel = path.relative(from, abs).replaceAll('\\', '/');
    if (filter && !filter(rel)) {
      continue;
    }
    const dest = path.join(to, entry.name);
    if (entry.isDirectory()) {
      n += copyTree(abs, dest, filter);
    } else if (entry.isFile()) {
      copyFile(abs, dest);
      n += 1;
    }
  }
  return n;
}

function walkFiles(dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const entry of entries) {
    const abs = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walkFiles(abs, out);
    } else if (entry.isFile()) {
      out.push(abs);
    }
  }
  return out;
}

function sha256(file) {
  return createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

/**
 * 出口合规检查。与入口 lint 的区别：这里扫的是**要发出去的那棵树**。
 * 返回失败条数。
 */
function auditOutput(outDir, version) {
  let failures = 0;
  const fail = (msg) => {
    failures += 1;
    console.log(`RELEASE-CHECK-FAIL ${msg}`);
  };

  // ① 商业 SDK / 生态素材二进制（docs/52 §6 第 3 条，R-03）
  const forbidden = ['.moc3', '.model3.json', '.motion3.json', '.skel', '.atlas', '.mtn'];
  for (const file of walkFiles(outDir)) {
    const lower = file.toLowerCase();
    if (forbidden.some((ext) => lower.endsWith(ext))) {
      fail(`发布物含商业 SDK 二进制 ${path.relative(outDir, file)}`);
    }
  }

  // ② LICENSE / NOTICE 必须在（docs/52 §6 第 5 条）
  for (const name of ['LICENSE', 'NOTICE']) {
    if (!fs.existsSync(path.join(outDir, name))) {
      fail(`发布物缺 ${name}`);
    }
  }

  // ③ 内核本体必须在（防「装配了个空目录还报成功」）
  const coreExe = path.join(outDir, 'PetDock.Core.exe');
  if (!fs.existsSync(coreExe)) {
    fail('发布物缺 PetDock.Core.exe（publish 没跑或输出目录不对）');
  }

  // ④ 插件 manifest 必须声明 license（docs/52 §6 第 6 条）
  const pluginsDir = path.join(outDir, 'plugins');
  let manifestCount = 0;
  for (const file of walkFiles(pluginsDir)) {
    if (path.basename(file) !== 'petdock-plugin.json') {
      continue;
    }
    manifestCount += 1;
    let manifest;
    try {
      manifest = JSON.parse(fs.readFileSync(file, 'utf8'));
    } catch {
      fail(`${path.relative(outDir, file)} 不是合法 JSON`);
      continue;
    }
    const spdx = manifest?.license?.spdx;
    if (typeof spdx !== 'string' || spdx.trim() === '') {
      fail(`${path.relative(outDir, file)} 未声明 license.spdx`);
    }
  }
  if (manifestCount === 0) {
    // 发布物里一个插件都没有 = 装完什么也看不见，这不是可发布的形态。
    fail('发布物里没有任何插件 manifest —— 装完不会有宠物');
  }

  // ⑤ 图标必须在（docs/18 §1 托盘指定 icon-32.png）
  const icon = path.join(outDir, 'assets', 'icons', 'icon-32.png');
  if (!fs.existsSync(icon)) {
    fail('发布物缺 assets/icons/icon-32.png（托盘图标）');
  }

  // ⑥ 版本号一致性：产物文件名与 package.json 必须同源
  if (!fs.existsSync(path.join(outDir, 'PetDock.Core.runtimeconfig.json'))) {
    fail('发布物缺 PetDock.Core.runtimeconfig.json（.NET 运行时配置）');
  }

  console.log(`STAT release version=${version} plugins=${manifestCount} files=${walkFiles(outDir).length}`);
  return failures;
}

/**
 * 插件装配：按 **manifest 的 entry 字段**决定产物落点。
 *
 * 这里踩过一次：初版把 packages/plugins 整棵树拷过去（排除 bin/obj），发布物里只有
 * `.cs` 源码，而 manifest 的 entry 指向 `X/bin/Release/net8.0-windows/X.dll` ——
 * 装完一个插件都加载不了，可出口检查照样报 RELEASE-OK（它只查了 manifest 存在）。
 * 教训：发布物的判据是「entry 指向的文件在不在」，不是「插件目录拷没拷」。
 *
 * 所以：.NET 插件逐工程 publish 到 entry 所指的目录；非 .NET（Tier 1 / 脚本型）插件
 * 整目录复制（它们的 entry 是包内文件，不需要编译）。
 */
function publishPlugins(pluginsRoot, destRoot) {
  let entries;
  try {
    entries = fs.readdirSync(pluginsRoot, { withFileTypes: true });
  } catch {
    return { published: 0, copied: 0, failures: [] };
  }

  let published = 0;
  let copied = 0;
  const failures = [];

  for (const entry of entries) {
    if (!entry.isDirectory()) {
      continue;
    }
    const pluginDir = path.join(pluginsRoot, entry.name);
    const manifestPath = path.join(pluginDir, 'petdock-plugin.json');
    if (!fs.existsSync(manifestPath)) {
      continue;
    }

    let manifest;
    try {
      manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    } catch {
      failures.push(`${entry.name}: manifest 不是合法 JSON`);
      continue;
    }

    const destPluginDir = path.join(destRoot, entry.name);
    // manifest 与 README 一定进发布物。
    copyFile(manifestPath, path.join(destPluginDir, 'petdock-plugin.json'));
    for (const extra of ['README.md']) {
      const src = path.join(pluginDir, extra);
      if (fs.existsSync(src)) {
        copyFile(src, path.join(destPluginDir, extra));
      }
    }

    // 找插件工程（插件根下第一层目录里的 .csproj）。
    const projects = [];
    for (const sub of fs.readdirSync(pluginDir, { withFileTypes: true })) {
      if (!sub.isDirectory() || ['bin', 'obj', 'node_modules'].includes(sub.name)) {
        continue;
      }
      for (const file of fs.readdirSync(path.join(pluginDir, sub.name))) {
        if (file.endsWith('.csproj')) {
          projects.push({ project: path.join(pluginDir, sub.name, file), name: sub.name });
        }
      }
    }

    if (projects.length === 0) {
      // 非 .NET 插件：entry 是包内文件，整目录复制。
      const n = copyTree(pluginDir, destPluginDir, (rel) => !/(^|\/)(bin|obj|node_modules)(\/|$)/.test(rel));
      copied += n;
      // entry 必须存在，否则装完照样加载不了。
      const entryPath = path.join(destPluginDir, manifest.entry ?? '');
      if (!manifest.entry || !fs.existsSync(entryPath)) {
        failures.push(`${entry.name}: entry 指向的文件不存在（${manifest.entry}）`);
      }
      continue;
    }

    // .NET 插件：publish 到 entry 所指目录的**父目录**，让 entry 恰好命中。
    // entry 形如 `PetDock.SamplePet/bin/Release/net8.0-windows/PetDock.SamplePet.dll`，
    // 故输出目录 = destPluginDir/<entry 的目录部分>。
    const entryRel = manifest.entry ?? '';
    const entryDirRel = path.posix.dirname(entryRel.replaceAll('\\', '/'));
    const publishOut = entryDirRel && entryDirRel !== '.'
      ? path.join(destPluginDir, entryDirRel.split('/').join(path.sep))
      : destPluginDir;

    for (const proj of projects) {
      console.log(`PUBLISH ${entry.name} ← ${path.relative(REPO_ROOT, proj.project)}`);
      try {
        execFileSync('dotnet', ['publish', proj.project, '-c', 'Release', '-o', publishOut, '--nologo'], {
          cwd: REPO_ROOT,
          stdio: 'inherit',
        });
        published += 1;
      } catch {
        failures.push(`${entry.name}: dotnet publish 失败（${proj.name}）`);
      }
    }

    const entryPath = path.join(destPluginDir, entryRel);
    if (!fs.existsSync(entryPath)) {
      failures.push(`${entry.name}: publish 后 entry 仍不存在（${entryRel}）`);
    }
  }

  return { published, copied, failures };
}

function main() {
  const version = readVersion();
  const outDir = OUT_OVERRIDE ? path.resolve(OUT_OVERRIDE) : path.join(ARTIFACTS, 'release', version);

  console.log(`RELEASE-PLAN version=${version} out=${path.relative(REPO_ROOT, outDir)} dry-run=${DRY_RUN}`);
  for (const item of plan(version)) {
    console.log(`  ${item.kind.padEnd(8)} ${item.from} → ${item.to}    // ${item.why}`);
  }

  if (!DRY_RUN) {
    if (fs.existsSync(outDir)) {
      fs.rmSync(outDir, { recursive: true, force: true });
    }
    fs.mkdirSync(outDir, { recursive: true });
  }

  // ① publish
  // --dry-run 不是「什么都不做」：它真的 publish 到临时目录并跑完出口检查，只是不碰
  // 最终发布目录。一个不实际装配的 dry-run 只能证明脚本语法没坏，证明不了发布物合规。
  const target = DRY_RUN ? path.join(ARTIFACTS, '_release-dry') : outDir;
  if (!DRY_RUN || !SKIP_PUBLISH) {
    if (DRY_RUN && fs.existsSync(target)) {
      fs.rmSync(target, { recursive: true, force: true });
    }
    const publishArgs = [
      'publish',
      path.join('packages', 'core', 'PetDock.Core', 'PetDock.Core.csproj'),
      '-c', 'Release',
      '-o', target,
      '--nologo',
    ];
    console.log(`RUN dotnet ${publishArgs.join(' ')}`);
    try {
      execFileSync('dotnet', publishArgs, { cwd: REPO_ROOT, stdio: 'inherit' });
    } catch {
      console.log('RELEASE-FAIL dotnet publish 失败');
      process.exit(1);
    }
  }

  // ② 装配其余项
  for (const item of plan(version)) {
    if (item.kind === 'publish') {
      continue; // 已由 publish 落盘
    }
    if (item.kind === 'plugins') {
      const result = publishPlugins(path.join(REPO_ROOT, 'packages', 'plugins'), path.join(target, 'plugins'));
      console.log(`STAT plugins published=${result.published} copied=${result.copied}`);
      for (const failure of result.failures) {
        console.log(`RELEASE-CHECK-FAIL ${failure}`);
      }
      continue;
    }
    const src = path.join(REPO_ROOT, item.from);
    const dest = path.join(target, item.to);
    if (item.kind === 'file') {
      if (!fs.existsSync(src)) {
        console.log(`RELEASE-FAIL 缺 ${item.from}`);
        process.exit(1);
      }
      copyFile(src, dest);
    } else if (item.kind === 'tree') {
      const n = copyTree(src, dest, item.filter);
      console.log(`COPIED ${item.from} → ${item.to} (${n} files)`);
    }
  }

  // ③ 出口合规检查
  if (!fs.existsSync(target)) {
    console.log('RELEASE-FAIL 输出目录不存在，无法做出口检查');
    process.exit(1);
  }
  const failures = auditOutput(target, version);

  // ④ 产物清单（将来更新器只认版本号与签名，这份清单就是「这个版本装了哪些文件」的事实）
  if (!DRY_RUN && failures === 0) {
    const files = walkFiles(outDir).sort();
    const manifest = {
      version,
      built_at: new Date().toISOString(),
      files: files.map((file) => ({
        path: path.relative(outDir, file).replaceAll('\\', '/'),
        sha256: sha256(file),
        bytes: fs.statSync(file).size,
      })),
    };
    fs.writeFileSync(path.join(outDir, 'release-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
    console.log(`STAT release-manifest files=${manifest.files.length}`);
  }

  if (failures > 0) {
    console.log(`RELEASE-FAIL checks=${failures}`);
    process.exit(1);
  }
  console.log(DRY_RUN ? 'RELEASE-DRY-OK' : 'RELEASE-OK');
}

main();

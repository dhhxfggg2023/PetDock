#!/usr/bin/env node
// tools/release.mjs —— M9 的发布管线：把「能跑的仓库」变成「能发出去的目录」。
//
// 为什么需要它：`dotnet publish` 只产出内核本体的 6 个文件（实测），没有 plugins/、
// 没有图标、没有 LICENSE/NOTICE —— 直接发出去的东西装完是打不开宠物的。发布物必须
// 由一处**可重复**的脚本装配，而不是靠人记得拷哪几个目录。
//
// 它同时是 原规格 52 §6「发布前合规清单」的机器判据：清单是七条勾选，勾选靠人，判据靠它。
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

/**
 * 宿主已经自带、插件目录里不该再带一份的程序集。
 *
 * .NET 插件 publish 时会把 ProjectReference 闭包整个复制到自己的输出目录：每个插件都带
 * PetDock.Core.dll/.exe/.pdb/.deps.json/.runtimeconfig.json 和 Wasmtime（含原生 dll），
 * 实测每个插件多出约 16 MB，七个插件就是一百多 MB 的纯重复。插件运行时由默认加载上下文
 * 解析到宿主根目录里的同一份程序集（同名同版本），插件目录里的副本永远不会被优先加载。
 *
 * PetDock.Sdk.dll 不在名单里：宿主本体不使用 SDK，根目录没有它，插件仍需自带。
 */
const HOST_ASSEMBLY_NAMES = new Set([
  'PetDock.Core.dll',
  'PetDock.Core.exe',
  'PetDock.Core.pdb',
  'PetDock.Core.deps.json',
  'PetDock.Core.runtimeconfig.json',
  'Wasmtime.Dotnet.dll',
  'Wasmtime.Dotnet.pdb',
  'wasmtime.dll',
]);

function argValue(flag) {
  const i = process.argv.indexOf(flag);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : null;
}

const DRY_RUN = process.argv.includes('--dry-run');
const SKIP_PUBLISH = process.argv.includes('--skip-publish');
const OUT_OVERRIDE = argValue('--out');

/** 版本号唯一事实源 = package.json 的 version（与 manifest/ABI 的语义化版本同形）。 */
/**
 * 可复现的构建时间。同一个提交重复构建必须得到同一个值 —— 否则 zip 的 sha256 每次都变，
 * 「Release 与本地一致」这条判据就永远说不清是产物变了还是时间戳变了。
 *
 * 2026-10-30：verify-release 上线第一天就撞到了这个 —— 同一个提交、同一个工作树，
 * 两次 release+pack 出来的 zip 差 3 个字节，全部来自 built_at 这一个字段。
 *
 * 取值顺序：git 的 HEAD 提交时间 → 环境变量 SOURCE_DATE_EPOCH → 最后才退回当前时间
 * （解压出来的源码包没有 .git，那时退化成不可复现是可接受的，但会打印一行说明）。
 */
function sourceDateEpoch() {
  const fromEnv = process.env.SOURCE_DATE_EPOCH;
  if (fromEnv && /^\d+$/.test(fromEnv)) {
    return new Date(Number(fromEnv) * 1000).toISOString();
  }
  try {
    const raw = execFileSync('git', ['log', '-1', '--format=%cI'], { cwd: REPO_ROOT, encoding: 'utf8' }).trim();
    if (raw) {
      return new Date(raw).toISOString();
    }
  } catch {
    console.log('STAT release-clock source=wall-clock reason=no-git（产物因此不可复现）');
  }
  return new Date().toISOString();
}

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
      why: '托盘与窗口图标（原规格 18 §1 指定 icon-32.png）',
    },
    {
      from: path.join('pets', 'sample-pet'),
      to: path.join('pets', 'sample-pet'),
      kind: 'tree',
      why: '引导第 2 步要放置的示例宠物（RFC-16 §3.3）；缺它，发行版启动时 PackLoader 找不到素材会抛 FileNotFoundException。放在 pets/ 而不是 assets/：它和用户导入的桌宠是同一类东西，运行期复制进 data/packs/ 后一一对应'
    },
    {
      from: path.join('assets', 'i18n'),
      to: path.join('assets', 'i18n'),
      kind: 'tree',
      why: '界面文案表（RFC-42 §3.2）；缺它每个键都回落到键名本身，托盘提示会显示 ui.tray.title.running',
    },
    {
      from: 'LICENSE',
      to: 'LICENSE',
      kind: 'file',
      why: '原规格 52 §6 第 5 条：Apache-2.0 正文',
    },
    {
      from: 'NOTICE',
      to: 'NOTICE',
      kind: 'file',
      why: '原规格 52 §6 第 5 条：第三方声明',
    },
    {
      from: 'README.md',
      to: 'README.md',
      kind: 'file',
      why: '装完之后用户看的第一份说明',
    },
    {
      from: path.join('packaging', 'INSTALL.md'),
      to: 'INSTALL.md',
      kind: 'file',
      why: 'RFC-25 §3.2：解压即用 / 数据在 data\\ / 删目录即卸载，三句话的唯一说明（README 面向开发者）',
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
 * 只留 win-x64 的 RID 资源。
 *
 * 为什么必须剪：`dotnet publish` 默认把**所有** RID 的 runtimes/ 都拖进来（linux-x64 /
 * linux-arm64 / osx-x64 / osx-arm64 / win-arm64 / win-x64，每个 wasmtime 十几到二十 MB），
 * 而 7 个插件各自 publish 一份 —— 实测未剪时发布树 711 MB / 164 文件，zip 242 MB。
 * 桌宠发一个 242 MB 的包没有正当理由，而且那些 .so/.dylib 在 Windows 上永远不会被加载。
 *
 * 只剪 `runtimes/<rid>`（rid ≠ win-x64）这一个形状：不碰程序集，也不碰插件自己的资源。
 */
function pruneForeignRids(root) {
  let removed = 0;
  const walk = (dir) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      if (!entry.isDirectory()) {
        continue;
      }
      const abs = path.join(dir, entry.name);
      if (entry.name === 'runtimes') {
        for (const rid of fs.readdirSync(abs, { withFileTypes: true })) {
          if (!rid.isDirectory() || rid.name === 'win-x64') {
            continue;
          }
          fs.rmSync(path.join(abs, rid.name), { recursive: true, force: true });
          removed += 1;
        }
        continue;
      }
      walk(abs);
    }
  };
  walk(root);
  return removed;
}

/**
 * 剪掉插件目录里重复携带的宿主程序集（见 §HOST_ASSEMBLY_NAMES）。
 *
 * 只扫 plugins/ 下；宿主根目录的文件一律不动。剪完顺手清掉空目录，免得 zip 里留下
 * 一排空壳（runtimes/win-x64/native/ 之类）。
 */
function prunePluginHostCopies(root) {
  const pluginsRoot = path.join(root, 'plugins');
  let removed = 0;
  for (const file of walkFiles(pluginsRoot)) {
    if (HOST_ASSEMBLY_NAMES.has(path.basename(file))) {
      fs.rmSync(file, { force: true });
      removed += 1;
    }
  }
  const clean = (dir) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      if (entry.isDirectory()) {
        clean(path.join(dir, entry.name));
      }
    }
    if (path.resolve(dir) !== path.resolve(pluginsRoot) && fs.readdirSync(dir).length === 0) {
      fs.rmdirSync(dir);
    }
  };
  clean(pluginsRoot);
  return removed;
}

function totalBytes(dir) {
  return walkFiles(dir).reduce((sum, file) => sum + fs.statSync(file).size, 0);
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

  // ① 商业 SDK / 生态素材二进制（原规格 52 §6 第 3 条，R-03）
  const forbidden = ['.moc3', '.model3.json', '.motion3.json', '.skel', '.atlas', '.mtn'];
  for (const file of walkFiles(outDir)) {
    const lower = file.toLowerCase();
    if (forbidden.some((ext) => lower.endsWith(ext))) {
      fail(`发布物含商业 SDK 二进制 ${path.relative(outDir, file)}`);
    }
  }

  // ② LICENSE / NOTICE 必须在（原规格 52 §6 第 5 条）
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

  // ④ 插件 manifest 必须声明 license（原规格 52 §6 第 6 条）
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
  // ④.5 插件目录不得重复携带宿主程序集（§HOST_ASSEMBLY_NAMES）：宿主根目录已有同一份，
  //      插件里再带一份只会把包撑大，且掩盖「插件到底依赖了什么」。
  for (const file of walkFiles(pluginsDir)) {
    if (HOST_ASSEMBLY_NAMES.has(path.basename(file))) {
      fail(`插件目录重复携带宿主程序集 ${path.relative(outDir, file).replaceAll('\\', '/')}`);
    }
  }

  if (manifestCount === 0) {
    // 发布物里一个插件都没有 = 装完什么也看不见，这不是可发布的形态。
    fail('发布物里没有任何插件 manifest —— 装完不会有宠物');
  }

  // ⑤ 图标必须在（原规格 18 §1 托盘指定 icon-32.png）
  const icon = path.join(outDir, 'assets', 'icons', 'icon-32.png');
  if (!fs.existsSync(icon)) {
    fail('发布物缺 assets/icons/icon-32.png（托盘图标）');
  }

  // ⑥ 版本号一致性：产物文件名与 package.json 必须同源
  if (!fs.existsSync(path.join(outDir, 'PetDock.Core.runtimeconfig.json'))) {
    fail('发布物缺 PetDock.Core.runtimeconfig.json（.NET 运行时配置）');
  }

  // ⑦ 示例素材包必须在（RFC-16 §3.3 第 2 步要放置它）。
  // 这条曾经缺失：发布装配只搬 assets/icons，于是发行版启动时视图实现
  // 从程序目录向上找不到 pets/sample-pet/petdock-sample.json，直接抛 FileNotFoundException。
  const samplePack = path.join(outDir, 'pets', 'sample-pet', 'petdock-sample.json');
  if (!fs.existsSync(samplePack)) {
    fail('发布物缺 pets/sample-pet/petdock-sample.json（引导要放置的示例宠物）');
  }

  // ⑧ 文案表必须在（RFC-42 §3.2）。这条曾经缺失：发布物只搬 icons/sample-pet，
  // 发行版里 I18n.Get 全部回落到键名，托盘提示直接显示「ui.tray.title.running」。
  const i18n = path.join(outDir, 'assets', 'i18n', 'zh-CN.json');
  if (!fs.existsSync(i18n)) {
    fail('发布物缺 assets/i18n/zh-CN.json（界面文案表）');
  }

  // ⑨ 便携件必须在（RFC-25 §3.2）：用户拿到的 zip 里必须有便携标记与 INSTALL.md。
  //    标记缺失 = 数据区跑回 %LOCALAPPDATA%，「一个文件夹」就不成立了；这条守的就是这个退化。
  for (const name of ['petdock.portable', 'INSTALL.md']) {
    if (!fs.existsSync(path.join(outDir, name))) {
      fail(`发布物缺 ${name}（便携标记/说明未随包，文件夹不自包含）`);
    }
  }

  // ⑩ 发布物里不许有非 win-x64 的 RID 资源：它们永远不会被加载，却把包撑到 242 MB
  //    （RFC-25 §2）。剪枝在 §pruneForeignRids，这条守「剪漏了」。
  for (const file of walkFiles(outDir)) {
    const rel = path.relative(outDir, file).replaceAll('\\', '/');
    const rid = rel.match(/(?:^|\/)runtimes\/([^/]+)\//);
    if (rid && rid[1] !== 'win-x64') {
      fail(`发布物含非 win-x64 的 RID 资源 ${rel}`);
    }
  }

  console.log(`STAT release version=${version} plugins=${manifestCount} files=${walkFiles(outDir).length} mb=${(totalBytes(outDir) / 1048576).toFixed(1)}`);
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
      try {
        fs.rmSync(outDir, { recursive: true, force: true });
      } catch (error) {
        // 最常见的一种失败：用户正开着上一个发行版（从 outDir 里启动的），它的 exe/dll 被锁住。
        // 报「EPERM, Permission denied: <路径>」等于让人去猜；这里把该做的事直接说出来。
        if (error && (error.code === 'EPERM' || error.code === 'EBUSY')) {
          console.error(`RELEASE-FAIL 产物目录被占用，删不掉：${path.relative(REPO_ROOT, outDir)}`);
          console.error('  多半是 PetDock 正从上一版发行目录里运行着（release.mjs 会先清空整棵目录）。');
          console.error('  先退出 PetDock，再重跑 npm run dist。');
          process.exit(1);
        }

        throw error;
      }
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

  // ②.5 剪掉非 win-x64 的 RID 资源（§pruneForeignRids：体积 + 永远不会被加载）
  const prunedRids = pruneForeignRids(target);
  console.log(`PRUNED runtimes non-win-x64=${prunedRids}`);

  // ②.5b 剪掉插件里重复的宿主程序集（§prunePluginHostCopies：体积 + 同一份程序集只该有一处）
  const prunedHost = prunePluginHostCopies(target);
  console.log(`PRUNED plugin host-copies=${prunedHost}`);

  // ②.6 写便携标记（RFC-25 §3.3）：core 的 PetDataStore.Resolve 见到它就把数据区落在
  //      <程序目录>\data，整个文件夹因此自包含 —— 复制到哪都能跑，删掉目录就是卸载干净。
  fs.writeFileSync(path.join(target, 'petdock.portable'), `${version}\n`, 'utf8');
  console.log('PORTABLE marker=petdock.portable');

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
      built_at: sourceDateEpoch(),
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

#!/usr/bin/env node
// petdock CLI —— docs/23-cli-tooling.md 的 6 条最小命令集。
//
// 为什么放在仓库根的 tools/ 而不是 packages/cli：
//   tools/depscan.mjs 的 D-01b 把 packages/ 的顶层目录白名单钉死为 core/sdk/plugins
//   （新增顶层目录须走 RFC）。本 CLI 是**面向插件作者的产品面**（docs/23 的 `petdock` 命令），
//   不是仓库门禁，但把它做成 packages/cli 需要先改一条宪法级规则；而它的零依赖自包含形态
//   让位置不影响正确性。这条错位在 docs/50 M5 收口时应由一次 RFC 决定（要么放宽 D-01b，
//   要么把它正式登记为 tools/ 下的第二个产品）。
//
// 零 npm 依赖：只用 Node 内置模块。schema 校验与仓库门禁**共用同一份权威实现**
//   （tools/validate-manifest.mjs，它从 docs/21 的 markdown 里抽 schema），
//   所以 CLI 不会自己复制第二份 schema。
//
// 用法：
//   node tools/petdock-cli/petdock.mjs create-plugin <name> [id]
//   node tools/petdock-cli/petdock.mjs lint [dir]
//   node tools/petdock-cli/petdock.mjs check [dir]
//   node tools/petdock-cli/petdock.mjs doctor
//   node tools/petdock-cli/petdock.mjs pack [dir] [--out dist/]
//
// 退出码：0 = 通过；1 = 有违规；2 = 用法错误。

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '..', '..');
const MANIFEST_NAME = 'petdock-plugin.json';
const VALIDATOR = path.join(REPO_ROOT, 'tools', 'validate-manifest.mjs');

const VERSION = '1.0.0';

// ABI 版本从内核源码读，不写死：docs/21 §2 的 api 兼容判据以 docs/11 ABI 为准，
// 而 AbiVersion 的真值只在 PetDock.Core 的 PetRuntimeAbi 里（Major/Minor 两个常量）。
const ABI_SOURCE = path.join(REPO_ROOT, 'packages', 'core', 'PetDock.Core', 'src', 'PetRuntime', 'PetTypes.cs');

const KINDS = ['importer', 'presenter', 'brain', 'interaction', 'state', 'service'];
const DEFAULT_CAPS = {
  importer: ['assets.read:cas'],
  presenter: [],
  brain: [],
  interaction: ['input.mousePosition'],
  state: ['persist.write:self', 'persist.read:self'],
  service: [],
};

function usage() {
  console.log(`petdock/${VERSION} (abi ${abiVersion().major}.${abiVersion().minor}, host .NET 8)`);
  console.log('');
  console.log('usage: petdock <command> [options]');
  console.log('  create-plugin <name> [id]   生成插件骨架（默认 kind=brain, tier=1）');
  console.log('  dev [--plugin <path>]       本地开发循环：校验 + 报告宿主会怎样处理改动');
  console.log('  lint [dir]                  校验 manifest 与入口文件（机器门禁）');
  console.log('  check [dir]                 插件 api 与宿主 ABI 的兼容性');
  console.log('  doctor                      诊断本机环境');
  console.log('  pack [dir] [--out dist/]    打包清单（不写文件：签名位未实现，见 docs/23 §9）');
}

/** 从内核源码读 ABI 版本；读不到就报错而不是猜（猜出来的版本号会让 check 撒谎）。 */
function abiVersion() {
  let src;
  try {
    src = fs.readFileSync(ABI_SOURCE, 'utf8');
  } catch {
    console.error(`petdock: 读不到 ABI 版本源 ${path.relative(REPO_ROOT, ABI_SOURCE)}`);
    process.exit(1);
  }
  const major = src.match(/public\s+const\s+int\s+Major\s*=\s*(\d+)/);
  const minor = src.match(/public\s+const\s+int\s+Minor\s*=\s*(\d+)/);
  if (!major || !minor) {
    console.error('petdock: 在 PetTypes.cs 里找不到 PetRuntimeAbi.Major / Minor');
    process.exit(1);
  }
  return { major: Number(major[1]), minor: Number(minor[1]) };
}

/**
 * 校验一份 manifest 是否符合 docs/21 的 schema。
 *
 * 判定逻辑与 tools/validate-manifest.mjs **共用同一份实现**（它从 docs/21 的 markdown 里抽
 * schema，schema 只有一份）：这里用动态 import 在**同进程内**调它的具名导出，
 * 而不是 spawn 一个子进程 —— 因为管道捕获子进程输出在受限通道里会静默失败
 * （实测 code_verify 通道里 spawnSync 报 `status=null out=undefined`，pwsh 通道正常；
 * 见 ledger/mistakes.md 的「code_verify 通道里跑一个会在自己旁边写辅助脚本的探针」）。
 * 同进程调用让本命令在两条通道下都可判定。
 */
async function validateManifest(manifestPath) {
  const mod = await import(pathToFileURL(VALIDATOR).href);
  return mod.validateManifestFile(manifestPath);
}

function findManifest(dir) {
  const direct = path.join(dir, MANIFEST_NAME);
  if (fs.existsSync(direct)) return direct;
  // 允许指向插件根：只在直接子目录里找一层，避免把整个磁盘扫一遍。
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const candidate = path.join(dir, entry.name, MANIFEST_NAME);
    if (fs.existsSync(candidate)) return candidate;
  }
  return null;
}

function readManifest(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (error) {
    console.error(`petdock: ${path.relative(process.cwd(), file)} 不是合法 JSON: ${error.message}`);
    process.exit(1);
  }
}

// ---------- lint ----------

async function cmdLint(argv) {
  // 默认目标是**当前目录**，不是仓库根：docs/23 §4 的用法是「在插件目录里直接 petdock lint」，
  // 作者照文档做时 cwd 就是插件根。指到仓库根会让作者看到一句莫名其妙的「找不到 manifest」。
  const target = argv[0] ? path.resolve(argv[0]) : process.cwd();
  const manifestFile = fs.existsSync(target) && fs.statSync(target).isDirectory()
    ? findManifest(target)
    : target;

  if (!manifestFile || !fs.existsSync(manifestFile)) {
    console.error(`petdock lint: 在 ${path.relative(REPO_ROOT, target)} 下找不到 ${MANIFEST_NAME}`);
    return 2;
  }

  const pluginRoot = path.dirname(manifestFile);
  let outcome;
  try {
    outcome = await validateManifest(manifestFile);
  } catch (error) {
    console.error(`petdock lint: 无法加载 schema 校验器 (${error.message})`);
    return 1;
  }

  for (const line of outcome.messages) {
    if (line.startsWith('OK ')) console.log('✓ manifest schema valid');
    else if (line.startsWith('VIOLATION ')) console.log(`✗ ${line.slice('VIOLATION '.length)}`);
  }
  if (!outcome.ok) {
    console.log('LINT-FAIL');
    return 1;
  }

  const manifest = readManifest(manifestFile);

  // capabilities 白名单已在 schema 里判定（docs/21 definitions.capabilityId），此处只复述结论。
  console.log(`✓ capabilities all whitelisted (${manifest.capabilities.length})`);
  console.log(`✓ license declared (${manifest.license.spdx})`);

  // entry 存在性：schema 表达不了「文件真的在磁盘上」，而这正是 docs/23 §4 示例里
  // 唯一一条 ✗ 行（`entry file missing: src/main.js`）。
  const entry = manifest.entry;
  if (typeof entry === 'string' && entry.length > 0) {
    const entryPath = path.join(pluginRoot, entry);
    if (!fs.existsSync(entryPath)) {
      console.log(`✗ entry file missing: ${entry}`);
      console.log('LINT-FAIL');
      return 1;
    }
    console.log(`✓ entry file present (${entry})`);

    // 下面是 M-07 走查暴露出来的三类漏检。它们全部是 docs/20 §1 与 docs/21 §2 明确
    // 要求「构建期失败」的判据，而早期 cmdLint 只做了 File.Exists —— 于是语法错的 entry、
    // import 了内核内部的 entry、tier=0 却给 JS entry 的插件，全都 LINT-PASS exit 0。
    const entryIssues = checkEntry(pluginRoot, entry, manifest);
    for (const issue of entryIssues) console.log(`✗ ${issue}`);
    if (entryIssues.length > 0) {
      console.log('LINT-FAIL');
      return 1;
    }
  } else {
    console.log('✓ no code unit (tier 0 declarative pack)');
  }

  console.log('LINT-PASS');
  return 0;
}

/** docs/20 §1 允许插件 import 的模块白名单（其余一律构建期拒绝，D-02）。 */
const ALLOWED_IMPORT = /^@petdock\/sdk(\/[a-z-]+)?$/;

/**
 * 对 entry 文件做三件事：语法、import 白名单、tier 与 entry 形态的一致性。
 *
 * 为什么语法检查要走子进程 `node --check` 而不是 `new Function`：ESM 的 `import`/`export`
 * 在 `new Function` 里必然是语法错，会把每个合法插件都判红。`node --check` 按扩展名
 * 决定解析模式（`.mjs` 必是 ESM；`.js` 看最近的 package.json 的 `type`），这也是
 * create-plugin 生成的脚手架里写 `"type": "module"` 的原因 —— 两者是配套的。
 *
 * 返回问题列表（空数组 = 通过），由调用方决定打印与退出码。
 */
function checkEntry(pluginRoot, entry, manifest) {
  const issues = [];
  const entryPath = path.join(pluginRoot, entry);
  const ext = path.extname(entry).toLowerCase();

  // 1) tier 0 是「声明式包」，不该带可执行入口（docs/21 §2 / docs/13 §2）。
  //    早期 lint 把 tier=0 + JS entry 也放行，作者会以为自己的代码会被执行。
  if (manifest.tier === 0 && (ext === '.js' || ext === '.mjs')) {
    issues.push(`tier 0 是声明式包，不能有 JS 入口 '${entry}'`
      + '（docs/13 §2：tier 0 只读声明；要跑代码请把 tier 改成 1）');
    return issues;
  }

  if (ext !== '.js' && ext !== '.mjs') return issues;

  // 2) 语法：让 node 自己解析一遍。`.js` 依赖 package.json 的 type=module 才是 ESM，
  //    而 create-plugin 生成时已经写了 —— 手写的插件若漏了，这里会如实报语法错。
  const syntax = spawnSync(process.execPath, ['--check', entryPath], { encoding: 'utf8' });
  if (syntax.status === null) {
    // 子进程根本没起来（受限通道里 spawnSync 会报 EPERM）。这是**通道问题，不是插件问题**：
    // 必须如实说「没检查」而不是谎报语法错 —— 否则作者会去改一份本来正确的文件。
    issues.push(`无法运行语法检查（${syntax.error?.code ?? 'spawn 失败'}）：请在普通终端重跑 petdock lint`);
    return issues;
  }
  if (syntax.status !== 0) {
    const firstLine = `${syntax.stderr ?? ''}`.split(/\r?\n/).find((line) => line.includes('SyntaxError'))
      ?? `${syntax.stderr ?? ''}`.split(/\r?\n/)[0];
    issues.push(`entry 语法错 ${entry}: ${(firstLine ?? '').trim()}`);
    return issues;
  }

  // 3) import 白名单（docs/20 §1 / D-02）：只允许 @petdock/sdk 及其子模块。
  //    这里做的是**静态文本扫描**，不解析 AST —— 它要挡住的是「作者手写时顺手 import 了
  //    内核内部包或 Node 内置模块」这类显眼错误，不是对抗混淆。
  //
  //    两条刻意的放行与不放行：
  //      * 相对路径（'./presenter.js'）放行 —— 多文件插件是正常的，文档也从没禁止；
  //      * `node:*` **不放行** —— 插件跑在沙箱里，Node 内置模块根本不存在。
  //        早期版本把它和相对路径一起 continue 掉了，于是 `import fs from 'node:fs'`
  //        能 LINT-PASS，作者会以为自己的代码能在宿主里读盘。
  const source = fs.readFileSync(entryPath, 'utf8');
  const importRe = /(?:^|\n)\s*(?:import|export)\b[^;\n]*?from\s+['"]([^'"]+)['"]|(?:^|\n)\s*import\s+['"]([^'"]+)['"]/g;
  for (const match of source.matchAll(importRe)) {
    const specifier = match[1] ?? match[2];
    if (!specifier || specifier.startsWith('.')) continue;
    if (specifier.startsWith('node:')) {
      issues.push(`entry 引用了 Node 内置模块 '${specifier}'`
        + '（插件跑在沙箱里，没有 Node 内置模块；docs/13 §2）');
      continue;
    }
    if (!ALLOWED_IMPORT.test(specifier)) {
      issues.push(`entry 引用了非 SDK 模块 '${specifier}'（docs/20 §1：只允许 @petdock/sdk/*，D-02）`);
    }
  }

  return issues;
}

// ---------- check ----------

function cmdCheck(argv) {
  const target = argv[0] ? path.resolve(argv[0]) : process.cwd();
  const manifestFile = fs.existsSync(target) && fs.statSync(target).isDirectory()
    ? findManifest(target)
    : target;
  if (!manifestFile || !fs.existsSync(manifestFile)) {
    console.error(`petdock check: 找不到 ${MANIFEST_NAME}`);
    return 2;
  }

  const manifest = readManifest(manifestFile);
  const host = abiVersion();
  const plugin = manifest.api;
  const same = plugin.major === host.major && plugin.minor === host.minor;
  // M-07 走查发现的第九条缺陷：早期版本这一行无条件打印 `compatible`，于是 api.major=2 的插件
  // 会同时看到「compatible (minor-skew)」和下面的「✗ major mismatch」，自相矛盾。
  // 结论必须先算出来再打印：major 不一致就不是 compatible，也没有「minor-skew」可言。
  const majorOk = plugin.major === host.major;
  const verdict = majorOk ? 'compatible' : 'incompatible (major)';
  const label = same ? 'exact' : (majorOk ? 'minor-skew' : 'major-mismatch');
  console.log(`plugin api ${plugin.major}.${plugin.minor} vs host api ${host.major}.${host.minor}`
    + ` → ${verdict} (${label})`);

  if (plugin.major !== host.major) {
    console.log(`✗ major mismatch: 插件声明 ${plugin.major}，宿主为 ${host.major}`
      + '（docs/20 §4：major 不匹配即拒绝加载，须把 api.major 改到宿主的主版本）');
    console.log('CHECK-FAIL');
    return 1;
  }

  if (plugin.minor > host.minor) {
    console.log(`✗ 插件声明 minor ${plugin.minor} 高于宿主 ${host.minor}`
      + '（docs/20 §4：minor 不一致应警告但加载；此处判为不一致以防作者以为新 API 可用）');
    console.log('CHECK-FAIL');
    return 1;
  }

  console.log('CHECK-PASS');
  return 0;
}

/** 版本号降序比较：只比较点分数字段，非数字段按字符串退让。 */
function compareVersionDesc(a, b) {
  const pa = a.split('.');
  const pb = b.split('.');
  for (let i = 0; i < Math.max(pa.length, pb.length); i += 1) {
    const na = Number(pa[i] ?? 0);
    const nb = Number(pb[i] ?? 0);
    if (Number.isNaN(na) || Number.isNaN(nb)) {
      const cmp = (pa[i] ?? '').localeCompare(pb[i] ?? '');
      if (cmp !== 0) return -cmp;
      continue;
    }
    if (na !== nb) return nb - na;
  }
  return 0;
}

// ---------- doctor ----------

function cmdDoctor() {
  const host = abiVersion();
  let failures = 0;

  const nodeMajor = Number(process.versions.node.split('.')[0]);
  const nodeOk = nodeMajor >= 20;
  console.log(`node ${process.versions.node} ${nodeOk ? '✓' : '✗ (需要 >= 20)'}`);
  if (!nodeOk) failures += 1;

  // dotnet 的定位按「已知安装位置 → PATH」两级，且一律以**绝对路径或 shell 解析**调用：
  //   1. 本机实测：spawnSync('where', ['dotnet']) 在受限通道里返回空（where.exe 起不来），
  //      于是机器上明明有 dotnet 也报「未找到」——不要用 where/which 当唯一判据；
  //   2. spawnSync('dotnet', …) 的数组形式在 Windows 上不经 shell，只认 .exe/完整路径，
  //      故加 shell: true 让系统自己解析扩展名。
  //   3. 但 spawn 本身在受限通道里会静默失败（实测 code_verify 通道里管道捕获子进程输出
  //      报 EPERM，stdout 为空），于是「机器上有 .NET 8 却报未找到」。所以先走**不 spawn**
  //      的判据：读安装目录下的 sdk/ 子目录名（.NET SDK 每个版本一个目录，名字就是版本号），
  //      读到了就不必起子进程；只有读不到时才退回 spawn。
  const dotnetCandidates = [
    process.env.DOTNET_ROOT && path.join(process.env.DOTNET_ROOT, 'dotnet.exe'),
    process.env.ProgramFiles && path.join(process.env.ProgramFiles, 'dotnet', 'dotnet.exe'),
    process.env['ProgramFiles(x86)'] && path.join(process.env['ProgramFiles(x86)'], 'dotnet', 'dotnet.exe'),
  ].filter(Boolean);

  let dotnetVersion = '';
  for (const candidate of dotnetCandidates) {
    if (!fs.existsSync(candidate)) continue;
    const sdkDir = path.join(path.dirname(candidate), 'sdk');
    let sdkVersions = [];
    try {
      sdkVersions = fs.readdirSync(sdkDir, { withFileTypes: true })
        .filter((entry) => entry.isDirectory() && /^\d+\./.test(entry.name))
        .map((entry) => entry.name);
    } catch {
      sdkVersions = [];
    }
    if (sdkVersions.length > 0) {
      // 取最高的 8.x（没有 8.x 就取最高的任意版本，让下面如实判 ✗）
      const byVersion = sdkVersions.sort(compareVersionDesc);
      dotnetVersion = byVersion.find((v) => v.startsWith('8.')) ?? byVersion[0];
      break;
    }
  }

  if (dotnetVersion === '') {
    // 兜底：安装目录读不到（非标准安装）时才起子进程问 --version。
    let dotnet = { status: 1, stdout: '' };
    for (const candidate of dotnetCandidates) {
      if (!fs.existsSync(candidate)) continue;
      dotnet = spawnSync(candidate, ['--version'], { encoding: 'utf8' });
      break;
    }
    if (dotnet.status !== 0) {
      dotnet = spawnSync('dotnet', ['--version'], { encoding: 'utf8', shell: true });
    }
    dotnetVersion = (dotnet.stdout ?? '').trim();
  }

  const dotnetOk = dotnetVersion.startsWith('8.');
  console.log(`host .NET ${dotnetVersion || '(未找到)'} ${dotnetOk ? '✓' : '✗ (需要 .NET 8 SDK)'}`);
  if (!dotnetOk) failures += 1;

  console.log(`abi ${host.major}.${host.minor} ✓ (摘自 packages/core PetRuntimeAbi)`);

  const coreDll = path.join(REPO_ROOT, 'packages', 'core', 'PetDock.Core',
    'bin', 'Release', 'net8.0-windows', 'PetDock.Core.dll');
  const coreBuilt = fs.existsSync(coreDll);
  console.log(`kernel built ${coreBuilt ? '✓' : '✗ (先跑 dotnet build PetDock.sln -c Release)'}`);
  if (!coreBuilt) failures += 1;

  const pluginsDir = path.join(REPO_ROOT, 'packages', 'plugins');
  let writable = false;
  try {
    fs.accessSync(pluginsDir, fs.constants.W_OK);
    writable = true;
  } catch {
    writable = false;
  }
  console.log(`plugins dir writable ${writable ? '✓' : '✗'} (${path.relative(REPO_ROOT, pluginsDir)})`);
  if (!writable) failures += 1;

  // Tier 1 沙箱基座：本项目用 Wasmtime（docs/14 §4 白名单首项），不是 QuickJS。
  // docs/23 §7 的示例行写的是 `quickjs sandbox OK` —— 那是规格里的旧措辞，此处如实报实际基座。
  // 判据是**直接读还原产物**（obj/project.assets.json 的 libraries），不 spawn 子进程：
  // 早期版本用 `node -e` 探针并把仓库根当 argv[1] 传，而 `node -e` 的 argv[1] 其实是
  // 第一个位置参数之前的占位，探针读到 undefined，于是机器上明明有 Wasmtime 也报 ✗。
  const assetsFile = path.join(REPO_ROOT, 'packages', 'core', 'PetDock.Core', 'obj', 'project.assets.json');
  let wasmOk = false;
  try {
    const assets = JSON.parse(fs.readFileSync(assetsFile, 'utf8'));
    wasmOk = Object.keys(assets.libraries ?? {}).some((key) => /^Wasmtime\//.test(key));
  } catch {
    wasmOk = false;
  }
  console.log(`tier-1 sandbox base ${wasmOk ? '✓ (Wasmtime)' : '✗ (Wasmtime 未还原，先跑 dotnet restore)'}`);
  if (!wasmOk) failures += 1;

  console.log(failures === 0 ? 'DOCTOR-PASS' : `DOCTOR-FAIL failures=${failures}`);
  return failures === 0 ? 0 : 1;
}

// ---------- pack ----------

const PACK_SKIP = new Set(['node_modules', '.git', 'bin', 'obj', 'dist', 'fixtures']);

function collectPackFiles(dir, root = dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (PACK_SKIP.has(entry.name)) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) collectPackFiles(full, root, out);
    else out.push(full);
  }
  return out;
}

function cmdPack(argv) {
  let dir = process.cwd();
  let outDir = 'dist/';
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--out') {
      outDir = argv[i + 1] ?? outDir;
      i += 1;
    } else {
      dir = path.resolve(argv[i]);
    }
  }

  const manifestFile = fs.existsSync(dir) && fs.statSync(dir).isDirectory()
    ? findManifest(dir)
    : dir;
  if (!manifestFile || !fs.existsSync(manifestFile)) {
    console.error(`petdock pack: 找不到 ${MANIFEST_NAME}`);
    return 2;
  }

  const pluginRoot = path.dirname(manifestFile);
  const manifest = readManifest(manifestFile);
  const files = collectPackFiles(pluginRoot);
  const bytes = files.reduce((sum, file) => sum + fs.statSync(file).size, 0);

  // docs/52 §5 / docs/23 §6：pack 绝不打包素材。这里只能核对「包里没有已知素材扩展名」，
  // 真正的素材指纹扫描是 docs/41 D-13a（tools/asset-manifest.mjs）的职责。
  const mediaRe = /\.(png|jpe?g|webp|gif|wav|mp3|moc3|model3\.json|vrm|pmx|ase)$/i;
  const media = files.filter((file) => mediaRe.test(file));

  console.log(`pack ${manifest.id}@${manifest.version}`);
  console.log(`  files ${files.length} bytes ${bytes} (${(bytes / 1024).toFixed(1)} KiB)`);
  console.log(`  out   ${outDir}${manifest.id}-${manifest.version}.petpack`);
  if (media.length > 0) {
    console.log(`✗ ${media.length} 个素材文件会被打进包（docs/23 §6 禁止）：`);
    for (const file of media.slice(0, 10)) console.log(`    ${path.relative(pluginRoot, file)}`);
    console.log('PACK-FAIL');
    return 1;
  }

  console.log('✓ 无素材文件（docs/23 §6 / docs/52 §5）');
  console.log('PACK-DRY-RUN （不写文件：.petpack 的签名位与格式未定，见 docs/23 §9 推迟项）');
  return 0;
}

// ---------- create-plugin ----------

function sanitizeToId(name) {
  const cleaned = name.toLowerCase().replace(/[^a-z0-9]+/g, '');
  return `com.example.${cleaned || 'plugin'}`;
}

/**
 * 生成插件骨架。
 *
 * **kind / tier 必须可选**：docs/22 §2 与 docs/23 §2 都写着「选择 kind 与 tier」，
 * 而早期实现把 `kind='brain'`、`tier=1` 写死 —— M-07 冷启动走查里，作者想做一个
 * importer（docs/22 §4 整节都在讲 importer），却只能得到 brain 骨架，只能手改 manifest。
 *
 * 用命令行参数而不是交互式提问：CLI 要被脚本与 CI 调用（docs/23 §8），
 * 交互式提问在非 TTY 下会挂住；参数化既能被脚本用，也能被人在终端里用。
 */
function cmdCreatePlugin(argv) {
  let name = '';
  let id = '';
  let kind = '';
  let tier = null;
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--kind') { kind = argv[i + 1] ?? ''; i += 1; }
    else if (arg === '--tier') { tier = Number(argv[i + 1]); i += 1; }
    else if (!name) name = arg;
    else if (!id) id = arg;
  }

  if (!name) {
    console.error('petdock create-plugin: 需要一个名字，例如 my-first-adapter');
    console.error('  可选：--kind <importer|presenter|brain|interaction|state|service> --tier <0|1|2>');
    return 2;
  }

  kind = kind || 'brain';
  if (!KINDS.includes(kind)) {
    console.error(`petdock create-plugin: 未知 kind '${kind}'（可选 ${KINDS.join(' | ')}）`);
    return 2;
  }
  // tier 默认值跟 kind 走：importer/presenter/state 在仓库里全是 tier 0（纯声明 + 内建实现），
  // 只有 brain/interaction 需要跑代码。默认给 1 会让 importer 骨架默认踩「tier 0 才有内建实现」。
  if (tier === null) tier = (kind === 'brain' || kind === 'interaction') ? 1 : 0;
  if (!Number.isInteger(tier) || tier < 0 || tier > 2) {
    console.error(`petdock create-plugin: tier 必须是 0 / 1 / 2，收到 '${tier}'`);
    return 2;
  }

  id = id || sanitizeToId(name);
  const slug = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'plugin';
  const target = path.resolve(process.cwd(), slug);

  if (fs.existsSync(target)) {
    console.error(`petdock create-plugin: ${slug} 已存在，不覆盖`);
    return 2;
  }

  const host = abiVersion();
  const srcDir = path.join(target, 'src');
  fs.mkdirSync(srcDir, { recursive: true });

  // tier 0 是声明式包（docs/13 §2），不该带可执行入口 —— 与 lint 的判据同源。
  const entry = tier === 0 ? '' : 'src/main.js';

  const manifest = {
    id,
    name: slug,
    version: '0.1.0',
    api: { major: host.major, minor: host.minor },
    kind,
    tier,
    entry,
    capabilities: DEFAULT_CAPS[kind],
    license: { spdx: 'MIT' },
  };
  fs.writeFileSync(path.join(target, MANIFEST_NAME), `${JSON.stringify(manifest, null, 2)}\n`);

  fs.writeFileSync(path.join(target, 'package.json'), `${JSON.stringify({
    name: id,
    version: '0.1.0',
    private: true,
    type: 'module',
    main: entry || undefined,
  }, null, 2)}\n`);

  if (entry) fs.writeFileSync(path.join(srcDir, entry.slice('src/'.length)), MAIN_JS_TEMPLATE(kind));
  else fs.rmdirSync(srcDir);
  fs.writeFileSync(path.join(target, 'README.md'), readmeTemplate(slug, id, kind, tier));

  console.log(`✓ created ${slug}/`);
  console.log(`    ${MANIFEST_NAME}   (id=${id}, kind=${kind}, tier=${tier})`);
  console.log('    package.json');
  if (entry) console.log(`    ${entry}`);
  console.log('    README.md');
  console.log(`  next: cd ${slug} && petdock lint && petdock check`);
  return 0;
}

/**
 * 骨架代码模板。按 kind 分叉，而不是给所有人一个 brain 模板。
 *
 * 为什么值得分叉：M-07 走查里作者做的是 importer，拿到的却是 `export const brain = {…}`，
 * 而 docs/22 §4 讲的是 `export const importer = { probe, import }` —— 骨架与它自己引用的
 * 文档互相矛盾，作者只能猜。模板是这个项目里唯一「一定会被逐字复制」的代码。
 */
function MAIN_JS_TEMPLATE(kind) {
  if (kind === 'importer') {
    return `// src/main.js —— Tier 1 importer 插件入口（docs/22 §4）。
//
// 宿主调用 probe 判断「这份素材我认不认得」，认得才调 import。
// 两者都收到 fileTree：一个只读的目录视图（has(path) / readJson(path) / list(dir)）。
//
// 只允许 import docs/20 §1 登记的模块；不要 import 内核内部包或 Node 内置模块
// （petdock lint 会在构建期拒绝）。

export const importer = {
  id: 'com.example.myimporter',

  /** 返回 0..1 的置信度；0 表示「不是我的格式」。不要在这里做昂贵的解码。 */
  async probe(fileTree) {
    if (!fileTree.has('manifest.json')) return 0
    return 0.9
  },

  /** 真正的转换。抛错即中止导入，宿主会把错误原样显示给用户。 */
  async import(fileTree) {
    const manifest = await fileTree.readJson('manifest.json')
    return {
      name: manifest.name ?? 'imported-pet',
      frames: [],
    }
  },
}

export default importer
`;
  }

  if (kind === 'presenter' || kind === 'state' || kind === 'service') {
    return `// src/main.js —— Tier 1 ${kind} 插件入口。
//
// 仓库里 ${kind} 的常用形态是 tier 0（声明式 + 内建实现），
// 只有需要自定义行为时才写代码。本文件由 create-plugin 生成，可直接改。
//
// 只允许 import docs/20 §1 登记的模块（petdock lint 会拒绝其它 import）。

export const ${kind} = {
  id: 'com.example.my${kind}',

  async init(ctx) {
    // 一次性初始化。不要在这里请求能力。
  },

  async dispose() {
    // 释放资源；宿主给 1000ms 上限（docs/13 §8 第 4 条）。
  },
}

export default ${kind}
`;
  }

  return `// src/main.js —— Tier 1 brain 插件入口（docs/11 §4 的 IBrain）。
//
// 宿主把本文件当模块加载，取它导出的 \`brain\` 对象。四个方法对应 docs/11 §4 的
// init / tick / onEvent / dispose；tick 返回 docs/16 §5 的 BrainDirective 数组。
//
// 只允许 import docs/20 §1 登记的模块；不要 import 内核内部包或 Node 内置模块
// （petdock lint 会在构建期拒绝）。

/** 本插件要订阅的事件名，必须是 docs/16 的 14 个之一；不声明就在这里留空数组。 */
export const events = ['core.clock.tick']

let elapsedMs = 0

export const brain = {
  meta: { name: 'My First Brain', version: '0.1.0' },

  async init(ctx) {
    // ctx 给出实例 id 与已批准的能力；在这里做一次性初始化，不要在这里请求能力。
    elapsedMs = 0
  },

  // dt 是毫秒增量；world 是宿主每帧采样的 WorldSnapshot（时间/鼠标/前台窗口）。
  tick(dt, world) {
    elapsedMs += dt
    // 示例：每 2 秒把宠物切到另一个片段。真实的 setAnimation 目标片段
    // 必须存在于所选呈现包里，否则仲裁器会记 refused。
    return []
  },

  onEvent(event) {
    // 只收到在 manifest.events（或本文件 events 导出）里声明过的事件。
  },

  async dispose() {
    // 释放资源。宿主给 1000ms 上限（docs/13 §8 第 4 条）。
  },
}

export default brain
`;
}

function readmeTemplate(slug, id, kind, tier) {
  return `# ${slug}

PetDock ${kind} 插件（Tier ${tier}），由 \`petdock create-plugin\` 生成。

## 下一步

    petdock lint      # 校验 manifest 与入口文件
    petdock dev       # 本地开发循环（报告宿主会怎样处理这次改动）
    petdock check     # 与宿主 ABI 的兼容性
    petdock pack      # 打包清单（素材零分发检查）

## 参考

- 插件契约：docs/11-pet-runtime-abi.md（ABI）、docs/13-plugin-system.md（生命周期）
- SDK 模块面：docs/20-plugin-sdk-spec.md
- manifest 字段：docs/21-manifest-schema.md
- 作者走查：docs/22-adapter-authoring-guide.md

## 素材许可

本插件不携带任何第三方素材。若你的插件需要素材，请让用户从本地导入
（素材零分发，docs/52 §5）——不要把它们提交进本仓库。
`;
}

// ---------- dev ----------

/**
 * 本地开发循环：校验 → 监听 → 打印宿主会怎么处理这次改动。
 *
 * **能力边界（docs/23 §3 的规格 vs 宿主的实际行为）**：规格写的是「起 dev server + 通知宿主
 * 热重载」，但宿主侧的监听只挂在一个文件上 —— `PluginHost.cs:179` 的 FileSystemWatcher 过滤
 * 模式是 `petdock-plugin.json` 一个名字，`PluginHost.Lifecycle.cs:207` 在 manifest 内容哈希
 * 未变时直接返回。所以：
 *   * 改 manifest → 宿主会真的热重载（这是它唯一监听的路径）；
 *   * 改 src/** 代码 → 宿主**不会**收到任何通知，必须重启宿主。
 * 本命令如实报这件事，而不是假装「保存即重载」：一个说谎的 dev server 比没有更糟。
 */
function cmdDev(argv) {
  let dir = process.cwd();
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === '--plugin') {
      dir = path.resolve(argv[i + 1] ?? '.');
      i += 1;
    } else {
      dir = path.resolve(argv[i]);
    }
  }

  const manifestFile = fs.existsSync(dir) && fs.statSync(dir).isDirectory()
    ? findManifest(dir)
    : dir;
  if (!manifestFile || !fs.existsSync(manifestFile)) {
    console.error(`petdock dev: 在 ${path.relative(process.cwd(), dir) || '.'} 下找不到 ${MANIFEST_NAME}`);
    return 2;
  }

  const pluginRoot = path.dirname(manifestFile);
  const manifest = readManifest(manifestFile);
  const host = abiVersion();
  const codeFiles = collectPackFiles(pluginRoot);

  console.log(`[dev] watching ${path.relative(process.cwd(), pluginRoot) || '.'} (${codeFiles.length} files)`);
  console.log(`[dev] manifest OK (id=${manifest.id}, tier=${manifest.tier})`);

  if (manifest.api?.major !== host.major) {
    console.log(`[dev] ✗ api.major ${manifest.api?.major} ≠ host ${host.major}（docs/20 §4：major 不匹配即拒绝加载）`);
    console.log('DEV-FAIL');
    return 1;
  }

  // M-07 走查发现的第七条缺陷：早期 dev 在 entry 被删、entry 有语法错时都打印 DEV-OK。
  // dev 的价值就在于「保存之前告诉我宿主会不会拒」，所以 entry 判据必须和 lint 同源。
  const entry = manifest.entry;
  if (typeof entry === 'string' && entry.length > 0) {
    if (!fs.existsSync(path.join(pluginRoot, entry))) {
      console.log(`[dev] ✗ entry file missing: ${entry}`);
      console.log('DEV-FAIL');
      return 1;
    }
    const entryIssues = checkEntry(pluginRoot, entry, manifest);
    if (entryIssues.length > 0) {
      for (const issue of entryIssues) console.log(`[dev] ✗ ${issue}`);
      console.log('DEV-FAIL');
      return 1;
    }
    console.log(`[dev] entry OK (${entry})`);
  }

  console.log('[dev] hot-reload: manifest only（宿主只监听 petdock-plugin.json，见 docs/23 §3 的已知差距）');
  console.log('[dev] 改 src/** 不会触发热重载，需重启宿主；改 manifest 会。');
  console.log('[dev] 宿主启动：dotnet run --project packages/core/PetDock.Core -c Release');
  console.log('DEV-OK');
  return 0;
}

// ---------- 分发 ----------

async function main() {
  const argv = process.argv.slice(2);
  const command = argv[0];
  const rest = argv.slice(1);

  if (!command || command === '--help' || command === '-h' || command === 'help') {
    usage();
    return 0;
  }
  if (command === '--version' || command === '-v') {
    const host = abiVersion();
    console.log(`petdock/${VERSION} (abi ${host.major}.${host.minor}, host .NET 8)`);
    return 0;
  }

  switch (command) {
    case 'create-plugin': return cmdCreatePlugin(rest);
    case 'dev': return cmdDev(rest);
    case 'lint': return await cmdLint(rest);
    case 'check': return cmdCheck(rest);
    case 'doctor': return cmdDoctor();
    case 'pack': return cmdPack(rest);
    default:
      console.error(`petdock: 未知命令 '${command}'`);
      usage();
      return 2;
  }
}

// main 是 async（lint 走同进程动态 import 校验器），必须 await 后再定退出码：
// 直接 process.exit(main()) 会把 Promise 传给 process.exit，报
// `TypeError [ERR_INVALID_ARG_TYPE]: The "code" argument must be of type number`。
process.exit(await main());

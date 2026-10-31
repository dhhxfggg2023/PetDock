#!/usr/bin/env node
// tools/verify.mjs —— D-16 验证门禁（RFC-38 §3）：一条命令跑完所有层，每层具名、可单独复现。
//
// 为什么存在：D-16 之前「跑一遍」是 原规格 98 §4 里的一段手抄命令清单，判绿靠人读末行
// UI-OK / HOST-OK；手抄一遍就有三种失败方式——漏层、把管道退出码当结论、事后说不清哪层没跑。
// 本文件把同样那批命令收进一张表：层 = 离真实运行时的距离，step = 一条命令 + 一个末行 marker。
// 每一步同时判「退出码 0」与「marker 真的出现在 stdout 里」：管道可以骗退出码，骗不了文件里的文本。
//
// 用法：
//   node tools/verify.mjs                  # 默认三层 static / build / contract
//   node tools/verify.mjs --all            # 全部六层（会建窗口、跑性能、打包）
//   node tools/verify.mjs --only window    # 只跑一层；--only contract:hosttest 只跑一层里的一步
//   node tools/verify.mjs --list           # 只打印层表（含当前 config）
//   node tools/verify.mjs --config release # 换配置（默认 debug）：dll 路径与 build 层的 -c 一起换
// 退出码：0 = 全过；1 = 有层失败（末行 VERIFY-FAIL 点名 layer/step）。

import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const NODE = process.execPath;
const STEP_TIMEOUT_MS = 20 * 60 * 1000;

// --config 的两侧取值：命令行的名字（小写）→ dotnet 的配置名（bin 子目录同名）。
// 为什么不是只换 dll 路径：Debug 的 dll 配 Release 的 build 会得到「验的是旧字节」这种最难发现的假绿，
// 所以选中的配置必须同时决定 dll 路径、build 层的 -c，以及证据里记下来的那个字段。
const CONFIG_DIRS = { debug: 'Debug', release: 'Release' };
const DEFAULT_CONFIG = 'debug';

let CONFIG = DEFAULT_CONFIG;
let LAYERS = [];

function coreDll(config) {
  return `packages/core/PetDock.Core/bin/${CONFIG_DIRS[config]}/net8.0-windows/PetDock.Core.dll`;
}

function node(script) {
  return { cmd: NODE, args: [script] };
}

function selftest(config, flag, extraArgs = [], marker = null) {
  return { cmd: 'dotnet', args: ['exec', coreDll(config), flag, ...extraArgs], marker };
}

// 层表按配置生成：调用一次得到一份，层/步 id 与 marker 不随配置变（换配置不该换判据）。
function layersFor(config) {
  const cfg = CONFIG_DIRS[config];
  return [
  {
    name: 'static',
    title: 'L0 离线纯逻辑：九道静态门禁，逐条单独跑',
    proves: '源码依赖方向、版本号三处一致、内核黑名单与体积、插件清单（内核校验器）、素材摆放与指纹、「界面文案不许留字面量」、「两门语言的键与占位符形状一致」，以及「每条文案键都有产品代码引用，或带着理由被显式豁免」——九份互不相同的静态事实互相一致。',
    notProven: '没有任何一行代码被执行：编译失败、插件装载失败、窗口画不出来，这一层照样全绿。文案表本身能被读进内存、能在 en-US 下画出来，也不是这一层证明的。',
    steps: [
      { id: 'depscan', ...node('tools/depscan.mjs'), marker: 'DEPSCAN-PASS' },
      // version-parity 一度只挂在 npm run lint 上，于是「跑过 verify」并不覆盖版本号漂移 ——
      // 而 verify 存在的理由正是「手抄命令就会漏层」。lint 里有的门，这一层一个都不能少。
      { id: 'version-parity', ...node('tools/version-parity.mjs'), marker: 'VERSION-PARITY-PASS' },
      { id: 'lint-core', ...node('tools/lint-core.mjs'), marker: 'LINT-CORE-PASS' },
      { id: 'manifest', ...node('tools/validate-manifest.mjs'), marker: 'MANIFEST-PASS' },
      { id: 'assets', ...node('tools/asset-manifest.mjs'), marker: 'ASSET-MANIFEST-PASS' },
      { id: 'fingerprint', ...node('tools/asset-fingerprint.mjs'), marker: 'FINGERPRINT-PASS' },
      { id: 'i18n', ...node('tools/i18n-lint.mjs'), marker: 'I18N-LINT-PASS' },
      { id: 'i18n-parity', ...node('tools/i18n-parity.mjs'), marker: 'I18N-PARITY-PASS' },
      // 第 9 道：键在两种语言里都在（i18n-parity 已判），但**没有任何产品代码引用它** ——
      // 那正是「功能整块不可达而门禁全绿」的形态（HANDOVER §3.1/§4.1）。
      { id: 'i18n-reachability', ...node('tools/i18n-reachability.mjs'), marker: 'I18N-REACHABILITY-PASS' },
    ],
  },
  {
    name: 'build',
    title: `L1 构建：${cfg} 装配（后面三层都跑这份产物）`,
    proves: '整个解决方案能编译，且下面 contract/window 层跑的就是这一份 dll。',
    notProven: cfg === 'Release'
      ? '只证明这一份 Release 装配能编译；编译通过不代表任何一条运行时判据成立。'
      : 'Release 配置没编译过（--config release 才会跑它）；编译通过不代表任何一条运行时判据成立。',
    steps: [
      {
        id: 'dotnet-build',
        cmd: 'dotnet',
        args: ['build', 'PetDock.sln', '-c', cfg, '--nologo', '-v', 'm'],
        // marker: null —— `dotnet build` 的结论行随区域设置漂（Build succeeded / 已成功生成），只判退出码。
        marker: null,
        timeoutMs: 10 * 60 * 1000,
      },
    ],
  },
  {
    name: 'contract',
    title: 'L2 进程内契约：真实装配 + 真实插件，无桌面交互',
    proves: `插件宿主全生命周期、帧驱动与降级链、事件总线、动画循环各自的可判定判据（跑的是 ${cfg} 那份 dll）。`,
    notProven: '不证明窗口真的显示在桌面上、不证明鼠标消息能穿透——那归 window 层；也不证明功耗。',
    steps: [
      {
        id: 'hosttest',
        ...selftest(config, '--hosttest', [], 'HOST-OK'),
        // 记录在案的约束：--hosttest 会在 %TEMP% 下造夹具，沙箱拒绝，必须把 TEMP/TMP 指到证据目录内。
        env: (dir) => {
          const tmp = path.join(dir, 'tmp-hosttest');
          fs.mkdirSync(tmp, { recursive: true });
          return { ...process.env, TEMP: tmp, TMP: tmp };
        },
      },
      { id: 'drivetest', ...selftest(config, '--drivetest', [], 'DRIVE-OK') },
      { id: 'eventtest', ...selftest(config, '--eventtest', [], 'EVENT-OK') },
      { id: 'animtest', ...selftest(config, '--animtest', ['idle', 'walk', 'hover'], 'ANIM-OK') },
    ],
  },
  {
    name: 'window',
    // 条数会变（RFC-37 位置持久化后 149→153），标题里写死数字就一定会过期：这里不复述条数，
    // 真实条数看 `--uitest` 自己的收尾行。
    title: 'L3 真实窗口：--uitest（全量 CHECK，会闪桌面）',
    proves: '分层窗真的建起来、托盘与面板真的能操作、导入/设置/位置/命中图/单实例这些 UI 母体判据成立。',
    notProven: '不证明「好不好看」「手感如何」：宠物在桌面上长什么样、点击穿透的手感、两只以上宠物的排布，都没有判据，只有人眼（原规格 98 §3.2）。这一层也**故意**不把 TEMP/TMP 指进证据目录（contract 层会指）：--uitest 有一条判据断言审计流水不落进仓库源码树，把 TEMP 搬进仓库反而会让它红。',
    // 这一层故意**不**把 TEMP/TMP 指进证据目录（contract 层会指）：`--uitest` 有一条判据专门
    // 断言审计流水不落进仓库源码树，把 TEMP 搬进仓库反而会让它红。别为了「对称」把这里统一掉。
    // 受限令牌下 %TEMP% 不可写时，夹具自己退到构建输出目录（bin/，见 SelfTestScratch），
    // 并打一行 STAT selftest-scratch 说明落点 —— 所以这一层在该环境下不需要外部改 TEMP 也能跑。
    steps: [{ id: 'uitest', ...selftest(config, '--uitest', ['--log', 'REPLACED'], 'UI-OK') }],
  },
  {
    name: 'perf',
    title: 'L4 真实功耗读数：--perftest / --residenttest',
    proves: 'D-14a~f 的绝对门限与相对回归，以及常驻形态（真实的 Program/UiHost 路径）的同一组读数。'
      + ' RFC-40 之后相对回归分三层：`*-baseline-shape` 判仓库状态（基线文件在不在、premise 在不在且 schema 对、'
      + '必需字段全不全），是硬门且与机器负载无关；`*-relative-diagnostic` 永远打印差值与'
      + '「若按旧语义会不会判红」，但不进失败数；`*-relative-gated` 只在基线 premise 的前提字段与本次运行'
      + '全等时发射，发射时才是硬门。',
    notProven: '读数只覆盖本机这一种硬件与窗口管理；前提（机器三段 / 空载水平+允许上浮 / fps 档 / 花名册 / '
      + '采样参数）不符时相对判据只诊断、不拦截，所以「本层没红」不等于「与基线一样快」——差值在 '
      + '`*-relative-diagnostic` 行里，要人读；8 小时浸泡（--soak 28800）不在这里，且至今没有一次'
      + '完整读数——2026-10-24 08:58 跑过一次，脚本自己死在 op_Subtraction 重载上（RFC-36 §7.2）。',
    steps: [
      { id: 'perftest', ...selftest(config, '--perftest', [], 'PERF-OK') },
      {
        id: 'residenttest',
        // 采集条件：`--seconds 10 --warmup 4000`，花名册 = packages/plugins 的目录
        // （经 PETDOCK_PLUGINS_ROOT 指到真源码树）。旧性能基线已随
        // 旧测试目录退役，本步只出读数与绝对门限，不再与基线做相对比较。
        // 花名册不存在时**不退回默认根**：直接把这一步判红并说清缺什么。
        ...selftest(config, '--residenttest', ['--seconds', '10', '--warmup', '4000'], 'RESIDENT-PERF-OK'),
        guard: () => {
          const roster = path.join(REPO_ROOT, 'packages', 'plugins');
          return fs.existsSync(roster) ? null : `花名册目录不存在：packages/plugins（本步要测真实插件装配，缺了只能空跑）`;
        },
        env: () => ({ ...process.env, PETDOCK_PLUGINS_ROOT: path.join(REPO_ROOT, 'packages', 'plugins') }),
      },
    ],
  },
  {
    name: 'dist',
    title: 'L5 真实发布物：release → pack → 泄漏扫描 → 解压后运行 → 摘要',
    proves: '发布目录能装配、能封成一个单顶层目录的 zip（顶层目录名不带版本号：更新是原地覆盖，名字带版本就会和内容对不上）、中央目录与本地头说法一致、清单与条目集对账、用 .NET ZipFile（资源管理器同一条路径）解压后 --hosttest 仍全过、同一棵树重跑得到同一份字节。',
    notProven: '不证明签名（本机无 signtool）、不证明另一台干净的 Windows 上能跑——只证明这台机器解压后能跑。'
      + '更新链条只证明到「搬运工自己按参数搬一次文件是对的」：真实的一次「用户点立即重启更新」'
      + '（宿主退出 → 搬运工接手 → 重启）没有在判据里端到端跑过，那需要关掉一个真的常驻实例。',
    steps: [
      { id: 'release', ...node('tools/release.mjs'), marker: 'RELEASE-OK', timeoutMs: 20 * 60 * 1000 },
      { id: 'pack', ...node('tools/pack.mjs'), marker: 'PACK-OK' },
      // 打包之后先问一句「这份东西里有没有**这台构建机**」：0.4.3 之前 dll/pdb 里带着绝对源路径，
      // 同一个提交换个目录编就变 —— 那种事 recon 不出来（repro 只在同一目录重跑），人也读不出来（压缩包里的二进制）。
      { id: 'leak-scan', ...node('tools/release-leak-scan.mjs'), marker: 'LEAK-OK' },
      // 顺序有语义：摘要必须描述**已经通过自检的那一个 zip**。先算摘要后跑自检的话，
      // 一个自检失败的 zip 也会拿到 .sha256 旁车文件，下游会把它当作「已验证的产物」发布。
      { id: 'portable-selftest', ...node('tools/portable-selftest.mjs'), marker: 'PORTABLE-OK' },
      // 搬运工的判据（2026-10-31）：它不跑在宿主进程里，所以 --uitest 碰不到它。
      // 这一步在临时目录里造一个「安装目录 + 暂存区」，真的调一次 PetDock.Update.exe，
      // 断言文件真的被换掉、备份真的在、result.txt 真的写了。
      { id: 'helper-selftest', ...node('tools/update-helper-selftest.mjs'), marker: 'HELPER-OK', timeoutMs: 5 * 60 * 1000 },
      // 市场索引必须与发行包同一次构建产出：装的是 zip 里的插件，索引里写的是它们各自的 sha256。
      { id: 'market-index', ...node('tools/gen-market-index.mjs'), marker: 'MARKET-OK' },
      { id: 'zip-sha256', kind: 'digest', marker: null },
      // 可复现不是口号，是判据：把同一棵树再装配一遍、再封一遍，产物必须逐字节相同。
      // 2026-10-30 实测过远程包与本地包差 35 个条目（PE 时间戳/PDB GUID 不同），
      // 而当时没有任何判据能说出「哪一次构建是错的」—— 靠人回忆。现在自己跑第二遍。
      { id: 'repro', kind: 'repro', marker: 'REPRO-OK', timeoutMs: 20 * 60 * 1000 },
      // 程序自身的更新源必须与产物同一次构建产出：面板的「检查更新」比的是这个文件里的版本，
      // 下的也是它声明的 sha256。手写一定会漂移成「声明的指纹和包不一样」。
      //
      // 位置在 repro **之后**，而且不能提前：这一步会改写仓库根的 update.json（一个 git 跟踪文件），
      // 一旦跑在 repro 前面，repro 重跑 release 时工作树就是脏的（source_dirty=true），
      // 两次构建必然不一致 —— 2026-10-31 实测就是这么红了一次。
      // 顺序口径：repro 判「同一棵树两次构建字节相同」，update-feed 是它的**下游**产物描述。
      { id: 'update-feed', ...node('tools/gen-update-feed.mjs'), marker: 'UPDATE-OK' },
      // 本地产物绿了不代表**发布出去了**：把 zip 传到 Release 是手工的一步。
      // 2026-10-30 真漏过一次（加了市场却忘了重发本体），所以这一步必须机器盯着。
      // 它要联网，且只在 dist 层跑（--only dist / --all）；默认三层不碰它。
      {
        id: 'release-align',
        ...node('tools/verify-release.mjs'),
        marker: 'RELEASE-VERIFY-OK',
      },
    ],
  },
  ];
}

const DEFAULT_LAYERS = ['static', 'build', 'contract'];

// 证据目录 README 的全局非证明项：它们不随层走，永远成立。
const GLOBAL_NOT_PROVEN = [
  '桌面观感与点击穿透手感：没有任何判据能判「好不好看」「点下去对不对」，只有人眼确认过才算。',
  '两只以上宠物的桌面排布：判据只证明窗口可见 + 泵在出帧。',
  '托盘「移除」真点一次看窗口关掉：只验了命令目标与清单交接。',
  '多屏 / 混合 DPI / 拔插副屏 / 真实全屏游戏：只有判据造的场景。',
];

function stamp() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function parseArgs(argv) {
  const opts = { layers: null, all: false, keepGoing: false, list: false, allowStale: false, config: DEFAULT_CONFIG };
  const wanted = [];
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--all') opts.all = true;
    else if (arg === '--keep-going') opts.keepGoing = true;
    else if (arg === '--allow-stale') opts.allowStale = true;
    else if (arg === '--list') opts.list = true;
    else if (arg === '--config') {
      const value = argv[i + 1];
      if (!value) fail(`--config 需要参数：${Object.keys(CONFIG_DIRS).join(' | ')}`);
      opts.config = value;
      i += 1;
    } else if (arg.startsWith('--config=')) opts.config = arg.slice('--config='.length);
    else if (arg === '--only') {
      const value = argv[i + 1];
      if (!value) fail('--only 需要参数，如 --only contract 或 --only contract:hosttest');
      wanted.push(value);
      i += 1;
    } else if (arg.startsWith('--only=')) wanted.push(arg.slice('--only='.length));
    else fail(`不认识的参数 ${arg}`);
  }
  if (wanted.length > 0) opts.layers = wanted;
  return opts;
}

function fail(message) {
  console.log(`VERIFY-FAIL config ${message}`);
  process.exit(1);
}

function selectLayers(names) {
  if (!names) return DEFAULT_LAYERS.map(layerByName);
  const picked = [];
  for (const raw of names.flatMap((name) => name.split(',')).map((name) => name.trim()).filter(Boolean)) {
    const [layerName, stepId] = raw.split(':');
    const layer = layerByName(layerName);
    if (!layer) fail(`没有叫 ${layerName} 的层（用 --list 看层表）`);
    if (stepId) {
      if (!layer.steps.some((step) => step.id === stepId)) fail(`层 ${layerName} 里没有叫 ${stepId} 的步`);
      picked.push({ ...layer, steps: layer.steps.filter((step) => step.id === stepId) });
    } else {
      picked.push(layer);
    }
  }
  return picked;
}

function layerByName(name) {
  return LAYERS.find((layer) => layer.name === name) ?? null;
}

function tail(file, lines = 20) {
  if (!fs.existsSync(file)) return '';
  const text = fs.readFileSync(file, 'utf8');
  const all = text.split(/\r?\n/).filter((line) => line.trim() !== '');
  return all.slice(-lines).join('\n');
}

// `--uitest` 需要把 --log 指向证据目录（它的判据详情只落文件）。args 里的 REPLACED 占位在运行时替换。
function resolveArgs(step, dir) {
  return (step.args ?? []).map((arg) => (arg === 'REPLACED' ? path.join(dir, 'uitest.log') : arg));
}

function runStep(step, dir) {
  const started = Date.now();
  const out = path.join(dir, `${step.id}.out`);
  const err = path.join(dir, `${step.id}.err`);

  if (step.kind === 'digest') {
    const result = zipDigest();
    fs.writeFileSync(out, `${result.detail}\n`, 'utf8');
    fs.writeFileSync(err, '', 'utf8');
    return { id: step.id, code: result.ok ? 0 : 1, marker: null, markerOk: true, durationMs: Date.now() - started, out, err };
  }

  if (step.kind === 'repro') {
    const detail = runRepro(step, dir);
    fs.writeFileSync(err, '', 'utf8');
    const ok = detail.includes(step.marker);
    fs.writeFileSync(out, detail, 'utf8');
    return { id: step.id, code: ok ? 0 : 1, marker: step.marker, markerOk: ok, durationMs: Date.now() - started, out, err };
  }

  // guard 是「跑之前就得成立的前提」：不成立时**不要**退回某个默认值继续跑。
  // H3 那次假红（基线 19.64MB vs 裸跑 23.7→30.6MB）就是静默退回默认花名册的形状。
  if (step.guard) {
    const reason = step.guard(dir);
    if (reason) {
      fs.writeFileSync(out, `CHECK verify-${step.id}-guard FAIL ${reason}\n`, 'utf8');
      fs.writeFileSync(err, '', 'utf8');
      return { id: step.id, code: 1, marker: step.marker, markerOk: false, durationMs: Date.now() - started, out, err };
    }
  }

  const fdOut = fs.openSync(out, 'w');
  const fdErr = fs.openSync(err, 'w');
  let result;
  try {
    result = spawnSync(step.cmd, resolveArgs(step, dir), {
      cwd: REPO_ROOT,
      // 不接管道：原生程序的输出接 PowerShell cmdlet 会被吞（记录在案），这里直接落到文件描述符。
      stdio: ['ignore', fdOut, fdErr],
      env: step.env ? step.env(dir) : process.env,
      timeout: step.timeoutMs ?? STEP_TIMEOUT_MS,
    });
  } finally {
    fs.closeSync(fdOut);
    fs.closeSync(fdErr);
  }

  const code = typeof result.status === 'number' ? result.status : 1;
  const text = fs.readFileSync(out, 'utf8');
  const markerOk = step.marker ? text.includes(step.marker) : true;
  return { id: step.id, code, marker: step.marker, markerOk, durationMs: Date.now() - started, out, err };
}

// D-18 的一半：zip 的摘要随产物落盘，产物能自证「我是被验证过的这一个」。
//
// 2026-10-30 改口径：旁车由 tools/pack.mjs 在封装成功时写，这里改为**核对**，不再覆盖。
// 原先只有这里写旁车，`npm run dist` 重建 zip 却不刷新旁车，于是 dist/ 里同时躺着一个新 zip
// 和一个描述旧 zip 的 .sha256 —— 谁拿它对账都会验错东西，而且没有任何判据报这件事。
// 核对而不是重写，才是「产物自证」：重写等于让被测者自己出题自己判卷。
function zipDigest() {
  const pkg = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8'));
  const rel = `artifacts/dist/PetDock-${pkg.version}-win-x64.zip`;
  const abs = path.join(REPO_ROOT, rel);
  if (!fs.existsSync(abs)) return { ok: false, detail: `CHECK verify-dist-zip-sha256 FAIL 缺 ${rel}` };
  const digest = createHash('sha256').update(fs.readFileSync(abs)).digest('hex');
  const sidecar = `${abs}.sha256`;
  const bytes = fs.statSync(abs).size;
  const where = path.relative(REPO_ROOT, sidecar);
  if (!fs.existsSync(sidecar)) {
    return { ok: false, detail: `CHECK verify-dist-zip-sha256 FAIL 缺旁车 ${where}（由 tools/pack.mjs 在封装成功时写）` };
  }
  const declared = fs.readFileSync(sidecar, 'utf8').trim().split(/\s+/)[0];
  if (declared !== digest) {
    return {
      ok: false,
      detail: `CHECK verify-dist-zip-sha256 FAIL 旁车与产物不符 declared=${declared} actual=${digest} 产物=${rel}（zip 被重建而旁车没跟上，跑 npm run dist 会一起刷新）`,
    };
  }
  return {
    ok: true,
    detail: `CHECK verify-dist-zip-sha256 PASS sha256=${digest} bytes=${bytes} sidecar=${where}`,
  };
}

/**
 * 可复现自证：同一棵树，再装配一遍、再封一遍，zip 的 sha256 必须不变。
 *
 * 为什么这一条非有不可：adjust 过 sha256 的构建如果只在「第二次」才不同，
 * 单次构建永远看不出来；而它一旦发生，「Release 上的包 == 本地最新构建」这条判据
 * 就会把人的注意力引到「是不是忘了上传」，而不是「构建本身不确定」。
 * 判据只认第二次跑出来的字节。
 */
function runRepro(step, dir) {
  const pkg = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8'));
  const rel = `artifacts/dist/PetDock-${pkg.version}-win-x64.zip`;
  const abs = path.join(REPO_ROOT, rel);
  const lines = [];
  if (!fs.existsSync(abs)) {
    return `CHECK verify-${step.id} FAIL 缺 ${rel}（先跑 pack）\n`;
  }
  const before = createHash('sha256').update(fs.readFileSync(abs)).digest('hex');
  const logPath = path.join(dir, `${step.id}.log`);
  const logFd = fs.openSync(logPath, 'w');
  try {
    for (const script of ['tools/release.mjs', 'tools/pack.mjs']) {
      const result = spawnSync(NODE, [script], {
        cwd: REPO_ROOT,
        stdio: ['ignore', logFd, logFd],
        env: process.env,
        timeout: step.timeoutMs ?? STEP_TIMEOUT_MS,
      });
      lines.push(`RUN ${script} exit=${result.status}`);
      if (result.status !== 0) {
        lines.push(`  → 第二次构建失败，详见 ${path.relative(REPO_ROOT, logPath)}`);
        return lines.join('\n') + `\nCHECK verify-${step.id} FAIL 第二次构建没跑通\n`;
      }
    }
  } finally {
    fs.closeSync(logFd);
  }
  const after = createHash('sha256').update(fs.readFileSync(abs)).digest('hex');
  lines.push(`BEFORE sha256=${before}`, `AFTER  sha256=${after}`);
  if (before !== after) {
    lines.push('  → 同一棵树重跑得到不同产物：构建不可复现，发布门无法判断「Release 上的包是不是最新构建」');
    lines.push('  → 先看 artifacts/release/<version>/release-manifest.json 的 source_dirty 与 source_commit');
    return lines.join('\n') + `\nCHECK verify-${step.id} FAIL 两次构建不一致\n`;
  }
  lines.push(`CHECK verify-${step.id} PASS 重新装配+封装得到同一份字节 sha256=${after}`);
  lines.push('REPRO-OK');
  return lines.join('\n') + '\n';
}

function printList(config) {
  console.log(`验证层表（D-16 / RFC-38）：默认跑 ${DEFAULT_LAYERS.join(' → ')}，--all 跑全部`);
  console.log(`config=${config}（--config debug|release 换配置；它同时决定 dll 路径与 build 层的 -c）`);
  for (const layer of LAYERS) {
    const marks = layer.steps.map((step) => (step.marker ? `${step.id}→${step.marker}` : `${step.id}→exit-only`));
    console.log(`  ${layer.name.padEnd(9)} ${layer.title}`);
    console.log(`            步：${marks.join('  ')}`);
  }
  console.log('--keep-going：某一层红了以后继续跑下一层。它只跨层、不跨步：同层里红步之后' +
    '的步依赖前一步的产物（如 release→pack），跳过它们只会制造更难读的二次失败。');
  console.log('--allow-stale：跳过「dll 比源码旧」的拦截。只给调试用——它让判据对着旧二进制判绿，' +
    '正是 H1 那条假绿的形状；默认行为是拦下并报 VERIFY-FAIL stale-artifact。');
  console.log('NOT-PROVEN 段随层表生成，见每次运行的 artifacts/verify/<stamp>/README.md');
}

// H1：`--only contract` 这类单层跑不经过 build 层，于是可以对着**上一次构建**的 dll 判绿。
// 2026-10-25 的 010446 / 012450 / 013154 三次单跑都是绿的、都没 build 过——它们证明的是旧二进制。
// 判据只做一件事：dll 的 mtime 必须不旧于 core 源码树里最新的 .cs。这不是「跑得对不对」，
// 是「判的到底是哪份产物」。
function newestSourceMtime() {
  const roots = [
    path.join(REPO_ROOT, 'packages', 'core', 'PetDock.Core', 'src'),
    path.join(REPO_ROOT, 'packages', 'core', 'PetDock.Core', 'test'),
  ];
  let newest = { mtime: 0, file: null };
  const walk = (dir) => {
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (entry.name === 'bin' || entry.name === 'obj') continue;
        walk(full);
      } else if (entry.name.endsWith('.cs')) {
        const mtime = fs.statSync(full).mtimeMs;
        if (mtime > newest.mtime) newest = { mtime, file: full };
      }
    }
  };
  for (const root of roots) walk(root);
  return newest;
}

function staleArtifact(config) {
  const dll = coreDll(config);
  if (!fs.existsSync(dll)) return `dll 不存在：${path.relative(REPO_ROOT, dll)}（先跑 --only build 或 --config 指定的构建）`;
  const dllMtime = fs.statSync(dll).mtimeMs;
  const newest = newestSourceMtime();
  if (!newest.file) return null;
  if (newest.mtime <= dllMtime) return null;
  const lagS = ((newest.mtime - dllMtime) / 1000).toFixed(1);
  return `dll 比源码旧 ${lagS}s：dll=${path.relative(REPO_ROOT, dll)} 最新源=${path.relative(REPO_ROOT, newest.file)}`
    + '（这一层会对着旧二进制判绿；先跑 --only build，或加 --allow-stale 明确表示你要判旧产物）';
}

// 末行与证据 README 必须同源：H2 那次同一次 --keep-going 全跑，README 报 perf/perftest、
// 终端报 m7/m7，因为两处各自在自己的循环里取了「第一个」和「最后一次」。
function summaryLine(failures) {
  if (failures.length === 0) return 'VERIFY-OK';
  const first = failures[0];
  const marker = first.marker ? (first.markerOk ? 'ok' : 'missing') : 'exit-only';
  const base = `VERIFY-FAIL layer=${first.layer} step=${first.id} exit=${first.code} marker=${marker}`;
  return failures.length === 1 ? base : `${base} failures=${failures.length}`;
}

// 判据只做一件事：dll 必须不旧于源码。哪些层需要 dll 由它们自己声明 needsArtifact。
function needsArtifact(layer) {
  return layer.steps.some((step) => step.cmd === 'dotnet');
}

function writeEvidence(dir, runs, executed, failures) {
  const lines = [];
  lines.push(`# 验证证据 ${path.basename(dir)}`, '');
  lines.push(`命令：\`node tools/verify.mjs ${executed.label}\` on ${new Date().toISOString()}`, '');
  lines.push(`配置：\`${executed.config}\`（dll = \`${coreDll(executed.config)}\`）`, '');
  const failed = failures;
  lines.push(`结论：\`${summaryLine(failures)}\``, '');
  if (failures.length > 1) lines.push(`共 ${failures.length} 处失败（--keep-going 每层在第一个红步处停）`, '');
  lines.push('| 层 | 步 | exit | marker | 耗时 | 证据 |', '|---|---|---|---|---|---|');
  for (const run of runs) {
    const marker = run.marker ? `${run.markerOk ? 'ok' : 'MISSING'} (${run.marker})` : 'exit-only';
    lines.push(`| ${run.layer} | ${run.id} | ${run.code} | ${marker} | ${(run.durationMs / 1000).toFixed(1)}s | \`${path.basename(run.out)}\` |`);
  }
  lines.push('', '## 每一层证明什么、不证明什么', '');
  for (const layer of executed.layers) {
    lines.push(`### ${layer.name} — ${layer.title}`, '');
    lines.push(`- 证明：${layer.proves}`);
    lines.push(`- **不证明**：${layer.notProven}`, '');
  }
  lines.push('## 本次没有运行的层', '');
  const skipped = LAYERS.filter((layer) => !executed.layers.some((run) => run.name === layer.name));
  if (skipped.length === 0) lines.push('- 无（--all）');
  for (const layer of skipped) lines.push(`- ${layer.name}：${layer.title}`);
  lines.push('', '## 全局非证明项（跨所有层成立，见 原规格 98 §3.2）', '');
  for (const item of GLOBAL_NOT_PROVEN) lines.push(`- ${item}`);
  lines.push('');

  fs.writeFileSync(path.join(dir, 'README.md'), lines.join('\n'), 'utf8');
  fs.writeFileSync(
    path.join(dir, 'verify.json'),
    `${JSON.stringify({ at: new Date().toISOString(), config: executed.config, dll: coreDll(executed.config), layers: executed.layers.map((l) => l.name), runs: runs.map(({ out, err, ...rest }) => ({ ...rest, out: path.basename(out), err: path.basename(err) })) }, null, 2)}\n`,
    'utf8',
  );
}

function main() {
  const opts = parseArgs(process.argv.slice(2));
  const requested = String(opts.config).toLowerCase();
  if (!CONFIG_DIRS[requested]) fail(`没有叫 ${opts.config} 的配置（可选 ${Object.keys(CONFIG_DIRS).join(' | ')}）`);

  // 配置在这里定下来，层表随后按它生成：dll 路径、build 层的 -c、证据里的 config 字段同源。
  CONFIG = requested;
  LAYERS = layersFor(CONFIG);

  // 判据 §6.5：notProven 是必填字段 —— 证据目录里那句「不证明什么」由表生成，不能靠人记得写。
  for (const layer of LAYERS) {
    if (!layer.notProven || layer.notProven.trim() === '') fail(`层 ${layer.name} 缺 notProven`);
    if (!layer.proves || layer.proves.trim() === '') fail(`层 ${layer.name} 缺 proves`);
  }

  if (opts.list) {
    printList(CONFIG);
    return;
  }

  const layers = opts.layers
    ? selectLayers(opts.layers)
    : selectLayers(opts.all ? LAYERS.map((layer) => layer.name) : DEFAULT_LAYERS);
  const dir = path.join(REPO_ROOT, 'artifacts', 'verify', stamp());
  fs.mkdirSync(dir, { recursive: true });
  const executed = {
    layers,
    config: CONFIG,
    label: (opts.layers
      ? opts.layers.join(',')
      : opts.all ? LAYERS.map((layer) => layer.name).join(',') : '（默认三层）')
      // 证据里的「命令」要能照着复现：非默认配置就把它写进去（默认 debug 时一字不加，保持原有文案）。
      + (CONFIG === DEFAULT_CONFIG ? '' : ` --config ${CONFIG}`),
  };
  const runs = [];
  const failures = [];
  let stoppedEarly = false;

  // H1：跑之前先问「我要判的是哪份产物」。只拦需要 dll 的层；build 层本身就是在修这个问题。
  // 问的**时刻**是第一个需要 dll 的层开跑之前，而不是整趟开头：请求里含 build 时（默认的
  // static+build+contract 就是），build 会在 contract 之前把 dll 刷新；在开头就问，会让默认
  // 那条命令在源码动过一次以后永远红——而修法就在同一条命令里，被自己的前置闸挡在门外。
  // 反过来，build 真的红了（--keep-going）时，闸门仍在 contract 开跑前拦下来：旧产物不会因为
  // 「我请求过 build」就被放过。顺序也按 layers 走：`--only contract,build` 里 contract 在前，
  // 于是它照样在 build 之前被拦。
  const artifactLayers = layers.filter((layer) => layer.name !== 'build' && needsArtifact(layer));
  const staleCheckBefore = opts.allowStale ? null : (artifactLayers[0]?.name ?? null);

  function recordStaleArtifact(layerName, reason) {
    const id = 'stale-artifact';
    const out = path.join(dir, `${id}.out`);
    fs.writeFileSync(out, `CHECK verify-${id} FAIL ${reason}\n`, 'utf8');
    fs.writeFileSync(path.join(dir, `${id}.err`), '', 'utf8');
    const run = { id, layer: layerName, code: 1, marker: null, markerOk: true, durationMs: 0, out, err: path.join(dir, `${id}.err`) };
    runs.push(run);
    failures.push(run);
    console.log(`  RUN  ${id} ... FAIL exit=1 marker=exit-only 0.0s`);
    console.log(`  --- stale-artifact.out ---\n${reason}`);
  }

  for (const layer of layers) {
    if (layer.name === staleCheckBefore) {
      const reason = staleArtifact(CONFIG);
      if (reason) {
        recordStaleArtifact(layer.name, reason);
        stoppedEarly = true;
        break;
      }
    }
    console.log(`== ${layer.name} — ${layer.title}`);
    for (const step of layer.steps) {
      process.stdout.write(`  RUN  ${step.id} ... `);
      const run = runStep(step, dir);
      run.layer = layer.name;
      runs.push(run);
      const ok = run.code === 0 && run.markerOk;
      console.log(`${ok ? 'PASS' : 'FAIL'} exit=${run.code} marker=${run.marker ? (run.markerOk ? run.marker : 'MISSING') : 'exit-only'} ${(run.durationMs / 1000).toFixed(1)}s`);
      if (!ok) {
        const outTail = tail(run.out);
        const errTail = tail(run.err);
        if (outTail) console.log(`  --- ${path.basename(run.out)} 末 20 行 ---\n${outTail}`);
        if (errTail) console.log(`  --- ${path.basename(run.err)} 末 20 行 ---\n${errTail}`);
        failures.push(run);
        break;
      }
    }
    if (failures.length > 0 && !opts.keepGoing) {
      stoppedEarly = true;
      break;
    }
  }

  writeEvidence(dir, runs, executed, failures);
  const evidenceRel = path.relative(REPO_ROOT, dir).split(path.sep).join('/');

  if (failures.length > 0) {
    if (stoppedEarly) console.log(`NOT PROVEN：后面的层没有跑；证据在 ${evidenceRel}`);
    if (failures.length > 1) {
      for (const failure of failures) {
        const marker = failure.marker ? (failure.markerOk ? 'ok' : 'missing') : 'exit-only';
        console.log(`  FAIL ${failure.layer}/${failure.id} exit=${failure.code} marker=${marker}`);
      }
    }
    console.log(summaryLine(failures));
    process.exit(1);
  }

  const skipped = LAYERS.filter((layer) => !layers.some((picked) => picked.name === layer.name)).map((layer) => layer.name);
  if (skipped.length > 0) {
    console.log(`NOT RUN：${skipped.join(' ')}（--all 才跑；它们会建窗口或耗时数分钟，见 RFC-38 §3.1）`);
  }
  console.log(`VERIFY-OK layers=${layers.length}/${LAYERS.length} steps=${runs.length} evidence=${evidenceRel}`);
}

main();

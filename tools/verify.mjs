#!/usr/bin/env node
// tools/verify.mjs —— D-16 验证门禁（RFC-38 §3）：一条命令跑完所有层，每层具名、可单独复现。
//
// 为什么存在：D-16 之前「跑一遍」是 docs/98 §4 里的一段手抄命令清单，判绿靠人读末行
// UI-OK / HOST-OK；手抄一遍就有三种失败方式——漏层、把管道退出码当结论、事后说不清哪层没跑。
// 本文件把同样那批命令收进一张表：层 = 离真实运行时的距离，step = 一条命令 + 一个末行 marker。
// 每一步同时判「退出码 0」与「marker 真的出现在 stdout 里」：管道可以骗退出码，骗不了文件里的文本。
//
// 用法：
//   node tools/verify.mjs                  # 默认三层 static / build / contract
//   node tools/verify.mjs --all            # 全部七层（会建窗口、跑性能、打包）
//   node tools/verify.mjs --only window    # 只跑一层；--only contract:hosttest 只跑一层里的一步
//   node tools/verify.mjs --list           # 只打印层表
// 退出码：0 = 全过；1 = 有层失败（末行 VERIFY-FAIL 点名 layer/step）。

import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const NODE = process.execPath;
const DEBUG_DLL = 'packages/core/PetDock.Core/bin/Debug/net8.0-windows/PetDock.Core.dll';
const STEP_TIMEOUT_MS = 20 * 60 * 1000;

function node(script) {
  return { cmd: NODE, args: [script] };
}

function selftest(flag, extraArgs = [], marker = null) {
  return { cmd: 'dotnet', args: ['exec', DEBUG_DLL, flag, ...extraArgs], marker };
}

const LAYERS = [
  {
    name: 'static',
    title: 'L0 离线纯逻辑：七道静态门禁，逐条单独跑',
    proves: '源码依赖方向、内核黑名单与体积、文档引用与 ID 登记、插件清单、素材摆放与指纹、格式覆盖——七份互不相同的静态事实互相一致。',
    notProven: '没有任何一行代码被执行：编译失败、插件装载失败、窗口画不出来，这一层照样全绿。',
    steps: [
      { id: 'depscan', ...node('tools/depscan.mjs'), marker: 'DEPSCAN-PASS' },
      { id: 'lint-core', ...node('tools/lint-core.mjs'), marker: 'LINT-CORE-PASS' },
      { id: 'doclink', ...node('tools/doclink.mjs'), marker: 'DOCLINK-PASS' },
      { id: 'manifest', ...node('tools/validate-manifest.mjs'), marker: 'MANIFEST-PASS' },
      { id: 'assets', ...node('tools/asset-manifest.mjs'), marker: 'ASSET-MANIFEST-PASS' },
      { id: 'fingerprint', ...node('tools/asset-fingerprint.mjs'), marker: 'FINGERPRINT-PASS' },
      { id: 'coverage', ...node('tools/check-dyberpet-coverage.mjs'), marker: 'COVERAGE-PASS' },
    ],
  },
  {
    name: 'build',
    title: 'L1 构建：Debug 装配（后面三层都跑这份产物）',
    proves: '整个解决方案能编译，且下面 contract/window 层跑的就是这一份 dll。',
    notProven: 'Release 配置没编译过（那归 dist 层）；编译通过不代表任何一条运行时判据成立。',
    steps: [
      {
        id: 'dotnet-build',
        cmd: 'dotnet',
        args: ['build', 'PetDock.sln', '-c', 'Debug', '--nologo', '-v', 'm'],
        // marker: null —— `dotnet build` 的结论行随区域设置漂（Build succeeded / 已成功生成），只判退出码。
        marker: null,
        timeoutMs: 10 * 60 * 1000,
      },
    ],
  },
  {
    name: 'contract',
    title: 'L2 进程内契约：真实装配 + 真实插件，无桌面交互',
    proves: '插件宿主全生命周期、帧驱动与降级链、事件总线、动画循环各自的可判定判据。',
    notProven: '不证明窗口真的显示在桌面上、不证明鼠标消息能穿透——那归 window 层；也不证明功耗。',
    steps: [
      {
        id: 'hosttest',
        ...selftest('--hosttest', [], 'HOST-OK'),
        // 记录在案的约束：--hosttest 会在 %TEMP% 下造夹具，沙箱拒绝，必须把 TEMP/TMP 指到证据目录内。
        env: (dir) => {
          const tmp = path.join(dir, 'tmp-hosttest');
          fs.mkdirSync(tmp, { recursive: true });
          return { ...process.env, TEMP: tmp, TMP: tmp };
        },
      },
      { id: 'drivetest', ...selftest('--drivetest', [], 'DRIVE-OK') },
      { id: 'eventtest', ...selftest('--eventtest', [], 'EVENT-OK') },
      { id: 'animtest', ...selftest('--animtest', ['idle', 'walk', 'hover'], 'ANIM-OK') },
    ],
  },
  {
    name: 'window',
    title: 'L3 真实窗口：--uitest（149 条 CHECK，会闪桌面）',
    proves: '分层窗真的建起来、托盘与面板真的能操作、导入/设置/位置/命中图/单实例这些 UI 母体判据成立。',
    notProven: '不证明「好不好看」「手感如何」：宠物在桌面上长什么样、点击穿透的手感、两只以上宠物的排布，都没有判据，只有人眼（docs/98 §3.2）。',
    steps: [{ id: 'uitest', ...selftest('--uitest', ['--log', 'REPLACED'], 'UI-OK') }],
  },
  {
    name: 'perf',
    title: 'L4 真实功耗读数：--perftest / --residenttest',
    proves: 'D-14a~f 的绝对门限与相对回归，以及常驻形态（真实的 Program/UiHost 路径）的同一组读数。',
    notProven: '读数只覆盖本机这一种硬件与窗口管理；8 小时浸泡（--soak）不在这里，且从未跑过（RFC-36 §7.2）。',
    steps: [
      { id: 'perftest', ...selftest('--perftest', [], 'PERF-OK') },
      {
        id: 'residenttest',
        // 基线 tests/perf.resident.json 的 note 钉死了它自己的采集条件：`--seconds 10 --warmup 4000`，
        // 花名册 = artifacts/_probe/plugroot 的 5 个插件（经 PETDOCK_PLUGINS_ROOT 覆盖）。
        // 裸跑 `--residenttest` 用的是默认 20s + packages/plugins 的 8 目录花名册，等于拿另一份
        // 装配去比这份基线——2026-10-24 首次全跑就是这样把 ws 判红（基线 19.64MB，裸跑 23.7→30.6MB）；
        // 同条件重跑后 ws 回到 19.5~19.8MB，证明红的机制是条件不可比，不是内存变胖。
        // 花名册不存在时（另一台机器上 artifacts/ 是 gitignore 的）退回默认根，不硬造。
        ...selftest('--residenttest', ['--seconds', '10', '--warmup', '4000'], 'RESIDENT-PERF-OK'),
        env: () => {
          const roster = path.join(REPO_ROOT, 'artifacts', '_probe', 'plugroot');
          return fs.existsSync(roster)
            ? { ...process.env, PETDOCK_PLUGINS_ROOT: roster }
            : process.env;
        },
      },
    ],
  },
  {
    name: 'm7',
    title: 'L5 跨进程沙箱：tests/m7 的 BongoCat 判据',
    proves: 'tier-2 沙箱的 spawn / 行分隔 JSON-RPC / 会话五态在真实子进程里成立。',
    notProven: '不证明第三方插件能在真实用户机器上装载——判据用的是本仓自带的测试包。',
    steps: [
      {
        id: 'm7',
        cmd: 'dotnet',
        args: ['run', '--project', 'tests/m7/PetDock.BongoCat.Tests', '-c', 'Release'],
        marker: 'M7-OK',
        timeoutMs: 15 * 60 * 1000,
      },
    ],
  },
  {
    name: 'dist',
    title: 'L6 真实发布物：release → pack → 摘要 → 解压后运行',
    proves: '发布目录能装配、能封成一个单顶层目录的 zip、清单与条目集对账、解压后 --hosttest 仍全过；zip 的 sha256 随产物落盘。',
    notProven: '不证明签名（本机无 signtool）、不证明自动更新（无规格）、不证明另一台干净的 Windows 上能跑——只证明这台机器解压后能跑。',
    steps: [
      { id: 'release', ...node('tools/release.mjs'), marker: 'RELEASE-OK', timeoutMs: 20 * 60 * 1000 },
      { id: 'pack', ...node('tools/pack.mjs'), marker: 'PACK-OK' },
      { id: 'zip-sha256', kind: 'digest', marker: null },
      { id: 'portable-selftest', ...node('tools/portable-selftest.mjs'), marker: 'PORTABLE-OK' },
    ],
  },
];

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
  const opts = { layers: null, keepGoing: false, list: false };
  const wanted = [];
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === '--all') opts.layers = LAYERS.map((layer) => layer.name);
    else if (arg === '--keep-going') opts.keepGoing = true;
    else if (arg === '--list') opts.list = true;
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
function zipDigest() {
  const pkg = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8'));
  const rel = `artifacts/dist/PetDock-${pkg.version}-win-x64.zip`;
  const abs = path.join(REPO_ROOT, rel);
  if (!fs.existsSync(abs)) return { ok: false, detail: `CHECK verify-dist-zip-sha256 FAIL 缺 ${rel}` };
  const digest = createHash('sha256').update(fs.readFileSync(abs)).digest('hex');
  const sidecar = `${abs}.sha256`;
  fs.writeFileSync(sidecar, `${digest}  ${path.basename(abs)}\n`, 'utf8');
  const bytes = fs.statSync(abs).size;
  return {
    ok: true,
    detail: `CHECK verify-dist-zip-sha256 PASS sha256=${digest} bytes=${bytes} sidecar=${path.relative(REPO_ROOT, sidecar)}`,
  };
}

function printList() {
  console.log(`验证层表（D-16 / RFC-38）：默认跑 ${DEFAULT_LAYERS.join(' → ')}，--all 跑全部`);
  for (const layer of LAYERS) {
    const marks = layer.steps.map((step) => (step.marker ? `${step.id}→${step.marker}` : `${step.id}→exit-only`));
    console.log(`  ${layer.name.padEnd(9)} ${layer.title}`);
    console.log(`            步：${marks.join('  ')}`);
  }
  console.log('NOT-PROVEN 段随层表生成，见每次运行的 artifacts/verify/<stamp>/README.md');
}

function writeEvidence(dir, runs, executed) {
  const lines = [];
  lines.push(`# 验证证据 ${path.basename(dir)}`, '');
  lines.push(`命令：\`node tools/verify.mjs ${executed.label}\` on ${new Date().toISOString()}`, '');
  const failed = runs.filter((run) => run.code !== 0 || !run.markerOk);
  lines.push(`结论：${failed.length === 0 ? 'VERIFY-OK' : `VERIFY-FAIL ${failed[0].layer}/${failed[0].id}`}`, '');
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
  lines.push('', '## 全局非证明项（跨所有层成立，见 docs/98 §3.2）', '');
  for (const item of GLOBAL_NOT_PROVEN) lines.push(`- ${item}`);
  lines.push('');

  fs.writeFileSync(path.join(dir, 'README.md'), lines.join('\n'), 'utf8');
  fs.writeFileSync(
    path.join(dir, 'verify.json'),
    `${JSON.stringify({ at: new Date().toISOString(), layers: executed.layers.map((l) => l.name), runs: runs.map(({ out, err, ...rest }) => ({ ...rest, out: path.basename(out), err: path.basename(err) })) }, null, 2)}\n`,
    'utf8',
  );
}

function main() {
  const opts = parseArgs(process.argv.slice(2));

  // 判据 §6.5：notProven 是必填字段 —— 证据目录里那句「不证明什么」由表生成，不能靠人记得写。
  for (const layer of LAYERS) {
    if (!layer.notProven || layer.notProven.trim() === '') fail(`层 ${layer.name} 缺 notProven`);
    if (!layer.proves || layer.proves.trim() === '') fail(`层 ${layer.name} 缺 proves`);
  }

  if (opts.list) {
    printList();
    return;
  }

  const layers = selectLayers(opts.layers);
  const dir = path.join(REPO_ROOT, 'artifacts', 'verify', stamp());
  fs.mkdirSync(dir, { recursive: true });
  const executed = { layers, label: opts.layers ? opts.layers.join(',') : '（默认三层）' };
  const runs = [];
  let failedLayer = null;

  for (const layer of layers) {
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
        failedLayer = { layer: layer.name, step: step.id, run };
        break;
      }
    }
    if (failedLayer && !opts.keepGoing) break;
  }

  writeEvidence(dir, runs, executed);
  const evidenceRel = path.relative(REPO_ROOT, dir).split(path.sep).join('/');

  if (failedLayer) {
    const { run } = failedLayer;
    console.log(`NOT PROVEN：本层之后的层没有跑；证据在 ${evidenceRel}`);
    console.log(`VERIFY-FAIL layer=${failedLayer.layer} step=${failedLayer.step} exit=${run.code} marker=${run.marker ? (run.markerOk ? 'ok' : 'missing') : 'exit-only'}`);
    process.exit(1);
  }

  const skipped = LAYERS.filter((layer) => !layers.some((picked) => picked.name === layer.name)).map((layer) => layer.name);
  if (skipped.length > 0) {
    console.log(`NOT RUN：${skipped.join(' ')}（--all 才跑；它们会建窗口或耗时数分钟，见 RFC-38 §3.1）`);
  }
  console.log(`VERIFY-OK layers=${layers.length}/${LAYERS.length} steps=${runs.length} evidence=${evidenceRel}`);
}

main();

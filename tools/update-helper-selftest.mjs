#!/usr/bin/env node
// tools/update-helper-selftest.mjs —— 搬运工的判据（2026-10-31）。
//
// 为什么要单独一份：PetDock.Update.exe 不跑在宿主进程里，所以 --uitest 碰不到它 ——
// 而它正是「更新会不会把程序换坏」这条链上唯一真正动文件的那一段。
// 判据必须真的调一次它，而不是读它的源码说自己相信。
//
// 造一个假的安装目录（里面有 PetDock.Core.exe 与 PetDock.Update.exe）、
// 造一个真的 zip（走与发布包同一个形状：单顶层目录）、按宿主的方式写下 pending.json，
// 然后：
//   ① 造一个「已经退出」的 pid（用 --wait-pid 指一个刚结束的进程）→ 它应该照常搬运；
//   ② 断言文件内容真的换成了新版本、旧文件进了 backup/、result.txt 写着 ok <版本>；
//   ③ 再造一次坏包（sha256 对不上）→ 它必须**不换文件**，并且写下 fail sha256。
//
// 它不重启任何东西：重启那一步会开一个真进程，门禁里没有人去关它。
// 所以这一步的 notProven 是「宿主退出 → 搬运工 → 重启」的整条链（见 verify.mjs 的 dist 层）。
//
// 用法：node tools/update-helper-selftest.mjs
// 退出码：0 = 全过（HELPER-OK）；1 = 任一失败。

import { execFileSync, spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ARTIFACTS = path.join(REPO_ROOT, 'artifacts');
const WORK = path.join(ARTIFACTS, '_helper-selftest');

let failures = 0;
function check(id, ok, detail) {
  if (!ok) {
    failures += 1;
  }
  console.log(`hs-${id} ${ok ? 'PASS' : 'FAIL'}${detail ? ` ${detail}` : ''}`);
}

function sha256(file) {
  return createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

function zipViaPowerShell(zipPath, entries) {
  const lines = [
    '$ErrorActionPreference = "Stop"',
    'Add-Type -AssemblyName System.IO.Compression.FileSystem',
    `if (Test-Path -LiteralPath '${zipPath}') { Remove-Item -LiteralPath '${zipPath}' -Force }`,
    `$zip = [IO.Compression.ZipFile]::Open('${zipPath}', 'Create')`,
  ];
  for (const [name, text] of entries) {
    lines.push(`$entry = $zip.CreateEntry('${name.replace(/'/g, "''")}')`);
    lines.push('$stream = $entry.Open()');
    lines.push('$writer = New-Object System.IO.StreamWriter($stream)');
    lines.push(`$writer.Write('${text.replace(/'/g, "''")}')`);
    lines.push('$writer.Dispose(); $stream.Dispose()');
  }
  lines.push('$zip.Dispose()');
  execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', lines.join('; ')], {
    cwd: REPO_ROOT,
    stdio: 'inherit',
  });
}

function deadPid() {
  // 起一个立刻退出的进程，拿它的 pid —— 已退出 ⇒ 搬运工应当直接往下走。
  const child = spawnSync(process.execPath ?? 'node', ['-e', '0'], { stdio: 'ignore' });
  return child.pid;
}

function stageInstall(root) {
  const install = path.join(root, 'install');
  fs.mkdirSync(install, { recursive: true });
  fs.writeFileSync(path.join(install, 'PetDock.Core.exe'), 'old-core');
  fs.writeFileSync(path.join(install, 'keep.txt'), 'unchanged');
  return install;
}

function main() {
  const helperSource = path.join(ARTIFACTS, 'release');
  const version = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8')).version;
  const releaseDir = path.join(helperSource, version);
  const helper = path.join(releaseDir, 'PetDock.Update.exe');
  if (!fs.existsSync(helper)) {
    console.log(`HELPER-FAIL 没有发布物里的 PetDock.Update.exe（${path.relative(REPO_ROOT, helper)}）—— 先跑 npm run dist`);
    process.exit(1);
  }

  fs.rmSync(WORK, { recursive: true, force: true });
  fs.mkdirSync(WORK, { recursive: true });

  // 搬运工要被拷到暂存区再启动（它要覆盖安装目录，包括它自己那一份）——
  // 这里照宿主的做法做一遍，于是这一步同时证明「副本能在别处跑起来」。
  const data = path.join(WORK, 'data');
  const updateDir = path.join(data, '_update');
  const helperDir = path.join(updateDir, 'helper');
  fs.mkdirSync(helperDir, { recursive: true });
  for (const name of fs.readdirSync(releaseDir)) {
    if (name.startsWith('PetDock.Update.')) {
      fs.copyFileSync(path.join(releaseDir, name), path.join(helperDir, name));
    }
  }

  const install = stageInstall(WORK);
  for (const name of fs.readdirSync(helperDir)) {
    fs.copyFileSync(path.join(helperDir, name), path.join(install, name));
  }

  // ① 好包：换文件、备份、写结果
  const zip = path.join(updateDir, `PetDock-${version}-win-x64.zip`);
  // 顶层目录名与真实发布包一致（**不带版本号**，见 tools/pack.mjs）：搬运工只剪掉顶层一格，
  // 夹具要是用了别的形状，测的就不是它真正会收到的那种包。
  zipViaPowerShell(zip, [
    ['PetDock/PetDock.Core.exe', 'new-core'],
    ['PetDock/plugins/x.txt', 'new-plugin'],
  ]);
  fs.writeFileSync(
    path.join(updateDir, 'pending.json'),
    JSON.stringify({ schema: 1, version, zip, sha256: sha256(zip) }),
  );
  fs.writeFileSync(path.join(updateDir, 'restart.txt'), version);

  const pid = deadPid();
  let run = spawnSync(
    path.join(helperDir, 'PetDock.Update.exe'),
    ['--apply', data, '--install', install, '--wait-pid', String(pid)],
    { stdio: 'ignore', timeout: 120000 },
  );

  const coreAfter = fs.readFileSync(path.join(install, 'PetDock.Core.exe'), 'utf8');
  const pluginAfter = fs.existsSync(path.join(install, 'plugins', 'x.txt'));
  const untouched = fs.readFileSync(path.join(install, 'keep.txt'), 'utf8');
  const backedUp = fs.existsSync(path.join(updateDir, 'backup', 'PetDock.Core.exe'));
  const result = fs.existsSync(path.join(updateDir, 'result.txt'))
    ? fs.readFileSync(path.join(updateDir, 'result.txt'), 'utf8').trim()
    : '';

  check('1', run.status === 0, `exit=${run.status}`);
  check('2', coreAfter === 'new-core' && pluginAfter, `core=${coreAfter} plugin=${pluginAfter}`);
  // 新包里没有的文件**不许**被动过：搬运是「覆盖新版本有的那些」，不是「清空安装目录」。
  check('3', untouched === 'unchanged', `keep=${untouched}`);
  check('4', backedUp, `backup=${backedUp}`);
  check('5', result === `ok ${version}`, `result="${result}"`);

  // ② 坏包：sha256 对不上 ⇒ 一个文件都不许换
  const badZip = path.join(updateDir, `PetDock-${version}-win-x64.zip`);
  zipViaPowerShell(badZip, [['PetDock/PetDock.Core.exe', 'tampered']]);
  fs.writeFileSync(
    path.join(updateDir, 'pending.json'),
    JSON.stringify({ schema: 1, version, zip: badZip, sha256: 'f'.repeat(64) }),
  );
  fs.writeFileSync(path.join(updateDir, 'restart.txt'), version);

  run = spawnSync(
    path.join(helperDir, 'PetDock.Update.exe'),
    ['--apply', data, '--install', install, '--wait-pid', String(deadPid())],
    { stdio: 'ignore', timeout: 120000 },
  );
  const coreStillOld = fs.readFileSync(path.join(install, 'PetDock.Core.exe'), 'utf8');
  const badResult = fs.existsSync(path.join(updateDir, 'result.txt'))
    ? fs.readFileSync(path.join(updateDir, 'result.txt'), 'utf8').trim()
    : '';
  check('6', run.status === 1 && coreStillOld === 'new-core' && badResult === 'fail sha256',
    `exit=${run.status} core=${coreStillOld} result="${badResult}"`);

  // ④ 回滚（2026-10-31 审计）：上一趟覆盖到一半被打断，盘上是新旧混杂的树。
  //
  // 造现场：安装目录里 PetDock.Core.exe 已经是**新**的（说明覆盖动过它），
  // backup/ 里留着旧的那一份，applying.txt 记着这次覆盖动过哪些文件 ——
  // 而 plugins/x.txt 是新版本**新增**的，回滚必须把它删掉（否则「回到旧版本」是假的）。
  const rollbackData = path.join(WORK, 'rollback');
  const rbUpdate = path.join(rollbackData, '_update');
  const rbBackup = path.join(rbUpdate, 'backup');
  const rbInstall = path.join(WORK, 'rollback-install');
  const rbHelper = path.join(rbUpdate, 'helper');
  fs.mkdirSync(rbBackup, { recursive: true });
  fs.mkdirSync(rbInstall, { recursive: true });
  fs.mkdirSync(rbHelper, { recursive: true });
  for (const name of fs.readdirSync(releaseDir)) {
    if (name.startsWith('PetDock.Update.')) {
      fs.copyFileSync(path.join(releaseDir, name), path.join(rbHelper, name));
    }
  }
  fs.writeFileSync(path.join(rbInstall, 'PetDock.Core.exe'), 'half-new');
  fs.writeFileSync(path.join(rbInstall, 'keep.txt'), 'unchanged');
  fs.mkdirSync(path.join(rbInstall, 'plugins'), { recursive: true });
  fs.writeFileSync(path.join(rbInstall, 'plugins', 'x.txt'), 'new-plugin');
  fs.writeFileSync(path.join(rbBackup, 'PetDock.Core.exe'), 'old-core');
  fs.writeFileSync(
    path.join(rbUpdate, 'applying.txt'),
    ['version=9.9.9', 'file|PetDock.Core.exe', 'file|plugins/x.txt'].join('\n'),
  );

  run = spawnSync(
    path.join(rbHelper, 'PetDock.Update.exe'),
    ['--apply', rollbackData, '--install', rbInstall, '--wait-pid', String(deadPid()), '--rollback'],
    { stdio: 'ignore', timeout: 120000 },
  );
  const rbCore = fs.readFileSync(path.join(rbInstall, 'PetDock.Core.exe'), 'utf8');
  const rbAdded = fs.existsSync(path.join(rbInstall, 'plugins', 'x.txt'));
  const rbJournal = fs.existsSync(path.join(rbUpdate, 'applying.txt'));
  const rbResult = fs.existsSync(path.join(rbUpdate, 'result.txt'))
    ? fs.readFileSync(path.join(rbUpdate, 'result.txt'), 'utf8').trim()
    : '';
  check('8', run.status === 0 && rbCore === 'old-core' && !rbAdded,
    'exit=' + run.status + ' core=' + rbCore + ' kept-added=' + rbAdded);
  check('9', !rbJournal && rbResult.startsWith('rolled-back'),
    'journal=' + rbJournal + ' result="' + rbResult + '"');

  // ⑤ 没有备份可换（上一次其实还没开始覆盖）：回滚要说「没什么可做」而不是假装换了什么。
  const emptyData = path.join(WORK, 'rollback-empty');
  const emptyUpdate = path.join(emptyData, '_update');
  const emptyInstall = path.join(WORK, 'rollback-empty-install');
  fs.mkdirSync(emptyUpdate, { recursive: true });
  fs.mkdirSync(emptyInstall, { recursive: true });
  fs.writeFileSync(path.join(emptyUpdate, 'applying.txt'), 'version=9.9.9');
  fs.writeFileSync(path.join(emptyInstall, 'PetDock.Core.exe'), 'old-core');
  run = spawnSync(
    path.join(rbHelper, 'PetDock.Update.exe'),
    ['--apply', emptyData, '--install', emptyInstall, '--wait-pid', String(deadPid()), '--rollback'],
    { stdio: 'ignore', timeout: 120000 },
  );
  const emptyResult = fs.existsSync(path.join(emptyUpdate, 'result.txt'))
    ? fs.readFileSync(path.join(emptyUpdate, 'result.txt'), 'utf8').trim()
    : '';
  check('10', run.status === 0 && emptyResult === 'rolled-back 0', 'exit=' + run.status + ' result="' + emptyResult + '"');

  // ⑥ 两边真的在用同一个文件名：搬运工写 applying.txt，宿主读 applying.txt。
  // 两个程序集互不引用（搬运工不许依赖内核），所以这个名字只能各写一份 ——
  // 而「各写一份」正是会漂的那一类东西，用一条源码级判据把它钉住。
  const hostStage = fs.readFileSync(
    path.join(REPO_ROOT, 'packages', 'core', 'PetDock.Core', 'src', 'Host', 'UpdateStage.cs'),
    'utf8',
  );
  const helperApply = fs.readFileSync(
    path.join(REPO_ROOT, 'packages', 'core', 'PetDock.Update', 'Applier.Rollback.cs'),
    'utf8',
  );
  const hostName = /AppliedFile\s*=\s*"([^"]+)"/.exec(hostStage)?.[1];
  const helperName = /JournalName\s*=\s*"([^"]+)"/.exec(helperApply)?.[1];
  check('11', Boolean(hostName) && hostName === helperName, 'host=' + hostName + ' helper=' + helperName);

  // ③ 参数不合法：什么都不做，退出码 2
  const noArgs = spawnSync(path.join(helperDir, 'PetDock.Update.exe'), ['--apply', data], {
    stdio: 'ignore',
    timeout: 60000,
  });
  check('7', noArgs.status === 2, `exit=${noArgs.status}`);

  console.log(`STAT helper version=${version} files=${fs.readdirSync(install).length}`);
  fs.rmSync(WORK, { recursive: true, force: true });

  if (failures > 0) {
    console.log(`HELPER-FAIL checks=${failures}`);
    process.exit(1);
  }
  console.log('HELPER-OK');
}

main();

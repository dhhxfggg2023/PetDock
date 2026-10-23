#!/usr/bin/env node
// tools/portable-selftest.mjs —— RFC-25 §4.2：验证「一个文件夹自包含」这条承诺。
//
// 没有安装器了（RFC-25 §3.4），所以这里不装、不写注册表、不建快捷方式。它证明四件事：
//   pf-1 解压 → 单顶层目录，exe 在顶层
//   pf-2 在解压目录里跑 --hosttest → exit 0（发布物能跑）
//   pf-3 在解压目录里真启动一次 → 数据区落在**包内** data\，%LOCALAPPDATA%\PetDock 一个文件都不多
//   pf-4 全程没有出现 PetDock 的卸载注册表键（HKCU / HKLM 都查）
//
// 输出走文件描述符而不是管道：`--hosttest` 往 stderr 写审计行，管道会让它异常退出
// （已记录在案的坑：不要把 hosttest 的输出接到管道上）。
//
// 用法：node tools/portable-selftest.mjs
// 退出码：0 = 四条全过（PORTABLE-OK）；1 = 任一失败。

import { execFileSync, spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ARTIFACTS = path.join(REPO_ROOT, 'artifacts');
const UNSET_KEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\PetDock';
const UNSET_KEY_64 = 'HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\PetDock';

function fail(message) {
  console.log(`PORTABLE-FAIL ${message}`);
  process.exit(1);
}

function countFiles(dir, out = 0) {
  if (!fs.existsSync(dir)) {
    return out;
  }
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const abs = path.join(dir, entry.name);
    out = entry.isDirectory() ? countFiles(abs, out) : out + 1;
  }
  return out;
}

function keyExists(key) {
  return spawnSync('reg.exe', ['query', key], { stdio: 'ignore' }).status === 0;
}

function tailOf(file, lines) {
  try {
    const text = fs.readFileSync(file, 'utf8').trim().split(/\r?\n/);
    return text.filter((line) => line).slice(-lines).join(' | ');
  } catch {
    return '';
  }
}

async function main() {
  const version = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8')).version;
  const top = `PetDock-${version}`;
  const zipPath = path.join(ARTIFACTS, 'dist', `${top}-win-x64.zip`);
  if (!fs.existsSync(zipPath)) {
    fail(`没有可测的 zip ${path.relative(REPO_ROOT, zipPath)} —— 先跑 npm run dist`);
  }

  const workRoot = path.join(ARTIFACTS, '_portable');
  fs.rmSync(workRoot, { recursive: true, force: true });
  fs.mkdirSync(workRoot, { recursive: true });
  execFileSync('tar', ['-xf', zipPath, '-C', workRoot], { cwd: REPO_ROOT, stdio: 'inherit' });

  const unpacked = path.join(workRoot, top);
  const exe = path.join(unpacked, 'PetDock.Core.exe');
  const logOut = path.join(workRoot, 'hosttest.out');
  const logErr = path.join(workRoot, 'hosttest.err');

  let failures = 0;
  const check = (id, ok, detail) => {
    if (!ok) {
      failures += 1;
    }
    console.log(`pf-${id} ${ok ? 'PASS' : 'FAIL'}${detail ? ` ${detail}` : ''}`);
  };

  // pf-1 单顶层目录 + exe / 便携标记在顶层
  const tops = fs.readdirSync(workRoot).filter((name) => fs.statSync(path.join(workRoot, name)).isDirectory());
  const marker = path.join(unpacked, 'petdock.portable');
  check('1', tops.length === 1 && tops[0] === top && fs.existsSync(exe) && fs.existsSync(marker),
    `tops=${tops.join(',')} exe=${fs.existsSync(exe)} marker=${fs.existsSync(marker)}`);

  // pf-2 发布物能跑：--hosttest 全过（输出走文件描述符，不过管道）
  // TEMP 指到工作目录内：本机沙箱拒绝在 %TEMP% 下建目录，而 hosttest 会在 %TEMP% 造夹具，
  // 撞上去就是 UnauthorizedAccessException + 0xE0434352（不是发布物的问题，见 RFC-25 §7）。
  const tmpDir = path.join(workRoot, 'tmp');
  fs.mkdirSync(tmpDir, { recursive: true });
  const fdOut = fs.openSync(logOut, 'w');
  const fdErr = fs.openSync(logErr, 'w');
  const hosttest = spawnSync(exe, ['--hosttest'], {
    cwd: unpacked,
    stdio: ['ignore', fdOut, fdErr],
    timeout: 240000,
    env: { ...process.env, TEMP: tmpDir, TMP: tmpDir },
  });
  fs.closeSync(fdOut);
  fs.closeSync(fdErr);
  check('2', hosttest.status === 0, `exit=${hosttest.status} tail="${tailOf(logOut, 2)}"`);

  // pf-3 真启动一次：数据区落在包内 data\，%LOCALAPPDATA%\PetDock 一个文件都不多
  const localData = path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), 'AppData', 'Local'), 'PetDock');
  const beforeLocal = countFiles(localData);
  const runOut = fs.openSync(path.join(workRoot, 'launch.out'), 'w');
  const runErr = fs.openSync(path.join(workRoot, 'launch.err'), 'w');
  const child = spawn(exe, [], { cwd: unpacked, stdio: ['ignore', runOut, runErr] });
  await new Promise((resolve) => setTimeout(resolve, 9000));
  const alive = child.exitCode === null;
  if (alive) {
    child.kill();
    await new Promise((resolve) => setTimeout(resolve, 1500));
  }
  fs.closeSync(runOut);
  fs.closeSync(runErr);
  const dataDir = path.join(unpacked, 'data');
  const dataFiles = countFiles(dataDir);
  const afterLocal = countFiles(localData);
  check('3', dataFiles > 0 && afterLocal === beforeLocal,
    `alive=${alive} data-files=${dataFiles} localappdata-delta=${afterLocal - beforeLocal}${dataFiles === 0 ? '' : ` names=${fs.readdirSync(dataDir).join(',')}`}`);

  // pf-4 没有写卸载注册表键（HKCU / HKLM 都没有）
  const hkcu = keyExists(UNSET_KEY);
  const hklm = keyExists(UNSET_KEY_64);
  check('4', !hkcu && !hklm, `hkcu=${hkcu} hklm=${hklm}`);

  console.log(`STAT portable version=${version} data-files=${dataFiles} localappdata-files=${afterLocal}`);
  fs.rmSync(workRoot, { recursive: true, force: true });

  if (failures > 0) {
    console.log(`PORTABLE-FAIL checks=${failures}`);
    process.exit(1);
  }
  console.log('PORTABLE-OK');
}

main();

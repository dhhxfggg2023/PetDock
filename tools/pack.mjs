#!/usr/bin/env node
// tools/pack.mjs —— RFC-25 §3.1：把装配好的发布目录封成**一个可下载的文件**。
//
// 与 release.mjs 的分工：release 负责「仓库 → 能跑的目录」，pack 负责「能跑的目录 → 一个 zip」。
// 分成两个脚本是因为它们可以分别失败：封装坏了不必重新 publish，装配坏了也不必重打包。
//
// 为什么用 tar.exe 而不是 Compress-Archive / System.IO.Compression.ZipFile（RFC-25 §2 实测）：
//   tar -a -c                 → 条目 `./x`、正斜杠、含隐藏文件   ← 采用
//   ZipFile.CreateFromDirectory → 条目 `sub\b.txt`（反斜杠，非 Windows 解压器会当成文件名）
//   Compress-Archive -Path d\* → 反斜杠，且**漏**隐藏文件
//
// 用法：
//   node tools/pack.mjs                # 输入 artifacts/release/<version>/，输出 artifacts/dist/
//   node tools/pack.mjs --out FILE     # 指定 zip 路径
//   node tools/pack.mjs --keep-staging # 保留 artifacts/_pack/ 便于人工核对
//
// 退出码：0 = zip 已生成且三条判据全过；1 = 任一判据失败（此时删除半成品 zip）。

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

const KEEP_STAGING = process.argv.includes('--keep-staging');
const OUT_OVERRIDE = argValue('--out');

function readVersion() {
  const pkg = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8'));
  if (!pkg.version || !/^\d+\.\d+\.\d+$/.test(pkg.version)) {
    console.log(`PACK-FAIL package.json 缺少合法 version（当前 ${pkg.version ?? '(none)'}）`);
    process.exit(1);
  }
  return pkg.version;
}

function fail(message) {
  console.log(`PACK-FAIL ${message}`);
  process.exit(1);
}

function main() {
  const version = readVersion();
  const top = `PetDock-${version}`;
  const name = `${top}-win-x64.zip`;
  const releaseDir = path.join(ARTIFACTS, 'release', version);
  const zipPath = OUT_OVERRIDE ? path.resolve(OUT_OVERRIDE) : path.join(ARTIFACTS, 'dist', name);
  const stagingRoot = path.join(ARTIFACTS, '_pack');
  const stagingDir = path.join(stagingRoot, top);

  if (!fs.existsSync(path.join(releaseDir, 'PetDock.Core.exe'))) {
    fail(`没有可封装的发布目录 ${path.relative(REPO_ROOT, releaseDir)} —— 先跑 node tools/release.mjs`);
  }
  const manifestPath = path.join(releaseDir, 'release-manifest.json');
  if (!fs.existsSync(manifestPath)) {
    fail(`发布目录缺 release-manifest.json（发布物清单是封装判据的输入）`);
  }
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  if (manifest.version !== version) {
    fail(`清单版本 ${manifest.version} 与 package.json 的 ${version} 不一致`);
  }

  // 单顶层目录：调用方给的是一个目录名，zip 里的条目就以它开头。
  fs.rmSync(stagingDir, { recursive: true, force: true });
  fs.mkdirSync(stagingRoot, { recursive: true });
  fs.cpSync(releaseDir, stagingDir, { recursive: true });
  const stagedFiles = countFiles(stagingDir);

  fs.mkdirSync(path.dirname(zipPath), { recursive: true });
  fs.rmSync(zipPath, { force: true });
  console.log(`RUN tar -a -c -f ${path.relative(REPO_ROOT, zipPath)} -C ${path.relative(REPO_ROOT, stagingRoot)} ${top}`);
  let listing;
  try {
    execFileSync('tar', ['-a', '-c', '-f', zipPath, '-C', stagingRoot, top], { cwd: REPO_ROOT, stdio: 'inherit' });
    listing = execFileSync('tar', ['-tf', zipPath], { cwd: REPO_ROOT, encoding: 'utf8' });
  } catch (error) {
    fail(`tar 失败：${error.message}`);
  }

  const entries = listing.split(/\r?\n/).filter((line) => line.trim() !== '');
  let failures = 0;
  const check = (name, ok, detail) => {
    if (!ok) {
      failures += 1;
    }
    console.log(`ix-pack-${name} ${ok ? 'PASS' : 'FAIL'}${detail ? ` ${detail}` : ''}`);
  };

  // ix-pack-1：单顶层目录 + 全正斜杠
  const badPrefix = entries.filter((e) => !e.startsWith(`${top}/`));
  const badSeparator = entries.filter((e) => e.includes('\\'));
  check('1', badPrefix.length === 0 && badSeparator.length === 0,
    `entries=${entries.length} bad-prefix=${badPrefix.length} backslash=${badSeparator.length}`);

  // ix-pack-2：清单里每个文件都在 zip 里有条目（一个不少）
  const entrySet = new Set(entries);
  const missing = manifest.files
    .map((file) => `${top}/${file.path}`)
    .filter((entry) => !entrySet.has(entry));
  check('2', missing.length === 0, `manifest=${manifest.files.length} missing=${missing.length}${missing.length ? ` first=${missing[0]}` : ''}`);
  for (const entry of missing.slice(0, 5)) {
    console.log(`  MISSING ${entry}`);
  }

  // ix-pack-3：自包含件随包走（用户手里只有这个 zip，标记与说明不能留在仓库里）
  const selfContained = ['petdock.portable', 'INSTALL.md'];
  const absent = selfContained.filter((file) => !entrySet.has(`${top}/${file}`));
  check('3', absent.length === 0, `absent=${absent.join(',') || 'none'}`);

  const bytes = fs.statSync(zipPath).size;
  console.log(`STAT pack version=${version} staged=${stagedFiles} entries=${entries.length} bytes=${bytes} zip=${path.relative(REPO_ROOT, zipPath)}`);

  if (!KEEP_STAGING) {
    fs.rmSync(stagingDir, { recursive: true, force: true });
  }
  if (failures > 0) {
    fs.rmSync(zipPath, { force: true });
    // 判据没过就把旁车一起删掉：留着它等于给一个坏产物发「已校验」的标签。
    fs.rmSync(zipPath + '.sha256', { force: true });
    console.log(`PACK-FAIL checks=${failures}`);
    process.exit(1);
  }

  // 旁车必须**和 zip 同一次生成**（2026-10-30 抓到的真缺陷）：原先只有 `verify --all` 的
  // digest 步骤写 .sha256，于是 `npm run dist` 重建 zip 之后，旁车还停在旧内容上，
   // 两个文件对不上而没人发现 —— 下游拿它当「这个产物被验证过」的证据就会验错东西。
   // 现在封装成功即写旁车；verify 的 digest 步骤改为**核对**一致性，不再覆盖。
  const digest = createHash('sha256').update(fs.readFileSync(zipPath)).digest('hex');
  const sidecar = `${zipPath}.sha256`;
  fs.writeFileSync(sidecar, `${digest}  ${path.basename(zipPath)}\n`, 'utf8');
  console.log(`STAT pack-sha256 sha256=${digest} sidecar=${path.relative(REPO_ROOT, sidecar)}`);

  // 发布物不带「来自 Internet」的标记（2026-10-30：用户报「打开 exe 有安全警告」）。
  // 那个对话框是 Windows 附件管理器的：它靠 NTFS 备用数据流 Zone.Identifier 判定「这个文件
  // 是从网上来的」。解压时资源管理器会把这个标记**复制给解出来的每一个文件**，于是用户双击
  // 包里的 exe 就会看到一次安全警告 —— 触发源在 zip 上，不在 exe 上。
  // 实测：本仓的产物本来就没有这个流（这里会打 zone=absent），所以这一步是**保证**而不是修复：
  // 谁把下载来的 zip 放进这个目录再重新打包，标记都会被清掉，并且有一行读数可查。
  const zone = `${zipPath}:Zone.Identifier`;
  let zoneState = 'absent';
  try {
    fs.readFileSync(zone);
    fs.rmSync(zone);
    zoneState = 'cleared';
  } catch {
    zoneState = 'absent';
  }
  console.log(`STAT pack-zone zone=${zoneState} zip=${path.basename(zipPath)}`);
  console.log('PACK-OK');
}

function countFiles(dir, out = 0) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const abs = path.join(dir, entry.name);
    out = entry.isDirectory() ? countFiles(abs, out) : out + 1;
  }
  return out;
}

main();

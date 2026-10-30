#!/usr/bin/env node
// tools/pack.mjs —— RFC-25 §3.1：把装配好的发布目录封成**一个可下载的文件**。
//
// 与 release.mjs 的分工：release 负责「仓库 → 能跑的目录」，pack 负责「能跑的目录 → 一个 zip」。
// 分成两个脚本是因为它们可以分别失败：封装坏了不必重新 publish，装配坏了也不必重打包。
//
// 为什么**不再**用 tar.exe（2026-10-30 改）：tar 会把每个条目的文件时间戳写进 zip，
// 于是同一个提交、内容逐字节相同的两次构建产出两个不同 sha256 的 zip ——
// 「Release 上的包 == 本地最新构建」这条判据因此永远说不清是产物变了还是时间戳变了。
// 现在用 tools/lib/zip.mjs 自己写：不写时间戳（固定 1980-01-01）、不写权限位、
// 条目按路径排序、deflate 固定级别 9。同样的输入 → 同样的字节。
//
// 早年用 tar 的理由（避免 ZipFile/Compress-Archive 的反斜杠与漏隐藏文件）依然成立，
// 那些问题在自写实现里根本不存在。
//
// 用法：
//   node tools/pack.mjs                # 输入 artifacts/release/<version>/，输出 artifacts/dist/
//   node tools/pack.mjs --out FILE     # 指定 zip 路径
//   node tools/pack.mjs --keep-staging # 保留 artifacts/_pack/ 便于人工核对
//
// 退出码：0 = zip 已生成且四条判据全过；1 = 任一判据失败（此时删除半成品 zip）。

import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { inflateRawSync } from 'node:zlib';
import { crc32, zipDirectory } from './lib/zip.mjs';

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
  console.log(`RUN zip ${path.relative(REPO_ROOT, zipPath)} <- ${top}/（确定性写入）`);
  // 顶层目录名进 zip：stagingDir 里就是 top/ 这一层，prefix 传空串即可（collectFiles 会带上目录名）。
  const zipped = zipDirectory(stagingRoot, zipPath);
  console.log(`STAT zip entries=${zipped.files} bytes=${zipped.bytes}`);

  // 校验条目（不再调 tar 列表：直接从写出去的文件读不了，改用自写实现的清单）
  const entries = listZipEntries(zipPath);
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

  // ix-pack-4：中央目录与本地头必须一致，且 CRC 与解压结果对得上。
  // 2026-10-30 抓到真缺陷：中央目录把压缩方法写成 0（stored）而数据实际是 deflate。
  // 两个头说法不同的 zip 就是坏包 —— tar 按本地头解压（自检全绿），
  // 资源管理器与 .NET 按中央目录解压（用户拿到的每个文件都是乱码）。
  const structure = inspectZipStructure(zipPath);
  check('4', structure.problems.length === 0,
    `entries=${structure.entries} crc-ok=${structure.crcOk} problems=${structure.problems.length}`);
  for (const problem of structure.problems.slice(0, 5)) {
    console.log(`  BAD ${problem}`);
  }

  const bytes = fs.statSync(zipPath).size;
  console.log(`STAT pack version=${version} staged=${stagedFiles} entries=${entries.length} bytes=${bytes} zip=${path.relative(REPO_ROOT, zipPath)}`);

  if (!KEEP_STAGING) {
    fs.rmSync(stagingDir, { recursive: true, force: true });
    // 目录壳也算残留：artifacts/ 里只该有有名字的产物（release/dist/verify/uitest-shots/acl-recovery）。
    // 只删空目录 —— 里面还有别的版本目录（--keep-staging 留下的）就保持不动。
    try {
      fs.rmdirSync(stagingRoot);
    } catch {
      // 不是空的，留着。
    }
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

/**
 * 逐条核对中央目录与本地头，并把数据解出来验 CRC。
 * 两个头说法不一致的 zip 是坏包：不同解压器会给出不同结果，用户拿到的那份可能是乱码。
 * 判据不看「哪种解压器能读」，只看「两种说法是否一致」。
 */
function inspectZipStructure(zipPath) {
  const buf = fs.readFileSync(zipPath);
  let end = -1;
  for (let i = buf.length - 22; i >= 0; i -= 1) {
    if (buf.readUInt32LE(i) === 0x06054b50) {
      end = i;
      break;
    }
  }
  if (end < 0) {
    fail('zip 里找不到中央目录结束记录');
  }
  const count = buf.readUInt16LE(end + 10);
  let offset = buf.readUInt32LE(end + 16);
  const problems = [];
  let crcOk = 0;
  for (let n = 0; n < count; n += 1) {
    const methodC = buf.readUInt16LE(offset + 10);
    const crcC = buf.readUInt32LE(offset + 16);
    const compC = buf.readUInt32LE(offset + 20);
    const uncompC = buf.readUInt32LE(offset + 24);
    const nameLen = buf.readUInt16LE(offset + 28);
    const extraLen = buf.readUInt16LE(offset + 30);
    const commentLen = buf.readUInt16LE(offset + 32);
    const localOff = buf.readUInt32LE(offset + 42);
    const name = buf.toString('utf8', offset + 46, offset + 46 + nameLen);
    offset += 46 + nameLen + extraLen + commentLen;

    const methodL = buf.readUInt16LE(localOff + 8);
    const crcL = buf.readUInt32LE(localOff + 14);
    const compL = buf.readUInt32LE(localOff + 18);
    const uncompL = buf.readUInt32LE(localOff + 22);
    const nameLenL = buf.readUInt16LE(localOff + 26);
    const extraLenL = buf.readUInt16LE(localOff + 28);
    const nameL = buf.toString('utf8', localOff + 30, localOff + 30 + nameLenL);

    if (nameL !== name) {
      problems.push(name + ' 本地头里的名字是 ' + nameL);
      continue;
    }
    if (methodC !== methodL) {
      problems.push(name + ' 压缩方法 中央=' + methodC + ' 本地=' + methodL + '（解压器会给出不同结果）');
      continue;
    }
    if (crcC !== crcL || compC !== compL || uncompC !== uncompL) {
      problems.push(name + ' 大小/CRC 不一致 中央=(' + uncompC + ',' + compC + ',' + crcC.toString(16) + ') 本地=(' + uncompL + ',' + compL + ',' + crcL.toString(16) + ')');
      continue;
    }
    const start = localOff + 30 + nameLenL + extraLenL;
    const packed = buf.subarray(start, start + compL);
    let plain;
    try {
      plain = methodC === 8 ? inflateRawSync(packed) : packed;
    } catch (error) {
      problems.push(name + ' 解压失败：' + error.message);
      continue;
    }
    if (plain.length !== uncompC) {
      problems.push(name + ' 解压后 ' + plain.length + ' 字节，头里写 ' + uncompC);
      continue;
    }
    if (crc32(plain) !== crcC) {
      problems.push(name + ' CRC 与解压结果对不上');
      continue;
    }
    crcOk += 1;
  }
  return { entries: count, problems, crcOk };
}

/** 从 zip 的中央目录读出条目名（自写实现配套的只读解析，不引第三方库）。 */
function listZipEntries(zipPath) {
  const buf = fs.readFileSync(zipPath);
  // 从尾部找中央目录结束记录
  let end = -1;
  for (let i = buf.length - 22; i >= 0; i -= 1) {
    if (buf.readUInt32LE(i) === 0x06054b50) {
      end = i;
      break;
    }
  }
  if (end < 0) {
    fail('zip 里找不到中央目录结束记录');
  }
  const count = buf.readUInt16LE(end + 10);
  let offset = buf.readUInt32LE(end + 16);
  const names = [];
  for (let n = 0; n < count; n += 1) {
    if (buf.readUInt32LE(offset) !== 0x02014b50) {
      fail('zip 中央目录条目损坏（偏移 ' + offset + '）');
    }
    const nameLen = buf.readUInt16LE(offset + 28);
    const extraLen = buf.readUInt16LE(offset + 30);
    const commentLen = buf.readUInt16LE(offset + 32);
    names.push(buf.toString('utf8', offset + 46, offset + 46 + nameLen));
    offset += 46 + nameLen + extraLen + commentLen;
  }
  return names;
}

function countFiles(dir, out = 0) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const abs = path.join(dir, entry.name);
    out = entry.isDirectory() ? countFiles(abs, out) : out + 1;
  }
  return out;
}

main();

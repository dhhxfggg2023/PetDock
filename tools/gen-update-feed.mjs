#!/usr/bin/env node
// tools/gen-update-feed.mjs —— 生成程序自身的更新源 update.json（2026-10-31）。
//
// 面板里的「检查更新」只认一份写死地址的更新源（PetDock.Core/src/Host/UpdateService.cs 的 Endpoint）。
//
// 这份更新源里**只有构建之前就确定的东西**：版本号与下载地址（都来自 package.json）。
// 刻意不写 sha256 / bytes —— 它们是「从本次构建产物量出来的」，一旦写进来就会形成
// 一个无解的环（2026-10-31 实测）：zip 里写着 source_commit=C，update.json 写着 sha256(zip_at_C)，
// 提交 update.json 让 HEAD 变成 C'，在 C' 重新构建 zip 里就是 C'，指纹又变了。
// 同一类错误上一轮在 plugins/index.json 的 generated 字段上已经付过一次代价。
//
// 所以完整性交给 Release 上同一个 zip 旁边的 **.sha256 旁车文件**（不进 git，随包上传）：
// 程序下载 zip 的同时拉旁车并比对。旁车由 tools/pack.mjs 产出。
//
// 与 plugins/index.json 同一条纪律：**不写生成时间**。
//
// 用法：
//   node tools/gen-update-feed.mjs            # 按 package.json 的版本写 update.json
//   node tools/gen-update-feed.mjs --check    # 只校验现有 update.json（门禁用）
//   node tools/gen-update-feed.mjs --out <p>  # 换输出路径
//
// 判据（失败即 exit 1）：
//   ix-update-1  version 与 package.json 一致
//   ix-update-2  不含从产物量出来的字段（sha256 / bytes）—— 反过来判，防的就是上面那个环
//   ix-update-3  url 在 UpdateService.AllowedDownloadPrefix 之下
//   ix-update-4  version 形状是 x.y.z
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const FEED_REL = 'update.json';
const DOWNLOAD_PREFIX = 'https://github.com/dhhxfggg2023/PetDock/releases/download/';

const args = process.argv.slice(2);
function arg(name) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : null;
}

const CHECK_ONLY = args.includes('--check');
const VERSION = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8')).version;
const outPath = arg('--out') ?? path.join(REPO_ROOT, FEED_REL);

function fail(message) {
  console.log('UPDATE-FAIL ' + message);
  process.exit(1);
}

function isVersion(text) {
  return typeof text === 'string' && /^\d+\.\d+\.\d+$/.test(text);
}

function expectedUrl() {
  return DOWNLOAD_PREFIX + 'v' + VERSION + '/PetDock-' + VERSION + '-win-x64.zip';
}

function verify(feed, label) {
  let bad = 0;
  const check = (name, ok, detail) => {
    if (!ok) {
      bad += 1;
    }
    console.log('ix-update-' + name + ' ' + (ok ? 'PASS' : 'FAIL') + (detail ? ' ' + detail : ''));
  };

  check('1', feed.version === VERSION, label + 'feed=' + feed.version + ' package=' + VERSION);
  check('2', feed.sha256 === undefined && feed.bytes === undefined,
    'has-sha=' + (feed.sha256 !== undefined) + ' has-bytes=' + (feed.bytes !== undefined));
  check('3', typeof feed.url === 'string' && feed.url.startsWith(DOWNLOAD_PREFIX), 'url=' + feed.url);
  check('4', isVersion(feed.version), 'shape version=' + feed.version);
  return bad;
}

function main() {
  const url = expectedUrl();

  if (CHECK_ONLY) {
    if (!fs.existsSync(outPath)) {
      fail('更新源不存在：' + path.relative(REPO_ROOT, outPath));
    }
    const feed = JSON.parse(fs.readFileSync(outPath, 'utf8'));
    const bad = verify(feed, '');
    console.log('STAT update-check version=' + feed.version + ' bad=' + bad);
    if (bad > 0) {
      fail('更新源与 package.json 不一致（' + bad + ' 处）');
    }
    console.log('UPDATE-CHECK-OK');
    return;
  }

  const feed = {
    schema: 1,
    version: VERSION,
    url,
    notes: '版本 ' + VERSION + '。解压后双击 PetDock.Core.exe；数据与宠物都在程序旁的 data\\ 里。',
  };

  const bad = verify(feed, '');
  if (bad > 0) {
    fail('checks=' + bad);
  }

  fs.writeFileSync(outPath, JSON.stringify(feed, null, 2) + '\n', 'utf8');
  console.log('STAT update version=' + feed.version + ' url=' + feed.url
    + ' out=' + path.relative(REPO_ROOT, outPath));
  console.log('UPDATE-OK');
}

main();

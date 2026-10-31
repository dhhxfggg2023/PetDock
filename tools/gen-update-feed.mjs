#!/usr/bin/env node
// tools/gen-update-feed.mjs —— 生成程序自身的更新源 update.json（2026-10-31）。
//
// 面板里的「检查更新」只认一份写死地址的更新源（PetDock.Core/src/Host/UpdateService.cs 的 Endpoint）。
// 它必须与发布物**同一次构建**产出，所以由本脚本从真实 zip 算指纹，而不是手写：
// 手写的 sha256 一定会在某次发布后与包不一致，而那条错误恰好会伪装成「更新装不上」。
//
// 与 plugins/index.json 同一条纪律：**不写生成时间**。理由见 gen-market-index.mjs ——
// wall clock 会弄脏 git 跟踪文件、让下一次构建变成「工作树不干净」；来源提交时间则是自我指涉。
//
// 用法：
//   node tools/gen-update-feed.mjs             # 从 artifacts/dist 的 zip 写 update.json
//   node tools/gen-update-feed.mjs --check     # 只校验现有 update.json 与产物一致（门禁用）
//   node tools/gen-update-feed.mjs --out <p>    # 换输出路径
//
// 判据（失败即 exit 1）：
//   ix-update-1  update.json 的 version 与 package.json 一致
//   ix-update-2  sha256 / bytes 与真实 zip 实测一致（不一致 = 这份更新源是假的）
//   ix-update-3  url 在 UpdateService.AllowedDownloadPrefix 之下
//   ix-update-4  sha256 形状合法（64 位十六进制）、version 是 x.y.z
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ARTIFACTS = path.join(REPO_ROOT, 'artifacts');
const FEED_REL = 'update.json';
const DOWNLOAD_PREFIX = 'https://github.com/dhhxfggg2023/PetDock/releases/download/';

const args = process.argv.slice(2);
function arg(name) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : null;
}

const CHECK_ONLY = args.includes('--check');
const VERSION = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8')).version;
const zipPath = arg('--zip') ?? path.join(ARTIFACTS, 'dist', 'PetDock-' + VERSION + '-win-x64.zip');
const outPath = arg('--out') ?? path.join(REPO_ROOT, FEED_REL);

function fail(message) {
  console.log('UPDATE-FAIL ' + message);
  process.exit(1);
}

function measure(file) {
  const bytes = fs.readFileSync(file);
  return { sha256: createHash('sha256').update(bytes).digest('hex'), bytes: bytes.length };
}

function isVersion(text) {
  return typeof text === 'string' && /^\d+\.\d+\.\d+$/.test(text);
}

function main() {
  if (!fs.existsSync(zipPath)) {
    fail('没有可用的发行包 ' + path.relative(REPO_ROOT, zipPath) + ' —— 先跑 npm run dist');
  }

  const actual = measure(zipPath);
  const expectedUrl = DOWNLOAD_PREFIX + 'v' + VERSION + '/PetDock-' + VERSION + '-win-x64.zip';

  if (CHECK_ONLY) {
    if (!fs.existsSync(outPath)) {
      fail('更新源不存在：' + path.relative(REPO_ROOT, outPath));
    }
    const feed = JSON.parse(fs.readFileSync(outPath, 'utf8'));
    let bad = 0;
    const check = (name, ok, detail) => {
      if (!ok) {
        bad += 1;
      }
      console.log('ix-update-' + name + ' ' + (ok ? 'PASS' : 'FAIL') + (detail ? ' ' + detail : ''));
    };

    check('1', feed.version === VERSION, 'feed=' + feed.version + ' package=' + VERSION);
    check('2', feed.sha256 === actual.sha256 && feed.bytes === actual.bytes,
      'feed-sha=' + String(feed.sha256).slice(0, 12) + ' actual=' + actual.sha256.slice(0, 12)
      + ' feed-bytes=' + feed.bytes + ' actual=' + actual.bytes);
    check('3', typeof feed.url === 'string' && feed.url.startsWith(DOWNLOAD_PREFIX), 'url=' + feed.url);
    check('4', /^[0-9a-f]{64}$/.test(String(feed.sha256)) && isVersion(feed.version), 'shape');

    console.log('STAT update-check version=' + feed.version + ' bad=' + bad);
    if (bad > 0) {
      fail('更新源与产物不一致（' + bad + ' 处）');
    }
    console.log('UPDATE-CHECK-OK');
    return;
  }

  const feed = {
    schema: 1,
    version: VERSION,
    url: expectedUrl,
    sha256: actual.sha256,
    bytes: actual.bytes,
    notes: '版本 ' + VERSION + '。解压后双击 PetDock.Core.exe；数据与宠物都在程序旁的 data\\ 里。',
  };

  const badShape = !/^[0-9a-f]{64}$/.test(feed.sha256) || !isVersion(feed.version);
  console.log('ix-update-4 ' + (badShape ? 'FAIL' : 'PASS') + ' shape version=' + feed.version);
  console.log('ix-update-3 ' + (feed.url.startsWith(DOWNLOAD_PREFIX) ? 'PASS' : 'FAIL') + ' url=' + feed.url);
  if (badShape) {
    fail('shape');
  }

  fs.writeFileSync(outPath, JSON.stringify(feed, null, 2) + '\n', 'utf8');
  console.log('STAT update version=' + feed.version + ' bytes=' + feed.bytes + ' sha256=' + feed.sha256.slice(0, 12)
    + ' out=' + path.relative(REPO_ROOT, outPath));
  console.log('UPDATE-OK');
}

main();

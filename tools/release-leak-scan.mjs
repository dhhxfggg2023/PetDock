#!/usr/bin/env node
// tools/release-leak-scan.mjs —— 发布物里不许出现**这台构建机**的路径、用户名、机器名（2026-10-31）。
//
// 为什么要有这一条：0.4.3 之前 `.dll`/`.pdb` 里带着绝对源路径，同一个提交换个目录编就变 —— 那件事
// 靠 `repro` 看不出来（它只在同一目录重跑），靠人读也看不出来（压缩包里的二进制）。现在构建改成
// `ContinuousIntegrationBuild`（见 Directory.Build.props），但「以后再有人加一个 [CallerFilePath]’，
// 或者把某个绝对路径当常量写进代码」这件事仍然只能靠判据盯。
//
// 判的是**构建机的身份**，不是「一切绝对路径」：自检夹具里那句 `C:\Users\testuser\…` 是假的、
// 用来测脱敏的，不构成泄露；真泄露是「把作者这台机器的用户名、仓库路径、机器名打进了发给别人的包」。
//
// 用法：node tools/release-leak-scan.mjs [--dir <发布目录>]
// 退出码：0 = 干净（LEAK-OK）；1 = 发现本机信息（LEAK-FAIL）。
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const version = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8')).version;
const args = process.argv.slice(2);
const dirArg = args.includes('--dir') ? args[args.indexOf('--dir') + 1] : null;
const releaseDir = dirArg ? path.resolve(REPO_ROOT, dirArg) : path.join(REPO_ROOT, 'artifacts', 'release', version);

function fail(message) {
  console.log('LEAK-FAIL ' + message);
  process.exit(1);
}

if (!fs.existsSync(releaseDir)) {
  fail('发布目录不存在：' + path.relative(REPO_ROOT, releaseDir) + ' —— 先跑 tools/release.mjs');
}

// 只有「本机才有」的串：换一台机器就换一个值。空值与过短的机器名不算（它们会命中正常文本）。
const identity = [
  ['仓库路径', REPO_ROOT],
  ['用户目录', os.homedir()],
  ['用户名', process.env.USERNAME ? '\\Users\\' + process.env.USERNAME : null],
  ['机器名', process.env.COMPUTERNAME && process.env.COMPUTERNAME.length >= 4 ? process.env.COMPUTERNAME : null],
].filter(([, value]) => typeof value === 'string' && value.length > 3);

function encodings(text) {
  const forms = new Set([text, text.replace(/\\/g, '/')]);
  const out = [];
  for (const form of forms) {
    out.push(Buffer.from(form, 'utf8'), Buffer.from(form, 'utf16le'));
  }
  return out;
}

const needles = identity.flatMap(([label, text]) => encodings(text).map((buf) => ({ label, text, buf })));

function walk(dir, acc = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) walk(full, acc);
    else acc.push(full);
  }
  return acc;
}

const files = walk(releaseDir);
let hits = 0;
for (const file of files) {
  const data = fs.readFileSync(file);
  for (const needle of needles) {
    if (!data.includes(needle.buf)) continue;
    hits += 1;
    console.log('HIT ' + path.relative(releaseDir, file) + '  <= ' + needle.label + ' :: ' + needle.text);
  }
}

if (hits > 0) {
  fail('发布物里出现本机信息 ' + hits + ' 处（这台机器的路径 / 用户名 / 机器名）');
}

console.log('STAT leak-scan version=' + version + ' files=' + files.length + ' identity-strings=' + identity.length + ' hits=0');
console.log('LEAK-OK');

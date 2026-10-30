#!/usr/bin/env node
// tools/asset-manifest.mjs —— 资产来源登记册的机器门禁。
//
// 规则：磁盘上存在的每一个资产文件，都必须在 assets/ASSETS.md 的登记表里有对应条目。
// 命中不到即报 UNREGISTERED。
//
// 为什么需要它（已发生的真实事故）：2026-10-15 清理脏树时，所有者放在仓库根的桌宠原图
// gj-meme-857.webp 被标成「一次性网络下载的表情图」直接删掉 —— 它未跟踪、无 git 历史、
// 不进回收站，三条恢复路径全不存在。根因是没有任何地方写清那个文件是什么。
// assets/ASSETS.md 是「写清」的地方，本脚本保证它不会随着时间失效：
// 新增资产而不登记，门禁就红；于是「删之前先查登记册」这件事有了机器兜底。
//
// 覆盖范围（资产 = 二进制/媒体类文件，文档不计）：
//   assets/**、fixtures/**、仓库根的媒体文件
// 跳过：*.md（文档不是资产）、.gitkeep（占位符）、bin/、obj/、node_modules/
//
// 三种状态都要报出来，因为它们对「能不能删」的含义完全不同：
//   tracked    —— 已入库，删了有 git 历史可恢复
//   ignored    —— 被 .gitignore 排除（本机自用素材），删了不可恢复
//   untracked  —— 未入库也没被忽略，删了不可恢复
//
// 用法：node tools/asset-manifest.mjs
// 退出码：0 = 全部已登记；1 = 有未登记资产。

import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const MANIFEST = path.join('assets', 'ASSETS.md');
const MANIFEST_ABS = path.join(REPO_ROOT, MANIFEST);

/** 媒体/资产类扩展名。刻意不含 .md —— 文档不是资产。 */
const ASSET_EXT = new Set([
  '.png', '.jpg', '.jpeg', '.webp', '.gif', '.bmp', '.ico', '.svg',
  '.wav', '.mp3', '.ogg', '.mp4', '.webm',
  '.ttf', '.otf', '.woff', '.woff2',
  '.zip', '.petpack',
]);

const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', '.git']);

/** 扫描一个目录树，收集资产文件（仓库相对 POSIX 路径）。 */
function walk(dir, out = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }

  for (const entry of entries) {
    if (SKIP_DIRS.has(entry.name)) {
      continue;
    }
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(full, out);
    } else if (entry.name !== '.gitkeep') {
      const ext = path.extname(entry.name).toLowerCase();
      if (ASSET_EXT.has(ext)) {
        out.push(path.relative(REPO_ROOT, full).replaceAll('\\', '/'));
      }
    }
  }
  return out;
}

function gitLines(args) {
  try {
    return execFileSync('git', args, { cwd: REPO_ROOT, encoding: 'utf8' })
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean);
  } catch {
    return [];
  }
}

function classify(relPaths) {
  if (relPaths.length === 0) {
    return { tracked: new Set(), ignored: new Set() };
  }

  const tracked = new Set(gitLines(['ls-files', '--', ...relPaths]));
  const ignored = new Set();
  try {
    // check-ignore 对未命中的路径 exit 1，用 execFileSync 会在非零时抛错，
    // 故这里直接读 stdout（命中才输出），并把失败当作「无命中」。
    const out = execFileSync('git', ['check-ignore', '--', ...relPaths], {
      cwd: REPO_ROOT,
      encoding: 'utf8',
    });
    for (const line of out.split('\n')) {
      const value = line.trim();
      if (value) {
        ignored.add(value);
      }
    }
  } catch (error) {
    // exit 1 = 没有任何路径被忽略；但部分命中时 git 仍会 exit 0。
    const out = error?.stdout ?? '';
    for (const line of String(out).split('\n')) {
      const value = line.trim();
      if (value) {
        ignored.add(value);
      }
    }
  }
  return { tracked, ignored };
}

/**
 * 登记判定：ASSETS.md 里出现该文件路径，或出现它的某个**子目录**（以 / 结尾）。
 *
 * 两条边界，都是被负例测出来的（门禁写错比不写更危险：它看起来在跑，实际不设防）：
 *
 * 1. 匹配必须终止在路径边界上（后面不能紧跟文件名字符）。否则登记册里的
 *    `assets/sample/pet-sample.png` 会让子串 `assets/sample/` 命中，该目录下
 *    **任何**新文件都被误判为已登记。
 * 2. 顶层容器目录（`assets`、`fixtures`）**不算登记**。否则登记册正文里出现的
 *    「# assets/ — 资产来源登记册」这句话会让整个 `assets/` 树全部免检 ——
 *    实测就是这样漏掉 `assets/sample/zz-probe.png` 的。登记必须针对具体子目录
 *    （如 `assets/sample/local/`）或具体文件，不能针对容器。
 */
const BOUNDARY = '(?![A-Za-z0-9_.-])';
const CONTAINER_DIRS = new Set(['assets', 'fixtures']);

function escapeRe(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function isRegistered(relPath, manifestText) {
  if (new RegExp(`${escapeRe(relPath)}${BOUNDARY}`).test(manifestText)) {
    return true;
  }

  const segments = relPath.split('/');
  for (let i = segments.length - 1; i >= 1; i--) {
    const dir = segments.slice(0, i).join('/');
    if (CONTAINER_DIRS.has(dir)) {
      continue;
    }
    if (new RegExp(`${escapeRe(dir)}/${BOUNDARY}`).test(manifestText)) {
      return true;
    }
  }
  return false;
}

function main() {
  if (!fs.existsSync(MANIFEST_ABS)) {
    console.log(`VIOLATION ${MANIFEST} 不存在 —— 资产登记册是删除资产前的唯一依据`);
    console.log('ASSET-MANIFEST-FAIL');
    process.exit(1);
  }
  const manifestText = fs.readFileSync(MANIFEST_ABS, 'utf8');

  const scanned = [];
  walk(path.join(REPO_ROOT, 'assets'), scanned);
  walk(path.join(REPO_ROOT, 'fixtures'), scanned);
  // 仓库根：只取媒体文件（不递归，根目录的目录另有归属）。
  for (const entry of fs.readdirSync(REPO_ROOT, { withFileTypes: true })) {
    if (entry.isFile() && ASSET_EXT.has(path.extname(entry.name).toLowerCase())) {
      scanned.push(entry.name);
    }
  }

  const assets = [...new Set(scanned)].sort();
  const { tracked, ignored } = classify(assets);

  let unregistered = 0;
  let ignoredCount = 0;
  let untrackedCount = 0;

  for (const rel of assets) {
    const state = tracked.has(rel) ? 'tracked' : ignored.has(rel) ? 'ignored' : 'untracked';
    if (state === 'ignored') ignoredCount += 1;
    if (state === 'untracked') untrackedCount += 1;

    if (!isRegistered(rel, manifestText)) {
      unregistered += 1;
      console.log(`UNREGISTERED [${state}] ${rel}`);
      console.log(`    → 在 ${MANIFEST} 的登记表里加一行（是什么 / 来源授权 / 可否发行 / 删了会怎样）`);
      if (state !== 'tracked') {
        console.log('    → 注意：该文件未入库，删掉不可恢复');
      }
    }
  }

  console.log(`STAT assets ${assets.length} tracked ${tracked.size} ignored ${ignoredCount} untracked ${untrackedCount}`);
  if (unregistered > 0) {
    console.log(`STAT unregistered ${unregistered}`);
    console.log('ASSET-MANIFEST-FAIL');
    process.exit(1);
  }
  console.log('ASSET-MANIFEST-PASS');
}

main();

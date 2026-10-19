#!/usr/bin/env node
// tools/asset-fingerprint.mjs —— docs/41 D-13a「零真实素材入库」的机器门禁。
//
// 为什么需要它：docs/41:105 写明「CI 对仓库做素材指纹扫描（已知生态素材目录结构、
// *.moc3 / *.model3.json 等扩展名 + 已知版权文件的哈希），命中即失败，直接服务 M-12」，
// 但 tools/ 下从来没有这个脚本 —— 规格里有一条门禁，实现里没有，等于没有门禁。
// 它同时是 docs/52 §6 发布前合规清单第 2、3 条的机器判据。
//
// 扫描范围 = **git 跟踪的文件**。这不是偷懒：`assets/user/` 与 `assets/sample/local/`
// 是所有者本机自用素材（.gitignore 整目录排除，docs/52 §5），它们永远不会进仓库、
// 也不会进安装包，把它们判成违规只会训练人忽略告警。要连磁盘一起查时用 `--all`。
//
// 判据三类（任一类命中即失败）：
//   1. 商业 SDK 二进制扩展名：*.moc3（Live2D 模型）、*.model3.json / *.motion3.json
//      （Cubism 元数据）、*.skel / *.atlas（Spine）、*.mtn（Shimeji）
//   2. 生态素材包结构标记：一个目录里同时出现该生态的包清单（DyberPet 的 info/info.json、
//      BongoCat 的 conf.json、Shimeji 的 conf/actions.xml）——单个文件可能是夹具，
//      但「清单 + 素材目录」的组合是真实角色包的特征
//   3. 已知版权文件哈希：tools/fingerprint-known.json 里登记的 sha256
//
// 用法：
//   node tools/asset-fingerprint.mjs             # 扫 git 跟踪的文件（门禁用）
//   node tools/asset-fingerprint.mjs --all       # 连未跟踪/被忽略的也扫（人工排查用）
//   node tools/asset-fingerprint.mjs --root DIR  # 扫指定目录（负例自测用）
//
// 退出码：0 = 无命中；1 = 有命中。

import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const KNOWN_HASHES = path.join(REPO_ROOT, 'tools', 'fingerprint-known.json');

/** 商业 SDK / 生态素材的二进制扩展名。命中即失败，无一例外。 */
const FORBIDDEN_EXT = new Map([
  ['.moc3', 'Live2D Cubism 模型（docs/52 §3：永不打包）'],
  ['.model3.json', 'Live2D Cubism 元数据（docs/52 §3）'],
  ['.motion3.json', 'Live2D Cubism 动作（docs/52 §3）'],
  ['.skel', 'Spine 骨架（docs/52 §3）'],
  ['.atlas', 'Spine 图集（docs/52 §3）'],
  ['.mtn', 'Shimeji 动作（v1 范围外，见 docs/50 推迟清单）'],
]);

/**
 * 生态角色包的「结构指纹」：清单文件名 → 判定用的内容特征。
 * 只看文件名不够（我们自己的夹具也叫 conf.json 就麻烦了），所以要求同一目录下
 * 同时出现清单与至少一个素材子目录，才算真实角色包。
 */
const PACK_MARKERS = [
  {
    manifest: ['info', 'info.json'],
    siblingDirs: ['pet', 'action', 'res'],
    reason: 'DyberPet 角色包结构（info/info.json + 素材目录）',
  },
  {
    manifest: ['conf.json'],
    siblingDirs: ['assets', 'img', 'image'],
    reason: 'BongoCat 角色包结构（conf.json + 素材目录）',
  },
  {
    manifest: ['conf', 'actions.xml'],
    siblingDirs: [],
    reason: 'Shimeji 角色包结构（conf/actions.xml）',
  },
];

const SKIP_DIRS = new Set(['node_modules', '.git', 'bin', 'obj']);

function argValue(flag) {
  const i = process.argv.indexOf(flag);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : null;
}

const SCAN_ALL = process.argv.includes('--all');
const ROOT_OVERRIDE = argValue('--root');
const SCAN_ROOT = ROOT_OVERRIDE ? path.resolve(ROOT_OVERRIDE) : REPO_ROOT;

/** git 跟踪的文件（相对仓库根的 POSIX 路径）。 */
function trackedFiles() {
  try {
    return execFileSync('git', ['ls-files'], { cwd: REPO_ROOT, encoding: 'utf8' })
      .split('\n')
      .map((line) => line.trim())
      .filter(Boolean);
  } catch {
    return [];
  }
}

/** 磁盘遍历（--all 或 --root 时使用）。 */
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
    } else if (entry.isFile()) {
      out.push(path.relative(SCAN_ROOT, full).replaceAll('\\', '/'));
    }
  }
  return out;
}

function loadKnownHashes() {
  try {
    const parsed = JSON.parse(fs.readFileSync(KNOWN_HASHES, 'utf8'));
    return Array.isArray(parsed.files) ? parsed.files : [];
  } catch {
    return [];
  }
}

function sha256(absPath) {
  try {
    return createHash('sha256').update(fs.readFileSync(absPath)).digest('hex');
  } catch {
    return null;
  }
}

function main() {
  let files;
  if (ROOT_OVERRIDE || SCAN_ALL) {
    files = walk(SCAN_ROOT).sort();
  } else {
    files = trackedFiles().sort();
  }

  const known = loadKnownHashes();
  const hits = [];

  // 零文件即失败。这一条不是形式主义：本脚本的 git 调用失败时返回空表（见 trackedFiles 的
  // catch），于是「一个文件都没扫」会安静地打成 PASS —— 一条在什么都看不见时判绿的门禁，
  // 比没有门禁更危险，因为它会被人当作证据引用。实测踩过：在 code_verify 通道里
  // git 不可用，输出 `STAT fingerprint scanned=0 ... FINGERPRINT-PASS`。
  if (files.length === 0) {
    console.log(`STAT fingerprint scanned=0 scope=${ROOT_OVERRIDE ? 'root=' + ROOT_OVERRIDE : SCAN_ALL ? 'all-files' : 'git-tracked'}`);
    console.log('FINGERPRINT-FAIL 扫描到 0 个文件 —— git 不可用或目录为空，门禁没有检查任何东西');
    process.exit(1);
  }

  // 判据 1：扩展名
  for (const rel of files) {
    const lower = rel.toLowerCase();
    for (const [ext, reason] of FORBIDDEN_EXT) {
      if (lower.endsWith(ext)) {
        hits.push({ path: rel, reason });
        break;
      }
    }
  }

  // 判据 2：生态角色包结构（同一目录里清单 + 素材子目录）
  //
  // 这里的每一条都**必须目录内判定**。曾经写成全局 `endsWith('/info/info.json')`，
  // 结果仓库里任何一处出现该路径，就会把命中报给**每一个**目录 —— 实测把自家的合成夹具
  // tests/fixtures/dyberpet-min/dyberpet-flat（它根本没有 info/ 目录）判成了真实角色包。
  // 一个会误报自家夹具的门禁，唯一的作用是训练人忽略告警。
  const dirFiles = new Map();
  const dirSubdirs = new Map();
  for (const rel of files) {
    const dir = path.posix.dirname(rel);
    if (!dirFiles.has(dir)) {
      dirFiles.set(dir, new Set());
    }
    dirFiles.get(dir).add(path.posix.basename(rel).toLowerCase());

    // 把「该文件所在的子目录」登记到每一级祖先上，供 siblingDirs 判定。
    const parts = rel.split('/');
    for (let depth = 1; depth < parts.length; depth += 1) {
      const ancestor = parts.slice(0, depth).join('/');
      const child = parts[depth];
      if (depth === parts.length - 1) {
        continue; // 最后一段是文件名，不是子目录
      }
      if (!dirSubdirs.has(ancestor)) {
        dirSubdirs.set(ancestor, new Set());
      }
      dirSubdirs.get(ancestor).add(child.toLowerCase());
    }
  }

  for (const [dir, names] of dirFiles) {
    const subdirs = dirSubdirs.get(dir) ?? new Set();
    for (const marker of PACK_MARKERS) {
      // 清单路径（相对该目录）逐段判定，不做全局匹配。
      const manifestPath = marker.manifest.join('/').toLowerCase();
      const manifestPresent = marker.manifest.length === 1
        ? names.has(manifestPath)
        : files.some((rel) => rel.toLowerCase() === (dir === '.' ? manifestPath : `${dir}/${manifestPath}`));
      if (!manifestPresent) {
        continue;
      }
      const siblingHit = marker.siblingDirs.length === 0
        ? true
        : marker.siblingDirs.some((d) => subdirs.has(d));
      if (siblingHit) {
        hits.push({ path: `${dir === '.' ? '' : dir + '/'}${manifestPath}`, reason: marker.reason });
      }
    }
  }

  // 判据 3：已知版权文件哈希
  if (known.length > 0) {
    const wanted = new Map(known.map((entry) => [entry.sha256, entry.note ?? '已知版权文件']));
    for (const rel of files) {
      const digest = sha256(path.join(SCAN_ROOT, rel));
      if (digest && wanted.has(digest)) {
        hits.push({ path: rel, reason: wanted.get(digest) });
      }
    }
  }

  const seen = new Set();
  for (const hit of hits) {
    const key = `${hit.path}|${hit.reason}`;
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    console.log(`FP-HIT ${hit.path}  ← ${hit.reason}`);
  }

  const scope = ROOT_OVERRIDE ? `root=${ROOT_OVERRIDE}` : SCAN_ALL ? 'all-files' : 'git-tracked';
  console.log(`STAT fingerprint scanned=${files.length} scope=${scope} known-hashes=${known.length} hits=${seen.size}`);
  if (seen.size > 0) {
    console.log('FINGERPRINT-FAIL 真实素材/商业 SDK 二进制不得入库，见 docs/41 D-13a 与 docs/52 §5');
    process.exit(1);
  }
  console.log('FINGERPRINT-PASS');
}

main();

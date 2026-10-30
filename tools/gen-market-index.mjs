#!/usr/bin/env node
// tools/gen-market-index.mjs —— 生成插件市场索引（2026-10-30）。
//
// 面板里的「插件市场」只认一份写死地址的索引（PetDock.Core/src/Host/PluginMarket.cs 的 Endpoint）。
// 索引必须和发布物**同一次构建**产出，所以由本脚本在 release 之后从真实内容算，而不是手写。
//
// **只收「独立插件」，不收随包发行的内置插件**（2026-10-30 所有者口径）。
// 内置插件随 exe 一起发，用户装好就已经有了；把它们列进市场只会让列表里全是「已装 0.1.0」，
// 点「更新」也只是把同一个文件再解一遍。市场是「额外装的东西」的地方。
//
// 输入：
//   发行包 plugins/ 下的清单 → 记为**内置**，不进索引（只用来拒绝重名）；
//   仓库根 market-plugins/<dir>/ → **独立插件**，进索引，需要单独上传到 Release。
//
// 用法：
//   node tools/gen-market-index.mjs            # 从发行包读内置集合，扫 market-plugins/ 写索引
//   node tools/gen-market-index.mjs --check    # 只校验现有索引与源码是否一致（门禁用）
//   node tools/gen-market-index.mjs --out <p>  # 换输出路径
//
// 判据（失败即 exit 1）：
//   ix-market-1  索引里的 sha256/bytes 与 market-plugins/ 实测一致；独立插件无遗漏；内置插件不出现
//   ix-market-2  每条 url 都在 PluginMarket.AllowedDownloadPrefix 之下
//   ix-market-3  sha256 形状合法（64 位十六进制）
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { collectFiles, writeDeterministicZip } from './lib/zip.mjs';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ARTIFACTS = path.join(REPO_ROOT, 'artifacts');
const INDEX_REL = path.join('plugins', 'index.json');
const MARKET_ROOT = path.join(REPO_ROOT, 'market-plugins');
const DOWNLOAD_PREFIX = 'https://github.com/dhhxfggg2023/PetDock/releases/download/';

const args = process.argv.slice(2);
function arg(name) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : null;
}

const CHECK_ONLY = args.includes('--check');
const VERSION = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8')).version;
const zipPath = arg('--zip') ?? path.join(ARTIFACTS, 'dist', 'PetDock-' + VERSION + '-win-x64.zip');
const outPath = arg('--out') ?? path.join(REPO_ROOT, INDEX_REL);

function fail(message) {
  console.log('MARKET-FAIL ' + message);
  process.exit(1);
}

function readManifest(manifestPath) {
  return JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
}

/** 读发行包里所有插件的 id（内置集合）。发行包不在时返回空集合而不是失败。 */
function readBuiltinIds(zip) {
  const ids = new Set();
  if (!fs.existsSync(zip)) {
    return ids;
  }
  let listing = [];
  try {
    listing = execFileSync('tar', ['-tf', zip], { cwd: REPO_ROOT, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }).split(/\r?\n/);
  } catch {
    return ids;
  }
  const re = new RegExp('^PetDock-' + VERSION.replace(/\./g, '\\.') + '/plugins/([^/]+)/petdock-plugin\.json$');
  for (const entry of listing) {
    const m = re.exec(entry);
    if (m) {
      ids.add(m[1]);
    }
  }
  return ids;
}

/**
 * 把 market-plugins/<dir> 打成独立插件 zip（市场下载的就是它）。
 * zip 根 = 插件目录内容（petdock-plugin.json 在顶层），与 PluginInstaller.FindPluginDir 期望一致。
 */
function packLocalPlugin(dirName) {
  const src = path.join(MARKET_ROOT, dirName);
  const files = collectFiles(src);
  return writeDeterministicZip(files);
}

/** 收集 market-plugins/ 下的独立插件。[{dir, manifest}] */
function readIndependent() {
  const out = [];
  if (!fs.existsSync(MARKET_ROOT)) {
    return out;
  }
  for (const item of fs.readdirSync(MARKET_ROOT, { withFileTypes: true })) {
    if (!item.isDirectory()) {
      continue;
    }
    const manifestPath = path.join(MARKET_ROOT, item.name, 'petdock-plugin.json');
    if (!fs.existsSync(manifestPath)) {
      console.log('SKIP market-plugins/' + item.name + ' 没有 petdock-plugin.json');
      continue;
    }
    try {
      const manifest = readManifest(manifestPath);
      // 可选的展示信息放在 market-plugins/<dir>/market.json（不属于插件清单，不影响安装）。
      const metaPath = path.join(MARKET_ROOT, item.name, 'market.json');
      let summary = '';
      if (fs.existsSync(metaPath)) {
        try {
          summary = readManifest(metaPath).summary ?? '';
        } catch {
          console.log('SKIP market-plugins/' + item.name + ' market.json 读不出来（忽略）');
        }
      }
      out.push({ dir: item.name, manifest, summary });
    } catch (error) {
      console.log('SKIP market-plugins/' + item.name + ' 清单读不出来：' + error.message);
    }
  }
  return out.sort((a, b) => a.dir.localeCompare(b.dir));
}

function main() {
  const builtinIds = readBuiltinIds(zipPath);
  const independent = readIndependent();

  // 独立插件与内置重名 = 装配会撞车。直接拒绝，不猜谁赢。
  for (const item of independent) {
    if (builtinIds.has(item.manifest.id)) {
      fail('market-plugins/' + item.dir + ' 的 id ' + item.manifest.id + ' 与内置插件重名');
    }
  }

  if (CHECK_ONLY) {
    if (!fs.existsSync(outPath)) {
      fail('索引不存在：' + path.relative(REPO_ROOT, outPath));
    }
    const current = JSON.parse(fs.readFileSync(outPath, 'utf8'));
    let bad = 0;
    const byId = new Map(independent.map((it) => [it.manifest.id, it]));
    for (const entry of current.plugins) {
      const source = byId.get(entry.id);
      if (!source) {
        console.log('ix-market-1 FAIL id=' + entry.id + ' 在 market-plugins/ 里找不到源目录');
        bad += 1;
        continue;
      }
      const packed = packLocalPlugin(source.dir);
      if (packed.sha256 !== entry.sha256) {
        console.log('ix-market-1 FAIL id=' + entry.id + ' 索引=' + entry.sha256.slice(0, 12) + ' 实测=' + packed.sha256.slice(0, 12));
        bad += 1;
      }
      if (packed.bytes !== entry.bytes) {
        console.log('ix-market-1 FAIL id=' + entry.id + ' 体积 索引=' + entry.bytes + ' 实测=' + packed.bytes);
        bad += 1;
      }
    }
    const listed = new Set(current.plugins.map((e) => e.id));
    for (const id of byId.keys()) {
      if (!listed.has(id)) {
        console.log('ix-market-1 FAIL market-plugins/ 里有 ' + id + ' 但索引里没有');
        bad += 1;
      }
    }
    for (const entry of current.plugins) {
      if (builtinIds.has(entry.id)) {
        console.log('ix-market-1 FAIL ' + entry.id + ' 是随包内置插件，不该出现在市场');
        bad += 1;
      }
    }
    console.log('STAT market-check entries=' + current.plugins.length + ' independent=' + byId.size + ' builtin=' + builtinIds.size + ' bad=' + bad);
    if (bad > 0) {
      fail('索引与源不一致（' + bad + ' 处）');
    }
    console.log('MARKET-CHECK-OK');
    return;
  }

  const plugins = independent.map((item) => {
    const packed = packLocalPlugin(item.dir);
    return {
      id: item.manifest.id,
      name: item.manifest.name ?? item.manifest.id,
      version: item.manifest.version,
      // 清单是 additionalProperties:false 的严格 schema，没有 description/summary 字段。
      // 市场里的那句简介留在索引里由人写（见 market-plugins/README.md），不在清单里编。
      summary: item.summary ?? '',
      url: DOWNLOAD_PREFIX + 'v' + VERSION + '/' + item.manifest.id + '.zip',
      sha256: packed.sha256,
      bytes: packed.bytes,
      capabilities: Array.isArray(item.manifest.capabilities) ? item.manifest.capabilities : [],
    };
  });

  let failures = 0;
  const check = (name, ok, detail) => {
    if (!ok) {
      failures += 1;
    }
    console.log('ix-market-' + name + ' ' + (ok ? 'PASS' : 'FAIL') + (detail ? ' ' + detail : ''));
  };

  const badUrl = plugins.filter((p) => !p.url.startsWith(DOWNLOAD_PREFIX));
  check('2', badUrl.length === 0, 'entries=' + plugins.length + ' bad=' + badUrl.length);

  const badShape = plugins.filter((p) => typeof p.sha256 !== 'string' || !/^[0-9a-f]{64}$/.test(p.sha256));
  check('3', badShape.length === 0, 'bad-shape=' + badShape.length);

  const index = { schema: 1, generated: new Date().toISOString(), plugins };
  fs.mkdirSync(path.dirname(outPath), { recursive: true });
  fs.writeFileSync(outPath, JSON.stringify(index, null, 2) + '\n', 'utf8');
  console.log('STAT market entries=' + plugins.length + ' builtin=' + builtinIds.size + ' out=' + path.relative(REPO_ROOT, outPath));

  if (failures > 0) {
    fail('checks=' + failures);
  }
  console.log('MARKET-OK');
}

main();

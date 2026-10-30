#!/usr/bin/env node
// tools/verify-release.mjs —— 发布对齐门（2026-10-30）。
//
// 为什么需要它：verify 的 dist 层只证明「本地这个 zip 是好的」，
// 而「把 zip 传到 Release」是手工的一步，**没有任何判据盯着它**。
// 2026-10-30 就真的漏了一次：加了插件市场、跑绿了全部门禁、也验证了「从 Release 下载插件包」，
// 却忘了重新发程序本体 —— Release 上还是加市场之前的构建，用户下到的版本没有市场功能。
// 那种错误靠人记得住是不可靠的：跑绿的门禁在说谎。
//
// 这条门做的事只有一件：**匿名**下载 Release 上的产物，与本地最新构建逐字节比对。
// 对不上就红，并明确说出「本地是哪个 / 远端是哪个 / 差多少字节」。
//
// 用法：
//   node tools/verify-release.mjs           # 校验 v<version> 的 Release 与本地产物一致
//   node tools/verify-release.mjs --tag v0.1.0
//   node tools/verify-release.mjs --list    # 只列 Release 上的资产，不比对
//
// 判据：
//   ix-release-1  Release 上要有 zip 与旁车两个资产
//   ix-release-2  匿名下载得到的 sha256 == 本地 artifacts/dist 旁车里的 sha256
//   ix-release-3  远端旁车文件的内容与远端 zip 实测一致（防止资产与旁车不同批）
//
// 退出码：0 = 对齐，或**检查不了**（网络/仓库不可达，此时打印 SKIP-RELEASE-ALIGN 并说明原因）；
//         1 = 确实不一致（这才是要拦的）。
//
// 为什么「检查不了」也返回 0：verify 的步判定是「退出码 0 + marker 出现」，没有步骤级跳过机制。
// 把网络抖动染成发布门红，会训练人忽略这条门 —— 那比偶尔漏检更糟。
// 代价是跳过必须**大声打出来**：SKIP 行里带原因，且不带 RELEASE-VERIFY-OK 标记，
// 所以在证据 README 里看得见「这一步没验」。
import { createHash } from 'node:crypto';
import https from 'node:https';
import tls from 'node:tls';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const ARTIFACTS = path.join(REPO_ROOT, 'artifacts');
const REPO_SLUG = 'dhhxfggg2023/PetDock';

const args = process.argv.slice(2);
function arg(name) {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1] : null;
}

const VERSION = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, 'package.json'), 'utf8')).version;
const TAG = arg('--tag') ?? 'v' + VERSION;
const LIST_ONLY = args.includes('--list');
const NAME = 'PetDock-' + VERSION + '-win-x64.zip';
const API = 'https://api.github.com/repos/' + REPO_SLUG + '/releases/tags/' + TAG;

function fail(code, message) {
  console.log('RELEASE-VERIFY-FAIL ' + message);
  process.exit(code);
}

function sha256File(file) {
  return createHash('sha256').update(fs.readFileSync(file)).digest('hex');
}

/**
 * 匿名下载到临时文件。返回 { file, digest, bytes } 或 null（失败时已打印原因）。
 *
 * 用 Node 自带的 https 而不是 curl：这台机器上 curl 走 schannel 取不到中间证书链
 * （curl: (60) unable to get local issuer certificate），而门禁脚本的约定是「零外部依赖、
 * 只用 Node 内置模块」。证书校验仍然开着（不设 NODE_TLS_REJECT_UNAUTHORIZED）。
 */
function download(url, target, headers = {}, redirects = 5) {
  return new Promise((resolve) => {
    const request = https.get(url, { headers: { 'user-agent': 'petdock', ...headers } }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirects > 0) {
        res.resume();
        resolve(download(res.headers.location, target, headers, redirects - 1));
        return;
      }

      if (res.statusCode !== 200) {
        res.resume();
        console.log('  HTTP ' + res.statusCode + ' ' + url);
        resolve(null);
        return;
      }

      const out = fs.createWriteStream(target);
      res.pipe(out);
      out.on('finish', () => {
        out.close(() => {
          if (!fs.existsSync(target) || fs.statSync(target).size === 0) {
            resolve(null);
            return;
          }
          resolve({ file: target, digest: sha256File(target), bytes: fs.statSync(target).size });
        });
      });
      out.on('error', () => resolve(null));
    });

    request.on('error', (error) => {
      console.log('  请求失败：' + error.message);
      resolve(null);
    });
    request.setTimeout(120000, () => {
      request.destroy();
      console.log('  超时');
      resolve(null);
    });
  });
}

/**
 * 让 Node 用系统根证书。这台机器上 Node 自带的 CA 包不认 github 的中间证书
 * （unable to verify the first certificate），而 curl 走 schannel 也有同样的问题。
 * 两种解决都不接受：关掉校验（NODE_TLS_REJECT_UNAUTHORIZED=0）会让这条门失去意义；
 * 依赖人记得加 --use-system-ca 则等于把「能不能验」交给调用方式。
 * 所以在这里显式把系统根证书装进去 —— 校验仍然开着。
 */
function trustSystemRoots() {
  try {
    if (typeof tls.getCACertificates === 'function' && typeof tls.setDefaultCACertificates === 'function') {
      const system = tls.getCACertificates('system');
      const bundled = tls.getCACertificates('bundled');
      tls.setDefaultCACertificates([...new Set([...system, ...bundled])]);
      return true;
    }
  } catch (error) {
    console.log('  （加载系统根证书失败：' + error.message + '）');
  }
  return false;
}

/** 拉一小段 JSON（资产列表）。返回解析后的对象或 null。 */
function fetchJson(url, headers = {}) {
  return new Promise((resolve) => {
    const request = https.get(url, { headers: { 'user-agent': 'petdock', accept: 'application/vnd.github+json', ...headers } }, (res) => {
      if (res.statusCode !== 200) {
        res.resume();
        resolve(null);
        return;
      }
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        try {
          resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));
        } catch {
          resolve(null);
        }
      });
    });
    request.on('error', () => resolve(null));
    request.setTimeout(60000, () => {
      request.destroy();
      resolve(null);
    });
  });
}

async function main() {
  const trusted = trustSystemRoots();

  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'petdock-release-'));

  try {
    if (LIST_ONLY) {
      console.log('RELEASE-VERIFY tag=' + TAG + ' 资产列表见下方 curl 输出');
      execFileSync('curl', ['-sS', '--ssl-no-revoke', '--max-time', '60', '-L', BASE + NAME, '-o', 'NUL', '-w', '%{http_code}\n'], { stdio: 'inherit' });
      return;
    }

    // ix-release-1：两个资产都要在。分别下载，任一失败就是缺资产。
    const localZip = path.join(ARTIFACTS, 'dist', NAME);
    const localSidecar = localZip + '.sha256';
    if (!fs.existsSync(localZip) || !fs.existsSync(localSidecar)) {
      fail(1, '本地没有 ' + path.relative(REPO_ROOT, localZip) + ' 或它的旁车 —— 先跑 npm run dist');
    }

    // 先向 API 要资产列表：browser_download_url 走的是 CDN，刚替换的资产可能还在缓存里
    // （2026-10-30 实测：替换后立刻下载拿到的仍是旧包，而资产的 size/hash 已经是新的）。
    // API 的 assets/<id> 端点带 accept: octet-stream 会直连，不受那个缓存影响。
    const token = process.env.GITHUB_TOKEN ?? process.env.GH_TOKEN ?? '';
    const authHeaders = token ? { authorization: 'Bearer ' + token } : {};
    const release = await fetchJson(API, authHeaders);
    if (!release || !Array.isArray(release.assets)) {
      console.log('SKIP-RELEASE-ALIGN reason=release-not-found tag=' + TAG);
      console.log('  「查不到 Release」不等于「对齐了」：这一步没验。');
      return;
    }

    const asset = release.assets.find((a) => a.name === NAME);
    const sideAsset = release.assets.find((a) => a.name === NAME + '.sha256');
    if (!asset || !sideAsset) {
      fail(1, 'Release ' + TAG + ' 上缺资产（zip=' + Boolean(asset) + ' 旁车=' + Boolean(sideAsset) + '）');
    }

    process.stdout.write('RUN 下载 ' + NAME + ' … ');
    const zipHeaders = { accept: 'application/octet-stream', ...authHeaders };
    const sideHeaders = { accept: 'application/octet-stream', ...authHeaders };
    const remoteZip = await download(asset.url, path.join(work, 'remote.zip'), zipHeaders);
    console.log(remoteZip ? remoteZip.bytes + ' 字节' : '失败');
    if (!remoteZip) {
      console.log('SKIP-RELEASE-ALIGN reason=download-failed asset=' + NAME);
      console.log('  「检查不了」不等于「对齐了」：这一步没验。要验就在网络通的时候重跑。');
      return;
    }

    process.stdout.write('RUN 下载旁车 … ');
    const remoteSide = await download(sideAsset.url, path.join(work, 'remote.sha256'), sideHeaders);
    console.log(remoteSide ? 'ok' : '失败');
    if (!remoteSide) {
      fail(1, 'Release 上缺 ' + NAME + '.sha256（旁车必须与 zip 同批发布）');
    }

    const remoteDeclared = fs.readFileSync(remoteSide.file, 'utf8').trim().split(/\s+/)[0];
    const localDigest = sha256File(localZip);
    const localBytes = fs.statSync(localZip).size;

    // ix-release-3：远端旁车描述的就是远端 zip（防止有人只换了 zip 没换旁车）。
    if (remoteDeclared !== remoteZip.digest) {
      fail(1, 'ix-release-3 远端旁车与远端 zip 不符：旁车=' + remoteDeclared.slice(0, 12) + ' 实测=' + remoteZip.digest.slice(0, 12));
    }
    console.log('ix-release-3 PASS 远端旁车与远端 zip 一致 sha256=' + remoteZip.digest.slice(0, 12));

    // ix-release-2：远端 == 本地最新构建。
    if (remoteZip.digest !== localDigest) {
      console.log('ix-release-2 FAIL Release 上的包不是本地最新构建：');
      console.log('  远端 sha256=' + remoteZip.digest + '  ' + remoteZip.bytes + ' 字节');
      console.log('  本地 sha256=' + localDigest + '  ' + localBytes + ' 字节');
      console.log('  差 ' + (localBytes - remoteZip.bytes) + ' 字节');
      console.log('  → 重新上传：删掉 Release 上的旧资产，再传 ' + NAME + ' 与它的旁车');
      fail(1, 'ix-release-2 不一致（忘了重新发布？）');
    }
    console.log('ix-release-2 PASS Release 与本地产物一致 sha256=' + localDigest.slice(0, 12) + ' bytes=' + localBytes);

    console.log('STAT release-verify tag=' + TAG + ' sha256=' + localDigest + ' bytes=' + localBytes);
    console.log('RELEASE-VERIFY-OK');
  } finally {
    fs.rmSync(work, { recursive: true, force: true });
  }
}

await main();

// tools/lib/source.mjs —— 「这份产物来自哪棵树」的唯一答案（2026-10-30）。
//
// 两个地方要它：发布清单（release.mjs）与插件市场索引（gen-market-index.mjs）。
// 两处都必须**可复现**：同一棵树重复执行得到同样的字节。否则「Release 上的包 == 本地构建」
// 这条判据就分不清是内容变了还是时间戳变了 —— 而分不清的判据等于没有判据。
//
// 为什么字段叫 source_commit_time 而不是 built_at：取的是 HEAD 的**提交时间**，不是打包时刻。
// 2026-10-30 它叫 built_at，于是发布一小时后看产物，清单上写着「一小时前构建的」，
// 而那个包是刚打的。名字骗人比数字错更难查，所以名字必须说出它到底是哪个时间。
import { execFileSync } from 'node:child_process';

/**
 * 读一棵工作树的来源标识。
 * @param {string} root 仓库根目录（git 命令的工作目录）
 * @returns {{commit: string|null, commitTime: string, dirty: boolean}}
 */
export function sourceIdentity(root) {
  const git = (args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
  let commit = null;
  let commitTime = null;
  let dirty = true;
  try {
    commit = git(['rev-parse', 'HEAD']) || null;
    const raw = git(['log', '-1', '--format=%cI']);
    if (raw) {
      commitTime = new Date(raw).toISOString();
    }
    dirty = git(['status', '--porcelain']).length > 0;
  } catch {
    // 解压出来的源码包没有 .git：产物因此不可复现，也追溯不到提交。调用方会据此打警告。
  }
  const epoch = process.env.SOURCE_DATE_EPOCH;
  if (epoch && /^\d+$/.test(epoch)) {
    commitTime = new Date(Number(epoch) * 1000).toISOString();
  }
  if (!commitTime) {
    commitTime = new Date().toISOString();
  }
  return { commit, commitTime, dirty: commit === null ? true : dirty };
}

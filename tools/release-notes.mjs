// tools/release-notes.mjs —— Release 正文的唯一一份模板。
//
// 2026-10-30 建它，是因为 GitHub 页面上那个「1 小时前」是 **Release 条目**的发布时间，
// 不是资产的：把新构建传进已经发布一小时的 Release，资产换了、时间纹丝不动，
// 于是「最新构建在哪」在页面上无解。字节看不出来，就把指纹写进正文。
//
// 2026-10-31 用户指出的问题：「这个发布页面问题很大啊，这里不应该写这个版本更新了什么吗。」
// 他说得对，而根因是**顺序反了**：上一版正文的第一句就是「解压即用版……」，
// 唯一的 h2 是「## 这个 Release 对应的构建」，整页没有一处说这次改了什么。
//
//   一个 Release 页面的第一职责，是告诉用户「这次更新带来了什么」；
//   构建溯源自证是**给验证者看的第二职责**，它该在末尾，不该顶替正文。
//
// 所以现在的结构（也是 GitHub 上常见的形状）：
//   ## 这次改了什么   ← 从提交历史自动生成（见 tools/release-notes.mjs 的 BuildChanges）
//   ## 怎么升级
//   ## 校验            ← sha256 / 提交 / 大小
//   ## 构建溯源        ← 折叠，放最后
//
// 正文由生成的，不手写：写两遍就会漂移，而漂移出来的正文会骗过 ix-release-4
// （它只比指纹，不比「改了什么」说没说）。

/**
 * 读「这次改了什么」的**主来源**：`release-notes/<version>.md`。
 *
 * 为什么不直接用 git log：提交信息是写给开发者的（「更新源去掉指纹字段：它和……是死循环」），
 * 而 Release 页面是写给**用户**的。前者当正文，等于让用户读开发日志。
 * 所以主来源是人写的这一份，git log 退到 detail 里作为可核对的明细。
 *
 * 缺这一份时**不编**：调用方会把 git log 当兜底，并在正文里如实说明这是自动提取的。
 */
export function readCuratedNotes(readFile, version) {
  try {
    const text = String(readFile(version) ?? '').trim();
    return text.length > 0 ? text : null;
  } catch {
    return null;
  }
}

/**
 * 把 git log 的一行行提交信息变成「这次改了什么」。
 *
 * 规则刻意简单、可预测：
 *   · 跳过版本号/发布类提交（「版本 X.Y.Z：…」「Release …」），它们不描述改动；
 *   · 每条取第一行（约定式提交的主旨），去掉结尾的句号；
 *   · 超过 max 条就折叠，并明说折叠了几条 —— 少列不能看起来像「只改了这些」。
 */
export function buildChanges(subjects, { max = 14, repo = 'dhhxfggg2023/PetDock', compare = null } = {}) {
  const skip = /^(版本\s*\d|release\b|发布\s*v?\d)/i;
  const lines = [];
  for (const raw of subjects) {
    const subject = String(raw ?? '').trim();
    if (!subject || skip.test(subject)) {
      continue;
    }
    lines.push(subject.replace(/[。.]$/, ''));
  }

  if (lines.length === 0) {
    return ['- 维护性更新（没有面向用户的改动记录）。'];
  }

  const shown = lines.slice(0, max).map((line) => '- ' + line);

  // 折叠时给**可比对的链接**，而不是一句「见提交历史」：
  // 用户要能自己核对「这次到底改了多少、都在哪」，一个 commits/main 首页做不到这件事。
  const link = compare ? `https://github.com/${repo}/compare/${compare}` : `https://github.com/${repo}/commits/main`;
  if (lines.length > max) {
    shown.push(`- …另有 ${lines.length - max} 条，见[完整改动](${link})。`);
  } else {
    shown.push('', `完整改动：[\`${compare ?? 'main'}\`](${link})`);
  }

  return shown;
}

/**
 * 生成 Release 正文。传入的都是已确定的字符串，本函数不碰 I/O。
 * commit 为空表示产物追溯不到提交（解压出来的源码包没有 .git）。
 *
 * `changes` 由调用方从 git log 取（见 buildChanges）；缺省时如实说「没有改动记录」，
 * 不编一段看起来像总结的话。
 */
export function buildNotes({
  version,
  fileName,
  commit,
  commitTime,
  dirty,
  sha256,
  bytes,
  changes = [],
  upgrade = [],
  compare = null,
  curated = null,
}) {
  const shortCommit = commit ? commit.slice(0, 12) : '(未知)';
  const sizeMb = (bytes / (1024 * 1024)).toFixed(1);

  return [
    `下载 [\`${fileName}\`](https://github.com/dhhxfggg2023/PetDock/releases/download/v${version}/${fileName})，`,
    '解压后双击 `PetDock.Core.exe`。不需要安装、不需要管理员、不写注册表。',
    '',
    '## 这次改了什么',
    '',
    // 人写的那份就是这一节的全部内容，标题层级也归它自己定。
    //
    // 为什么**不**统一注入「怎么升级」：那一段说的是「用户要做什么」，而不同版本该做什么并不一样 ——
    // 0.3.0 只能下载、不能自动安装（那个能力 0.4.0 才有），拿 0.4.0 的口径去教 0.3.0 的用户，
    // 就是让他在一个不存在的按钮上找半天。所以升级动作跟着版本写，模板只管拼装。
    ...(curated
      ? curated.split('\n')
      : [...(changes.length > 0 ? changes : ['- 维护性更新（没有面向用户的改动记录）。']), '',
         '_（这一节是从提交历史自动提取的，还没有人工整理过。）_',
         '',
         ...(upgrade.length > 0 ? ['## 怎么升级', '', ...upgrade] : [])]),
    // 人写的那份是 trim 过的，末尾没有空行；补一个，免得和下面的 '## 下载' 粘在一起。
    '',
    '## 下载',
    '',
    '| 文件 | 说明 |',
    '| --- | --- |',
    `| \`${fileName}\` | 解压即用，${sizeMb} MB |`,
    `| \`${fileName}.sha256\` | 校验值 |`,
    '',
    '只支持 64 位 Windows（win-x64）。没有代码签名，第一次运行 SmartScreen 可能提示',
    '「Windows 已保护你的电脑」——点「更多信息 → 仍要运行」。',
    '',
    '<details>',
    '<summary>构建溯源（这个包是怎么来的）</summary>',
    '',
    '| 项 | 值 |',
    '| --- | --- |',
    `| 版本 | \`${version}\` |`,
    `| 来源提交 | \`${shortCommit}\` |`,
    `| 提交时间 | \`${commitTime}\` |`,
    `| sha256 | \`${sha256}\` |`,
    `| 大小 | \`${bytes}\` 字节 |`,
    '| 工作树 | ' + (dirty ? '**有未提交改动（产物不可复现）**' : '干净') + ' |',
    '',
    '校验：`Get-FileHash -Algorithm SHA256 ' + fileName + '` 应与上表的 sha256 一致。',
    '',
    '页面上的发布时间属于 **Release 条目**，不属于这个文件 —— 判断新旧请对上表的 sha256 与来源提交。',
    '同一份构建重新上传不会让时间前进，只有换指纹才说明内容变了。',
    '',
    '</details>',
    '',
  ].join('\n');
}

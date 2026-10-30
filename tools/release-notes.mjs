// tools/release-notes.mjs —— Release 正文的唯一一份模板（2026-10-30）。
//
// 为什么要模板化：GitHub 页面上那个「1 小时前」是 **Release 条目**的发布时间，不是资产的。
// 2026-10-30 我把新构建传进了已经发布一小时的那个 Release：资产换了、页面时间纹丝不动，
// 于是「最新构建在哪」在页面上无解。我自己的对齐门只比字节，所以它全绿 ——
// 门禁看不见的东西，用户看得见。
//
// 字节看不出来，就把指纹写进正文：正文声明 sha256 与来源提交，
// 页面本身因此能回答「这是不是最新构建」，而 verify-release 的 ix-release-4 校验它。
//
// 两个消费方共用这一份文本：发布时 `node tools/verify-release.mjs --notes` 打印它，
// 门禁时 verify-release 校验远端正文里确实有同一份指纹。写两遍就会漂移。

/**
 * 生成 Release 正文。传入的都是已确定的字符串，本函数不碰 I/O。
 * commit 为空表示产物追溯不到提交（解压出来的源码包没有 .git）。
 */
export function buildNotes({ version, fileName, commit, commitTime, dirty, sha256, bytes }) {
  const shortCommit = commit ? commit.slice(0, 12) : '(未知)';
  return [
    '解压即用版。下载 `' + fileName + '`，解压后双击 `PetDock.Core.exe`。',
    '不需要安装、不需要管理员、不写注册表。详见压缩包里的 `INSTALL.md`。',
    '',
    '## 这个 Release 对应的构建',
    '',
    '| 项 | 值 |',
    '| --- | --- |',
    '| 版本 | `' + version + '` |',
    '| 来源提交 | `' + shortCommit + '` |',
    '| 提交时间 | `' + commitTime + '` |',
    '| sha256 | `' + sha256 + '` |',
    '| 大小 | `' + bytes + '` 字节 |',
    '| 工作树 | ' + (dirty ? '**有未提交改动（产物不可复现）**' : '干净') + ' |',
    '',
    '校验：`Get-FileHash -Algorithm SHA256 ' + fileName + '` 应与上表的 sha256 一致。',
    '页面上的发布时间属于 **Release 条目**，不属于这个文件 —— 判断新旧请对上表的 sha256 与来源提交，',
    '同一份构建重新上传不会让时间前进，只有换指纹才说明内容变了。',
    '',
  ].join('\n');
}

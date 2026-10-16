#!/usr/bin/env node
// 账本快照导出：把开发账本（决策 / 教训 / 任务板 / 交接与判据文档）从「只存在本机」的位置
// 复制进仓库的 ledger/ 目录，使项目记忆随 git 一起持久化。
//
// 为什么需要它：`.gitignore:33` 忽略 `.dsh-code/`，git 跟踪 0 个文件 —— 132KB decisions.md、
// 163KB mistakes.md、3.6MB journal、44KB 任务板、全部交接文档都只在这台机器上，一次磁盘故障
// 或误删就全没了。而 `docs/` 是**规格**（冻结契约），不是**状态**；两者不能混。
//
// 用法：
//   node tools/ledger-export.mjs                 # 导出到 <repo>/ledger/
//   node tools/ledger-export.mjs --out <dir>     # 换目标目录
//   node tools/ledger-export.mjs --journal-full  # 连整份 journal.jsonl 一起导出
//
// 幂等：重复运行覆盖同名文件。只读账本，不修改它。
import { existsSync, mkdirSync, copyFileSync, readdirSync, statSync, writeFileSync, readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { homedir } from 'node:os';
import { join, resolve, basename } from 'node:path';

const argv = process.argv.slice(2);
const outIdx = argv.indexOf('--out');
const JSON_FILES = ['project.json', 'tasks.json'];
const MD_FILES = ['decisions.md', 'mistakes.md'];
// 交接 / 判据 / 会话摘要类文档：名字带这些前缀的一律带走（HANDOFF-*、session-digest-*、
// M1-*、M2-*、RFC-*、RESOLUTION-*、*-VERIFY.md、*-contract-frozen.md …）。
const MD_PREFIXES = ['HANDOFF', 'session-digest', 'M1-', 'M2-', 'RFC-', 'RESOLUTION', 'M3-', 'M4-'];
const MD_SUFFIXES = ['-contract-frozen.md', '-VERIFY.md', '-verify.md'];
const JOURNAL_TAIL_LINES = 500;

function resolveRepoRoot() {
  // 从本脚本位置向上找含 package.json 的目录。
  let dir = resolve(import.meta.dirname ?? process.cwd());
  for (let i = 0; i < 8; i++) {
    if (existsSync(join(dir, 'package.json')) && existsSync(join(dir, 'docs'))) return dir;
    const parent = resolve(dir, '..');
    if (parent === dir) break;
    dir = parent;
  }
  return resolve(process.cwd());
}

function resolveLedgerDir(repoRoot) {
  // 账本可能就在仓库里（`.dsh-code/`），也可能按 CENTRAL 指针迁到了本机单一存储。
  const local = join(repoRoot, '.dsh-code');
  const centralPointer = join(local, 'CENTRAL');
  const candidates = [];
  if (existsSync(centralPointer)) {
    try {
      const pointed = readFileSync(centralPointer, 'utf8').trim();
      if (pointed) candidates.push(pointed);
    } catch { /* 指针读不到就退回本地 */ }
  }
  candidates.push(local);
  const slug = basename(repoRoot).toLowerCase();
  candidates.push(join(homedir(), '.dsh', 'code', 'projects', slug));
  for (const c of candidates) {
    if (c && existsSync(join(c, 'decisions.md'))) return c;
  }
  return null;
}

function sha256(file) {
  return createHash('sha256').update(readFileSync(file)).digest('hex').slice(0, 16).toUpperCase();
}

const repoRoot = resolveRepoRoot();
const ledgerDir = resolveLedgerDir(repoRoot);
if (ledgerDir === null) {
  console.error('LEDGER-EXPORT-FAIL 找不到账本目录（试过 .dsh-code/、CENTRAL 指针、~/.dsh/code/projects/<slug>/）');
  process.exit(1);
}

const outDir = outIdx >= 0 ? resolve(argv[outIdx + 1]) : join(repoRoot, 'ledger');
mkdirSync(outDir, { recursive: true });

const picked = [];
const add = (name) => {
  const src = join(ledgerDir, name);
  if (!existsSync(src) || !statSync(src).isFile()) return;
  copyFileSync(src, join(outDir, name));
  picked.push({ name, size: statSync(join(outDir, name)).size, sha: sha256(join(outDir, name)) });
};

for (const f of JSON_FILES) add(f);
for (const f of MD_FILES) add(f);
for (const entry of readdirSync(ledgerDir, { withFileTypes: true })) {
  if (!entry.isFile() || !entry.name.endsWith('.md')) continue;
  if (MD_FILES.includes(entry.name)) continue;
  if (MD_PREFIXES.some((p) => entry.name.startsWith(p)) || MD_SUFFIXES.some((s) => entry.name.endsWith(s))) {
    add(entry.name);
  }
}

// journal：默认只带尾部若干行（3.6MB 的全文对 git 太重），--journal-full 时带全文。
const journal = join(ledgerDir, 'journal.jsonl');
let journalNote = '未导出（账本里没有 journal.jsonl）';
if (existsSync(journal)) {
  const text = readFileSync(journal, 'utf8');
  const lines = text.split('\n').filter((l) => l.trim().length > 0);
  if (argv.includes('--journal-full')) {
    writeFileSync(join(outDir, 'journal.jsonl'), text);
    picked.push({ name: 'journal.jsonl', size: statSync(join(outDir, 'journal.jsonl')).size, sha: sha256(join(outDir, 'journal.jsonl')) });
    journalNote = `整份导出（${lines.length} 条）`;
  } else {
    const tail = lines.slice(-JOURNAL_TAIL_LINES).join('\n') + '\n';
    writeFileSync(join(outDir, 'journal-tail.jsonl'), tail);
    picked.push({ name: 'journal-tail.jsonl', size: Buffer.byteLength(tail), sha: sha256(join(outDir, 'journal-tail.jsonl')) });
    journalNote = `只带尾部 ${Math.min(JOURNAL_TAIL_LINES, lines.length)}/${lines.length} 条（要全文用 --journal-full）`;
  }
}

let head = 'unknown';
try {
  head = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repoRoot, encoding: 'utf8' }).trim();
} catch { /* 非 git 仓库就留 unknown */ }

const stamp = new Date().toISOString();
const rows = picked.map((p) => `| \`${p.name}\` | ${p.size} B | \`${p.sha}\` |`).join('\n');
const readme = `# ledger/ — 开发账本的仓库内快照

**这不是规格，是状态。** 规格在 \`docs/\`（冻结契约、权威顺序 \`00-vision\` > \`01-principles\` >
\`02-glossary\` > \`40/41/43\`）；本目录只是把**只存在本机**的开发账本定期搬进 git，使决策与教训
不随一台机器一起消失。

- 导出时间：${stamp}
- 来源账本：\`${ledgerDir}\`
- 仓库 HEAD：\`${head}\`
- 导出命令：\`node tools/ledger-export.mjs\`（幂等，只读账本）；\`.githooks/pre-commit\` 会让**每次提交**都自动刷新本目录（非阻塞：导出失败也不挡提交）。
- 跨卷副本：\`node tools/ledger-export.mjs --backup\` 会把同一套文件另存到 \`%USERPROFILE%\\petdock-ledger-backup\\\`（提交钩子已默认开启；防的是项目目录被误删）。
- **没有远程仓库**：\`git remote -v\` 为空，所以提交与快照都只在这台机器上——防误删，防不了整盘/整机故障。要真正离机，需要建一个私有远程并 \`git push\`。
- journal：${journalNote}

| 文件 | 大小 | sha256(前16) |
|---|---|---|
${rows}

## 新会话怎么用这套东西

1. 先 \`code_brief\`：它读活的账本（\`.dsh-code/\` 或 CENTRAL 指向的本机存储），给出项目怎么跑、
   在飞的任务、已定的决策、已付过代价的坑。**只在本目录里找线索，说明活账本已经不在本机。**
2. 本目录的 \`decisions.md\` / \`mistakes.md\` 在活账本丢失时是**唯一**的记忆副本；\`project.json\`
   的 commands 是「这个项目怎么跑」的权威。
3. \`HANDOFF-*.md\` 与 \`session-digest-*.md\` 是历史会话的交接件，**其中的「下一步」是当时的状态，
   不是现在的指令**；现在的任务以 \`tasks.json\` / 团队看板为准。
4. 提交本目录时用 \`chore(ledger): …\` 前缀；它不参与任何机器门禁（\`docs/\` 的 doclink 不扫这里）。
`;

writeFileSync(join(outDir, 'README.md'), readme);

console.log(`LEDGER-EXPORT-OK out=${outDir} source=${ledgerDir} files=${picked.length} head=${head.slice(0, 8)}`);
for (const p of picked) console.log(`  ${p.name.padEnd(42)} ${String(p.size).padStart(9)} B  ${p.sha}`);
console.log(`  ${'README.md'.padEnd(42)} ${String(statSync(join(outDir, 'README.md')).size).padStart(9)} B  （本目录说明 + 清单）`);

// --backup：把同一套文件另存到用户主目录（跨卷），防「项目目录被整个删掉」这一种突发状况。
if (argv.includes('--backup')) {
  const backupDir = join(homedir(), 'petdock-ledger-backup');
  mkdirSync(backupDir, { recursive: true });
  let bytes = 0;
  for (const entry of readdirSync(outDir, { withFileTypes: true })) {
    if (!entry.isFile()) continue;
    copyFileSync(join(outDir, entry.name), join(backupDir, entry.name));
    bytes += statSync(join(backupDir, entry.name)).size;
  }
  console.log(`LEDGER-BACKUP-OK dir=${backupDir} bytes=${bytes}`);
}


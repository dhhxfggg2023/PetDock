// C# 源码里的字符串字面量扫描器（2026-10-31）。
//
// 为什么是共享库：两个静态门禁都要问「这段文字出现在哪些字面量里」——
//   tools/i18n-lint.mjs          问「字面量里有没有中文」（用户文案不许写死在源码里）
//   tools/i18n-reachability.mjs  问「这条文案键有没有被产品代码引用」
// 两处各写一份扫描器就会各自漂移：一份认得注释、另一份不认，于是同一条键在两个门禁里
// 得到相反结论（一个说「已引用」、一个说「写死的文案」），而没人知道该信哪一个。
//
// 为什么不用正则：C# 的注释（// /// /* */）、字符串形态（"..." @"..." $"..." $@"..."）、
// 字符字面量 'a' 与转义 \" 都会骗过朴素正则 —— 一句注释里的 "cp.market.title" 会被当成引用，
// 于是一条死键看起来「有人在用」。注释不是代码，扫描器必须先把注释丢掉。
//
// 只做这两件事，不做语法树：本仓库要判的就是「字面量内容」与「它右边是不是 +」，
// 一个逐字符状态机足够，也不需要 Roslyn。

const KEY_LIKE = /^[a-z][a-z0-9]*(?:\.[a-z0-9]+)*\.?$/;

/**
 * 扫描 C# 源码，返回其中的字符串字面量与它们的动态拼接信号。
 *
 * 每个元素：
 *   value    字面量的文本内容（不含引号；转义保持原样，不做反转义 —— 键里不会有转义）
 *   line     1 起的行号
 *   raw      原始形态（含引号与 @/$ 前缀），用于报错时让人一眼认出
 *   concat   这个字面量的右边紧跟一个 +（说明它被拿去和别的片段拼起来）
 *   interpolated  是 $"..." 形态（含 {} 洞），且洞在内容里出现过
 */
export function stringLiterals(source) {
  const found = [];
  const n = source.length;
  let i = 0;
  let line = 1;

  const push = (start, end, quoteEnd, startLine, meta) => {
    const value = source.slice(start, end);
    const after = source.slice(quoteEnd, quoteEnd + 8);
    found.push({
      value,
      line: startLine,
      raw: source.slice(Math.max(0, start - 2), Math.min(n, quoteEnd + 1)),
      concat: /^\s*\+/.test(after),
      interpolated: meta.interpolated && value.includes('{'),
    });
  };

  while (i < n) {
    const c = source[i];
    if (c === '\n') {
      line += 1;
      i += 1;
      continue;
    }
    if (c === '/' && source[i + 1] === '/') {
      while (i < n && source[i] !== '\n') i += 1;
      continue;
    }
    if (c === '/' && source[i + 1] === '*') {
      i += 2;
      while (i < n && !(source[i] === '*' && source[i + 1] === '/')) {
        if (source[i] === '\n') line += 1;
        i += 1;
      }
      i += 2;
      continue;
    }
    if (c === "'") {
      // 字符字面量：'a'、'\''、'\n' —— 里面不可能有文案键。
      i += 1;
      while (i < n && source[i] !== "'") {
        if (source[i] === '\\') i += 1;
        i += 1;
      }
      i += 1;
      continue;
    }

    const verbatim = c === '@' && source[i + 1] === '"';
    const interpPlain = c === '$' && source[i + 1] === '"';
    const interpVerbatim = c === '$' && source[i + 1] === '@' && source[i + 2] === '"';
    const plain = c === '"';

    if (verbatim || interpPlain || interpVerbatim || plain) {
      const interpolated = interpPlain || interpVerbatim;
      const startLine = line;
      let start;
      let cursor;
      if (verbatim) {
        start = i + 2;
        cursor = i + 2;
      } else if (interpVerbatim) {
        start = i + 3;
        cursor = i + 3;
      } else if (interpPlain) {
        start = i + 2;
        cursor = i + 2;
      } else {
        start = i + 1;
        cursor = i + 1;
      }

      while (cursor < n) {
        if (verbatim || interpVerbatim) {
          if (source[cursor] === '"') {
            if (source[cursor + 1] === '"') {
              cursor += 2;
              continue;
            }
            break;
          }
        } else {
          if (source[cursor] === '\\') {
            cursor += 2;
            continue;
          }
          if (source[cursor] === '"') break;
        }
        if (source[cursor] === '\n') line += 1;
        cursor += 1;
      }

      push(start, cursor, cursor + 1, startLine, { interpolated });
      i = cursor + 1;
      continue;
    }
    i += 1;
  }

  return found;
}

/** 字面量文本是不是「像文案键」的形态（用于判断前缀候选，不用于判断引用）。 */
export function looksLikeKey(value) {
  return value.length >= 6 && KEY_LIKE.test(value);
}

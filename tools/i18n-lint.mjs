#!/usr/bin/env node
// D-16 static layer: user-visible copy must live in the resource table, not in the source.
// RFC-42 4.1. Scans src/Ui and src/Host for CJK inside *string literals* (comments are not copy;
// a comment explaining why a key exists is fine, a literal the user reads is not).
// Exit 0 only when the scanned surface is clean; today it is red by design (the table lands in slice 3).

import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

const ROOT = process.cwd();
const CORE = join(ROOT, 'packages', 'core', 'PetDock.Core');
const SCAN = [join(CORE, 'src', 'Ui'), join(CORE, 'src', 'Host')];
const SKIP_FILE = /^Program[^/\\]*\.cs$/; // command-line stdout is a diagnostic surface, not the UI
const CJK = /[\u3000-\u303f\u3400-\u4dbf\u4e00-\u9fff\uff00-\uffef]/u;

function walk(dir, out) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const entry of entries) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) walk(full, out);
    else if (entry.name.endsWith('.cs')) out.push(full);
  }
  return out;
}

// Returns [{line, text}] for CJK inside string literals. A hand-rolled scanner is enough here:
// C# file-level trivia is /* */, ///, and //, and literals are "..." / @"..." / $"..." (with {{ }} escapes).
function literalsWithCjk(source) {
  const found = [];
  let line = 1;
  let i = 0;
  const n = source.length;
  const emit = (start, end) => {
    const text = source.slice(start, end);
    if (CJK.test(text)) {
      const at = source.slice(0, start).split('\n').length;
      found.push({ line: at, text: text.replace(/\s+/g, ' ').slice(0, 120) });
    }
  };
  while (i < n) {
    const c = source[i];
    if (c === '\n') {
      line++;
      i++;
      continue;
    }
    if (c === '/' && source[i + 1] === '/') {
      while (i < n && source[i] !== '\n') i++;
      continue;
    }
    if (c === '/' && source[i + 1] === '*') {
      i += 2;
      while (i < n && !(source[i] === '*' && source[i + 1] === '/')) {
        if (source[i] === '\n') line++;
        i++;
      }
      i += 2;
      continue;
    }
    if (c === '\'') {
      i++;
      while (i < n && source[i] !== '\'') {
        if (source[i] === '\\') i++;
        i++;
      }
      i++;
      continue;
    }
    const verbatim = c === '@' && source[i + 1] === '"';
    const interp = (c === '$' && source[i + 1] === '"') || (c === '$' && source[i + 1] === '@' && source[i + 2] === '"');
    if (verbatim || interp || c === '"') {
      let start;
      if (verbatim) {
        start = i + 2;
        i += 2;
      } else if (interp) {
        start = source[i + (source[i + 1] === '@' ? 2 : 1)] === '"' ? i + (source[i + 1] === '@' ? 3 : 2) : i + 1;
        i += source[i + 1] === '@' ? 3 : 2;
      } else {
        start = i + 1;
        i++;
      }
      while (i < n) {
        if (verbatim && source[i] === '"') {
          if (source[i + 1] === '"') {
            i += 2;
            continue;
          }
          break;
        }
        if (!verbatim && source[i] === '\\') {
          i += 2;
          continue;
        }
        if (!verbatim && source[i] === '"') break;
        if (source[i] === '\n') line++;
        i++;
      }
      emit(start, i);
      i++;
      continue;
    }
    i++;
  }
  return found;
}

const hits = [];
for (const dir of SCAN) {
  for (const file of walk(dir, [])) {
    if (SKIP_FILE.test(file.split(sep).pop())) continue;
    for (const hit of literalsWithCjk(readFileSync(file, 'utf8'))) {
      hits.push({ file: relative(ROOT, file).replaceAll(sep, '/'), ...hit });
    }
  }
}

if (hits.length === 0) {
  console.log('STAT i18n-lint files=0 literals=0');
  console.log('I18N-LINT-PASS');
  process.exit(0);
}

const byFile = new Map();
for (const hit of hits) byFile.set(hit.file, (byFile.get(hit.file) ?? 0) + 1);
for (const [file, count] of [...byFile].sort((a, b) => b[1] - a[1])) {
  console.log(`VIOLATION ${file} literals=${count}`);
  for (const hit of hits.filter((h) => h.file === file)) {
    console.log(`  ${file}:${hit.line}  ${hit.text}`);
  }
}
console.log(`STAT i18n-lint files=${byFile.size} literals=${hits.length}`);
console.log('I18N-LINT-FAIL move the text into assets/i18n/<lang>.json and read it by key (RFC-42 3.1)');
process.exit(1);

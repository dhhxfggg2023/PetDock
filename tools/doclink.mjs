// doclink.mjs - machine gate D-04b
// Spec: docs/40-development-paradigm.md section 3 (rule D-04b).
// Checks: (1) dangling docs/NN-xxx.md and research/NN-xxx.md references in
// docs/**/*.md and the root README.md; (2) stable-ID registration for the
// M / AM / N / P / D / R / SM namespaces against each namespace's registry.
// Zero npm dependencies, Node built-ins only. Run: node tools/doclink.mjs
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)));
const DOCS_DIR = join(ROOT, 'docs');
const README = join(ROOT, 'README.md');

// Registry of record per namespace, confirmed by grep against docs/
// (see tools/README.md for the grep evidence and the D-20 note).
const REGISTRIES = {
  M: ['docs/00-vision.md'],
  AM: ['docs/00-vision.md'],
  N: ['docs/00-vision.md'],
  P: ['docs/01-principles.md'],
  D: [
    'docs/40-development-paradigm.md',
    'docs/41-testing-strategy.md',
    'docs/43-rfc-and-adr.md',
    'docs/42-contribution-guide.md',
  ],
  SM: ['docs/10-architecture-overview.md'],
  R: ['docs/51-risk-register.md'],
};

const violations = [];

function walk(dir) {
  const out = [];
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const entry of entries) {
    if (entry.name.startsWith('.') || entry.name === 'node_modules') continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...walk(full));
    else if (entry.isFile()) out.push(full);
  }
  return out;
}

function relPath(p) {
  return relative(ROOT, p).split(sep).join('/');
}

function readText(file) {
  try {
    return readFileSync(file, 'utf8');
  } catch {
    return null;
  }
}

function collectRegistered() {
  const registered = new Set();
  for (const [ns, files] of Object.entries(REGISTRIES)) {
    for (const rel of files) {
      const src = readText(join(ROOT, rel));
      if (src === null) continue;
      const re = new RegExp(`\\b${ns}-(\\d{2})(?!\\d)`, 'g');
      let m;
      while ((m = re.exec(src)) !== null) registered.add(`${ns}-${m[1]}`);
    }
  }
  return registered;
}

const registered = collectRegistered();

function scanTargets() {
  const files = [];
  if (existsSync(DOCS_DIR)) {
    for (const f of walk(DOCS_DIR)) if (f.toLowerCase().endsWith('.md')) files.push(f);
  }
  if (existsSync(README)) files.push(README);
  return files;
}

for (const file of scanTargets()) {
  const src = readText(file);
  if (src === null) continue;
  const lines = src.split(/\r?\n/);
  for (let i = 0; i < lines.length; i += 1) {
    const line = lines[i];
    const lineNo = i + 1;
    // D-04b exemption: anything wrapped in strikethrough is skipped.
    const struckSpans = [...line.matchAll(/~~(.*?)~~/g)].map((m) => m[1]);
    const clean = line.replace(/~~.*?~~/g, ' ');
    const struckIds = new Set();
    for (const span of struckSpans) {
      for (const m of span.matchAll(/\b(AM|SM|M|P|D|R|N)-(\d{2})(?!\d)/g)) struckIds.add(m[0]);
    }
    // dangling file references
    for (const m of clean.matchAll(/(?:docs|research)\/\d{2}-[a-z0-9-]+\.md/g)) {
      const ref = m[0];
      if (!existsSync(join(ROOT, ref))) {
        violations.push(`VIOLATION D-04b ${relPath(file)}:${lineNo} dangling reference ${ref}`);
      }
    }
    // stable ID registration
    for (const m of clean.matchAll(/\b(AM|SM|M|P|D|R|N)-(\d{2})(?!\d)/g)) {
      const id = m[0];
      if (!registered.has(id) && !struckIds.has(id)) {
        violations.push(`VIOLATION D-04b ${relPath(file)}:${lineNo} unregistered ID ${id}`);
      }
    }
  }
}

for (const v of violations) console.log(v);
if (violations.length > 0) {
  console.log('DOCLINK-FAIL');
  process.exit(1);
}
console.log('DOCLINK-PASS');

// depscan.mjs - machine gates D-01a / D-01b / D-02a / D-02b / D-02c
// Spec: docs/40-development-paradigm.md sections 1-2 (rules D-01, D-02).
// Zero npm dependencies, Node built-ins only. Run: node tools/depscan.mjs
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { basename, dirname, extname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(fileURLToPath(new URL('..', import.meta.url)));
const PACKAGES_DIR = join(ROOT, 'packages');
const LAYERS = ['core', 'sdk', 'plugins'];
const RANK = { core: 0, sdk: 1, plugins: 2 };
// D-02d: a cross-layer skip edge (e.g. plugins -> core) is forbidden, but while
// the middle layer does not exist yet the only legal path is the direct one.
const SDK_EXISTS = existsSync(join(PACKAGES_DIR, 'sdk'));
const SKIP_DIRS = new Set(['node_modules', 'obj', 'bin']);
const JS_EXTS = new Set(['.js', '.mjs', '.cjs', '.ts', '.tsx', '.jsx', '.mts', '.cts']);
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
    if (entry.name.startsWith('.') || SKIP_DIRS.has(entry.name)) continue;
    const full = join(dir, entry.name);
    if (entry.isDirectory()) out.push(...walk(full));
    else if (entry.isFile()) out.push(full);
  }
  return out;
}

function relPath(p) {
  return relative(ROOT, p).split(sep).join('/');
}

function layerOf(absPath) {
  const rel = relative(PACKAGES_DIR, absPath);
  if (rel === '' || rel.startsWith('..')) return null;
  const head = rel.split(sep)[0];
  return LAYERS.includes(head) ? head : null;
}

function packageRootOf(absPath) {
  let dir = dirname(absPath);
  while (dir.startsWith(PACKAGES_DIR + sep)) {
    if (existsSync(join(dir, 'package.json'))) return dir;
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  const layer = layerOf(absPath);
  return layer ? join(PACKAGES_DIR, layer) : dirname(absPath);
}

function isTestPath(absPath) {
  const rel = relative(PACKAGES_DIR, absPath).split(sep);
  return rel.some((s) => s.toLowerCase() === 'test' || s.toLowerCase() === 'tests');
}

function stripComments(src) {
  return src
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/(^|[^:])\/\/[^\n]*/g, '$1');
}

function extractSpecifiers(src) {
  const clean = stripComments(src);
  const found = [];
  const patterns = [
    /(?<![.\w$])(?:import|export)\s+[^'";]*?\bfrom\s*['"]([^'"]+)['"]/g,
    /(?<![.\w$])(?:import|export)\s*['"]([^'"]+)['"]/g,
    /(?<![.\w$])import\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
    /(?<![.\w$])require\s*\(\s*['"]([^'"]+)['"]\s*\)/g,
  ];
  for (const re of patterns) {
    let m;
    while ((m = re.exec(clean)) !== null) found.push(m[1]);
  }
  return found;
}

function collectPackages() {
  const pkgs = [];
  if (!existsSync(PACKAGES_DIR)) return pkgs;
  for (const layer of LAYERS) {
    const dir = join(PACKAGES_DIR, layer);
    if (!existsSync(dir)) continue;
    for (const file of walk(dir)) {
      if (basename(file) !== 'package.json') continue;
      let pkg;
      try {
        pkg = JSON.parse(readFileSync(file, 'utf8'));
      } catch {
        continue;
      }
      const deps = {};
      for (const key of ['dependencies', 'devDependencies']) {
        const section = pkg[key];
        if (section && typeof section === 'object') Object.assign(deps, section);
      }
      pkgs.push({
        layer,
        file,
        name: typeof pkg.name === 'string' ? pkg.name : null,
        deps,
      });
    }
  }
  return pkgs;
}

// Returns the rule id for a bad edge, or null when the edge is allowed.
// core/test -> sdk is the sanctioned stub-registration path (P-03 item 4,
// D-02c: only core/test -> plugins is called out as a failure).
// A cross-layer skip edge (docs/40 section 2: plugins -> sdk -> core, no
// skipping) is D-02d. It must be tested before the reverse-edge early
// return below: plugins -> core is both a lower-rank target (would return
// null there) and a layer skip, which is the blind spot this rule closes.
// The plugins -> core edge is exempted only while packages/sdk does not
// exist, so the exemption self-expires when the middle layer is created.
function edgeKind(fromFile, fromLayer, toLayer) {
  if (RANK[fromLayer] - RANK[toLayer] > 1) {
    if (fromLayer === 'plugins' && toLayer === 'core' && !SDK_EXISTS) return null;
    return 'D-02d';
  }
  if (RANK[toLayer] <= RANK[fromLayer]) return null;
  if (fromLayer === 'core' && isTestPath(fromFile)) {
    if (toLayer === 'plugins') return 'D-02c';
    if (toLayer === 'sdk') return null;
  }
  return 'D-02b';
}

// D-01b: only core / sdk / plugins may sit directly under packages/
if (existsSync(PACKAGES_DIR)) {
  for (const entry of readdirSync(PACKAGES_DIR, { withFileTypes: true })) {
    if (entry.isDirectory() && !LAYERS.includes(entry.name)) {
      violations.push(
        `VIOLATION D-01b packages/ top-level directory '${entry.name}' is not one of core/sdk/plugins`
      );
    }
  }
}

// D-02a: package.json dependency boundary
const pkgs = collectPackages();
const nameToLayer = new Map();
for (const p of pkgs) if (p.name) nameToLayer.set(p.name, p.layer);
for (const p of pkgs) {
  for (const dep of Object.keys(p.deps)) {
    const depLayer = nameToLayer.get(dep);
    if (depLayer && RANK[depLayer] > RANK[p.layer]) {
      violations.push(
        `VIOLATION D-02a ${relPath(p.file)} declares upper-layer dependency '${dep}' (layer ${depLayer})`
      );
    }
  }
}

// D-01a / D-02b / D-02c: import graph across layers
for (const layer of LAYERS) {
  const dir = join(PACKAGES_DIR, layer);
  if (!existsSync(dir)) continue;
  for (const file of walk(dir)) {
    if (!JS_EXTS.has(extname(file).toLowerCase())) continue;
    let src;
    try {
      src = readFileSync(file, 'utf8');
    } catch {
      continue;
    }
    const fromPkg = packageRootOf(file);
    for (const spec of extractSpecifiers(src)) {
      const relFile = relPath(file);
      if (spec.startsWith('.')) {
        const target = resolve(dirname(file), spec);
        const toLayer = layerOf(target);
        if (toLayer === null) {
          violations.push(
            `VIOLATION D-01a ${relFile} relative import '${spec}' resolves outside packages/`
          );
          continue;
        }
        if (packageRootOf(target) !== fromPkg) {
          violations.push(
            `VIOLATION D-01a ${relFile} bare cross-package relative import '${spec}' -> ${relPath(target)}`
          );
        }
        const kind = edgeKind(file, layer, toLayer);
        if (kind) {
          violations.push(
            `VIOLATION ${kind} ${relFile} ${layer} -> ${toLayer} via relative import '${spec}'`
          );
        }
      } else {
        let toLayer = null;
        for (const [name, l] of nameToLayer) {
          if (spec === name || spec.startsWith(name + '/')) {
            toLayer = l;
            break;
          }
        }
        if (toLayer === null) continue;
        const kind = edgeKind(file, layer, toLayer);
        if (kind) {
          violations.push(
            `VIOLATION ${kind} ${relFile} ${layer} -> ${toLayer} via package import '${spec}'`
          );
        }
      }
    }
  }
}

// D-02b / D-02d for C#: a project reference may not point at an upper layer,
// and may not skip a layer. Every layer is scanned, not just core.
let csprojCount = 0;
for (const layer of LAYERS) {
  const layerDir = join(PACKAGES_DIR, layer);
  if (!existsSync(layerDir)) continue;
  for (const file of walk(layerDir)) {
    if (!file.toLowerCase().endsWith('.csproj')) continue;
    csprojCount += 1;
    let src;
    try {
      src = readFileSync(file, 'utf8');
    } catch {
      continue;
    }
    const re = /<ProjectReference\b[^>]*\bInclude="([^"]+)"/g;
    let m;
    while ((m = re.exec(src)) !== null) {
      const target = resolve(dirname(file), m[1]);
      const toLayer = layerOf(target);
      if (toLayer === null) {
        violations.push(
          `VIOLATION D-02b ${relPath(file)} ProjectReference '${m[1]}' escapes packages/${layer}`
        );
        continue;
      }
      const kind = edgeKind(file, layer, toLayer);
      if (kind) {
        violations.push(
          `VIOLATION ${kind} ${relPath(file)} ${layer} -> ${toLayer} via ProjectReference '${m[1]}'`
        );
      }
    }
  }
}
console.log(`STAT csproj-files ${csprojCount}`);

for (const v of violations) console.log(v);
if (violations.length > 0) {
  console.log('DEPSCAN-FAIL');
  process.exit(1);
}
console.log('DEPSCAN-PASS');

// package.json 与内核 csproj 的版本一致性（2026-10-28）。
//
// 版本号的唯一事实源是 package.json（tools/release.mjs 已经这么用），但 .NET 程序集的
// AssemblyVersion/ProductVersion 只认 csproj。两处各写一个数字必然漂移，漂移的表现是
// 「设置页关于写着 v1.0.0，发布包叫 0.1.0」——用户看不出来哪个是真的。
// 这里不生成文件（生成会让构建依赖 Node），只做门禁：不一致就红。
import fs from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
const csprojPath = path.join(root, 'packages', 'core', 'PetDock.Core', 'PetDock.Core.csproj');
const csproj = fs.readFileSync(csprojPath, 'utf8');
const match = csproj.match(/<Version>([^<]+)<\/Version>/);
const declared = match?.[1]?.trim();

if (!pkg.version || !/^\d+\.\d+\.\d+$/.test(pkg.version)) {
  console.log(`VERSION-PARITY-FAIL package.json version 不是 x.y.z：${pkg.version ?? '(none)'}`);
  process.exit(1);
}

if (declared !== pkg.version) {
  console.log(`VERSION-PARITY-FAIL package.json=${pkg.version} csproj=${declared ?? '(none)'}`);
  process.exit(1);
}

console.log(`VERSION-PARITY-PASS version=${pkg.version}`);

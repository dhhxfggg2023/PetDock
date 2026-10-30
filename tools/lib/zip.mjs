// tools/lib/zip.mjs —— 确定性 zip 写入器（2026-10-30）。
//
// 为什么自己写而不是调 tar.exe：tar 会把**每个条目的文件时间戳**写进 zip。
// 同一个提交、内容逐字节相同、只是构建时刻不同 → 两个 zip 的 sha256 不同。
// 于是「Release 上的包 == 本地最新构建」这条判据永远说不清是产物变了还是时间戳变了
// （2026-10-30 verify-release 上线第一天就撞到：104 个文件内容零差异，zip 哈希不同）。
//
// 压缩用 deflate 且**固定级别 9**：zlib 对同样的输入与参数是确定性的（不带时间戳、不带随机），
// 所以「同样的输入 → 同样的字节」仍然成立，同时把发行包维持在 6~7 MB 量级。
// 不用 stored（不压缩）：那虽然最确定，但会把 6.4 MB 的包撑到 17.8 MB —— 下载体积是给用户的，
// 不该为了内部判据的方便去牺牲它。
//
// 不写时间戳（固定 1980-01-01）、不写权限位、条目按路径排序、UTF-8 标志位固定。
import { createHash } from 'node:crypto';
import zlib from 'node:zlib';
import fs from 'node:fs';
import path from 'node:path';

const LOCAL = 0x04034b50;
const CENTRAL = 0x02014b50;
const END = 0x06054b50;

/** 固定压缩级别。改动它会改变所有 zip 的 sha256，因此当作格式的一部分。 */
const DEFLATE_LEVEL = 9;

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n += 1) {
    let c = n;
    for (let k = 0; k < 8; k += 1) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c;
  }
  return table;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i += 1) {
    c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  }
  return (c ^ 0xffffffff) >>> 0;
}

/**
 * 收集一个目录下的全部文件（相对路径用正斜杠）。
 * prefix 是 zip 内的顶层目录名（例如 'PetDock-0.1.0'），空串表示文件放在 zip 根。
 */
export function collectFiles(root, prefix = '') {
  const out = [];
  const walk = (dir, rel) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      const next = rel ? `${rel}/${entry.name}` : entry.name;
      if (entry.isDirectory()) {
        walk(full, next);
      } else {
        out.push({ rel: next, full });
      }
    }
  };
  walk(root, prefix);
  out.sort((a, b) => a.rel.localeCompare(b.rel));
  return out;
}

/** 把 [{rel, full}] 写成一个确定性 zip。返回 { buffer, sha256, bytes }。 */
export function writeDeterministicZip(files) {
  const chunks = [];
  const central = [];
  let offset = 0;

  for (const file of files) {
    const data = fs.readFileSync(file.full);
    const nameBuf = Buffer.from(file.rel, 'utf8');
    const crc = crc32(data);
    const packed = zlib.deflateRawSync(data, { level: DEFLATE_LEVEL });

    const local = Buffer.alloc(30);
    local.writeUInt32LE(LOCAL, 0);
    local.writeUInt16LE(20, 4); // version needed
    local.writeUInt16LE(0x0800, 6); // UTF-8 名称
    local.writeUInt16LE(8, 8); // deflate
    local.writeUInt16LE(0, 10); // time = 0
    local.writeUInt16LE(0x0021, 12); // date = 1980-01-01（固定）
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(packed.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBuf.length, 26);
    local.writeUInt16LE(0, 28);

    chunks.push(local, nameBuf, packed);

    const cen = Buffer.alloc(46);
    cen.writeUInt32LE(CENTRAL, 0);
    cen.writeUInt16LE(20, 4);
    cen.writeUInt16LE(20, 6);
    cen.writeUInt16LE(0x0800, 8);
    cen.writeUInt16LE(0, 10);
    cen.writeUInt16LE(0, 12);
    cen.writeUInt16LE(0x0021, 14);
    cen.writeUInt32LE(crc, 16);
    cen.writeUInt32LE(packed.length, 20);
    cen.writeUInt32LE(data.length, 24);
    cen.writeUInt16LE(nameBuf.length, 28);
    cen.writeUInt32LE(offset, 42);
    central.push(Buffer.concat([cen, nameBuf]));

    offset += local.length + nameBuf.length + packed.length;
  }

  const centralBuf = Buffer.concat(central);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(END, 0);
  end.writeUInt16LE(files.length, 8);
  end.writeUInt16LE(files.length, 10);
  end.writeUInt32LE(centralBuf.length, 12);
  end.writeUInt32LE(offset, 16);

  const buffer = Buffer.concat([...chunks, centralBuf, end]);
  return {
    buffer,
    sha256: createHash('sha256').update(buffer).digest('hex'),
    bytes: buffer.length,
  };
}

/** 便捷：目录 → 确定性 zip 文件。 */
export function zipDirectory(root, zipPath, prefix = '') {
  const files = collectFiles(root, prefix);
  const { buffer, sha256, bytes } = writeDeterministicZip(files);
  fs.mkdirSync(path.dirname(zipPath), { recursive: true });
  fs.writeFileSync(zipPath, buffer);
  return { files: files.length, sha256, bytes };
}

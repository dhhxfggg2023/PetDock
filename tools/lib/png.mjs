// tools/lib/png.mjs —— 程序化素材共用的 PNG 编码与画布原语。
//
// 为什么单独成模块：`tools/gen-sample-pet.mjs`（docs/31 示例宠物）与
// `tools/gen-bongocat-pack.mjs`（docs/33 BongoCat）都要「自己编码真正的 PNG」。
// 两份拷贝会各自漂移，而这类代码漂移的表现是「文件存在、退出码 0、解码器拒绝」——
// 正是 gen-sample-pet 文件头警告的那种沉默失败。所以编码器只有这一份。
//
// 关键点（沿用 gen-sample-pet 的原始约定）：自己写签名 + IHDR + IDAT + IEND + CRC32，
// 而不是把裸 RGBA 缓冲写进一个 .png 文件名里。
import zlib from 'node:zlib'

// ---------- PNG 编码 ----------

const CRC_TABLE = (() => {
  const table = new Int32Array(256)
  for (let n = 0; n < 256; n++) {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    table[n] = c
  }
  return table
})()

function crc32(buf) {
  let c = 0xffffffff
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}

function chunk(type, data) {
  const out = Buffer.alloc(12 + data.length)
  out.writeUInt32BE(data.length, 0)
  out.write(type, 4, 'ascii')
  data.copy(out, 8)
  out.writeUInt32BE(crc32(Buffer.concat([Buffer.from(type, 'ascii'), data])), 8 + data.length)
  return out
}

/** RGBA 缓冲（每像素 4 字节）→ 真 PNG 字节流。 */
export function encodePng(rgba, width, height) {
  const raw = Buffer.alloc(height * (width * 4 + 1))
  for (let y = 0; y < height; y++) {
    raw[y * (width * 4 + 1)] = 0 // 每行的过滤器字节：0 = None
    rgba.copy(raw, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4)
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(width, 0)
  ihdr.writeUInt32BE(height, 4)
  ihdr[8] = 8 // 位深
  ihdr[9] = 6 // 颜色类型 6 = RGBA
  ihdr[10] = 0 // 压缩方法
  ihdr[11] = 0 // 过滤方法
  ihdr[12] = 0 // 非隔行
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

// ---------- 画布原语 ----------

export function canvas(width, height) {
  return { w: width, h: height, px: Buffer.alloc(width * height * 4, 0) }
}

export function blend(c, x, y, rgb, alpha) {
  if (alpha <= 0 || x < 0 || y < 0 || x >= c.w || y >= c.h) return
  const i = (y * c.w + x) * 4
  const a = Math.min(1, alpha)
  const dstA = c.px[i + 3] / 255
  const outA = a + dstA * (1 - a)
  if (outA <= 0) return
  for (let k = 0; k < 3; k++) {
    c.px[i + k] = Math.round((rgb[k] * a + c.px[i + k] * dstA * (1 - a)) / outA)
  }
  c.px[i + 3] = Math.round(outA * 255)
}

/** 椭圆填充；边缘 1px 内做覆盖率抗锯齿，保证形状平滑且结果确定。 */
export function ellipse(c, cx, cy, rx, ry, rgb, alpha = 1) {
  const x0 = Math.floor(cx - rx - 2)
  const x1 = Math.ceil(cx + rx + 2)
  const y0 = Math.floor(cy - ry - 2)
  const y1 = Math.ceil(cy + ry + 2)
  for (let y = y0; y <= y1; y++) {
    for (let x = x0; x <= x1; x++) {
      const dx = (x + 0.5 - cx) / rx
      const dy = (y + 0.5 - cy) / ry
      const d = Math.sqrt(dx * dx + dy * dy)
      const coverage = d <= 1 ? 1 : d >= 1 + 1 / Math.min(rx, ry) ? 0 : (1 + 1 / Math.min(rx, ry) - d) * Math.min(rx, ry)
      if (coverage > 0) blend(c, x, y, rgb, coverage * alpha)
    }
  }
}

export function circle(c, cx, cy, r, rgb, alpha = 1) {
  ellipse(c, cx, cy, r, r, rgb, alpha)
}

/**
 * 三角形填充（猫耳这类多边形）。用 2×2 超采样做覆盖率抗锯齿：
 * 与 ellipse 的解析覆盖率不同，三角形没有简单闭式，超采样在 128px 尺度上足够且仍是确定性的。
 */
export function triangle(c, ax, ay, bx, by, dx, dy, rgb, alpha = 1) {
  const minX = Math.floor(Math.min(ax, bx, dx)) - 1
  const maxX = Math.ceil(Math.max(ax, bx, dx)) + 1
  const minY = Math.floor(Math.min(ay, by, dy)) - 1
  const maxY = Math.ceil(Math.max(ay, by, dy)) + 1

  const edge = (px, py, qx, qy, rx, ry) => (qx - px) * (ry - py) - (qy - py) * (rx - px)
  const total = edge(ax, ay, bx, by, dx, dy)
  if (total === 0) return
  const sign = Math.sign(total)

  for (let y = minY; y <= maxY; y++) {
    for (let x = minX; x <= maxX; x++) {
      let hits = 0
      for (const oy of [0.25, 0.75]) {
        for (const ox of [0.25, 0.75]) {
          const px = x + ox
          const py = y + oy
          const w0 = edge(bx, by, dx, dy, px, py) * sign
          const w1 = edge(dx, dy, ax, ay, px, py) * sign
          const w2 = edge(ax, ay, bx, by, px, py) * sign
          if (w0 >= 0 && w1 >= 0 && w2 >= 0) hits++
        }
      }
      if (hits > 0) blend(c, x, y, rgb, (hits / 4) * alpha)
    }
  }
}

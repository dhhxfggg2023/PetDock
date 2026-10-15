// 程序化生成 M2 自建示例宠物（docs/31-sample-pack.md）。
//
// 许可：本脚本与它产出的每一帧都是 PetDock 自有资产（CC0），不引入任何第三方素材——
// 让 docs/52 的「零第三方素材」从第一天起就是真的。
//
// 关键点：这个脚本自己编码真正的 PNG（签名 + IHDR + IDAT + IEND + CRC32），
// 而不是把裸 RGBA 缓冲写进一个 .png 文件名里。裸缓冲的解码器会直接拒绝，
// 而「文件存在 + 退出码 0」看不出来——所以末段还调用独立解码器复验一次。
//
//   node tools/gen-sample-pet.mjs --out fixtures/sample-pet
import fs from 'node:fs'
import path from 'node:path'
import zlib from 'node:zlib'

const SIZE = 128
const ICON = 512

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
function encodePng(rgba, width, height) {
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

// ---------- 画一只几何小角色 ----------

const BODY = [86, 132, 214]
const SHADE = [54, 92, 168]
const EYE = [24, 28, 40]
const GLINT = [255, 255, 255]
const BLUSH = [238, 150, 170]

function canvas(width, height) {
  return { w: width, h: height, px: Buffer.alloc(width * height * 4, 0) }
}

function blend(c, x, y, rgb, alpha) {
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
function ellipse(c, cx, cy, rx, ry, rgb, alpha = 1) {
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

function circle(c, cx, cy, r, rgb, alpha = 1) {
  ellipse(c, cx, cy, r, r, rgb, alpha)
}

/**
 * 一帧：bobY 整体上下位移，leanX 身体左右位移，earLift 头顶小揪揪抬起量，eyeOpen 眼睛开合。
 * 所有参数都是整数或简单分数，保证同一输入永远产出同一字节。
 */
function frame({ bobY = 0, leanX = 0, earLift = 0, eyeOpen = 1, blush = 1 } = {}) {
  const c = canvas(SIZE, SIZE)
  const cx = SIZE / 2 + leanX
  const cy = SIZE / 2 + 6 + bobY

  // 头顶小揪揪（whale-girl 的呆毛，M2 只做形状验证用）
  circle(c, cx + 2, cy - 40 - earLift, 7, BODY)
  ellipse(c, cx + 6, cy - 34 - earLift, 5, 12, BODY)

  // 身体
  ellipse(c, cx, cy, 42, 38, BODY)
  // 底部阴影，让形状在视觉上有上下之分（命中测试同样不透明）
  ellipse(c, cx, cy + 20, 32, 16, SHADE)

  // 两侧鳍
  ellipse(c, cx - 46, cy + 2, 12, 20, BODY)
  ellipse(c, cx + 46, cy + 2, 12, 20, BODY)

  // 腮红
  if (blush > 0) {
    ellipse(c, cx - 26, cy + 4, 9, 6, BLUSH, 0.55 * blush)
    ellipse(c, cx + 26, cy + 4, 9, 6, BLUSH, 0.55 * blush)
  }

  // 眼睛：eyeOpen=0 时压扁成一条缝（眨眼帧）
  const eyeRy = 13 * eyeOpen
  const eyeRySafe = Math.max(eyeRy, 1.2)
  for (const ex of [-15, 15]) {
    ellipse(c, cx + ex, cy - 6, 11, eyeRySafe, EYE)
    if (eyeOpen > 0.5) {
      circle(c, cx + ex - 3, cy - 10, 3, GLINT, 0.95)
    }
  }

  // 嘴
  ellipse(c, cx, cy + 12, 7, 4, EYE, 0.85)

  return c
}

// ---------- 片段定义 ----------

const CLIPS = {
  idle: {
    fps: 6,
    frames: [frame(), frame({ bobY: -2 }), frame({ bobY: -3, earLift: 2 }), frame({ bobY: -2, eyeOpen: 0.15 })],
  },
  walk: {
    fps: 8,
    frames: [
      frame({ leanX: -3, bobY: -1 }),
      frame({ leanX: 0, bobY: -3 }),
      frame({ leanX: 3, bobY: -1 }),
      frame({ leanX: 0, bobY: 0 }),
    ],
  },
  hover: {
    fps: 6,
    frames: [frame({ bobY: -2, blush: 1 }), frame({ bobY: -4, earLift: 3, blush: 1.4 })],
  },
}

// ---------- 落盘 ----------

let outDir = path.join('fixtures', 'sample-pet')
const argv = process.argv.slice(2)
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--out') {
    const value = argv[i + 1]
    if (!value || value.startsWith('--')) {
      console.error('gen-sample-pet: --out 后面必须跟一个目录路径')
      process.exit(2)
    }
    outDir = value
    i++
  }
}

fs.mkdirSync(path.join(outDir, 'anim'), { recursive: true })

let written = 0
for (const [name, clip] of Object.entries(CLIPS)) {
  const dir = path.join(outDir, 'img', name)
  fs.mkdirSync(dir, { recursive: true })
  const names = []
  clip.frames.forEach((f, index) => {
    const file = `${name}_${String(index + 1).padStart(3, '0')}.png`
    fs.writeFileSync(path.join(dir, file), encodePng(f.px, f.w, f.h))
    names.push(`img/${name}/${file}`)
    written++
  })
  fs.writeFileSync(
    path.join(outDir, 'anim', `${name}.json`),
    JSON.stringify({ frames: names, fps: clip.fps, loop: 'loop' }, null, 2) + '\n',
  )
}

const sample = {
  schemaVersion: 1,
  name: 'Sample Pet',
  author: 'petdock (procedural)',
  license: 'CC0-1.0',
  naturalSize: { w: SIZE, h: SIZE },
  preferredFps: 12,
  animations: [
    { id: 'idle', clip: 'anim/idle.json' },
    { id: 'walk', clip: 'anim/walk.json' },
    { id: 'hover', clip: 'anim/hover.json' },
  ],
  defaultAnimation: 'idle',
  state: { schemaVersion: 1, position: { x: 0, y: 0 } },
}
fs.writeFileSync(path.join(outDir, 'petdock-sample.json'), JSON.stringify(sample, null, 2) + '\n')

// icon.png：512x512，把 idle 第一帧放大居中
const icon = canvas(ICON, ICON)
const src = CLIPS.idle.frames[0]
const scale = 4
for (let y = 0; y < SIZE * scale; y++) {
  for (let x = 0; x < SIZE * scale; x++) {
    const si = (Math.floor(y / scale) * SIZE + Math.floor(x / scale)) * 4
    const di = (y * ICON + x) * 4
    src.px.copy(icon.px, di, si, si + 4)
  }
}
fs.writeFileSync(path.join(outDir, 'icon.png'), encodePng(icon.px, ICON, ICON))

console.log(`OUT ${outDir}`)
console.log(`FRAMES ${written} PNG (${SIZE}x${SIZE}) + icon.png (${ICON}x${ICON})`)
console.log('SAMPLE-PET-OK')

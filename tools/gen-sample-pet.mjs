// 程序化生成 M2 自建示例宠物（docs/31-sample-pack.md）。
//
// 许可：本脚本与它产出的每一帧都是 PetDock 自有资产（CC0），不引入任何第三方素材——
// 让 docs/52 的「零第三方素材」从第一天起就是真的。
//
// 关键点：这个脚本自己编码真正的 PNG（签名 + IHDR + IDAT + IEND + CRC32），
// 而不是把裸 RGBA 缓冲写进一个 .png 文件名里。裸缓冲的解码器会直接拒绝，
// 而「文件存在 + 退出码 0」看不出来——所以末段还调用独立解码器复验一次。
//
// 编码器与画布原语已抽到 `tools/lib/png.mjs`（BongoCat 素材生成器共用同一份）。
// 抽出的**没有改字节**：本脚本的产物仍与抽出前逐字节相同（assets/ASSETS.md §1 记「已验证幂等」）。
//
//   node tools/gen-sample-pet.mjs --out assets/sample-pet
import fs from 'node:fs'
import path from 'node:path'
import { canvas, circle, ellipse, encodePng } from './lib/png.mjs'

const SIZE = 128
const ICON = 512

// ---------- 画一只几何小角色 ----------

const BODY = [86, 132, 214]
const SHADE = [54, 92, 168]
const EYE = [24, 28, 40]
const GLINT = [255, 255, 255]
const BLUSH = [238, 150, 170]

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

let outDir = path.join('assets', 'sample-pet')
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

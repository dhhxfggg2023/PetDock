// 程序化生成 BongoCat 交互包的素材（docs/33-bongocat.md）。
//
// 许可：本脚本与它产出的每一帧都是 PetDock 自有资产（CC0）。docs/33 §6 的三条法律约束
// 在这里是代码事实而不是承诺：
//   1. 不引用、不打包、不逆向原 BongoCat 应用的任何素材 —— 本文件不读任何外部文件；
//   2. 造型是**自绘的几何猫**（耳朵=三角形、头=椭圆、爪=圆角块），与原应用那只手绘猫无关；
//   3. 包名不冒充官方（manifest 的 id 是 com.petdock.bongocat.interaction，作者是 petdock）。
//
// 输出形态刻意用 docs/31 Sample Pack 格式（petdock-sample.json + anim/*.json + img/*.png）：
// 这样现成的 sprite-seq 呈现层直接能渲染，不需要内核认识第二种素材格式
// （docs/01 P-02 第 2 条：core 不认识任何素材格式）。
//
//   node tools/gen-bongocat-pack.mjs --out assets/bongocat-pack
import fs from 'node:fs'
import path from 'node:path'
import { canvas, circle, ellipse, encodePng, triangle } from './lib/png.mjs'

const SIZE = 128
const ICON = 512

// ---------- 配色（自选，与原应用无关） ----------

const FUR = [246, 222, 196]
const FUR_SHADE = [226, 194, 162]
const EAR_IN = [240, 172, 176]
const OUTLINE = [72, 56, 52]
const EYE = [46, 40, 40]
const GLINT = [255, 255, 255]
const NOSE = [232, 140, 148]
const PAW = [250, 232, 212]
const PAW_SHADE = [228, 200, 176]

/**
 * 一只猫头 + 一对前爪。
 *
 * @param {object} o
 * @param {number} o.gazeX   瞳孔横向偏移（-1..1，按朝向）
 * @param {number} o.gazeY   瞳孔纵向偏移（-1..1）
 * @param {number} o.headX   头整体横向位移（朝某侧时整颗头偏一点，比只动瞳孔像"看"）
 * @param {number} o.headY   头整体纵向位移
 * @param {number} o.bobY    呼吸起伏
 * @param {number} o.earTilt 耳朵倾角（朝左右时一侧耳朵压低）
 * @param {number} o.eyeOpen 眼睛开合（1 = 全开，0.1 = 眨眼）
 * @param {'none'|'left'|'right'} o.paw 哪只爪子抬起敲一下
 * @param {number} o.pawLift 抬爪高度
 */
function cat({
  gazeX = 0,
  gazeY = 0,
  headX = 0,
  headY = 0,
  bobY = 0,
  earTilt = 0,
  eyeOpen = 1,
  paw = 'none',
  pawLift = 0,
} = {}) {
  const c = canvas(SIZE, SIZE)
  const cx = SIZE / 2 + headX
  const cy = SIZE / 2 - 10 + headY + bobY

  // —— 身体（头下方一块，让抬爪有落点）——
  ellipse(c, SIZE / 2, cy + 46, 30, 20, FUR_SHADE)

  // —— 前爪：先画爪子再画头，头压住爪根 ——
  const pawBaseY = cy + 40
  const leftPawY = pawBaseY - (paw === 'left' ? pawLift : 0)
  const rightPawY = pawBaseY - (paw === 'right' ? pawLift : 0)
  ellipse(c, SIZE / 2 - 26, leftPawY, 13, 11, PAW)
  ellipse(c, SIZE / 2 + 26, rightPawY, 13, 11, PAW)
  // 爪子上的三道趾缝：抬起来的那只才画，落回去就看不见
  if (paw === 'left') {
    for (const dx of [-4, 0, 4]) {
      ellipse(c, SIZE / 2 - 26 + dx, leftPawY - 6, 0.8, 4, PAW_SHADE, 0.9)
    }
  } else if (paw === 'right') {
    for (const dx of [-4, 0, 4]) {
      ellipse(c, SIZE / 2 + 26 + dx, rightPawY - 6, 0.8, 4, PAW_SHADE, 0.9)
    }
  }

  // —— 耳朵：两个三角形，朝左右时一侧压低 ——
  const earLeftLift = earTilt > 0 ? -3 : 0
  const earRightLift = earTilt < 0 ? -3 : 0
  triangle(c, cx - 30, cy - 24 + earLeftLift, cx - 12, cy - 44 + earLeftLift, cx - 6, cy - 20, FUR)
  triangle(c, cx + 30, cy - 24 + earRightLift, cx + 12, cy - 44 + earRightLift, cx + 6, cy - 20, FUR)
  triangle(c, cx - 25, cy - 25 + earLeftLift, cx - 14, cy - 38 + earLeftLift, cx - 10, cy - 23, EAR_IN, 0.85)
  triangle(c, cx + 25, cy - 25 + earRightLift, cx + 14, cy - 38 + earRightLift, cx + 10, cy - 23, EAR_IN, 0.85)

  // —— 头 ——
  ellipse(c, cx, cy, 34, 30, FUR)
  // 下巴阴影：让"头"和"身体"在视觉上分开
  ellipse(c, cx, cy + 16, 22, 10, FUR_SHADE, 0.55)

  // —— 眼睛：瞳孔按朝向偏移，眼白/眼睑不动 ——
  const eyeRy = Math.max(11 * eyeOpen, 1.2)
  for (const ex of [-12, 12]) {
    ellipse(c, cx + ex, cy - 2, 8, eyeRy, EYE)
    if (eyeOpen > 0.5) {
      circle(c, cx + ex + gazeX * 3.2 - 2, cy - 4 + gazeY * 3.0, 2.6, GLINT, 0.95)
    }
  }

  // —— 鼻子 + 嘴 ——
  triangle(c, cx - 3, cy + 10, cx + 3, cy + 10, cx, cy + 14, NOSE)
  ellipse(c, cx, cy + 17, 5, 2.6, OUTLINE, 0.7)

  // —— 胡须 ——
  for (const side of [-1, 1]) {
    for (const dy of [-2, 2]) {
      ellipse(c, cx + side * 26, cy + 12 + dy, 9, 0.7, OUTLINE, 0.55)
    }
  }

  return c
}

// ---------- 朝向：4 个方向 × 2 帧（第二帧是极轻微的回摆，让"看"不是死板的一格） ----------

const GAZES = {
  up: { gazeY: -1, headY: -3 },
  down: { gazeY: 1, headY: 3 },
  left: { gazeX: -1, headX: -4, earTilt: 1 },
  right: { gazeX: 1, headX: 4, earTilt: -1 },
}

const CLIPS = {}

// idle：呼吸 + 眨眼，4 帧 loop
CLIPS.idle = {
  fps: 6,
  loop: 'loop',
  frames: [cat(), cat({ bobY: -2 }), cat({ bobY: -3 }), cat({ bobY: -2, eyeOpen: 0.1 })],
}

// look_<dir>：2 帧 loop，朝向帧之间只有极小差别（平滑过渡的素材侧保障）
for (const [name, gaze] of Object.entries(GAZES)) {
  CLIPS[`look_${name}`] = {
    fps: 4,
    loop: 'loop',
    frames: [cat(gaze), cat({ ...gaze, bobY: -1 })],
  }
}

// paw_l / paw_r：抬 → 落 → 回位，3 帧 once（敲一下）
for (const side of ['left', 'right']) {
  CLIPS[`paw_${side[0]}`] = {
    fps: 8,
    loop: 'once',
    frames: [cat({ paw: side, pawLift: 12 }), cat({ paw: side, pawLift: 4 }), cat()],
  }
}

// ---------- 落盘 ----------

let outDir = path.join('assets', 'bongocat-pack')
const argv = process.argv.slice(2)
for (let i = 0; i < argv.length; i++) {
  if (argv[i] === '--out') {
    const value = argv[i + 1]
    if (!value || value.startsWith('--')) {
      console.error('gen-bongocat-pack: --out 后面必须跟一个目录路径')
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
    JSON.stringify({ frames: names, fps: clip.fps, loop: clip.loop }, null, 2) + '\n',
  )
}

const sample = {
  schemaVersion: 1,
  name: 'BongoCat (PetDock rewrite)',
  author: 'petdock (procedural)',
  license: 'CC0-1.0',
  naturalSize: { w: SIZE, h: SIZE },
  preferredFps: 12,
  animations: Object.keys(CLIPS).map((id) => ({ id, clip: `anim/${id}.json` })),
  defaultAnimation: 'idle',
  state: { schemaVersion: 1, position: { x: 0, y: 0 } },
}
fs.writeFileSync(path.join(outDir, 'petdock-sample.json'), JSON.stringify(sample, null, 2) + '\n')

// icon.png：512x512，idle 首帧放大
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
console.log(`CLIPS ${Object.keys(CLIPS).length} (${Object.keys(CLIPS).join(',')})`)
console.log(`FRAMES ${written} PNG (${SIZE}x${SIZE}) + icon.png (${ICON}x${ICON})`)
console.log('BONGOCAT-PACK-OK')

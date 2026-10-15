// Scan every model id the LIVE gateway advertises (GET /inference/v1/models) using the
// PR #330 recipe, so the id form is authoritative rather than taken from the local catalog.
//   POST https://opencode.ai/inference/v1/chat/completions
//   authorization: Bearer public · UA opencode/1.18.31 (no "deepseek-harness" token)
//   x-opencode-session AND x-opencode-session-id = canonically minted id (low 12 bits ~0xFFE)
//   x-opencode-client: cli · x-opencode-request · x-opencode-project: global
//   tools: bash AND read, each declaring at least one property
import { writeFileSync } from 'node:fs'

const BASE = 'https://opencode.ai'
const UA = 'opencode/1.18.31'
const BASE62 = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz'

const mintSessionId = (timestamp = Date.now(), seq = 1) => {
  const ms = timestamp * 0x1000 + seq
  const hex = (~ms & 0xffffffffffff).toString(16).padStart(12, '0')
  let tail = ''
  for (let i = 0; i < 14; i++) tail += BASE62[(i * 7 + 13) % 62]
  return `ses_${hex}${tail}`
}

const listing = await fetch(`${BASE}/inference/v1/models`, { headers: { authorization: 'Bearer public', 'user-agent': UA } })
const ids = (await listing.json()).data.map((m) => m.id)
console.log(`live listing: ${ids.length} models`)

const tool = (name) => ({
  type: 'function',
  function: { name, description: 'noop', parameters: { type: 'object', properties: { command: { type: 'string' } } } },
})

async function probe(model, seq) {
  const session = mintSessionId(Date.now(), seq)
  const started = Date.now()
  try {
    const res = await fetch(`${BASE}/inference/v1/chat/completions`, {
      method: 'POST',
      redirect: 'error',
      headers: {
        'content-type': 'application/json',
        authorization: 'Bearer public',
        'user-agent': UA,
        'x-opencode-client': 'cli',
        'x-opencode-session': session,
        'x-opencode-session-id': session,
        'x-opencode-request': 'msg_0123456789abABCDEFGHIJKLMN',
        'x-opencode-project': 'global',
      },
      body: JSON.stringify({ model, messages: [{ role: 'user', content: 'ping' }], max_tokens: 8, tools: [tool('bash'), tool('read')] }),
    })
    const text = await res.text()
    let type = ''
    let message = text
    try {
      const parsed = JSON.parse(text)
      type = parsed?.error?.type ?? ''
      message = parsed?.error?.message ?? text
    } catch {}
    const verdict = res.status === 200 ? 'OK'
      : type === 'FreeTierError' ? 'FREE-TIER-GATE'
        : message?.includes?.('Model is unavailable') ? 'UPSTREAM-DEAD'
          : res.status >= 500 ? 'UPSTREAM-5xx' : 'OTHER'
    return { model, verdict, status: res.status, ms: Date.now() - started, detail: `${type} ${String(message).slice(0, 100).replace(/\s+/g, ' ')}` }
  } catch (error) {
    return { model, verdict: 'TRANSPORT', status: 0, ms: Date.now() - started, detail: `${error?.name}: ${error?.message}` }
  }
}

const results = []
let seq = 1
for (const model of ids) {
  const r = await probe(model, seq++)
  results.push(r)
  console.log(`${r.verdict.padEnd(15)} ${String(r.status).padStart(3)} ${String(r.ms).padStart(5)}ms  ${model.padEnd(46)} ${r.detail}`)
}

const tally = {}
for (const r of results) tally[r.verdict] = (tally[r.verdict] ?? 0) + 1
console.log('\n=== tally ===')
for (const [k, v] of Object.entries(tally).sort()) console.log(`${k}: ${v}`)
for (const verdict of ['OK', 'FREE-TIER-GATE', 'UPSTREAM-DEAD', 'UPSTREAM-5xx', 'OTHER', 'TRANSPORT']) {
  const list = results.filter((r) => r.verdict === verdict).map((r) => r.model)
  if (list.length) console.log(`\n${verdict} (${list.length}): ${list.join(', ')}`)
}
writeFileSync('E:/projects/PetDock/tools/free-lane-scan.json', JSON.stringify({ at: new Date().toISOString(), base: BASE, tally, results }, null, 2))
console.log('\nwrote tools/free-lane-scan.json')

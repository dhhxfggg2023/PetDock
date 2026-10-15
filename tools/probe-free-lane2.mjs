// Second-stage diagnosis: the #330 recipe still 403s on this machine, so find which
// remaining dimension is refusing. Tests: model listing reachability, per-model scope,
// plain client UA, streaming, and a second host path.
import { CLIENT_UA, UPSTREAM_BASE, gatewayHeaders, mintSessionId } from 'file:///C:/Users/dhhxfggg/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/upstream.js'

const tool = (name) => ({
  type: 'function',
  function: { name, description: 'noop', parameters: { type: 'object', properties: { command: { type: 'string' } } } },
})

async function show(label, res, text) {
  console.log(`${label}\n  status=${res?.status} ct=${res?.headers?.get?.('content-type')}\n  body=${String(text).slice(0, 200).replace(/\s+/g, ' ')}`)
  console.log('')
}

// 1. is the lane reachable at all?
{
  const res = await fetch(`${UPSTREAM_BASE}/inference/v1/models`, { headers: { authorization: 'Bearer public', 'user-agent': CLIENT_UA } })
  const text = await res.text()
  let ids = []
  try { ids = (JSON.parse(text).data ?? []).map((m) => m.id) } catch {}
  console.log(`1. GET /inference/v1/models  status=${res.status}  count=${ids.length}  sample=${ids.slice(0, 6).join(',')}`)
  console.log(`   space-bunny-free listed: ${ids.includes('space-bunny-free')}`)
  console.log('')
}

// 2. chat with the plainest possible client identity
for (const [label, opts] of [
  ['2a plain opencode UA, stream, no extra opencode headers', { ua: CLIENT_UA, stream: true, minimal: true }],
  ['2b opencode UA + all opencode headers, stream', { ua: CLIENT_UA, stream: true, minimal: false }],
  ['2c nemotron-3.5-lightning-free', { ua: CLIENT_UA, stream: true, minimal: false, model: 'nemotron-3.5-lightning-free' }],
  ['2d ling-3.1-flash-free', { ua: CLIENT_UA, stream: true, minimal: false, model: 'ling-3.1-flash-free' }],
]) {
  const session = mintSessionId()
  const headers = { 'content-type': 'application/json', authorization: 'Bearer public', 'user-agent': opts.ua }
  if (!opts.minimal) Object.assign(headers, { 'x-opencode-client': 'cli', 'x-opencode-session': session, 'x-opencode-session-id': session, 'x-opencode-request': 'msg_reprobe000000000', 'x-opencode-project': 'global' })
  if (opts.stream) headers.accept = 'text/event-stream'
  const res = await fetch(`${UPSTREAM_BASE}/inference/v1/chat/completions`, {
    method: 'POST',
    headers,
    body: JSON.stringify({ model: opts.model ?? 'space-bunny-free', messages: [{ role: 'user', content: 'ping' }], max_tokens: 8, stream: !!opts.stream, tools: [tool('bash'), tool('read')] }),
    redirect: 'error',
  })
  await show(label, res, await res.text())
}

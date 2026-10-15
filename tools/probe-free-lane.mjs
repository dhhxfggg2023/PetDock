// Repro for the free lane as of 2026-10-15, testing the recipe documented in
// PR #330 (Ebony-Vinyl/dsh-our-free-model) and issue #310:
//   path /inference/v1/chat/completions (NOT /zen/v1), header x-opencode-session-id,
//   canonically minted session id, bash AND read declared each with >=1 property,
//   UA without the token "deepseek-harness".
// Uses the plugin's own helpers so headers/session are byte-faithful.
import { CLIENT_UA, UPSTREAM_BASE, gatewayHeaders, sessionForConversation, mintSessionId, SESSION_RE } from 'file:///C:/Users/dhhxfggg/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/upstream.js'

const MODEL = process.argv[2] ?? 'space-bunny-free'
const ATTRIBUTION = 'deepseek-harness/0.1.7 (+https://github.com/deepseek-ai/deepseek-harness)'

const tool = (name) => ({
  type: 'function',
  function: { name, description: 'noop', parameters: { type: 'object', properties: { command: { type: 'string' } } } },
})
const body = () => ({
  model: MODEL,
  messages: [{ role: 'user', content: 'ping' }],
  max_tokens: 8,
  tools: [tool('bash'), tool('read')],
})

async function attempt(label, { path = '/inference/v1/chat/completions', ua, session, extraHeaders = {} }) {
  const headers = gatewayHeaders({ session, requestId: 'msg_reprobe000000000', stream: false })
  headers['user-agent'] = ua
  Object.assign(headers, extraHeaders)
  const started = Date.now()
  try {
    const res = await fetch(`${UPSTREAM_BASE}${path}`, { method: 'POST', headers, body: JSON.stringify(body()), redirect: 'error' })
    const text = await res.text()
    console.log(`${label}\n  status=${res.status} ms=${Date.now() - started}\n  path=${path}\n  ua=${ua}\n  session=${session} (canonical=${SESSION_RE.test(session)})\n  body=${text.slice(0, 240).replace(/\s+/g, ' ')}`)
  } catch (error) {
    console.log(`${label}\n  THREW ${error?.name}: ${error?.message}`)
  }
  console.log('')
}

const sha = sessionForConversation('repro')
const mint = mintSessionId()

await attempt('E  #330 recipe: /inference/v1 + session-id header + minted session + bash/read with a property + clean UA',
  { ua: `dsh/0.1.7 ${CLIENT_UA}`, session: mint, extraHeaders: { 'x-opencode-session-id': mint, 'x-opencode-client': 'cli' } })

await attempt('F  same but shipped UA (harness token)',
  { ua: `${ATTRIBUTION} ${CLIENT_UA}`, session: mint, extraHeaders: { 'x-opencode-session-id': mint, 'x-opencode-client': 'cli' } })

await attempt('G  same but sha256-derived session',
  { ua: `dsh/0.1.7 ${CLIENT_UA}`, session: sha, extraHeaders: { 'x-opencode-session-id': sha, 'x-opencode-client': 'cli' } })

await attempt('H  same but WITHOUT x-opencode-session-id',
  { ua: `dsh/0.1.7 ${CLIENT_UA}`, session: mint })

await attempt('I  control: /zen/v1 path with everything else correct',
  { path: '/zen/v1/chat/completions', ua: `dsh/0.1.7 ${CLIENT_UA}`, session: mint, extraHeaders: { 'x-opencode-session-id': mint, 'x-opencode-client': 'cli' } })

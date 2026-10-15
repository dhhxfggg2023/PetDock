// Probe the plugin's three self-reported failures from Node directly:
//   1. announcement feed   (src/feed.js DEFAULT_FEED_SOURCES)
//   2. update-check manifest (src/updater.js DEFAULT_MANIFEST_SOURCES + Ed25519 verify)
//   3. the free lane (already known 403) — kept here for one table
// If these answer for plain Node fetch but not for the plugin, the break is in the
// plugin's request construction or in the host that rewrites fetch, not the network.
import crypto from 'node:crypto'

const REPO = 'Ebony-Vinyl/dsh-our-free-model'
const PINNED = 'MCowBQYDK2VwAyEAeLdSVwYFyazc2PIBC0oLsvo4LghGEQz9iXIl3CqRuXI='
const UA = 'Mozilla/5.0'

const targets = [
  ['feed raw main', `https://raw.githubusercontent.com/${REPO}/main/feed/announcements.json`],
  ['feed jsdelivr main', `https://cdn.jsdelivr.net/gh/${REPO}@main/feed/announcements.json`],
  ['feed raw master', `https://raw.githubusercontent.com/${REPO}/master/feed/announcements.json`],
  ['manifest raw main', `https://raw.githubusercontent.com/${REPO}/main/feed/manifest.json`],
  ['manifest jsdelivr main', `https://cdn.jsdelivr.net/gh/${REPO}@main/feed/manifest.json`],
  ['models listing', 'https://opencode.ai/inference/v1/models'],
]

for (const [label, url] of targets) {
  const started = Date.now()
  try {
    const res = await fetch(url, { headers: { 'user-agent': UA, authorization: url.includes('opencode.ai') ? 'Bearer public' : undefined }, redirect: 'follow' })
    const text = await res.text()
    console.log(`${label.padEnd(24)} ${String(res.status).padStart(3)} ${String(Date.now() - started).padStart(5)}ms  ${String(text.length).padStart(7)}B  ${url}`)
    if (label.startsWith('manifest raw main') && res.status === 200) {
      const manifest = JSON.parse(text)
      const { signature, ...rest } = manifest
      const canonical = JSON.stringify(rest, Object.keys(rest).sort())
      const ok = crypto.verify(null, Buffer.from(canonical, 'utf8'), crypto.createPublicKey({ key: Buffer.from(PINNED, 'base64'), format: 'der', type: 'spki' }), Buffer.from(signature ?? '', 'base64'))
      console.log(`  manifest version=${manifest.version ?? '?'} files=${manifest.files?.length ?? '?'} signed=${Boolean(signature)} signatureValid=${ok}`)
    }
    if (label.startsWith('feed raw main') && res.status === 200) {
      const feed = JSON.parse(text)
      console.log(`  feed announcements=${feed.announcements?.length ?? '?'} ids=${(feed.announcements ?? []).map((a) => a.id).slice(0, 4).join(', ')}`)
    }
  } catch (error) {
    console.log(`${label.padEnd(24)} THREW ${String(Date.now() - started).padStart(5)}ms  ${error?.name}: ${error?.message}  ${url}`)
  }
}

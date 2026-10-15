// Why do GitHub raw and jsDelivr fail while opencode.ai works? Get the real cause
// (DNS vs TCP vs TLS) instead of Node fetch's opaque "fetch failed".
import dns from 'node:dns'
import https from 'node:https'
import tls from 'node:tls'

const hosts = ['raw.githubusercontent.com', 'cdn.jsdelivr.net', 'objects.githubusercontent.com', 'opencode.ai', 'github.com']

for (const host of hosts) {
  await new Promise((resolve) => {
    dns.lookup(host, { all: true }, (error, addresses) => {
      if (error) console.log(`DNS  ${host.padEnd(32)} FAIL ${error.code} ${error.message}`)
      else console.log(`DNS  ${host.padEnd(32)} OK   ${addresses.map((a) => a.address).join(', ')}`)
      resolve()
    })
  })
}

const tcp = (host, port = 443) => new Promise((resolve) => {
  const started = Date.now()
  const socket = tls.connect({ host, port, servername: host, timeout: 8000 }, () => {
    console.log(`TLS  ${host.padEnd(32)} OK   ${Date.now() - started}ms  ${socket.getProtocol()}`)
    socket.destroy()
    resolve()
  })
  socket.on('timeout', () => { console.log(`TLS  ${host.padEnd(32)} TIMEOUT after ${Date.now() - started}ms`); socket.destroy(); resolve() })
  socket.on('error', (error) => { console.log(`TLS  ${host.padEnd(32)} FAIL ${error.code} ${error.message} (${Date.now() - started}ms)`); resolve() })
})

for (const host of hosts) await tcp(host)

// And a plain GET through node:https to see the status or the error
const get = (url) => new Promise((resolve) => {
  const started = Date.now()
  const req = https.get(url, { headers: { 'user-agent': 'Mozilla/5.0' }, timeout: 10000 }, (res) => {
    console.log(`GET  ${new URL(url).host.padEnd(32)} ${res.statusCode} ${Date.now() - started}ms`)
    res.resume()
    res.on('end', resolve)
  })
  req.on('timeout', () => { console.log(`GET  ${new URL(url).host.padEnd(32)} TIMEOUT`); req.destroy(); resolve() })
  req.on('error', (error) => { console.log(`GET  ${new URL(url).host.padEnd(32)} FAIL ${error.code} ${error.message} (${Date.now() - started}ms)`); resolve() })
})

await get('https://raw.githubusercontent.com/Ebony-Vinyl/dsh-our-free-model/main/feed/manifest.json')
await get('https://cdn.jsdelivr.net/gh/Ebony-Vinyl/dsh-our-free-model@main/feed/manifest.json')
await get('https://opencode.ai/inference/v1/models')

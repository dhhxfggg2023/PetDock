// Lane-level probe: drive each lane's OWN module (the real code path the plugin uses),
// not a hand-rolled request. Lanes on this install (from catalog.json): free, sealed/EAC, kilo.
import { fetchKiloListing, KILO_BASE, postKiloStreamed } from 'file:///C:/Users/dhhxfggg/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/kilo.js'
import { unlockSealedLane } from 'file:///C:/Users/dhhxfggg/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/vault.js'
import { fetchSealedListing, postSealedStreamed } from 'file:///C:/Users/dhhxfggg/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/eac.js'

const line = (s) => console.log(s)

line(`kilo base: ${KILO_BASE}`)

// ---- Kilo lane ----
try {
  const listing = await fetchKiloListing()
  const ids = (listing?.data ?? listing?.models ?? []).map((m) => m.id ?? m.name ?? m)
  line(`KILO listing: OK, ${ids.length} ids, sample ${ids.slice(0, 5).join(', ')}`)
  let first = ''
  try {
    await postKiloStreamed({
      body: { model: ids[0] ?? 'kilo-auto/free', messages: [{ role: 'user', content: 'ping' }], max_tokens: 8, stream: true },
      onData: (frame) => { if (!first) first = JSON.stringify(frame).slice(0, 120) },
      timeoutMs: 60000,
    })
    line(`KILO turn on ${ids[0]}: OK, first frame ${first}`)
  } catch (error) {
    line(`KILO turn on ${ids[0]}: FAILED ${error?.code ?? ''} ${error?.message ?? error}`)
  }
} catch (error) {
  line(`KILO listing: FAILED ${error?.code ?? ''} ${error?.message ?? error}`)
}

// ---- Sealed / EAC lane ----
const credential = unlockSealedLane({ profileName: 'desktop' })
if (credential === null) {
  line('SEALED credential: unlock returned null (host not recognized outside DSH, or no seal)')
} else {
  line(`SEALED credential: mode=${credential.mode} base=${credential.base} flavor=${credential.flavor} hasUserToken=${Object.hasOwn(credential, 'userToken')}`)
  try {
    const listing = await fetchSealedListing(credential)
    const ids = (listing?.data ?? []).map((m) => m.id ?? m)
    line(`SEALED listing: OK, ${ids.length} ids: ${ids.join(', ')}`)
    try {
      await postSealedStreamed({
        credential,
        body: { model: ids[0], messages: [{ role: 'user', content: 'ping' }], max_tokens: 8, stream: true },
        onData: () => {},
        timeoutMs: 90000,
      })
      line(`SEALED turn on ${ids[0]}: OK`)
    } catch (error) {
      line(`SEALED turn on ${ids[0]}: FAILED ${error?.code ?? ''} ${error?.message ?? error}`)
    }
  } catch (error) {
    line(`SEALED listing: FAILED ${error?.code ?? ''} ${error?.message ?? error}`)
  }
}

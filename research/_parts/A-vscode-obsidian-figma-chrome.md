# Prior art: four extension platforms

Research for **PetDock** (microkernel + everything-is-a-plugin). Scope: VS Code extensions, Obsidian
plugins, Figma plugins, Chrome Manifest V3 extensions. For each: **isolation**, **API versioning**,
**core-bloat control**, **citable failure modes**, and the **transferable pattern**.

Verification legend: claims are linked to the page they came from. Where a widely repeated claim is
*not* in the primary source, it is marked **unverified**.

---

## 1. VS Code extensions

### 1.1 Isolation — a separate Extension Host process, but not a sandbox

VS Code runs extension code in an **Extension Host**, never in the UI process. There are three hosts,
with different runtimes and locations: a **local** Node.js host, a **web** host running in a Browser
WebWorker, and a **remote** Node.js host in a container/SSH/WSL/Codespace
([Extension Host](https://code.visualstudio.com/api/advanced-topics/extension-host)). The documented
purpose is stability, not security: "The Extension Host in VS Code prevents extensions from: Impacting
startup performance / Slowing down UI operations / Modifying the UI" — the same page. Extensions are
loaded lazily so "the Markdown extension should only be loaded when a user opens a Markdown file."

This is an **availability** boundary, not a **trust** boundary. The local host is a full Node.js
runtime with the user's filesystem and network access; an academic study of VS Code extension data
exposure describes it as "each extension in a separate Node.js process, known as the extension host"
([arXiv 2412.00707](https://arxiv.org/html/2412.00707v2)). Nothing in the host stops an extension from
reading `~/.ssh`. Where the extension runs is declared, not enforced, by `extensionKind`:
`["workspace"]` means "requires access to workspace contents", `["ui"]` means "**must** run close to the
UI because it requires access to local assets, devices, or capabilities or because low latency is
required" ([Extension Host](https://code.visualstudio.com/api/advanced-topics/extension-host)). A
`capabilities` field (`untrustedWorkspaces`, `virtualWorkspaces`) lets an extension *declare* how it
behaves in a restricted workspace, but it is a declaration the extension makes about itself
([Extension Manifest](https://code.visualstudio.com/api/references/extension-manifest)).

**Takeaway for PetDock: VS Code proves process isolation buys UI liveness, not safety. If PetDock wants
safety it needs a permission model, not just a process.**

### 1.2 Activation — the declarative surface that keeps the core small

An extension declares **activation events** in `package.json`; the core activates the extension only
when one fires. The full list is broad and enumerable — `onLanguage`, `onCommand`, `onDebug` (+ four
finer-grained debug variants), `onFileSystem`, `onView`, `onUri`, `onWebviewPanel`, `onCustomEditor`,
`onAuthenticationRequest`, `onTaskType`, `onNotebook`, `onRenderer`, `onTerminal`,
`onTerminalProfile`, `onTerminalShellIntegration`, `onWalkthrough`, `onIssueReporterOpened`,
`onChatParticipant`, `onLanguageModelTool`, `onEditSession`, `onSearch`, `onOpenExternalUri`,
`workspaceContains`, `onStartupFinished`, and the blunt `*`
([Activation Events](https://code.visualstudio.com/api/references/activation-events)).

The important evolution is **inference**. Since **1.74.0**, five contribution points activate their own
extension without an explicit activation event — `commands`→`onCommand`, `authentication`→
`onAuthenticationRequest`, `languages`→`onLanguage`, `customEditors`→`onCustomEditor`,
`views`→`onView`; since **1.76.0** `tasks`→`onTaskType`
([v1.74 release notes](https://code.visualstudio.com/updates/v1_74),
[Activation Events](https://code.visualstudio.com/api/references/activation-events)). This is a
deliberate reduction of declarative boilerplate: the *contribution point itself* is now the activation
contract, so the two lists cannot drift apart.

`*` is explicitly discouraged ("please use this activation event in your extension only when no other
activation events combination works"), and an extension "**must** export an `activate()` function …
and it will be invoked **only once**" plus an optional `deactivate()`.

### 1.3 Core-bloat control — `contributes` + a `when`-clause context language

Two mechanisms keep features out of the core:

1. **Declarative contribution points.** `package.json#contributes` declares commands, menus, views,
   viewContainers, configuration, languages, grammars, debuggers, taskDefinitions, customEditors,
   notebooks, walkthroughs, iconThemes, keybindings, snippets, semanticTokenModifiers, terminal,
   typescriptServerPlugins, authentication, chatParticipants, languageModelTools, and more
   ([Extension Manifest](https://code.visualstudio.com/api/references/extension-manifest)). The core
   renders these without loading extension code.
2. **A `when` clause context language.** Menus, keybindings, view visibility and command enablement
   are gated by expressions over context keys, with operators `!`, `&&`, `||`, `==`/`!=`, `>`,`<`,
   `=~` (regex, flags `i s m u`; `g`/`y` ignored) and `in`/`not in` for dynamic lookup against an array
   or object key set. Extensions extend the namespace themselves via
   `vscode.commands.executeCommand('setContext', 'ext.supportedFolders', [...])`, and settings are
   readable as `config.editor.tabCompletion`
   ([when clause contexts](https://code.visualstudio.com/api/references/when-clause-contexts)).

The `in` operator is the notable one: `"explorerResourceIsFolder && resourceFilename in
ext.supportedFolders"` lets an extension drive *core UI* from its own state without the core knowing
what a "supported folder" is. This is how the core avoids absorbing per-extension concepts.

### 1.4 API versioning — semver `engines`, plus a formal "proposed" staging track

Two layers:

- **`engines.vscode`** is a required semver range that gates *install*, and "cannot be `*`". `^1.8.0`
  means 1.8.0 and onwards. It is explicitly the mechanism for shipping against a not-yet-stable API:
  publishing with `^1.9.0` while Stable is 1.8.0 means "your new extension version will only be
  available on VS Code >=1.9.0" ([Publishing Extensions](https://code.visualstudio.com/api/working-with-extensions/publishing-extension)).
  Date-tagged Insiders builds are addressable as `^1.56.0-20210428`.
- **Proposed APIs.** New API lands as `vscode.proposed.<proposalName>.d.ts`, enabled with
  `"enabledApiProposals": ["<name>"]`, and is available **only on Insiders**. The hard rule: "**You
  cannot publish an extension that uses a proposed API.** We may likely make breaking changes in the
  next release and we never want to break existing extensions"
  ([Extension API process](https://github.com/microsoft/vscode/wiki/Extension-API-process),
  [Using Proposed API](https://code.visualstudio.com/api/advanced-topics/using-proposed-api)). Moving a
  proposal to stable is a documented four-step migration ending with a mandatory `engines.vscode` bump
  so only VS Code versions containing the finalized API can install the extension.

This is a two-track system: **stable API is append-only and semver-gated; unstable API is physically
unpublishable.** The marketplace is the enforcement point, not a runtime flag.

### 1.5 Packaging, distribution, and the marketplace fracture

`vsce package` produces a **VSIX** (a zip); `vsce publish` uploads it. `.vscodeignore` prunes files;
`vscode:prepublish` runs a build step; platform-specific VSIXs are published with `--target win32-x64`
etc.; pre-release builds use `--pre-release` and are only supported from VS Code 1.63.0
([Publishing Extensions](https://code.visualstudio.com/api/working-with-extensions/publishing-extension)).
`extensionDependencies` declares a hard dependency; `extensionPack` bundles extensions to install
together ([Extension Manifest](https://code.visualstudio.com/api/references/extension-manifest)).

**The marketplace is the real control plane.** Microsoft's Marketplace terms restrict offerings to
"In-Scope Products and Services" (Visual Studio, VS Code, Codespaces, Azure DevOps…), which excludes
non-Microsoft forks; **Open VSX**, created by TypeFox/Gitpod in 2019 and hosted by the Eclipse
Foundation, exists as the vendor-neutral alternative
([Open VSX — Wikipedia](https://en.wikipedia.org/wiki/Open_VSX),
[Visual Studio Code is designed to fracture](https://ghuntley.com/fracture/)).

**Citable failure modes:**

- **April 2025 — the C/C++ extension enforcement.** With v1.24.5 (3 April 2025) Microsoft added an
  environment check to the closed-source C/C++ extension; installing it outside Microsoft products
  yields: *"The C/C++ extension may be used only with Microsoft Visual Studio, Visual Studio for Mac,
  Visual Studio Code, Azure DevOps, Team Foundation Server, and successor Microsoft products and
  services…"* VSCodium and Cursor users lost IntelliSense and debugging. Cursor's CEO said the company
  is "transitioning away from these extensions" to open-source alternatives; a developer reportedly
  filed an FTC complaint ([The Register, 24 Apr 2025](https://www.theregister.com/2025/04/24/microsoft_vs_code_subtracts_cc_extension/)).
- **Precedent for the pattern**: the Python extension's language server was replaced by the proprietary
  Pylance as the default, and the same play ran for C#/OmniSharp
  ([ghuntley.com/fracture](https://ghuntley.com/fracture/)).

**Unverified:** a 2025 claim that "VS Code is going closed source" as such. The verifiable 2025 event is
the *extension-license enforcement* above; the VS Code source remains MIT while the shipped product and
the Marketplace are proprietary.

**Transferable pattern:** a *declarative manifest + lazily-inferred activation* is what lets a
microkernel ship dozens of feature surfaces without the core knowing about any of them — and the
marketplace, not the runtime, is where you enforce API stability.

---

## 2. Obsidian plugins

### 2.1 Isolation — **none. Plugins run in-process with full trust.**

This is the honest answer and the sources say it plainly. Obsidian is an Electron app; plugins are
loaded into the app's own JS context. The community answer on the official forum is that "within
Electron, implementing (and enforcing) a permission model is either flat out impossible … or, if
possible, extremely challenging (more than building obsidian itself). There is no way in electron to
'containerize' the plugin code"
([Security of the plugins — Obsidian Forum](https://forum.obsidian.md/t/security-of-the-plugins/7544)).
The discussion around Obsidian's own plugin-directory relaunch repeats the framing: "it is a very rare
plugin indeed that will need to run unsandboxed executable code with full disk and network access.
Currently, that's every plugin"
([Hacker News](https://news.ycombinator.com/item?id=48109970)).

Obsidian's own docs concede the blast radius operationally rather than architecturally: "When developing
plugins, one mistake can lead to unintended changes to your vault. To prevent data loss, you should
never develop plugins in your main vault. Always use a separate vault dedicated to plugin development"
([Build a plugin](https://docs.obsidian.md/Plugins/Getting+started/Build+a+plugin)).

### 2.2 Manifest and distribution

Plugins are plain directories: `<vault>/.obsidian/plugins/<id>/` containing `main.js` plus
`manifest.json`. `main.js` is the **bundle** — the sample plugin compiles TypeScript to it via
`npm run dev`, and Obsidian loads nothing else. There is no build tooling shipped *in* the app by
default; the docs point at Node.js and npm, and separately at a third-party In-App Builder plugin
([Build a plugin](https://docs.obsidian.md/Plugins/Getting+started/Build+a+plugin),
[In-App Builder](https://forum.obsidian.md/t/plugin-in-app-builder-build-your-plugins-right-inside-obsidian-beta/101887)).

Manifest schema ([Manifest reference](https://docs.obsidian.md/Reference/Manifest)):

| Field | Required | Notes |
|---|---|---|
| `id` | yes (plugins) | lowercase letters and hyphens only; can't end with `plugin`; can't contain `obsidian`; should match the folder name locally or e.g. `onExternalSettingsChange` won't fire |
| `name` | yes | unique; no emoji/special chars; must not use core plugin names ("Live Preview", "Bases") |
| `version` | yes | Semantic Versioning `x.y.z` |
| `minAppVersion` | yes | minimum Obsidian version |
| `author`, `authorUrl` | yes / no | |
| `description` | yes (plugins) | ≤250 chars, ends with a period, no emoji |
| `isDesktopOnly` | yes (plugins) | must be `true` if using Node.js/Electron APIs |
| `fundingUrl` | no | string, or object of named links |

`minAppVersion` is the *only* version-negotiation mechanism, and it is enforced by the directory, not
by a runtime capability check: the submission requirements say to "set the `minAppVersion` in the
Manifest … to the minimum required version of the Obsidian app that your plugin is compatible with"
([Submission requirements](https://docs.obsidian.md/community-directory/submission-requirements-for-plugins)).
`isDesktopOnly` exists because "The Node.js and Electron APIs are only available in the desktop version
of Obsidian" — same page — so mobile gets a hard, declared capability split.

### 2.3 Review and failure modes

Distribution moved from the `obsidian-releases` GitHub repo to a **Community site + developer
dashboard** with **automated reviews of every version** (not just first submission): "The new automated
review system scans every version for security and code quality, not just the initial submission."
Scorecards surface check results; manual review is now reserved for "popular plugins, featured plugins,
and issues flagged by the community"; a version that fails review "is removed from search within 24
hours"; closed-source plugins are not accepted for new submissions; an official ESLint plugin lets
authors run the check locally ([The future of Obsidian plugins](https://obsidian.md/blog/future-of-plugins)).
An independent third party, **Plugin Observer**, re-analyses every version for privacy/data-flow and
diff behaviour between releases, while cautioning that "Neither system should be treated as a
definitive safety verdict" ([plugin.observer/about](https://plugin.observer/about)).

**Citable incident — PhantomPulse (Elastic Security Labs, REF6598).** Attackers posed as a VC firm on
LinkedIn, moved the target to Telegram, gave them credentials to a cloud-hosted Obsidian vault, and told
them to enable **community plugin sync**. The vault shipped a pre-configured **Shell Commands** plugin
whose `data.json` executed Base64-encoded PowerShell (`Invoke-Expression` → `iwr` → `BitsTransfer` →
in-memory reflective loader → PHANTOMPULSE RAT), with an AppleScript + LaunchAgent chain on macOS.
Notably, "an attacker cannot remotely force the installation or enablement of a community plugin via
vault sync alone" — the plugins directory and `community-plugins.json` are local, opt-in sync toggles
("Active community plugin list", "Installed community plugins"), both off by default — so the payload
needed one act of social engineering to cross the boundary. The payload lived entirely in **JSON
configuration**, and "execution is handed off by a signed, trusted Electron application, making
parent-process-based detection the critical layer"
([Phantom in the vault](https://www.elastic.co/security-labs/threat-command/phantom-in-the-vault)).

That is the sharpest lesson in this whole document: **with no isolation, a plugin's *data* is as
dangerous as its *code*.** A configuration file that names a command is a program.

**Transferable pattern:** Obsidian shows the low-friction end of the spectrum — a directory, a
`manifest.json` and a `main.js` is a complete plugin — and shows exactly what you pay for it: the only
defences left are review, scorecards, disclosure, and user education. PetDock should treat "config that
can name a shell command" as privileged capability, not as data.

---

## 3. Figma plugins

### 3.1 Isolation — two iframes, a hard capability split, and message passing

Figma's model is the most architecturally explicit of the four. Plugin code runs on the **main thread
in a sandbox**: "The sandbox is a minimal JavaScript environment and **does not expose browser APIs**."
`XMLHttpRequest`, `fetch`, `setTimeout` and the DOM are "not *directly* available from the sandbox". To
get a UI or browser APIs you create an iframe via `figma.showUI()`. The split is symmetrical and stated
as such: "The main thread can access the Figma 'scene' … but not the browser APIs. Conversely, the
iframe can access the browser APIs, but not the Figma scene. The main thread and the iframe can
communicate with each other through **message passing**"
([How plugins run](https://developers.figma.com/docs)).

So the design is **two mutually-exclusive capability domains** — scene access XOR DOM access — rather
than a trust boundary around a single domain. Network egress is separately mediated by the manifest: if
`networkAccess` limits domains, "Figma blocks that attempt and returns a content-security policy (CSP)
error"; enforcement covers "requests made by the plugin" and does **not** extend to resources a
third-party site loaded inside the plugin's own iframe.

### 3.2 The bridge

`figma.ui.postMessage(pluginMessage, options?)` "can send almost any data type or plain object, as long
as it's a serializable object… You can send objects, arrays, numbers, strings, booleans, null,
undefined, Date objects and Uint8Array objects. However, **functions and prototype chains of objects
will not be sent**", with the same restrictions as the browser's structured-clone algorithm
([postMessage](https://developers.figma.com/docs)).

**Verified**: the structured-clone restriction, the two-domain split, and the message-passing-only
bridge. **Unverified**: the commonly-repeated framing that "plugin API objects are proxies over a
message boundary and you cannot hold a reference to a node" — Figma's docs describe serialization and
async loading, not proxies, and I could not find that wording in the official pages. Treat it as a
reasonable mental model, not a citation.

### 3.3 Manifest — capability declarations, version pinning, staged APIs

`manifest.json` ([Plugin Manifest](https://developers.figma.com/docs)):

| Field | Meaning |
|---|---|
| `api` | "The version of the Figma API used by the plugin… **we don't auto-upgrade the api version of plugins** to give you the chance to test your plugin against the new version" |
| `main` / `ui` | entry JS / optional HTML for the iframe (`__html__`, `__uiFiles__`) |
| `editorType` | `figma`, `figjam`, `dev`, `slides`, `buzz` — some combinations unsupported (`["figjam","dev"]`) |
| `documentAccess` | must be `dynamic-page`; **required for all new plugins** |
| `networkAccess` | `{allowedDomains[], reasoning?, devAllowedDomains[]}`; `["none"]` = no network; `["*"]` or localhost requires `reasoning`; the domain list is **shown on the plugin's Community page** |
| `permissions` | `currentuser`, `activeusers`, `fileusers`, `payments`, `teamlibrary` — each unlocks a specific `figma.*` surface |
| `capabilities` | `textreview`, `codegen`, `inspect`, `vscode` |
| `parameters` / `parameterOnly` / `menu` / `relaunchButtons` | declarative command surfaces |
| `enableProposedApi` | "This flag is only meant for development, and **will not work in published plugins**" |
| `enablePrivatePluginApi`, `build` | private-plugin APIs; a shell command run before loading `main`/`ui` |

`api` is the versioning story: **pinned per plugin, never auto-upgraded.** `enableProposedApi` mirrors
VS Code's proposed track, but enforced by the *runtime* rather than the marketplace.

### 3.4 Lifecycle, and the 2023 async migration as a citable breaking change

A plugin must call `figma.closePlugin()`; otherwise "the plugin runs indefinitely and the user sees a
'Running [your plugin name]' toast notification". The user can cancel at any time, in which case Figma
itself calls `closePlugin()` ([How plugins run](https://developers.figma.com/docs)).
**The host always retains a kill switch** — a plugin cannot hold the session hostage.

The breaking change: in 2023 Figma started **dynamically loading pages**, so a plugin could no longer
assume the whole document was resident. With `"documentAccess": "dynamic-page"`, calling any deprecated
synchronous accessor **throws**: `figma.getNodeById()` → `getNodeByIdAsync()`, `getLocalTextStyles()` →
`getLocalTextStylesAsync()`, `figma.currentPage = p` → `await figma.setCurrentPageAsync(p)`,
`instance.mainComponent` → `await instance.getMainComponentAsync()`, `node.fillStyleId = x` →
`await node.setFillStyleAsync(x)`. `PageNode.children` throws unless `PageNode.loadAsync()` was called;
`figma.root.findAll()` requires `figma.loadAllPagesAsync()`. Without the manifest field, legacy plugins
trigger a full-file load that "can take around **20 to 30 seconds**" on large files
([Migrating Plugins to Dynamically Load Pages](https://developers.figma.com/docs)).

Two migration affordances are worth copying: Figma shipped **TypeScript typings + a purpose-built
ESLint plugin that auto-fixes many of the changes**, and published a regex for JS-only codebases to
locate every call site. The migration was made *mechanically checkable*, not just documented.

**Transferable pattern:** Figma's bridge is the strongest core-bloat control of the four — the core
exposes a **scene**, never a DOM, and the plugin must declare its network domains, its permission
surfaces and its capabilities in the manifest. A plugin literally cannot reach outside the capabilities
it declared.

---

## 4. Chrome extensions — Manifest V3

### 4.1 Isolation — a service worker, and the 30-second guillotine

MV3 replaces persistent background pages with an **extension service worker**. Chrome terminates it:

- after **30 seconds of inactivity** (receiving an event or calling an extension API resets the timer);
- when a single request/event/API call takes longer than **5 minutes**;
- when a `fetch()` response takes more than **30 seconds** to arrive.

"Any global variables you set will be lost if the service worker shuts down" — state must go to
`browser.storage`, IndexedDB, or CacheStorage
([Service worker lifecycle](https://developer.chrome.com/docs)).
Google's own guidance: "you should design your service worker to be resilient against unexpected
termination… avoid keeping your service worker alive indefinitely if possible."

The lifetime has been patched repeatedly, which is itself the lesson — each row is a class of extension
that broke: Chrome 105 native messaging keeps it alive; 109 offscreen-document messages reset timers;
110 API calls (not just running handlers) reset timers; 114 long-lived messaging keeps it alive (opening
a port no longer resets); 116 active `WebSocket` traffic resets the timer and a set of user-prompt APIs
may exceed five minutes; 118 active debugger sessions keep it alive; 120 `alarms` may be set to a 30s
minimum to match the lifecycle. The same page recommends setting `minimum_chrome_version` when an
extension depends on any of these.

### 4.2 Isolation of network access — declarative, not imperative

`declarativeNetRequest` "is used to block or modify network requests by specifying declarative rules.
This lets extensions modify network requests **without intercepting them and viewing their content**,
thus providing more privacy"
([declarativeNetRequest](https://developer.chrome.com/docs)).
The API is *data*, not code: rulesets are JSON, and three kinds exist — **static** (packaged, declared
under `declarative_net_request.rule_resources`), **dynamic** (persist across sessions and updates), and
**session** (cleared on browser shutdown/update). Rule actions: `block`, `redirect`, `allow`,
`upgradeScheme`, `modifyHeaders`, `allowAllRequests`.

The API's whole shape is a budget: max **100** static rulesets declared, **50** enabled,
**30,000** guaranteed static rules across enabled rulesets (beyond that is a global pool shared with
other extensions, queryable via `getAvailableStaticRuleCount()`), **5,000** session rules, **5,000**
"unsafe" dynamic rules, **30,000** safe dynamic rules (Chrome 121+; before Chrome 120 dynamic+session
shared a 5,000 cap), and **1,000** regex rules with each compiled rule <2KB.

Permission split — the detail that makes the warning UX work
([declarativeNetRequest](https://developer.chrome.com/docs)):

| Permission | Install-time warning | Grants |
|---|---|---|
| `declarativeNetRequest` | **yes** | implicit `allow`/`allowAllRequests`/`block` rules; "Use this when possible to avoid needing to request full access to hosts" |
| `declarativeNetRequestWithHostAccess` | **no** | same capability, but host permissions must be requested first |
| `declarativeNetRequestFeedback` | — | `getMatchedRules()`, `onRuleMatchedDebug` (unpacked/dev only) |

### 4.3 Permissions, warnings, and optionality

`permissions` (a known-string allowlist), `host_permissions` (match patterns), and the runtime-granted
`optional_permissions` / `optional_host_permissions`; `content_scripts.matches` also grants host access.
Google's framing: "Permissions help to limit damage if your extension is compromised by malware", and
"Consider using optional permissions wherever the functionality of your extension permits"
([Declare permissions](https://developer.chrome.com/docs)).
Adding or changing `host_permissions` or `content_scripts.matches` **triggers a new warning on update** —
so broadening access is a visible, user-facing event, not a silent update.

`minimum_chrome_version` gates installs ("Not compatible" replaces the install button) and, critically,
"Existing users of your extension won't receive updates when the `minimum_chrome_version` is higher than
their current browser version. **This happens silently**" ([Minimum Chrome Version](https://developer.chrome.com/docs)).

### 4.4 Review and the MV2→MV3 deprecation

Review is "a combination of manual and automated systems" and takes "a few days… up to a few weeks".
Factors that slow it: broad host permissions (`*://*/*`, `https://*/*`), sensitive execution
permissions, and sheer code volume — "**Obfuscation is disallowed**… Minification is allowed, but it can
also make reviewing extension code more difficult." Enforcement escalates: **rejection** (listing
unchanged, users not notified), **warning** (7–30 days to fix), **takedown** (removed from the Store,
users not notified immediately; Chrome auto-disables after several weeks of unresolved violation), and
**malware/extreme** (no developer notification at all, disabled on all devices, **cannot be
re-enabled**) ([Chrome Web Store review process](https://developer.chrome.com/docs)).

**The deprecation timeline** ([MV2 support timeline](https://developer.chrome.com/docs)):

| Date | Event |
|---|---|
| Jan 2022 | Store stops accepting new public/unlisted MV2 extensions |
| Jun 2022 | …then new private MV2 extensions |
| Jun 3 2024 | Phase-out begins on Beta/Dev/Canary; MV2 extensions lose the Featured badge |
| Oct 9 2024 | Disabling begins on Stable; users may re-enable; enterprise `ExtensionManifestV2Availability` exempts until June 2025 |
| Mar 31 2025 | MV2 disabled by default on all channels; users can still turn them back on |
| Jul 24 2025 | With Chrome 138, MV2 disabled everywhere; users can no longer re-enable. Policy removed with Chrome 139, which affects **all** users at once |
| Aug 31 2026 | All remaining MV2 extensions removed from the Chrome Web Store (Chrome ≤138 installs keep running, no updates) |

The rationale was announced in 2019: "Web Request and Declarative Net Request: Explaining the impact on
Extensions in Manifest V3" ([Chromium Blog, 12 Jun 2019](https://blog.chromium.org/2019/06/web-request-and-declarative-net-request.html)).

**Citable failure modes:**

- **EFF's objection.** "We reject declarativeNetRequest as a replacement for blocking webRequest.
  Instead, Google should let developers choose to use either API" — arguing the change hurts privacy and
  security extensions ([EFF, Dec 2021](https://www.eff.org/deeplinks/2021/12/googles-manifest-v3-still-hurts-privacy-security-innovation)).
  Mozilla's own documentation frames DNR as the more private option because "extensions do not read the
  network requests made on the user's behalf"
  ([MDN](https://developer.mozilla.org/en-US/docs)).
  Both can be true; the disagreement is about *who* holds the filtering logic.
- **uBlock Origin.** Raymond Hill declined to port the original uBlock Origin to MV3, citing its
  limitations; users are pointed to **uBlock Origin Lite**, which drops dynamic filtering and
  script-blocking ([uBlock Origin Lite](https://ublockorigin.com/lite)). Chrome does not auto-replace
  the original with Lite. In practice this is the single most-cited casualty of the migration, and the
  enterprise policy escape hatch (`ExtensionManifestV2Availability`) is the only thing that extended its
  life on managed fleets.
- **The structural lesson**: `webRequest`'s blocking mode was **imperative** — code decided per request.
  DNR is **declarative** — rules are data the browser evaluates. Moving logic from code into data is
  what made both the privacy argument and the rule-budget limits possible.

**Transferable pattern:** a declarative, budgeted rule language (`declarativeNetRequest`) plus
install-time warnings and runtime-granted optional permissions is how a host keeps a powerful capability
reviewable — and the MV2 sunset shows the cost of that choice: the extensions that needed imperative
per-request logic could not be ported at all.

---

## Cross-cutting synthesis

| | Isolation | API versioning | Core-bloat control | Worst citable failure |
|---|---|---|---|---|
| **VS Code** | Separate Extension Host process; **not a security sandbox** | `engines.vscode` semver gate + proposed APIs unpublishable | `contributes` + `when`-clause context language | Apr 2025 C/C++ extension locked out of forks |
| **Obsidian** | **None — in-process, full trust** | `minAppVersion` only | None: plugin supplies its own UI and commands | PhantomPulse RAT via a synced vault's Shell Commands config |
| **Figma** | Main-thread sandbox (no DOM/network) XOR UI iframe; message passing only | `api` pinned per plugin, never auto-upgraded; `enableProposedApi` dev-only | Scene-not-DOM + manifest capability/permission/domain declarations | 2023 dynamic-page migration: sync accessors now throw |
| **Chrome MV3** | Service worker, terminated after 30s idle | `minimum_chrome_version`; MV2→MV3 sunset | Declarative rules as data, with hard budgets | uBlock Origin cannot be ported; EFF rejection |

Three patterns recur and are worth adopting directly:

1. **Declare capabilities, don't grant them.** Figma's `permissions` + `capabilities` + `networkAccess`
   and Chrome's `optional_permissions` both make the plugin's reach a manifest fact that users and
   reviewers can read *before* install. Obsidian has none of this and it is exactly what its incident
   exploited.
2. **Stage unstable API behind an unpublishable track.** VS Code's proposed APIs cannot be published at
   all; Figma's `enableProposedApi` does not work in published plugins. Both let the API evolve without
   ever owing a compatibility promise.
3. **Keep feature logic out of the core by making the contract data.** `contributes` + `when` clauses,
   Figma's manifest `menu`/`parameters`, and Chrome's DNR rules all push per-feature decisions into
   declarative JSON that the host evaluates. The core then needs no knowledge of any particular plugin —
   which is the microkernel property PetDock is after.

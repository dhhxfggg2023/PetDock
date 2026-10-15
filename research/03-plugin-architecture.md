# PetDock — Plugin Architecture: DSH Reverse-Engineering + Prior Art + Proposal

> Research note 03. Reconnaissance only — no PetDock code, no scaffolding.
> Part 1 is read-only analysis of the installed DSH app on this machine; nothing under `E:\tools\DSH` was modified.
> Part 2 is web research; every non-obvious claim carries a markdown link, and anything unconfirmed is marked **unverified**.

**Evidence base for Part 1.** The packaged app `E:\tools\DSH\resources\app.asar` (121,348,951 bytes) was extracted read-only to `E:\tools\DSH_extract` with `npx --yes @electron/asar extract` (exit 0). The extracted tree plus the user profile at `C:\Users\dhhxfggg\.dsh\profiles\desktop` are the two sources. Every path cited below is a real file on disk that was read.

---

## Part 1 — How DSH actually does it

### 1.0 The one-sentence answer

DSH is not "an app with a plugin system". It is **a Cordis dependency-injection kernel plus a YAML composition file**, where *every* capability — the LLM, the filesystem, the shell, the web server, the UI, the agent loop itself — is a plugin mounted by that file. The kernel is roughly one package (`@deepseek-ai/cordis`, 4.0.4, 9 source files); the other 288 `@deepseek-ai/*` packages in the runtime are plugins.

### 1.1 The manifest: `package.json` with a `dsh` key

There is **no separate `plugin.json`**. A DSH plugin is an npm package whose `package.json` carries a top-level `dsh` object. The shared type declarations live in `@deepseek-ai/dsh-package-manifest`, whose README gives the canonical shape (`E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-package-manifest\README.md:34-43`):

```ts
const manifest: DshPackageManifest = {
  name: 'example-dsh-plugin',
  version: '1.0.0',
  engines: { node: '>=24', dsh: '0.1.5-alpha.1' },
  dsh: {
    manifestVersion: 1,
    bundle: { patch: './cordis.patch.yml' },
    client,
  },
}
```

Field-by-field, from `dsh-package-manifest\README.md:50-55` and real installed plugins:

| Field | Meaning |
|---|---|
| `dsh.manifestVersion` | Manifest **format** identifier. Currently `1`. Explicitly *independent of the npm package version and of the session format version* (`README.md:52`). |
| `engines.dsh` | Author-declared compatible DSH versions as a **SemVer range**, sitting beside `engines.node`/`engines.npm`. |
| `dsh.bundle.patch` | One patch-file path **or an ordered list** of paths, each relative to the package root. This is what makes a package a *bundle*. |
| `dsh.client` | Declares the package has a **browser half**: `platform` (required string, e.g. `'web'`), `inject` (module names it wants), `external` (non-baseline module requests), `immediately` (boolean). Validated at `dsh-client-modules\lib\index.js:63-68`. |
| `package.json.icon` | Read as an **image data URL** for plugin display metadata (`dsh-app-boot\README.md:85`). |
| `dsh.compatibility` | **Not** in the official type. A community convention seen in `dsh-streamfold@0.7.5`: `{ dsh: ">=0.2.0-rc.2 <0.3.0", dshReleases: {...}, profiles: ["web"] }`. |

Two things the manifest notably does **not** have: any `activationEvents`, and any `permissions` field. Both are handled elsewhere (§1.4, §1.5).

**A load-bearing honesty from the vendor** (`dsh-package-manifest\README.md:93`):

> **Compatibility is declarative.** Current installers and loaders do not enforce `dsh.manifestVersion` or `engines.dsh`; declaring a range does not reject incompatible hosts or validate SemVer syntax.

The *enforced* check is a different one: before importing a plugin, DSH checks the package's **`peerDependencies`** on `@deepseek-ai/dsh` and `@deepseek-ai/dsh-*` against the single runtime version from `getDshRuntimeVersion()` (`dsh-app-boot\README.md:52`). `workspace:^`/`~`/`*` all mean "this runtime". Missing DSH peers impose no constraint; **invalid ranges are incompatible**. And the same README adds the sentence a plugin-host designer must not skip:

> These checks use peer declarations, not `engines.dsh`, and **are not a sandbox against malicious package code**.

### 1.2 Discovery, loading, enabling, versioning

**Discovery** is by filesystem + a YAML composition, not a registry scan. A *profile* lives at `$DSH_HOME/profiles/<name>` (here `C:\Users\dhhxfggg\.dsh\profiles\desktop`) and consists of:

- `package.json` — the profile's own manifest, whose `dsh.profile.bundles` is an **ordered list** of bundle package names;
- `cordis.yml` — the resolved composition (a list of `{id, name, disabled, config}` entries), 1654+ lines on this machine;
- `cordis.patch.yml` — the user's override layer;
- `node_modules/` — the installed plugins.

The real `C:\Users\dhhxfggg\.dsh\profiles\desktop\package.json`:

```json
{
  "name": "dsh-profile-desktop",
  "private": true,
  "dependencies": {
    "@nanmicoder/dsh-agent-teams": "0.1.23",
    "aegis": "git+https://github.com/GanyuanRan/Aegis.git",
    "billion-context": "0.1.197",
    "dsh-code-mode": "link:E:\\projects\\dsh-code-mode",
    "dsh-our-free-model": "github:Ebony-Vinyl/dsh-our-free-model",
    "dsh-plugin-wallpaper-engine": "1.3.2",
    "dsh-streamfold": "^0.7.5",
    "dsh-web-search-free": "1.8.0",
    "dshmarket": "1.66.21"
  },
  "dsh": { "profile": { "bundles": [ ...12 names... ], "patchReload": "live" } }
}
```

**Bundles vs plugins — the key distinction.** A *plugin row* is one `{id, name, config}` entry in a composition file. A *bundle* is an npm package that **ships a patch file** which inserts rows. The composition is layered (`dsh-app-boot\README.md:50`):

```
[bundle layers in dsh.profile.bundles order] → [profile cordis.patch.yml] → [home-level cordis.patch.yml]
```

A bundle's `dsh.bundle.patch` names one file or an ordered list; `bundlePatchFiles` validates the declaration, `bundlePatchPaths` resolves it, and the layer concatenates the patch lists in order. A real bundle patch is 4 lines — `C:\Users\dhhxfggg\.dsh\profiles\desktop\node_modules\dshmarket\cordis.patch.yml`:

```yaml
# dsh bundle patch: inserts this plugin into a profile's layer stack.
- insert:
    - id: dsh-market
      name: 'dshmarket'
```

Installing a package that declares **no** `dsh.bundle` produces exactly this warning (`dsh-plugin-manager\lib\index.js:251`):

> `dsh: warning: <name> declares no dsh.bundle — installed as a plain dependency, not a profile layer`

**Loading** is the Cordis Loader (`@deepseek-ai/cordis-plugin-loader`). It owns an `EntryTree`, imports plugin modules **by name**, applies config, and keeps the running graph in sync with entry updates. Entry options (`cordis-plugin-loader\README.md`):

| Field | Description |
|---|---|
| `id` | Stable id for resolving, updating, removing the entry |
| `name` | Module specifier imported by the loader |
| `config` | Config passed to the plugin |
| `group` | Marks the entry as a group whose `config` is a child entry list |
| `disabled` | Stops the entry and prevents it from starting |
| `inject` | Adds required services or intercept config for this entry |

**Enable/disable** is per-row and per-bundle. The Plugin Manager "toggle updates only `disabled` in the last matching override in the profile's `cordis.patch.yml`, or appends an override when none matches. Matching uses the entry id and any module-name assertion. A bundle toggle changes `package.json`'s ordered `dsh.profile.bundles` list" (`dsh-plugin-manager\README.md:40`). A subtlety worth stealing: *disabling retains the dependency; enabling appends the bundle at the end, which can change configuration precedence.*

**Installation** is npm-registry-based, delegated to pnpm (`dsh-plugin-manager\README.md:46-54`): `inspect(spec)` asks the registry through `pnpm view`; `installBundle` checks GitHub repos with `git ls-remote` before pnpm, then runs `pnpm add --registry <r>`. Registries are asked **in turn** — `options.registry` → configured `registry` → `fallbackRegistries` (default `['https://registry.npmmirror.com/']`) — and a registry outside the configured set "is asked alone, so a private registry never falls through to a public one". Specs accepted: registry name, absolute path, git address, tarball. On failure the operation **restores `package.json` and `pnpm-lock.yaml` as snapshotted before pnpm ran**.

**Versioning / compatibility enforcement** has three distinct layers, and it matters that they are distinct:

1. **Declarative** (`engines.dsh`, `dsh.manifestVersion`) — recorded, **not enforced**.
2. **Enforced peer check** — `peerDependencies` on `@deepseek-ai/dsh*` resolved against the single runtime version; an incompatible peer **rejects the install before pnpm runs** ("so nothing is downloaded and no build script runs"), and at startup an incompatible bundle is **skipped** and listed in `skippedBundles`.
3. **Exact-version exemptions** — the escape hatch, in the profile's own `compatibility.json` (not `package.json`), mapping exact `package-name@version` to a list of exact DSH runtime versions. Neither a plugin upgrade nor a DSH upgrade inherits permission; the tool requires `acceptRisk: true` after warning about "crashes or data loss" (`dsh-plugin-manager\README.md:65`).

Note the granularity: compatibility is per **bundle**, checked when the profile's bundle layers load; and the README is explicit that these boundaries "do not cover plugins another embedder mounts through its own `ctx.plugin` call".

### 1.3 The contribution model: services, events, slots — not a `contributes` map

DSH has **no declarative `contributes` block**. Everything a plugin provides is imperative and typed, through four mechanisms:

**(a) Services — the DI spine.** A plugin declares `inject` (what it needs) and `provide` (what it offers). From `cordis\src\registry.ts:98-111`:

```ts
export interface Base<T = any> {
  name?: string
  Config?: StandardSchemaV1<any, T>   // standard-schema validator, applied before start
  inject?: Inject                      // services required; plugin loads only while all available
  provide?: string | string[]          // service name(s) it provides
  intercept?: Dict<boolean>            // intercept config it consumes
}
```

A plugin can be a function `(ctx, config) => any`, a class `new (ctx, config) => any`, or an object with `apply(ctx, config)`. Registration is `ctx.plugin(plugin, config)`; `ctx.inject(deps, callback)` is shorthand that **unloads and re-runs the callback whenever a required service changes** (`registry.ts:300-302`). The `@Inject()` decorator exists for classes and methods (`registry.ts:37-60`). This is the single most important structural fact: **a plugin that needs a capability it cannot get simply never loads** — it waits, or its fiber fails.

**(b) Events — the hook surface** (§1.4).

**(c) Slots — the UI contribution model.** `@deepseek-ai/dsh-client-ui-slots` gives browser plugins typed UI regions with four kinds: `single` (one occupant), `list` (ordered entries), `keyed` (dispatch by key), `chain` (entries elect themselves). The discipline is explicit (`dsh-client-ui-slots\README.md:46`):

> **Declaring a slot is claiming it**: the registering entry becomes the only entry allowed to render that key, and registering into an undeclared slot, declaring an already-declared child, mounting one shared handle under two scopes, or registering a chain without `select` **throws at load**.

Chain entries carry a `priority`; selectors "run in ascending `priority` order (ties in registration order); the first non-null return elects its entry". An entry's disposer "collapses its declared child slots recursively — ledger rows, contributions, and store mounts die on one lifecycle axis".

**(d) Real contribution points, by name.** From the shipped packages: tools (`ctx.tools.register`), slash commands (`dsh-commands`, `dsh-command-*`), UI slots and pages, skills (`ctx.skills.register` / `registerProvider`), LLM providers (`dsh-llm-*`), session projections, storage backends (`ctx.storage`), spill stores (`ctx.spillStore`), directory pickers, telemetry exporters, web search providers, shell/terminal backends, subagent providers, and MCP servers (`dsh-mcp-client` registers discovered MCP tools into the same registry). Even **the agent loop** (`dsh-agent-loop`) and the **system prompt** (`dsh-system-prompt`) are plugins.

**Conflict resolution** differs by mechanism and is worth noting as a design lesson: slots **throw at load** on a duplicate declaration; skills are "**first-wins with a warning**" for same-name runtime registrations in one layer; tool guards are **monotonic** (§1.5); a second `ctx.spillStore` registration **fails** ("one implementation per context").

### 1.4 Lifecycle and the hook surface

**Two independent hook layers exist**, and conflating them would be a mistake.

**Layer 1 — Cordis fiber lifecycle.** Every plugin runs inside a *Fiber* with states `pending → loading → active → failed → unloading → disposed` (`dsh-host-plugin-inventory\README.md:32`). The kernel's own events (`cordis\src\events.ts:329-352`) are:

| Event | Signature | Mode |
|---|---|---|
| `internal/plugin` | `(fiber: Fiber) => void` | emit |
| `internal/status` | `(fiber, oldValue: FiberState) => void` | emit |
| `internal/config` | `(this: Fiber, config, next) => any` | **waterfall** |
| `internal/service` | `(this: Context, name, value) => void` | emit |
| `internal/update` | `(this: Fiber, config, noSave, next) => void` | **waterfall** — skip `next()` to veto |
| `internal/get` | `(ctx, name, error, next) => any` | **waterfall** |
| `internal/set` | `(ctx, name, value, error, next) => boolean` | **waterfall** |
| `internal/listener` | `(this: Context, name, listener, prepend) => void` | **bail** — non-null replaces registration |
| `internal/dispatch` | `(mode, name, args, thisArg) => void` | emit |

Five dispatch modes are supported and are the ordering primitive (`events.ts:32`): `emit` (sync, ignore returns), `parallel` (await all), `serial` (await in order until one bails), `bail` (sync until one returns non-null/non-false), `waterfall` (listeners wrap a final `next`; **not calling `next()` vetoes**, including the built-in behaviour). Ordering within a mode is registration order, with `{ prepend: true }` to jump the queue and `{ global: true }` to bypass context filtering. **Priority is therefore not a number — it is registration order plus a boolean.**

Crucially, **listeners are effects on a fiber**: `ctx.on()` returns a disposer, and "the listener is removed automatically when the fiber unloads" (`events.ts:279-281`). Plugin teardown is structural, not a convention.

**Layer 2 — the agent/tool pipeline** (`dsh-tools\README.md:105`). Every tool call runs a **fixed pipeline**:

```
tools/pre-execute   (extensible allow/deny/ask waterfall)
  → registered monotonic guards   (ctx.tools.guard)
  → tools/execute                 (around-dispatch: timeout, retry, metrics)
  → tools/post-execute            (inspect/replace content, attach context)
  → definition-owned finalizeContent
  → tools/result                  (observe-only, immutable final outcome)
```

`ctx.tools.restrict(filter)` applies an allow/deny mask to the global tools one agent inherits; "masks intersect, scoped registrations stay visible, and the restriction lifts when disposed". A tool definition carries a name, description, parameter schema, canonical `output` declaration, and `execute`; **model arguments are validated before execution**, and "invalid input becomes a normal error result".

**Layer 3 — the external-process hook protocol**, which is the genuinely novel part. `@deepseek-ai/dsh-hook-protocol` is a *dialect adapter*: it lets an existing Claude Code or Codex `hooks.json` run under DSH. Its documented event names are `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Stop`, `SessionStart`. Semantics (`dsh-hook-protocol\README.md:36-49,63`):

- **exit code 2 blocks** the prompt or tool call, with stderr shown as the reason;
- **any other exit code is a non-blocking failure** — the action proceeds and the failure is logged;
- a hook can **attach context** (extra text the model sees next request);
- a hook can request a run halt via `{"continue": false}` — **recorded but with no run-level effect** (a stated limitation, not a bug);
- a hook config selects events by name or pattern; absent/empty/`'*'` means all.

The merge rule is a **most-restrictive fold**: "`deny > ask > allow` precedence, keeps the first `continue: false` stop sticky, and accumulates context in hook order". And the design philosophy line is one PetDock should copy verbatim (`README.md:75`):

> **Never throw into the loop.** Every failure mode — malformed JSON, an invalid regex, an executor rejection — degrades to a contained outcome or a non-match, so a hook can never crash the calling turn.

Hook invocations are recorded as durable log-only session events `hook/invoked` / `hook/result`, paired by `handlerId`, and an invariant companion "rejects `hook/*` events appended outside an open turn, a result without a matching invoked, an unknown dialect, or a non-finite duration".

**Startup and reload failure policy** (`dsh-app-boot\README.md:94-107`) is a full matrix, and the distinction it draws is the important one:

| Failure | Optional entry | Required entry |
|---|---|---|
| Module import throws | Warn; continue | Stop startup |
| Config schema validation fails | Warn; continue | Stop startup |
| Sync/async `apply()` throws | Warn; continue | Stop startup |
| An injected service is unavailable | Warn; **entry waits for its dependency** | Stop startup |
| Unhandled rejection from detached async work | **Fatal: dispose app, exit nonzero** | Fatal |
| Uncaught exception from a sync callback (timer, stream `'data'`) | **Fatal: dispose app, exit nonzero** | Fatal |

The required list is small and named: `agent-loop`, `webserver`, `modules`, `connection`, `headless-runner`, `acp`, `sdk-jsonrpc-server`. And the honesty is notable: **"Required ids absent from a profile and disabled required entries do not affect startup"** — you can ship a profile with no agent loop at all.

### 1.5 Permissions, capabilities, and the tool policy

This is where DSH is most interesting and most limited, so it deserves precision.

**There is no per-plugin permission manifest.** Grepping the entire installed runtime for `permissions`, `capabilities`, or `toolPolicies` in package manifests returns nothing: the only `toolPolicies` reference anywhere in this environment is in *my own* tool description, describing a **deployment-owner-declared** concept (`config toolPolicies`) that is not present in the extracted runtime's READMEs.

What exists instead is a **session-scoped sandbox mode** plus a **per-call approval**, and — separately — a **per-agent tool mask**:

1. **Sandbox mode** (`dsh-sandbox-policy`, config at `cordis.yml:127-131`): `mode: process.env.DSH_PERMISSION_MODE ?? 'workspace-write'`, `workspaceRoot: process.cwd()`. The mode is one of `read-only` / `workspace-write` / `danger-full-access`, and it is stored as a **log-only `sandbox/mode` session event**; `effective = explicit grant ?? fold(events) ?? deployment default`, so an override survives restart by replay and two sessions never see each other's state. Enforcement backends are separate plugins (`dsh-fs-sandbox`, `dsh-bash-sandbox`, `dsh-pwsh-sandbox`, `dsh-sandbox-windows-acl`), and the *tool* layer renders refusals as `[sandbox: file access denied under <mode> mode]` plus an escalation hint.

2. **Approval** (`dsh-user-approval`, `cordis.yml:140-145`): `policy: (DSH_PERMISSION_MODE === 'danger-full-access') ? 'never' : 'ask'`. Permission *presets* bundle the two (`dsh-permission-presets`): `read-only → sandbox read-only + approval ask`; `workspace-write → sandbox workspace-write + approval ask`; `danger-full-access → sandbox danger-full-access + approval never`.

3. **Per-agent tool restriction** (`dsh-tools`): `ctx.tools.restrict(filter)` intersects masks; `ctx.tools.guard(guard)` registers a **monotonic synchronous guard** after the extensible waterfall — "a returned reason denies the call, and **no later listener can turn that denial back into permission**". That monotonicity is the one real security invariant in the tool layer.

4. **Plugin-manager actions are gated**: "Every tool action requires `danger-full-access` or approval for that call. Under lower sandbox modes, `ask` requests approval; `never`, rejection, cancellation, or an unavailable approval channel prevents execution." And the sentence that names the trust boundary outright:

> Profile changes persist across sessions, and **installed Host code executes in-process outside the workspace sandbox.**

**So the honest answer to "what is a tool policy, and how does the host enforce it"**: in DSH, *the plugin itself is not restricted at all*. Once installed, a plugin is in-process Node with full trust. What is restricted is **what the model may do through tools** (masks + monotonic guards), **what the session may touch on disk** (sandbox mode), and **what a human must approve** (approval policy). A plugin can bypass all three, because a plugin *is* the host.

### 1.6 Keeping the core small

The mechanism is the Cordis kernel + the bundle/profile split, and it is genuinely thin:

- **Kernel**: `@deepseek-ai/cordis` — 9 source files (`context.ts`, `events.ts`, `fiber.ts`, `index.ts`, `logger.ts`, `reflect.ts`, `registry.ts`, `service.ts`, `utils.ts`), ~1,600 lines total, dependencies only on `@standard-schema/spec` and `@deepseek-ai/cosmokit`.
- **Everything else**: 288 `@deepseek-ai/*` packages in `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\` — including the UI, the LLM adapters, the sandbox, the agent loop, and the tools.
- **The composition is data.** `cordis.yml` (1654+ lines) is the whole app. Changing an app surface means changing a profile, not code: `web`, `headless`, `acp`, `sdk`, `sdk-minimal` "start distinct compositions from the same launcher" (`dsh-app-boot\README.md:50`).
- **The Electron shell is a separate product.** `E:\tools\DSH_extract\package.json` is `@deepseek-ai/dsh-desktop` 0.2.0-rc.2 (private, `main: lib/main.js`, 490 KB bundled) with its own tiny dependency set (`electron-updater`, `semver`, `ws`, `@deepseek-ai/dsh-api-gateway`, `@deepseek-ai/cordis`), and `E:\tools\DSH_extract\dsh\package.json` is `@deepseek-ai/dsh-desktop-runtime` — the bundled runtime. So the layering is: **Electron shell → bundled DSH runtime → profile → bundles → plugins**.
- **Grouping and isolation are first-class.** The Loader has two builtins, `cordis:include` and `cordis:group`; "a group row gives one `isolate` realm to a provider and its consumers together". Real example from `cordis.yml`: the `compaction` group is `isolate: { compaction: true, toolResultPruner: true }`. This is how a plugin's service instance is kept from leaking into an unrelated tree.

**How a plugin is expected to be written**: modern ESM (`"type": "module"`), authored in TypeScript against the published `DshPackageManifest` types, **published as built JavaScript** (`dsh-client-modules\README.md:50`: "the host serves built client bundles, so `pnpm run build` must have produced each `lib/client.js` before launch; a missing bundle fails activation loudly with one build instruction and a package/path list"). There is no in-app transpile step and no sandboxed interpreter. A plugin with a UI has **two halves in one package**: the Node half at the package root, the browser half behind `exports["./client"]`, discovered through `dsh.client` in the manifest.

### 1.7 What is genuinely good here — and what I would not copy

**Good, and worth copying:**

1. **A DI kernel makes "plugin" mean "capability", not "add-on".** Because the LLM, the shell, and the UI are all just services, a plugin's dependency on them is a typed declaration (`inject`) that the loader can *wait on* rather than a runtime lookup that can be null. An entry whose injected service is unavailable **waits** instead of crashing.
2. **The composition is a versioned data artifact.** `cordis.yml` + ordered patch layers + `dsh.profile.bundles` means an app surface is diffable, reviewable, and reproducible; `--dump-config` prints the exact effective configuration before boot, and `--dump-config-schema` reads each composed module's schema.
3. **Lifecycle is structural, not conventional.** Listeners, tools, slots, and store handles are all *effects on a fiber* and die with it. There is no "uninstall hook" a plugin can forget to implement.
4. **Failure containment is designed, documented, and differentiated by criticality.** Optional plugin fails → warn and continue; required plugin fails → stop startup; a *detached* async rejection → fatal. That third rule is what stops a half-dead plugin from silently corrupting state.
5. **"Never throw into the loop."** The hook protocol degrades every failure (bad JSON, bad regex, executor rejection) into a contained outcome. A plugin author cannot crash the host by accident.
6. **Honest limits in the docs.** "Compatibility is declarative." "These checks are not a sandbox against malicious package code." "`HookOutput.updatedInput` is parsed but not honored." "A folded halt has no run-level effect." This is unusually trustworthy documentation and it is itself a product feature.
7. **Monotonic guards.** Once a guard denies, no later listener can re-allow. That is the correct shape for a security decision and it is rare in plugin systems.

**Weaknesses I would not copy:**

1. **No isolation whatsoever, stated as a fact rather than a caveat.** "Installed Host code executes in-process outside the workspace sandbox." Combined with a plugin manager that installs from npm *and* from arbitrary git URLs and tarballs, *and* pnpm build scripts that run with the host user's permissions, this means the plugin API is not a security boundary. For DSH (a developer tool whose users are the same people who write the plugins) that is a defensible trade. For a consumer desktop-pet platform that expects third-party pets from strangers, it is not.
2. **Declarative-but-unenforced compatibility.** Shipping `engines.dsh` and `manifestVersion` while the loader ignores them trains authors to write fields that mean nothing. Either enforce or drop them.
3. **The enforced check is `peerDependencies`, which conflates "I need this library version" with "I am compatible with this host".** It works, but it is a proxy for a real API-version contract, and it produces the awkward `compatibility.json` exemption mechanism as a patch over the top.
4. **Version granularity is per bundle, checked at composition time**, and explicitly does not cover dynamically mounted plugins. A bundle is a coarse unit; one bad row skips the whole layer.
5. **Configuration precedence is order-dependent and surprising.** The Plugin Manager README admits that re-enabling a bundle "appends the bundle at the end, which can change configuration precedence", and `link:`/`git:` dependencies in the profile manifest make the effective version depend on the machine. `cordis.patch.yml` files accumulate `.bak-<timestamp>` siblings (`cordis.patch.yml.bak-1790776382011` is present on this machine).
6. **A global patch layer cannot be scoped to a preset.** The `dsh-code-mode` patch file contains a long comment explaining that `auto: false` "cannot be set per preset from configuration" because the profile patch is a global layer, so the switch had to be moved into a module. That is a real expressiveness limit in the override model, discovered the hard way.
7. **No plugin-level capability declaration means no plugin-level consent UI.** A user installing a DSH plugin cannot be told "this plugin wants to read your files and make network requests", because nothing asks.

---

## Part 2 — Prior art

> Detailed per-system notes, with sources, are in the sibling files:
> - `research\_parts\A-vscode-obsidian-figma-chrome.md` — VS Code, Obsidian, Figma, Chrome MV3
> - `research\_parts\B-obs-blender-godot-unity-emacs-neovim.md` — OBS, Blender, Godot, Unity, Emacs Lisp, Neovim Lua
> - `research\_parts\C-protocol-and-sandbox.md` — Kubernetes CRDs, Terraform providers, WASM Component Model/WASI p2, Extism, Wasmtime, QuickJS, isolated-vm, Deno permissions, Lua sandboxes
> - `research\_parts\D-distribution-and-failures.md` — Steam Workshop, itch.io, mod.io, npm, Open VSX, and the five failure modes with named incidents

### 2.1 The four architectural camps

Every system surveyed lands in one of four camps. The camp determines what is possible, and the choice is nearly irreversible.

| Camp | Isolation | API versioning | Core-bloat control | Examples |
|---|---|---|---|---|
| **In-process, full trust** | None — plugin code shares the address space | Manifest `minAppVersion` / advisory | Convention and review only | Emacs Lisp, Neovim Lua, Obsidian, DSH, Blender addons |
| **Separate trusted process** | Process boundary — **liveness, not security** | `engines.vscode` semver + proposed-API staging | Extension Host + declarative `contributes` | VS Code, Chrome MV3 (service worker) |
| **Native ABI** | Same process, binary interface | ABI version macros; breaks on major bumps | Registration structs + plugin dirs | OBS, Godot GDExtension, Unity asmdef |
| **Protocol / capability sandbox** | Process, gRPC, or wasm instance with explicit capability handles | Protocol version negotiated at handshake | The core only speaks the protocol | Terraform providers, Kubernetes CRDs, WASI p2, Extism, Wasmtime |

Two findings from the research deserve to be stated plainly here.

**VS Code's Extension Host is not a security sandbox.** It exists so a hung or buggy extension cannot freeze the UI — a *liveness* boundary. An extension still runs with the user's privileges and can read the filesystem. Treating "we run plugins in a separate process" as a security claim is the single most common confusion in this design space, and PetDock must not repeat it.

**The update channel is the attack surface, not the plugin API.** Wiz's 2025 analysis of VS Code extension marketplaces found 550+ secrets in 500+ extensions, 100+ valid Azure DevOps PATs across ~85k installs, and 30+ Open VSX tokens across ~100k installs — and concluded that **auto-update is what converts one stolen publisher token into mass compromise**. The same shape recurs in Shai-Hulud (npm), Cyberhaven (Chrome), and the 3CX supply chain. A plugin architecture that gets isolation right and signing wrong is still compromised.

### 2.2 Transferable patterns, by precedent

| Pattern | Precedent | What PetDock takes |
|---|---|---|
| Declarative capability manifest, imperative implementation | VS Code `contributes` | Manifest declares *what kinds* of things a plugin contributes (so the host can show, enable, and warn before running code); the code that implements them is imperative |
| Activation on demand | VS Code inferred activation since 1.74/1.76 | No `activationEvents` list to maintain; the host derives activation from declared contributions and lazily imports |
| Two capability domains joined by a bridge | Figma (scene XOR DOM, structured-clone `postMessage`) | Pet behavior runs in a domain with **no** direct handle to the host's scene graph; it gets a message channel |
| API version pinned per plugin, never auto-upgraded | Figma `api` field | A plugin's API surface is frozen at the version it declared |
| Declarative rules as data with hard budgets | Chrome MV3 `declarativeNetRequest` | Where a plugin's effect can be expressed as data (pet layout zones, trigger tables), express it as data so the host can validate and budget it |
| Grant at use, not at install | Chrome `optional_permissions`; the Felt et al. permission-fatigue literature | Never an install-time wall of checkboxes; ask when the pet first tries to touch the network, scoped to a concrete object, always visible and revocable, default deny |
| Stable C ABI so bindings survive engine upgrades | Godot GDExtension (`gdextension_interface.h`) | Version the *host-facing ABI*, not the host |
| Protocol version negotiated at handshake | Terraform `tfplugin5`/`tfplugin6` over gRPC | A plugin and host that disagree on protocol fail at handshake with a named reason, not at first call |
| Capability handles instead of ambient authority | WASI preview 2 preopens | A plugin can only touch what it was handed |
| Fuel metering / epoch interruption | Wasmtime | Preemption for untrusted behavior loops — a pet that spins must be stoppable |
| Memory limits per instance | QuickJS `JS_SetMemoryLimit`; V8 isolates | Per-plugin memory ceiling |
| Explicit permission scopes | Deno `--allow-read=<path>` | Path- and host-scoped grants, not booleans |
| Content-addressed local mirror, yank-never-delete | Atom's post-sunset package death; Steam Workshop author deletion | Installed plugins keep working offline and after a takedown |
| Versioned API + deprecation window + conversion shim | Kubernetes API deprecation policy | A served API version is never removed without a shim and a published clock |

### 2.3 The five ways plugin architectures fail

Each of these is a structural failure mode with named, citable incidents. Full detail and links are in `research\_parts\D-distribution-and-failures.md`.

**1. Trust-boundary collapse / supply-chain compromise.**
*Mechanism:* the real boundary is the **publisher credential plus the auto-update channel**, not the plugin API. A plugin that can execute code at startup, with the host's credentials, and updates itself silently is a remote code execution channel with a version number.
*Named examples:* Shai-Hulud npm worm (Sept 2025, 500+ packages, then 700+ in the November wave, with GitHub Actions persistence so uninstalling the package does not end it — [CISA alert](https://www.cisa.gov/news-events/alerts/2025/09/23/widespread-supply-chain-compromise-impacting-npm-ecosystem)); xz-utils CVE-2024-3094 (a maintainer *role* captured over years, not a credential stolen once); Cyberhaven Chrome extension 24.10.4 (~35 extensions / 2.6M users); GhostAction (327 users, 817 repos, 3,325 secrets).
*Rule:* **sign every artifact and make the update the signed event; run plugin code in a separate restricted process with no host credentials; make startup execution an explicit, revocable capability; tombstone vacated namespaces.**

**2. API breakage / ecosystem reset.**
*Mechanism:* the replacement API ships before it is complete, or the old one is retired on a date rather than on a capability.
*Named examples:* Firefox 57's XUL→WebExtensions flag day (2017); Chrome MV2→MV3, gate closed Jan 2022 and full removal Aug 31 2026 — four and a half years, and it still disabled working extensions ([Chrome timeline](https://developer.chrome.com/docs/extensions/develop/migrate/mv2-deprecation-timeline)); Blender 2.79→2.80 invalidated most addons; OBS 27→28 ABI break.
*Rule:* **version the API independently of the host; serve N−2 shims; start the deprecation clock only when the replacement is feature-complete.**

**3. Permission fatigue / consent theatre.**
*Mechanism:* an install-time prompt that appears for most plugins carries no signal, so users click through — and once trained, they click through the prompt that mattered.
*Named examples:* Felt et al. 2012 found Android users ignore permission warnings and comprehend them poorly ([UC Berkeley](https://www2.eecs.berkeley.edu/Pubs/TechRpts/2012/EECS-2012-26.html)); the 2011 companion found all-domain permissions on more than half of extensions, so the warning distinguished nothing ([USENIX](https://www.usenix.org/event/webapps11/tech/final_files/Felt.pdf)); Bongard-Blanchy et al. 2022 found roughly half of permission requests judged unnecessary.
*Rule:* **never ask at install; grant at use, scoped to a concrete object, always visible and revocable, default deny.**

**4. Core bloat / plugin capture.**
*Mechanism:* the plugin API is too weak to express what the platform's own features need, so first-party features get written against private APIs — and the public API stops being the real one.
*Named examples:* Jenkins plugin dependency hell, with near-universal plugin-to-plugin runtime dependencies, resolved only when the CDF began covering infrastructure costs in Jan 2020 ([Harness](https://www.harness.io/blog/dependency-plugin-hell-jenkins), [Jenkins](https://www.jenkins.io/blog/2020/08/04/cdf-graduation)); WordPress absorbing plugin functionality into core; VS Code absorbing Copilot Chat.
*Rule:* **ship first-party features as plugins on the same public API; widen the API instead of adding a core special case; publish the API's known gaps.**

**5. Distribution abandonment / bit-rot.**
*Mechanism:* content that only exists at a remote URL, or only at the author's discretion, disappears — and a plugin with no offline path takes its user's data with it.
*Named examples:* Atom's package management stopped working at sunset, and separately GitHub's Jan 2023 certificate revocation **bricked Atom 1.63.0/1.63.1 outright** ([GitHub](https://github.blog/news-insights/product-news/sunsetting-atom/)); Steam Workshop items deleted by their authors; Blender addons abandoned across the 2.8 and 4.2 breaks.
*Rule:* **content-addressed local mirror with offline reinstall; yank, never delete; an explicit compatibility range; abandonment as a first-class visible state.**

---

## Part 3 — Recommended architecture for PetDock

The goal — "万物皆可插件", every pet *format* is a plugin, and the SDK is itself a product — sets the requirements: the core must be tiny, a pet plugin must be unable to crash or compromise the dock, and a plugin author must be able to trust that their plugin keeps working.

Each choice below names its precedent and, where DSH already made the choice, whether PetDock should follow.

### 3.1 The shape

```
PetDock host (small, audited)
├── kernel: service registry + lifecycle + event bus      ← Cordis's role
├── renderer / scene compositor                           ← the only thing that draws
├── capability broker (fs, net, window, input, tray)      ← WASI-preopen shape
└── plugin runtime host (one per isolation tier)

Plugins (tiers):
  T1 pet-format      .pet / Live2D / Spine / sprite-sheet adapters   ← untrusted, sandboxed
  T2 behavior        interaction logic, schedules, reactions         ← untrusted, sandboxed
  T3 integration     OS hooks, other apps, network                   ← signed, brokered caps
  T4 host extension  new contribution points, new runtimes           ← first-party/signed only
```

**Justification.** VS Code needed an Extension Host because a bad extension must not freeze the UI; Figma needed two capability domains because scene mutation and DOM rendering have incompatible trust requirements; WASI preview 2 needed capability handles because ambient authority cannot be revoked. PetDock has all three problems at once — third-party animation code, third-party logic, and a UI that must stay at 60 fps — so it needs the boundary that all three arrived at independently, and it should get it right the first time rather than after an incident.

**What PetDock should NOT copy from DSH:** running plugins in-process with full trust. That is correct for a developer harness whose users write their own plugins. It is wrong for a consumer dock that will accept a pet from a stranger. DSH's own README says it plainly — the peer check "is not a sandbox against malicious package code" — and PetDock should take that sentence as a requirement rather than a caveat.

### 3.2 Manifest schema

A single `petdock.json` at the package root, plus `package.json` for npm identity. Two files, not one, because the plugin's *runtime* contract and its *package* identity change on different clocks — DSH's `manifestVersion`-independent-of-package-version instinct is right even though it did not enforce it.

```jsonc
{
  "manifestVersion": 1,              // format version of THIS file
  "id": "com.example.shimeji",       // reverse-DNS, globally unique, tombstoned on removal
  "name": { "en": "Shimeji", "zh": "桌面宠物" },
  "version": "1.2.0",                // semver of the plugin
  "kind": "pet-format",              // pet-format | behavior | integration | host-extension
  "api": "1.4",                      // PINNED host API version — never auto-upgraded (Figma precedent)
  "engines": { "petdock": ">=1.0 <2.0" },   // advisory range, and ENFORCED (unlike DSH)
  "entry": {
    "host": "./dist/index.mjs",      // ESM, prebuilt — DSH precedent: no in-app transpile
    "ui":   "./dist/ui.js"           // optional second half, like dsh.client
  },
  "capabilities": [                  // DECLARATIVE, brokered, default-deny
    { "id": "fs.read",  "scope": "plugin-data" },       // only its own directory, by default
    { "id": "net.http", "scope": ["api.example.com"] }, // host allowlist, not a boolean
    { "id": "window.overlay", "scope": "primary" },
    { "id": "pet.animate", "scope": "own-instance" }
  ],
  "contributes": {                   // declarative contribution points (VS Code precedent)
    "petFormats":  [{ "ext": [".pet", ".petproj"], "mime": "application/x-pet" }],
    "settings":    [{ "key": "shimeji.speed", "type": "number", "default": 1.0 }],
    "commands":    [{ "id": "shimeji.spawn", "title": "Spawn another" }],
    "uiPanels":    [{ "slot": "settings.pet.detail", "order": 20 }]
  },
  "limits": { "memoryMB": 256, "cpuMsPerTick": 4, "wallMsPerTick": 16 },  // Wasmtime/QuickJS precedent
  "integrity": { "algo": "sha256", "digest": "…" }   // content-addressed install (Atom lesson)
}
```

**Field rationale, each tied to a precedent:**

- `api` **pinned per plugin** — Figma's manifest carries `api` and never auto-upgrades a plugin. This is the single most important field for the stability promise in §3.7.
- `capabilities` **as objects with scopes, not booleans** — Deno's `--allow-read=<path>` and WASI's preopens. A boolean `"network": true` cannot be scoped, cannot be shown meaningfully, and cannot be partially revoked.
- `contributes` **declarative** — VS Code. It is what lets the host render a plugin in a list, validate it, and warn *before running any code*. DSH's imperative-only model cannot do this, which is why DSH's inventory is read-only and its install has no capability consent screen.
- `limits` **in the manifest** — Wasmtime fuel/epoch and QuickJS `JS_SetMemoryLimit`. Publishing the ceiling tells the author what to design for instead of surprising them at runtime.
- `integrity` — npm provenance/sigstore precedent, and the direct lesson of Wiz's finding that the update channel is the attack surface.
- `engines.petdock` — present **and enforced**, unlike DSH's `engines.dsh`. If it is not enforced it should not be shipped.

### 3.3 Discovery and loading

1. **Discovery is directory-based**, in a documented search order: `<userdata>/plugins/<id>/` then bundled `<app>/plugins/<id>/`. No registry scan at startup, no network on the load path. *Precedent:* Obsidian's `<vault>/.obsidian/plugins/<id>/` is the low-friction extreme and its friction is why it has thousands of plugins; the failure it invites is solved by signing, not by making discovery harder.
2. **The composition is a versioned data file**, not code — DSH's `cordis.yml` is the best part of DSH. A `petdock.lock` records every plugin id, version, and digest, and the host can dump the effective composition before boot.
3. **Layers, in precedence order**: built-in defaults → signed third-party bundle → user override file. *Precedent:* DSH's ordered bundle layers plus a user patch layer. **Fix DSH's two mistakes here**: (a) make an override *scopable* to one plugin instance, since DSH discovered a global patch layer cannot express a per-preset setting and had to move a switch into code; (b) make re-enabling a bundle **preserve its position** instead of appending, since DSH's append changes configuration precedence.
4. **Activation is on demand** — a plugin's code is imported when one of its declared contributions is first needed. *Precedent:* VS Code's inferred activation (1.74/1.76) removed the separate `activationEvents` list entirely; PetDock should never have one.
5. **One plugin's failure is contained.** A plugin that fails to load is marked `failed` with its reason, shown in the UI, and the dock keeps running. A plugin that fails *at runtime* is restarted a bounded number of times, then quarantined with its state preserved. *Precedent:* DSH's optional-vs-required failure matrix, minus the fatal-on-detached-rejection rule — which is correct for a single-process app but impossible to honor once plugins are isolated, because isolation is exactly what makes "fatal" unnecessary.

### 3.4 Contribution points (v1, deliberately few)

Keep the first version small and widen it later; the Jenkins lesson is that a weak API causes core bloat, but the opposite failure — a huge API designed before any plugin exists — causes a frozen mistake. Each point below is the *minimum* needed for "each pet format is a plugin".

| Point | Shape | Precedent |
|---|---|---|
| `petFormats` | file extension / MIME → loader | the product requirement |
| `behavior` | tick + event handlers over a pet instance | Figma's `main` thread |
| `uiPanels` | entries into host-declared slots | DSH slots; VS Code views |
| `commands` | namespaced `<plugin-id>.<command>` | VS Code; DSH commands |
| `settings` | typed schema, host renders the UI | VS Code `configuration` |
| `triggers` | data-only rules (time, idle, window event) | Chrome MV3 DNR: rules as data |
| `assets` | declared, content-addressed, size-budgeted | Steam Workshop / npm |

**Namespacing and conflict resolution**, stated as rules rather than left to discovery:
- Every contribution id is `<plugin-id>.<local-name>`; a plugin cannot register outside its own namespace.
- A `single`-occupancy slot **throws at load** on duplicate registration (DSH's slot rule — strict is right).
- A `list` slot is order-stable with an explicit `order` field and registration order as the tiebreak (DSH's chain rule).
- A `keyed` slot's duplicate key is a **load error naming both plugins**, never a silent override.
- A plugin may not contribute a contribution *point* (only `host-extension` tier may), which is what stops the API from being redefined by a plugin.

### 3.5 Lifecycle and hooks

Explicit, versioned, and small. Names are PetDock's own; the *shapes* follow the precedents.

```
install   → onInstall(ctx)            once, may be async
activate  → onActivate(ctx)           when first needed; must return; ctx is the capability handle set
tick      → onTick(ctx, dtMs)         behavior tier only, budgeted
event     → onEvent(ctx, evt)         evt ∈ { windowFocus, windowResize, idle, click, timer, petSpawn, petDespawn }
deactivate→ onDeactivate(ctx)         release everything; the host then revokes all capabilities
uninstall → onUninstall(ctx)          may be skipped (offline, crash) — never required for correctness
```

**Rules that matter more than the names:**

- **Every hook is optional.** A plugin that implements only `petFormats` needs no `onTick`.
- **Hooks never throw into the loop.** *Precedent: DSH's hook protocol, verbatim* — a malformed payload, a timeout, or a rejection degrades to a contained outcome with a logged reason. A pet plugin must not be able to break the dock's frame loop by returning garbage.
- **A hook that exceeds its `limits` is interrupted, not killed silently.** *Precedent:* Wasmtime's epoch interruption. The plugin gets a `budgetExceeded` event first, so it can degrade gracefully; a repeat offender is quarantined.
- **Ordering is registration order with an explicit `priority`, and only where ordering is meaningful.** DSH's five dispatch modes (`emit`/`parallel`/`serial`/`bail`/`waterfall`) are the right vocabulary and should be adopted as-is, including the `waterfall` semantics where *not calling `next()` vetoes* — that is the cleanest way to express "a plugin may suppress a host action".
- **Teardown is structural.** Every capability handle, subscription, and slot registration is owned by the plugin's activation and dies with it. *Precedent:* DSH fibers. A plugin cannot leak a listener into the host.
- **Guards are monotonic.** Where a plugin participates in an allow/deny decision (e.g. "may this pet appear on a locked screen?"), a denial is final and no later listener may re-allow it. *Precedent:* DSH's `ctx.tools.guard`, which is the one security invariant in DSH's tool layer and is worth copying exactly.

### 3.6 Permissions and isolation

**The tier decides the substrate; the capability list decides the grant.**

| Tier | Substrate | Why |
|---|---|---|
| T1 pet-format | **WASM component (WASI p2)** where the format can compile to it; otherwise a **QuickJS isolate** with memory + interrupt limits | Third-party parsing and animation code is the largest untrusted surface and the least trusted; it must be unable to touch the filesystem or network at all |
| T2 behavior | **QuickJS isolate per plugin**, or a worker process | Behavior is small, event-driven, and wants a familiar language |
| T3 integration | **Separate process**, brokered capabilities, signed | Needs OS access, so it needs a real process boundary and a signature |
| T4 host-extension | **In-process, first-party or signed-by-us only** | It extends the API itself; it cannot be sandboxed by that API |

**Why not one substrate for everything.** WASM gives the strongest capability model and the best performance story, but the Component Model and WASI preview 2 are still stabilizing, and a pet-format author's code is often an existing JS/TS library that cannot be compiled to wasm today. QuickJS gives a familiar language with real memory and interrupt limits and no ambient authority, at interpreter speed. Deno's permission model is the right *vocabulary* but runs a whole runtime per plugin, which is too heavy for a dock with a dozen pets. So: **WASM where it fits, QuickJS where it must, a process where OS access is needed, and never in-process for anything third-party.**

**Enforcement rules:**
- **Capabilities are handles, not flags.** A plugin receives a `net` handle that can only reach its declared hosts; there is no global `fetch`. *Precedent:* WASI preopens; Figma's `networkAccess.allowedDomains`.
- **Default deny, grant at use.** The first time a pet tries to reach the network, the user is asked, in context, naming the host. *Precedent:* Chrome `optional_permissions` and the Felt et al. finding that install-time walls are ignored.
- **Grants are visible and revocable in one place**, and revoking a grant deactivates the plugin rather than letting it fail unpredictably.
- **No plugin reads another plugin's data directory** unless both declare it and the user grants it.
- **Startup execution is a capability.** A plugin that wants to run before its pet is spawned must declare it and be granted it. *Precedent:* the Shai-Hulud lesson — persistence at startup is what turns a bad update into a permanent compromise.

### 3.7 Versioning and the API stability policy

This is the part that decides whether plugin authors trust the platform, so it is stated as a contract with teeth.

**The policy, in six clauses:**

1. **The host API is versioned independently of the app.** `api: "1.4"` in the manifest, and the host publishes a table of which API versions it serves. *Precedent:* Kubernetes API versioning, Figma's pinned `api`. An app release does not move the API version; an API change does.
2. **Within a major API version, nothing is ever removed and no behaviour silently changes.** Adding an optional field is fine; adding a *required* parameter is a new API version.
3. **A major version is retired only after a published window and a working shim.** Concretely: the host serves **N and N−1** (and ideally N−2), the old version is deprecated with a **named removal release**, and every deprecation logs a one-line warning naming the plugin, the API it uses, and the replacement. *Precedent:* Chrome's MV2→MV3 timeline is the cautionary version — four and a half years and it still broke working extensions; Firefox 57 is the flag-day version. PetDock should publish the window *with* the deprecation, not after it.
4. **The deprecation clock starts only when the replacement is feature-complete.** *Precedent:* Firefox 57's failure was retiring XUL before WebExtensions could do what XUL did. If a plugin cannot express something in the new API, the old one is not deprecated yet.
5. **Breaking changes are detected before the user sees them.** The host ships a **compatibility checker** the author runs in CI (declared API version vs. host-served versions vs. the plugin's own `engines.petdock`), and the install path refuses an incompatible plugin with a *named reason* rather than a stack trace. *Precedent:* Terraform's protocol handshake; DSH's peer check rejecting before pnpm runs — which is the right *behaviour* even though DSH's *mechanism* (peer deps as a proxy) is one to improve on.
6. **Unenforced declarations are not shipped.** If `engines.petdock` is in the schema, the loader enforces it. DSH ships `engines.dsh` and `manifestVersion` and documents that neither is enforced; that trains authors to write fields that mean nothing. PetDock should either enforce a field or omit it.

**Support matrix, published and machine-readable:**

| API | Host support | Status | Removal |
|---|---|---|---|
| `1.x` | all 1.x hosts | current | — |
| `2.x` | 2.0+ | current | — |
| `0.x` | 1.0–1.3 only | **deprecated since 1.4** | removed in 2.0, shim until then |

### 3.8 Distribution

1. **Registry is npm-compatible but curated for the *listing*, not for install.** A plugin is an npm package; the store lists only packages that pass automated checks (manifest validity, declared capabilities match observed behaviour, no obfuscated code, digest recorded). *Precedent:* Open VSX is the vendor-neutral model; npm is the transport; the store is a *view*, not a gate — because a gate that blocks install is a gate that will be bypassed.
2. **Signing and provenance.** Publisher identity is a key, not a name. Every artifact is signed; **the update is the signed event**, and a signature that does not match the previous publisher's key for that plugin id triggers an explicit user re-consent. *Precedent:* Wiz's finding on auto-update as the compromise multiplier; npm provenance/sigstore (with the honest caveat that provenance attests *where* a build ran, not that it is safe).
3. **Namespaces are tombstoned, never recycled.** A removed plugin id cannot be claimed by another publisher. *Precedent:* the ReversingLabs "name reuse" pattern; Shai-Hulud's GitHub Actions persistence.
4. **Yank, never delete.** A version can be unlisted but stays installable and reinstallable, and installed plugins keep working offline from a content-addressed local mirror. *Precedent:* Atom's post-sunset package death and the Jan 2023 cert revocation that bricked installed builds; Steam Workshop author deletion.
5. **Build scripts are never run on install by default.** Where a plugin needs a build, it must publish the built artifact; a postinstall script requires an explicit, per-package grant that is recorded and revocable. *Precedent:* npm's postinstall is "the single largest code-execution surface"; DSH's `approvedBuilds` gate is the right shape (per-name, persistent, requiring explicit approval) even though its service "does not verify conversation approval".
6. **Abandonment is a first-class visible state.** A plugin with no release in N months is labelled as such in the store and in the dock, and a plugin whose declared API version is deprecated shows the migration path. *Precedent:* Blender's dual-build workaround and the abandoned-addon wave after 2.8/4.2.

---

## Part 4 — Summary of the proposal, by precedent

| Decision | Precedent it follows | What it fixes from DSH |
|---|---|---|
| Cordis-style DI kernel; every capability is a plugin | DSH itself — its best idea | (kept) |
| Composition as versioned data (`petdock.lock`, layered overrides) | DSH `cordis.yml` + patch layers | Overrides are scopable to one plugin; re-enable preserves position |
| Declarative `petdock.json` with `contributes` **and** `capabilities` | VS Code `contributes`; Deno/WASI scopes | DSH has no capability declaration and no consent UI |
| Pinned `api` version per plugin; host serves N, N−1 | Figma `api`; Kubernetes API versioning | DSH's real check is `peerDependencies`, a proxy for an API contract |
| `engines.petdock` present **and enforced** | (DSH's field, fixed) | DSH documents its own fields as unenforced |
| Capability handles with scopes, default deny, grant at use | WASI preopens; Chrome optional permissions | DSH restricts the *model's* tools, never the *plugin* |
| Tiered isolation: WASM / QuickJS / process / in-process | Wasmtime, QuickJS, Figma bridge, VS Code host | DSH runs everything in-process with full trust |
| Memory, CPU, and wall-clock limits per plugin, interrupt-first | Wasmtime fuel + epoch; QuickJS interrupt handler | DSH has no per-plugin limits |
| Structural teardown; hooks are effects that die with activation | DSH fibers | (kept) |
| Monotonic guards on any allow/deny decision | DSH `ctx.tools.guard` | (kept, generalized) |
| Never throw into the loop | DSH hook protocol, verbatim | (kept) |
| Five dispatch modes incl. `waterfall`-vetoes | DSH event bus | (kept) |
| Signed artifacts, update-is-the-signed-event, tombstoned namespaces | Wiz; npm provenance; ReversingLabs | DSH installs from npm, git, and tarballs with no signature |
| Content-addressed local mirror; yank never delete | Atom's post-sunset death | DSH depends on the registry being up |
| No build scripts on install by default | npm postinstall history; DSH `approvedBuilds` | DSH runs pnpm lifecycle scripts with user privileges |
| Published deprecation window + shims, clock starts at feature-complete | Kubernetes policy; Chrome MV2 timeline; Firefox 57 | DSH declares compatibility but enforces a different thing |

---

## Appendix — Every DSH path cited in Part 1

All paths are on this machine and were read during this session.

**Extracted app (read-only):**
- `E:\tools\DSH_extract\package.json` — Electron shell `@deepseek-ai/dsh-desktop` 0.2.0-rc.2
- `E:\tools\DSH_extract\dsh\package.json` — bundled runtime `@deepseek-ai/dsh-desktop-runtime`, 289 deps
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\cordis\package.json` — kernel 4.0.4
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\cordis\src\registry.ts` — `Plugin.Base`, `Inject`, `RegistryService`
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\cordis\src\events.ts` — `DispatchMode`, `Events`, `EventsService`
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\cordis-plugin-loader\README.md` — `EntryTree`, entry options
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-package-manifest\README.md` — the manifest contract
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-app-boot\README.md` — profiles, bundles, admission, failure matrix
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-plugin-manager\README.md` — install/enable/remove, registries, exemptions
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-plugin-manager\lib\index.js` — `not-a-bundle` refusal, patch loading
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-hook-protocol\README.md` — hook semantics, merge, `hook/*` events
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-tools\README.md` — tool registry, execution pipeline, guards, restrict
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-client-ui-slots\README.md` — slot kinds, declaration discipline
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-client-modules\README.md` — `dsh.client`, lazy bundles, `/plugins` route
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-host-plugin-inventory\README.md` — inventory projection, fiber phases
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-sandbox-policy\README.md` — mode as a log-only event
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-hmr\README.md` — reload queue, config table
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-skill\README.md` — provider registry, invocation policy
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-typert-protocol\README.md` — `@Remote`, wire identities, `RemoteError`
- `E:\tools\DSH_extract\dsh\node_modules\@deepseek-ai\dsh-sdk-protocol`, `…\dsh-sdk-app`, `…\dsh-sdk-minimal` — the SDK packages

**User profile (read-only):**
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\package.json` — `dsh.profile.bundles`, 12 bundles
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\cordis.yml` — the 1654+ line composition
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\cordis.patch.yml` — user override layer
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\compatibility.json` — (referenced; not present on this machine)
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\node_modules\dsh-streamfold\package.json` — community manifest with `dsh.compatibility`
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\node_modules\billion-context\package.json` — community manifest, `dsh.bundle.patch`
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\node_modules\dshmarket\cordis.patch.yml` — a 4-line bundle patch
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\node_modules\dsh-code-mode\cordis.patch.yml` — a 255-line preset patch
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\.plugin-manager\logs\` — operation logs (`pnpm.log`, `git.log`)
- `C:\Users\dhhxfggg\.dsh\profiles\desktop\.dsh-market\state.json` — `{"disabled":[],"groups":{},"groupOrder":[],"region":"china","regionAuto":true}`

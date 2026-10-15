# Part C — Plugin over a Protocol, and the Sandbox Substrate

**Scope.** Prior-art research for **PetDock**, a Windows desktop-pet platform built as a microkernel host with everything-is-a-plugin. This part covers (1) the "plugin over a protocol" camp — Kubernetes CRDs/operators and Terraform providers — and (2) the **sandbox substrate** camp, which is the load-bearing half: WebAssembly Component Model + WASI preview 2, Extism, Wasmtime/Wasmer, QuickJS, `isolated-vm`, Deno permissions, and Lua sandboxes.

**Method.** Every non-obvious claim is linked to a primary source. Claims I could not verify against a primary source are marked **[unverified]**. Stability/maturity claims about the Component Model and WASI are treated with particular suspicion and are separated into their own section (§3.4) because the secondary literature on this topic is unreliable in a specific, predictable direction: it reports the *intent* of the standard as if it were the *status* of the standard.

For each system below: **isolation mechanism**, **how it versions the plugin ABI**, **how a bad plugin is prevented from taking down the host**, and **the transferable pattern for PetDock**.

---

## Group 1 — Plugin over a protocol

The defining property of this camp: the plugin is *not code loaded into the host*. The host and the plugin agree on a **data contract** (a schema, a protobuf service, a versioned REST resource) and the plugin runs wherever it likes. Nothing about the host's memory, type system, or crash domain is shared. The cost is that every interaction is a serialization boundary, and the entire versioning problem moves into the *contract* rather than into the *loader*.

### 1. Kubernetes CRDs + controllers/operators

#### 1.1 The extension mechanism

A **CustomResourceDefinition** (CRD) registers a new REST resource type with `kube-apiserver`. In `apiextensions.k8s.io/v1` a CRD declares `spec.versions` — each entry has `name` (`v1beta1`, `v1`, …), a `served` flag, a `storage` flag, and a **required** `openAPIV3Schema`. Exactly one version must be marked as the storage version. The API server then serves each `served` version at a real HTTP REST endpoint (`/apis/example.com/v1beta1`, `/apis/example.com/v1`) ([Versions in CustomResourceDefinitions](https://kubernetes.io/docs/tasks/extend-kubernetes/custom-resources/custom-resource-definition-versioning/)).

The crucial design decision: **the API server stores one version but serves many.** Reads are converted on the way out; writes land at the current storage version. If you update an existing object, "it is rewritten at the version that is currently the storage version. This is the only way that objects can change from one version to another." A `v1beta1` object can therefore coexist with a `v1` object indefinitely — the migration is lazy and per-object, driven by the clients' own write traffic.

#### 1.2 Versioning the ABI: conversion, and the removal gate

Conversion has two strategies ([same doc](https://kubernetes.io/docs/tasks/extend-kubernetes/custom-resources/custom-resource-definition-versioning/)):

- `strategy: None` — only the `apiVersion` string (and possibly field pruning) changes. The doc warns this "is unlikely to lead to good results if the schemas differ between the storage and requested version. In particular, you should not use this strategy if the same data is represented in different fields between versions."
- `strategy: Webhook` — a `conversion` webhook called with a `ConversionReview` object. `conversionReviewVersions` is a **required** field in `apiextensions.k8s.io/v1`, and the webhook must support at least one `ConversionReview` version understood by the current *and previous* API server. If no listed version is supported, **the CRD is refused at creation time** — a hard, load-time ABI check rather than a runtime surprise.

The `ConversionReview` contract is strict in ways that matter for PetDock's own converter design: `convertedObjects` must match the order of `request.objects`; `kind`, `metadata.uid`, `metadata.name`, `metadata.namespace` **must not** be changed; only `metadata.labels` and `metadata.annotations` may be; all other metadata changes are ignored. And the failure mode is documented as a hazard: "Failing conversion can disrupt read and write access to the custom resources, including the ability to update or delete the resources. Conversion failures should be avoided whenever possible, and should not be used to enforce validation constraints."

**Version removal is a four-step gate**, and this is the pattern most worth stealing:

1. Ensure all clients are fully migrated (the apiserver logs show who still calls the old version).
2. Set `served: false` on the old version — a *reversible* step. "If any clients are still unexpectedly using the old version they may begin reporting errors... If this occurs, switch back to using `served:true` on the old version, migrate the remaining clients to the new version and repeat this step."
3. Complete the storage migration and verify the old version is gone from `status.storedVersions`.
4. Only then remove it from `spec.versions` and drop conversion support for it.

There is also a scar visible in the schema itself: in `apiextensions.k8s.io/v1beta1` the field was `version` (singular); in `v1` it is `versions`. The old field is deprecated but must still be honoured — "if it is not empty, it must match the first item in the `versions` field" ([same doc](https://kubernetes.io/docs/tasks/extend-kubernetes/custom-resources/custom-resource-definition-versioning/)). That is exactly the kind of compatibility residue a young plugin platform accumulates and then cannot remove.

Deprecation is signalled in-band since v1.19: a `deprecated: true` flag per version plus an optional `deprecationWarning` string, delivered as a `Warning` response header.

#### 1.3 The Deprecation Policy — the actual guarantee

This is the single most transferable artifact in Group 1, and it is worth quoting precisely ([Kubernetes Deprecation Policy](https://kubernetes.io/docs/reference/using-api/deprecation-policy/)):

- **Rule #1**: "API elements may only be removed by incrementing the version of the API group." Once added at a version, an element cannot be removed from that version "or have its behavior significantly changed, regardless of track."
- **Rule #2**: API objects must round-trip between versions in a given release **without information loss**, except for whole REST resources absent from some versions. New fields added in v2 must round-trip to v1 and back, "which means v1 might have to add an equivalent field or represent it as an annotation."
- **Rule #3**: a version may not be deprecated in favour of a *less* stable version. GA can replace beta/alpha; beta can replace earlier beta/alpha but not GA; alpha can replace earlier alpha only.
- **Rule #4a** — the numbers:
  - **GA** versions "may be marked as deprecated, but must not be removed within a major version of Kubernetes."
  - **Beta** versions are deprecated no more than **9 months or 3 minor releases** after introduction (whichever is longer), and no longer served **9 months or 3 minor releases after deprecation** (whichever is longer).
  - **Alpha** versions "may be removed in any release without prior deprecation notice."
- **Rule #4b**: the "preferred" and "storage" version for a group may not advance until after a release that supports **both** the new and the previous version — so a user can upgrade and roll back without anything having been converted.
- A note states plainly: "There are no current plans for a major version revision of Kubernetes that removes GA APIs."
- A second note is the honest admission of a real limit: "Until [#52185](https://github.com/kubernetes/kubernetes/issues/52185) is resolved, no API versions that have been persisted to storage may be removed... the API server must remain capable of decoding/converting previously persisted data from storage."

The doc then walks a concrete 16-release timeline (`X` … `X+15`) from `v1alpha1` → `v1beta1` → `v1` → `v2alpha1` → `v2`, showing how a version is introduced, deprecated, and removed across *years* of releases. Deprecation is also observable, not just documented: from v1.19 a deprecated endpoint returns a `Warning` header, adds a `"k8s.io/deprecated":"true"` annotation to the audit event, and increments the `apiserver_requested_deprecated_apis` gauge, whose `removed_release` label tells an operator **which release will finally delete it** ([same doc](https://kubernetes.io/docs/reference/using-api/deprecation-policy/)).

#### 1.4 Admission webhooks

Extension without recompiling the server: `ValidatingWebhookConfiguration` and `MutatingWebhookConfiguration` register HTTP callbacks ([Dynamic Admission Control](https://kubernetes.io/docs/reference/access-authn-authz/extensible-admission-controllers/)). Details that matter for a plugin host:

- Mutating webhooks run first and may modify the object; validating webhooks run after all mutations. A validating webhook is the one that sees the final state.
- `timeoutSeconds` must be between **1 and 30 seconds**; default is **10**.
- `failurePolicy: Fail` (the default) rejects the API request on any error calling the webhook; `Ignore` continues. Note the sharp edge: "The Kubernetes API server does **not** apply a failure policy when the webhook is reached successfully, and the webhook implementation has explicitly rejected the request... An explicit rejection, correctly transmitted, always denies the API request, regardless of the `failurePolicy` setting."
- `sideEffects: None | NoneOnDryRun` — a webhook that makes out-of-band changes "must also have a reconciliation mechanism (like a controller) that periodically determines the actual state of the world", because "a call to an admission webhook does not guarantee the admitted object will be persisted as is, or at all."
- `admissionReviewVersions` is required, and the same current-and-previous-server rule applies as for conversion webhooks.
- `matchConditions` (up to 64) are CEL expressions evaluated before the webhook is called, with `object`, `oldObject`, `request`, and an `authorizer` available.
- The doc's own warning: "Admission webhooks are essentially part of the cluster control-plane. You should write and deploy them with great caution."

#### 1.5 The operator pattern, and why `kube-apiserver` stays small

Operators are "clients of the Kubernetes API that act as controllers for a Custom Resource" ([Operator pattern](https://kubernetes.io/docs/concepts/extend-kubernetes/operator/)). The extension point is therefore **data plus a control loop**, never code inside the server: "The Controller will normally run outside of the control plane, much as you would run any containerized application. For example, you can run the controller in your cluster as a Deployment." The doc lists a deliberately polyglot toolkit set — Charmed Operator Framework, Java Operator SDK, Kopf (Python), kube-rs (Rust), kubebuilder, KubeOps (.NET), shell-operator, and others — i.e. the SDK is a *client* library in each language, not a host-language plugin interface. (The doc does not itself name `controller-runtime`; it is the Go library underneath kubebuilder — **[unverified]** against that page.)

**Transferable pattern for PetDock.** Adopt the *data-plus-controller* split for anything that manages state over time (a pet's persisted mood, a learned routine): the host owns the store and the API, the plugin is a client that watches and reconciles, and a plugin crash is an ordinary client disconnect rather than a host event. Copy two things wholesale: (a) the **removal gate** — `served: false` is a reversible first step taken before anything is deleted, and (b) the **round-trip rule** — a plugin ABI version may only be retired once every object written under it can still be decoded, which for a desktop app means every pet save file. Do **not** copy the webhook pattern for in-process work: `failurePolicy: Fail` with a 10-second default timeout is exactly the availability trade a desktop pet must not make.

---

### 2. Terraform providers

#### 2.1 The protocol

The Terraform plugin protocol is "a versioned interface between Terraform CLI and Terraform Plugins", implemented in **Protocol Buffers and gRPC**, with the canonical definitions in the Terraform CLI repository ([Terraform plugin protocol](https://developer.hashicorp.com/terraform/plugin/terraform-plugin-protocol)). The versioning rule is stated cleanly and is worth copying verbatim:

> "Major versions of the protocol delineate Terraform CLI and Terraform Plugin compatibility. Minor versions of the protocol are additive."

Concretely: **protocol v5** is compatible with Terraform CLI **0.12 and later**; **protocol v6** is compatible with **1.0 and later** and adds only additive capability — `Nested Attributes` via the `NestedType` field on `SchemaAttribute`, and per-nested-attribute sensitivity instead of sensitivity on an entire computed attribute. The current definitions are protocol **v6.10** and **v5.10** ([same page](https://developer.hashicorp.com/terraform/plugin/terraform-plugin-protocol)).

So the answer to "does a provider built against protocol 5 still work with newer Terraform?" is **yes, by construction** — that is the entire point of the major/minor split, and the page explicitly names a provider that still targets it (`terraform-plugin-sdk/v2`, an implementation of protocol 5). Compatibility translation is a first-class, shipped artifact rather than an aspiration: the listed implementations include `tf5to6server` ("translate protocol version 5 providers into protocol version 6"), `tf6to5server` (the reverse), `tf5muxserver` and `tf6muxserver` (combine multiple providers), and the SDK/framework layer on top of them. `terraform-plugin-sdk/v2` and `terraform-plugin-framework` are the two higher-level SDKs; `terraform-plugin-go`'s `tf5server`/`tf6server` are the lower-level ones ([same page](https://developer.hashicorp.com/terraform/plugin/terraform-plugin-protocol)).

The registry participates: "During discovery, the Terraform Registry uses the protocol version as additional compatibility metadata when deciding which plugin versions Terraform CLI can select" — protocol version is published in the registry manifest at release time ([same page](https://developer.hashicorp.com/terraform/plugin/terraform-plugin-protocol)). The consumer side constrains the plugin by semver in `required_providers` ([Provider Requirements](https://developer.hashicorp.com/terraform/language/providers/requirements)), and version constraints are the usual operator/comma grammar ([Version Constraints](https://developer.hashicorp.com/terraform/language/expressions/version-constraints)).

#### 2.2 The transport: `go-plugin` over stdio

The mechanism underneath is HashiCorp's [`go-plugin`](https://pkg.go.dev/github.com/hashicorp/go-plugin): plugins are **standalone binaries launched as subprocesses**, communicating over `net/rpc` or gRPC on a single connection (yamux multiplexing for net/rpc; HTTP/2 for gRPC). The README states the isolation property directly:

> "Plugins can't crash your host process: A panic in a plugin doesn't panic the plugin user."

and the capability property:

> "Plugins can be relatively secure: The plugin only has access to the interfaces and args given to it, not to the entire memory space of the process."

Other features that transfer: `ProtocolVersion` — "a very basic 'protocol version' [that] can be incremented to invalidate any previous plugins... When a protocol version is incompatible, a human friendly error message is shown to the end user"; `NegotiatedVersion` after `Start()`; `SecureConfig`, which verifies a plugin binary's checksum before running (and the README correctly notes the host "should ensure the checksum was provided by a trusted and authoritative source"); `ReattachConfig` for "host upgrade while a plugin is running"; stdout/stderr mirroring; and a `log` bridge that prefixes plugin output with the plugin's binary path. The stated limit is explicit: "the plugin system is over RPC, it is currently only designed to work over a local [reliable] network. Plugins over a real network are not supported and will lead to unexpected behavior."

The **handshake magic cookie** is `TF_PLUGIN_MAGIC_COOKIE`, defined in the SDK as a `plugin.HandshakeConfig` with the source comment "The magic cookie values should NEVER be changed" ([terraform-plugin-sdk `plugin/serve.go`](https://github.com/hashicorp/terraform-plugin-sdk/blob/main/plugin/serve.go)). Its purpose is worth stating carefully because it is routinely over-read: the magic cookie is a **launch-intent check, not a security control** — it exists so that a user who accidentally runs a provider binary by hand gets a friendly error instead of a hang. That framing appears in third-party restatements of the go-plugin docs ([Quorum plugin docs](https://goquorum.readthedocs.io/PluggableArchitecture/PluginDevelopment/)); I did not find it verbatim on the upstream go-plugin page — **[unverified]**. Treat the cookie as UX, and never as authentication.

#### 2.3 The distribution-risk case study

On **10 August 2023** HashiCorp relicensed Terraform from MPL 2.0 to the **Business Source License 1.1**, restricting use of Terraform to build competing products ([OpenTofu Manifesto](https://opentofu.org/manifesto)). The manifesto was published 15 August; a fork of the last MPL version (Terraform 1.5) was announced 25 August and donated to the Linux Foundation as **OpenTofu**, which was [accepted into the CNCF on 23 April 2025 at the Sandbox maturity level](https://www.cncf.io/projects/opentofu/) ([secondary timeline](https://spacelift.io/blog/terraform-license-change)).

The lesson is not about licences. It is that a plugin ecosystem's *host* is a single point of governance failure, and the ecosystem survives a licence change only because the protocol was already a published, independently implementable contract — the provider protocol is defined by protobuf files that a fork can reimplement, and provider binaries built for it keep working against the fork. **A protocol is the only asset that survives the host being taken away.**

**Transferable pattern for PetDock.** This is the closest structural match in Group 1 to what a desktop pet platform actually needs, and it is where I would take the most from: (a) **plugin = separate process, protocol = gRPC/protobuf over stdio**, which buys crash isolation for free and is exactly what a pet plugin wants, since a hung behaviour must not freeze the pet; (b) **major = compatibility break, minor = additive only**, with a translator shipped as a first-class artifact rather than a migration guide; (c) **the protocol definition is the published, independently implementable artifact**, not the host's source. PetDock should adopt `go-plugin`-style handshakes and `required_providers`-style semver constraints, and should explicitly *not* adopt the magic cookie as a security boundary.

---

## Group 2 — Sandbox substrates (the key deliverable)

### 3. WebAssembly Component Model + WASI preview 2

#### 3.1 Why components exist

Core WebAssembly modules have functions restricted to `i32`/`i64`/`f32`/`f64`. Compound values must be represented by convention: a string becomes "(offset, length) into a linear memory" plus an exported memory the caller imports. The Component Model documentation is refreshingly blunt about how weak that is — "there is nothing in the type system to prevent the returned length from being confused with the returned offset, since both are integers. Also, the name of the memory used for the input and output strings must be established by convention" ([Why the Component Model?](https://component-model.bytecodealliance.org/design/why-component-model.html)).

A **component** fixes both problems at once. It is "a self-describing WebAssembly binary that interacts only through interfaces instead of shared memory", and — the sentence that matters most for a sandbox — "in components, memories are never exported or imported; they are not shared." Unlike core modules, "a component may not export a memory and thus it cannot indirectly communicate to others by writing to its memory and having others read from that memory." Two components compose when one's imports are satisfied by another's exports, arbitrarily deep ([Components](https://component-model.bytecodealliance.org/design/components.html)).

The consequence the docs draw is a security one: "The relationships within a graph of components can be statically analysed: for example, to verify that a component containing business logic has no access to a component containing personally identifiable information" ([Why the Component Model?](https://component-model.bytecodealliance.org/design/why-component-model.html)). **The capability surface is statically inspectable in the binary.**

#### 3.2 WIT: worlds, interfaces, resources, and the canonical ABI

**WIT** (Wasm Interface Type) is the IDL. It "isn't a general-purpose programming language and doesn't define behaviour; it defines only *contracts* between components" ([WIT overview](https://component-model.bytecodealliance.org/design/wit.html)). The pieces a PetDock plugin ABI would actually use:

- **Primitives**: `bool`, `s8/s16/s32/s64`, `u8/u16/u32/u64`, `f32/f64`, `char`, `string`. Note the deliberate simplification: `f32`/`f64` "logically only have a single `nan` value", and the bit pattern is not preserved across the boundary.
- **Built-in generics**: `list<T>`, `option<T>`, `result<T, E>` (with `_` for "no data"), `tuple<...>`. `option` and `result` are special-cased variants so they map idiomatically into host languages.
- **User-defined types**: `record` (named fields), `variant` (tagged union), `enum` (data-less variant), `flags` (bitfield), `type` aliases. Neither records nor variants may be generic.
- **`resource`** — the capability primitive. "A resource is a handle to some entity that exists outside of the component... entities that should be passed by reference rather than by value." A resource can have methods (implicit `self`), static functions, and **at most one** constructor. `borrow<T>` denotes a temporary loan; an owned handle is destroyed when dropped. The `blob` example in the docs gives exactly the shape PetDock wants for a window or a sprite handle:

  ```wit
  resource blob {
      constructor(init: list<u8>);
      write: func(bytes: list<u8>);
      read: func(n: u32) -> list<u8>;
      merge: static func(lhs: blob, rhs: blob) -> blob;
  }
  ```

- **`interface`** — a named set of types and functions, with `use types.{dimension, point}` to reuse declarations across interfaces.
- **`world`** — the component's contract: its imports and exports. `world multi-function-device { export printer; import error-reporter; }`. Cross-package imports use `package/name` syntax and apply at **interface** level, not package level: `world http-proxy { export wasi:http/incoming-handler; import wasi:http/outgoing-handler; }`. Worlds can `include` other worlds.
- **`package`** — `package documentation:example@1.0.1;`, where the version is **semver** and the package ID requires a namespace and a name.

The bit-level meaning of all of this is fixed by the **Canonical ABI**. Resources are lowered to handles: the explainer defines `resource.new`/`resource.drop` built-ins, and `T.rep` — the representation of a resource — "is currently limited to `i32` or `i64`" ([Component Model Explainer](https://github.com/WebAssembly/component-model/blob/main/design/mvp/Explainer.md)). `stream<T>` and `future<T>` were added for WASI 0.3 as Canonical ABI values rather than resources, so they travel across the boundary "the same way primitives do", with each end represented as "an integer index into a per-component handle table" ([WIT overview](https://component-model.bytecodealliance.org/design/wit.html), [Canonical ABI](https://component-model.bytecodealliance.org/advanced/canonical-abi.html)).

#### 3.3 Capability-based security: the model PetDock should copy

This is the deliverable's core claim, and [wasi.dev/security](https://wasi.dev/security) states it better than any blog:

> "WASI is built on principles of capability-based security. Code running under WASI has **no ambient authority**: a WebAssembly module or component starts with no access to the outside world and can only perform operations that the host explicitly grants. The host decides what each Wasm binary can see and touch, and that decision is enforced at the runtime boundary."

and the contrast that makes it worth copying on Windows specifically:

> "This contrasts with the ambient-authority model used by operating systems like Linux or Windows, where a process inherits its user's broad permissions and can reach anything the user can reach (filesystem, network, environment variables) unless additional sandboxing is layered on top. WASI is deny-by-default (or more accurately: **no capability handle by default**)."

The mechanism differs by version, and the difference is instructive:

- **WASI 0.1 modules** import `wasi_snapshot_preview1` functions but operate only on resources handed to them at startup, "chiefly preopened file descriptors. A module can call `fd_read` but only against the descriptors it has been given, and **there is no way to ask for a new one**."
- **WASI 0.2 components** express capabilities directly in WIT. "Each capability (filesystem access, networking, environment, clocks, randomness) is a separate import, and the host instantiates the component with only the imports it has granted. The component's interface contract is the capability surface, and it is **statically inspectable in the component binary itself**."
- **WASI 0.3** makes some handles implicit at the world level (e.g. `wasi:sockets/network`), but "The host still grants network access at the world boundary."

All three quoted from [wasi.dev/security](https://wasi.dev/security). The Component Model's own framing of the layering: it "fills the role of an OS's process model (defining how processes start up and communicate with each other) while WASI fills the role of an OS's many I/O interfaces" ([Component Model FAQ](https://github.com/WebAssembly/component-model/blob/main/design/high-level/FAQ.md)) — and the same FAQ notes the model supports both "shared-everything" and "shared-nothing" linking, so "shared-nothing" is a *choice the producer toolchain makes*, not a guarantee the runtime imposes.

#### 3.4 Honest stability status — read this before betting on it

The task brief explicitly asks me to be honest here, and the primary sources are considerably more conservative than the surrounding blog literature.

**What is actually true:**

- The Component Model repo says the model "is currently being developed incrementally as part of WASI 'Developer Preview' releases. The Component Model features enabled as part of a WASI Developer Preview release are kept stable by producer and consumer tools so that they can be used outside the browser in production settings to collect real-world feedback" ([WebAssembly/component-model README](https://github.com/WebAssembly/component-model)). Note the phrase **Developer Preview**, and note that the stability guarantee is a *tooling* convention ("kept stable by producer and consumer tools"), not a standards-body guarantee.
- The shipped previews are enumerated: **0.2.0** — first release based on the Component Model, with shared-nothing and shared-everything linking, high-level value types, `resource` types with handles, and WIT. **0.3.0** — first release with native concurrency: `async` functions, `stream`s, `future`s, plus new Canonical ABI built-ins. **0.3.1** — adds the `map` type and `implements`/`external-id` annotations. Future previews will gate further features (e.g. cooperative threads) ([same README](https://github.com/WebAssembly/component-model)).
- The WASI repo confirms 0.2 (Preview 2) is "a modular collection of APIs defined with the Wit IDL" and that **0.3 (Preview 3) is "the current preview"**, replacing explicit streams and polling with the component model's native `async` via `future` and `stream` ([WebAssembly/WASI README](https://github.com/WebAssembly/WASI)).
- **The interfaces are not yet standardized.** In the WASI phase process, **Phase 5 is "The Feature is Standardized"**. The live proposals table shows **Clocks, Random, Filesystem, Sockets, CLI, and HTTP all at Phase 3** ("Implementation Phase"), with **nothing at Phase 4 and nothing at Phase 5**. The document also states the versioning consequence: "Proposals remain in the 0.x semver range until they reach Phase 5 and are fully standardized. At that point, a 1.0 release should be made available." ([WASI Proposals](https://github.com/WebAssembly/WASI/blob/main/docs/Proposals.md)). Note that `wasi:keyvalue`, `wasi:nn`, `wasi:webgpu`, `wasi:config`, `wasi:messaging`, `wasi:http` timezone support, and many others are only at **Phase 2 or Phase 1** — the "rich capability set" is largely aspirational.
- A major-version bump is permitted "only if the old API can be implemented in terms of the new API. As part of the new version, champions are expected to provide a tool that enables this backwards-compatibility. If that is not possible, then a new API proposal with a new name should be started." ([same doc](https://github.com/WebAssembly/WASI/blob/main/docs/Proposals.md)) — a good rule, and also an admission that breaking changes are expected.
- WASI 0.2.0's launch was a WASI Subgroup vote and was framed by the ecosystem as "ready for use by **library implementers**", with the explicit caveat that the interfaces are "fundamentally lower levels of abstraction" and that the idiomatic-developer experience ("compile to WASI and use your normal standard library") was "likely a few months away" as of January 2024 ([wasmCloud: WASI 0.2.0 and Why It Matters](https://wasmcloud.com/blog/wasi-preview-2-officially-launches)).

**What is [unverified] and should not be relied on:** numerous 2026-dated secondary sources assert a Component Model "phase 3" WebAssembly spec status, WASI 0.3 shipping in mid-2026, WASI 1.0 standardization "late 2026 or early 2027", and that the Component Model is "shipping everywhere". I could not corroborate any of these against a primary standards document in this session, and the primary documents above contradict the strong form of the claim: the model is still described as a *Developer Preview* mechanism and the core WASI interfaces are still at Phase 3. **Treat "WASI 1.0" as unscheduled.** Any PetDock decision that depends on WASI being finalized is a decision made on a rumour.

**Transferable pattern for PetDock.** Two things, and they are separable:

1. **The capability model is right and is available today**, even without the Component Model: preopens/handles granted at instantiation, deny-by-default, no ambient authority, and "no way to ask for a new one." PetDock's pet plugins should receive *handles* (a window, a sprite atlas, a timer source, a named host function) and nothing else.
2. **WIT is the right shape for PetDock's plugin ABI** — `world` = a plugin's contract, `interface` = a capability, `resource` = a live host object, `package@semver` = the version. But implement it against **core modules + hand-written bindings**, or against a pinned preview, and **do not** build the loader on the assumption that `wasi:*` is a frozen standard. Pin the exact preview version in every plugin's manifest.

---

### 4. Extism

**Isolation mechanism: a WASM instance inside the host process**, with the host/guest boundary being the *host function* surface. Extism is a Rust code runtime that embeds a WebAssembly engine (Wasmtime by default) and exposes one uniform plugin interface across many host languages.

**PDKs** exist for Rust, JavaScript/TypeScript, Go, C#, F#, C, Haskell, Zig, and AssemblyScript; a plugin exports a function and reads/writes through the PDK (`Host.inputString()`, `Host.outputString(...)`, `pdk.Input()`, `pdk.OutputString(...)`), and the same `.wasm` runs under any host SDK ([Plugin Quickstart](https://extism.org/docs/quickstart/plugin-quickstart/)). Note two build facts with real cost implications: **all JS and Go plugins need WASI to run**, and Rust plugins with no syscalls can target `wasm32-unknown-unknown` without it ([same page](https://extism.org/docs/quickstart/plugin-quickstart/)). The JS PDK is a *compiler* (`extism-js <input.js> -i <interface.d.ts> -o <out.wasm>`) with an explicit `plugin.d.ts` declaring exports as `(): I32` — the boundary is integers, and the interface file is hand-maintained.

**The manifest is the capability grant**, and it is the most directly stealable artifact in this section ([The Manifest](https://extism.org/docs/concepts/manifest/)):

```jsonc
{
  "wasm": [ { "path": "...", "name": "main", "hash": "<sha256 hex>" } ],   // or "data", or "url" + headers/method
  "memory": { "max_pages": 4, "max_http_response_bytes": 4096, "max_var_bytes": 4096 },
  "allowed_hosts": ["example.com"],        // empty = no hosts; null = ALL hosts
  "allowed_paths": { "/path/on/disk": "plugin/path" },   // empty/null = no file access
  "config": { "mykey": "myvalue" }
}
```

Three details deserve emphasis. First, **`max_pages` is the memory cap and one page is 64 KiB**, so `max_pages: 4` is 256 KiB — the units are easy to get wrong by four orders of magnitude. Second, the **defaults are the dangerous end**: `allowed_hosts` empty means *no* hosts but `null` means *all* hosts, and `allowed_paths` empty or `null` means no file access — so the schema relies on the distinction between "absent", "empty", and "null", which is exactly the kind of three-way state a PetDock manifest format should collapse. Third, **the hash is optional** — "it's optional, but recommended" — so content integrity is opt-in.

**Host functions** are the whole capability story: "A Host Function is essentially a custom function that your host 'injects' into a plug-in and appears to the plug-in a WebAssembly 'import' or an `extern` function declaration." The signature is `{ name, input_param_types: Array<WasmValueType>, output_return_types: Array<WasmValueType>, user_data: void* }`, with `WasmValueType` limited to `I32/I64/F32/F64/V128` ([Host Functions](https://extism.org/docs/concepts/host-functions/)). `UserData` "must live at least as long as the `Plugin`" and must be `Send + Sync` if plugins are pooled across threads — passing non-thread-safe data as `UserData` in a multi-threaded context is stated to be **undefined behaviour** ([same page](https://extism.org/docs/concepts/host-functions/)).

**On the "a plugin system, not a sandbox" framing.** I could **not** find that phrase on extism.org, and I am marking the attribution **[unverified]**. What the docs actually say is the opposite in emphasis: "WebAssembly also helps ensure that these plug-ins cannot harm your application accidentally or maliciously through various sandboxing techniques" ([Plug-in System](https://extism.org/docs/concepts/plug-in-system)), and the home page markets "Secure by Default" ([extism.org](https://extism.org)). The *correct* reading — which the "not a sandbox" phrasing is reaching for — is that **Extism is a plugin system whose isolation is entirely inherited from the wasm sandbox plus whatever host functions you chose to grant**, and the docs say so in their own words: "these imports can be very powerful as they allow you to selectively imbue your plug-ins with the capabilities and APIs of your application itself" ([What is a Plug-in?](https://extism.org/docs/concepts/plug-in/)). Third-party analyses make the sharper point that `allowed_hosts` restricts only Extism's *built-in* HTTP host function and not custom host functions ([systemshardening.com](https://www.systemshardening.com/articles/wasm/extism-plugin-security)) — **[unverified]** against Extism's own docs, though it follows directly from the fact that `allowed_hosts` is enforced inside the built-in HTTP function.

**Ecosystem evidence.** Helm's HIP-0026 evaluated Wasm plugin systems and concluded Extism "is the most mature and well-supported Wasm plugin system today... actively maintained by a team of core contributors with a solid backwards compatibility policy, has a strong security focus" ([Helm HIP-0026](https://helm.sh/community/hips/hip-0026)) — a community evaluation, not a primary spec, and worth citing as such.

**Transferable pattern for PetDock.** The **declarative manifest as the capability grant** is the single best idea here and I would copy it almost literally: one file per plugin naming its wasm source, an optional-but-required-in-practice content hash, memory ceiling, allowed hosts, allowed path mappings, and a free-form `config` bag. Extism's own weakness is the lesson for the copy: **make the hash mandatory, collapse the empty/null ambiguity into one explicit deny, and state plainly in the manifest schema that the sandbox is exactly the host functions the manifest's capability list maps to.**

---

### 5. Wasmtime / Wasmer

**Isolation mechanism: a WebAssembly instance in-process**, with per-`Store` host state and an explicitly configured capability context. The embedding shape is `Config` → `Engine` → `Store` → `Module`/`Component` → `Linker` → `Instance`, with all limits configured on `Config` (engine-wide) and `Store` (per-instance).

#### 5.1 Interrupting a plugin

Wasmtime offers two independent mechanisms, and choosing between them is a real engineering decision, not a preference ([Interrupting Wasm Execution](https://docs.wasmtime.dev/examples-interrupting-wasm.html)):

**Fuel — deterministic.** `config.consume_fuel(true)`; "Most WebAssembly instructions consume 1 unit of fuel. Some instructions, such as `nop`, `drop`, `block`, and `loop`, consume 0 units"; "when fuel is entirely consumed it will cause wasm to trap" ([Store](https://docs.wasmtime.dev/api/wasmtime/struct.Store.html)). A trap is `Trap::OutOfFuel`. Two operational facts matter: **"a `Store` starts with no fuel, so if you enable this option you'll have to be sure to pour some fuel into `Store` before executing some code"**, and `Config::operator_cost` lets you set per-byte/per-element/per-page costs in addition to the flat per-operator cost, which is how you stop a guest from buying a large copy cheaply ([Config](https://docs.wasmtime.dev/api/wasmtime/struct.Config.html)).

**Epochs — wall-clock, cheap.** `config.epoch_interruption(true)`; a global epoch counter lives on the `Engine` and is incremented by `Engine::increment_epoch` (typically from a timer thread); the `Store` tracks a deadline set by `Store::set_epoch_deadline`, and "An initial deadline must be set before executing code... If this deadline is not configured then wasm will immediately trap." Three behaviours are available: trap (`epoch_deadline_trap`, the default), call an arbitrary callback, or async-yield to the executor ([Config](https://docs.wasmtime.dev/api/wasmtime/struct.Config.html)). Measured overhead is "around a 10% slowdown", versus fuel which is 2–3× slower than epochs; the trade is that epochs are non-deterministic, based on wall-time rather than an instruction count ([Interrupting Wasm Execution](https://docs.wasmtime.dev/examples-interrupting-wasm.html)).

Crucially, the docs state the security property explicitly: **"Epochs are designed to handle malicious WebAssembly guests – the deadline check cannot be avoided by WebAssembly code. It is safe to use epoch deadlines to limit the execution time of untrusted code."** Neither fuel nor epochs works with the Winch compiler.

**Two limits of the interruption model, both stated by the docs and both directly relevant to a desktop pet that must stay responsive:**

1. **Host-blocked code is not interrupted.** "Epochs (and fuel) do not assist in handling WebAssembly code blocked in a call to the host. For example if the WebAssembly function calls `wasi:io/poll.poll` to sleep epochs will not assist in waking this up or timing it out. Epochs intentionally only affect running WebAssembly code itself and it's left to the embedder to determine how best to wake up indefinitely blocking code in the host." The recommended fix is the `async` variant of WASI host functions plus an outer timeout.
2. **Bulk-memory instructions check the deadline once.** "bulk-data-transfer instructions, such as `memory.copy`, only check the epoch once at the start of the operation... a guest using a 64-bit linear memory with a 128GiB size could issue a 128GiB `memory.copy` which would have no preemption within the instruction itself. Hosts which need strict time limits for guests right now are recommended to ensure that the store's allocated heap size (linear memory + GC heap) are bounded with a `ResourceLimiter`." **This is the strongest argument in the whole document for capping memory even when you only care about time.**

#### 5.2 Memory and resource caps

`ResourceLimiter` is a store-level trait: `memory_growing(current, desired, maximum) -> Result<bool>` and `table_growing(...)`, with provided `instances()`, `tables()`, `memories()` each defaulting to **10,000**. Return `Ok(false)` to make `memory.grow` return -1; `Err(e)` to behave as if a trap was raised. The trait's own scope limit is stated honestly: **"this trait does not limit 100% of memory allocated via a `Store`. Wasmtime will still allocate memory to track data structures and additionally embedder-specific memory allocations are not tracked via this trait. This trait only limits resources allocated by a WebAssembly instance itself."** And CPU limiting is explicitly out of scope: "limiting CPU resources needs to be done with something such as `Config::consume_fuel` or `Config::epoch_interruption`" ([ResourceLimiter](https://docs.wasmtime.dev/api/wasmtime/trait.ResourceLimiter.html)).

`Config::max_wasm_stack` comes with a warning that is easy to misread as a safety feature: "this knob only limits the stack space consumed by wasm code. More importantly, it does not ensure that this much stack space is available on the calling thread stack. Exhausting the thread stack typically leads to an **abort** of the process" ([Config](https://docs.wasmtime.dev/api/wasmtime/struct.Config.html)). A stack setting can therefore convert a recoverable plugin fault into a host kill.

#### 5.3 Capability grants: the WASI context

`WasiCtxBuilder` is where capability-based security becomes concrete, and its **defaults are the security model** ([WasiCtxBuilder](https://docs.wasmtime.dev/api/wasmtime_wasi/struct.WasiCtxBuilder.html)):

> "stdin is closed; stdout and stderr eat all input and it doesn't go anywhere; no env vars; no arguments; no preopens; clocks use the host implementation of wall/monotonic clocks; RNGs are all initialized with random state...; TCP/UDP are allowed but all addresses are denied by default. `wasi:sockets/ip-name-lookup` is denied by default."

Filesystem access is granted only through `preopened_dir(host_path, guest_path, perms: FsPerms)` — `FsPerms::ReadOnly` or `FsPerms::ReadWrite` — and the doc states the enforcement: "By default WebAssembly does not have access to the filesystem because there are no preopened directories. **All filesystem operations, such as opening a file, are done through a preexisting handle.** ... WASI will also prevent access outside of files provided here. For example `..` can't be used to traverse up from the `host_path`." Network is granted by `allow_tcp`/`allow_udp`/`allow_ip_name_lookup`, optionally narrowed per-connection by `socket_addr_check`. `max_random_size` defaults to 64M "to protect the host implementation from memory exhaustion from untrusted guest input".

#### 5.4 The engine's own guarantees

Wasmtime's security page enumerates what core wasm gives you, and it is a good checklist for judging any PetDock substrate ([Security](https://docs.wasmtime.dev/security.html)): the callstack is inaccessible (return addresses are not in application-visible memory, so stack-smashing is structurally impossible); pointers compile to checked offsets into linear memory; all control transfers go to type-checked destinations; **"All interaction with the outside world is done through imports and exports. There is no raw access to system calls or other forms of I/O"**; and there is no undefined behaviour. Defence-in-depth additions: linear memories are preceded by a **2GB guard region**; memory is zeroed after an instance finishes "to prevent accidental leakage of information between instances in the face of other bugs"; table bounds checks and `br_table` are Spectre-mitigated; and — relevant to a pet that prints to a console — **terminal output is filtered**: "If untrusted code is allowed to print text which is displayed to a terminal, it may emit ANSI-style escape sequences... which... can have side effects including writing to files, executing commands, injecting text into the stream as if the user had typed it, or reading the output of previous commands", so "Wasmtime now filters writes to output streams when they are connected to a terminal to translate escape sequences into inert replacement sequences."

**Wasmer** offers the same broad model with a different ergonomics: per-instance isolation with shared read-only compiled code pages ([Wasmer: WebAssembly Clouds](https://wasmer.io/posts/wasm-clouds-the-world-after-containers)), and a **metering middleware** that is "a middleware for tracking how many operators are executed in total and putting a limit on the total number of operators executed" — the same idea as fuel, exposed as a compiler middleware rather than a `Config` flag ([wasmer_middlewares::metering](https://wasmerio.github.io/wasmer/crates/doc/wasmer_middlewares/metering/index.html)). I did not verify Wasmer's `ResourceLimiter`-equivalent or its current WASI-preview-2 support in this session — **[unverified]**.

**Transferable pattern for PetDock.** Copy the **layered limit stack**, because no single knob is sufficient: (1) epoch deadline for wall-clock preemption (cheap, non-deterministic, unbypassable by guest code); (2) a hard linear-memory cap via a limiter, which is *also* what bounds the worst-case `memory.copy` time slice; (3) a bounded host-call timeout, because neither epochs nor fuel touch a guest blocked in a host function; (4) a per-plugin output filter, because a "pet" that writes to a console or a terminal-like surface is a real injection channel. And take the defaults: **stdin closed, no env, no args, no preopens, all addresses denied** is the correct starting position for a third-party pet plugin.

---

### 6. QuickJS, `quickjs-emscripten`, `isolated-vm`, and Deno permissions

This family is the "runtime in a runtime" approach: instead of compiling the plugin to a portable sandbox bytecode, you embed a *scripting engine* you control, and hand it only the globals you choose.

#### 6.1 QuickJS

QuickJS (current version **2026-06-04**) is "a small and embeddable Javascript engine" supporting almost all of ES2025, "just a few C files, no external dependency, 210 KiB of x86 code for a simple 'hello world' program", with a full runtime lifecycle "in less than 300 microseconds"; garbage collection is reference counting with cycle removal, so destruction is deterministic ([QuickJS](https://bellard.org/quickjs/quickjs.html)).

The sandboxing API is small and complete:

- `JS_SetMemoryLimit()` — "set a global memory allocation limit to a given `JSRuntime`"; custom allocators via `JS_NewRuntime2()`; `JS_SetMaxStackSize()` for the maximum system stack size ([same doc](https://bellard.org/quickjs/quickjs.html)).
- `JS_SetInterruptHandler()` — "set a callback which is regularly called by the engine when it is executing code. This callback can be used to implement an execution timeout." The CLI uses it for Ctrl-C ([same doc](https://bellard.org/quickjs/quickjs.html)).
- The host/guest boundary is `JS_NewCFunction()` / `JS_SetPropertyFunctionList()`, with a `JSClassID` mechanism for attaching C opaque pointers to JS objects via `JS_GetOpaque()`/`JS_SetOpaque()`. That is the handle system: **a capability is an opaque C pointer wrapped in a JS object, and there is no way to reach another one.**

One scar is instructive: "the bytecode format is linked to a given QuickJS version. Moreover, no security check is done before its execution. Hence the bytecode should not be loaded from untrusted sources" ([same doc](https://bellard.org/quickjs/quickjs.html)). A precompiled-artifact format is an ABI, and an unchecked one is an ABI you must never accept from a plugin author.

**`quickjs-emscripten`** wraps this for JS hosts, and its API maps one-to-one onto the sandboxing knobs: `runtime.setMemoryLimit(bytes)` (applies to all contexts in the runtime **in aggregate**), `runtime.setMaxStackSize(bytes)`, `runtime.setInterruptHandler(() => ++interruptCycles > 1024)`, plus the convenience `shouldInterruptAfterDeadline(Date.now() + 1000)` and a per-call `memoryLimitBytes` ([quickjs-emscripten](https://github.com/justjake/quickjs-emscripten)). Each `QuickJSContext` has its own globals and built-ins, and **"By default, no host functionality is exposed to code running inside QuickJS"** — you build the capability surface explicitly by `setProp`-ing functions onto `vm.global`. The ergonomics carry a real hazard: every value crossing the boundary is a handle that must be manually `.dispose()`d, and `context.dispose()` throws `RuntimeError: abort(Assertion failed: list_empty(&rt->gc_obj_list)...)` if you forgot one. The project's own status line is the honest one: **"This project makes every effort to be secure, but has not been audited."** It was inspired by Figma's plugin-runtime work ([same README](https://github.com/justjake/quickjs-emscripten)).

**Who uses this for untrusted code:** **Shopify Functions** compiles merchant-authored JavaScript through **Javy**, a JavaScript-to-WebAssembly toolchain that embeds a JS runtime in Wasm, precisely so that merchant code can be run safely; Shopify's engineering post cites "the strong sandbox of Wasm lets us run untrusted code with confidence, its predictable performance lets us define and impose strict resource [limits]" ([Shopify Engineering](https://shopify.engineering/javascript-in-webassembly-for-shopify-functions), [JavaScript for Functions](https://shopify.dev/docs/apps/build/functions/programming-languages/javascript-for-functions)). Javy is now a Bytecode Alliance hosted project ([Bytecode Alliance](https://bytecodealliance.org/articles/javy-hosted-project)) and produces modules in the 1–16 KB range ([bytecodealliance/javy](https://github.com/bytecodealliance/javy)). Note the composite structure: **QuickJS for the language, Wasm for the isolation** — the JS engine is not itself the sandbox.

#### 6.2 `isolated-vm` (Node/V8)

`isolated-vm` exposes V8's `Isolate` interface: "This allows you to create JavaScript environments which are completely *isolated* from each other... a fresh JavaScript environment completely free of extraneous capabilities provided by the nodejs runtime" ([isolated-vm](https://github.com/laverdet/isolated-vm)).

- `new ivm.Isolate({ memoryLimit })` — in MB, default 128, minimum 8, and with an explicit honesty note: **"Note that this is more of a guideline instead of a strict limit. A determined attacker could use 2-3 times this limit before their script is terminated."**
- `timeout` (milliseconds) is available on `context.eval`, `evalClosure`, `script.run`, `module.evaluate`, and `reference.apply*` — "Maximum amount of time in milliseconds this script is allowed to run before execution is canceled. Default is no timeout."
- `isolate.cpuTime` / `isolate.wallTime` in nanoseconds give you the accounting primitives to build your own budget.
- The boundary-crossing primitives are the design lesson. **`Reference`** is "a pointer to a value stored in any isolate"; **`ExternalCopy`** is "some value that is stored outside of any v8 isolate", copied in via the structured clone algorithm, with `transferOut` making ArrayBuffer ownership transfer "complete in constant time since it doesn't have to copy an arbitrarily large object"; **`Callback`** wraps simple functions with arguments and returns "always copied using the same method as `ExternalCopy`". The FAQ is blunt about why: "How do I pass a [module, function, object, library] into an isolate?" — **"You don't! Isolates are `isolated`... It may help to think of the question in the context of a web browser. How would you pass a function from nodejs into Firefox? You can't, it is nonsense."** Every crossing is a copy or an explicit handle, and that is precisely why it is expensive and why `ExternalCopy` exists.
- The security warnings are the most valuable part of the README: "At a minimum you should take care not to leak any instances of `isolated-vm` objects (`Reference`, `ExternalCopy`, etc) to untrusted code. **It is usually trivial for an attacker to use these instances as a springboard back into the nodejs isolate which will yield complete control over a process.**" And on failure containment: "It's a good idea to keep instances of `isolated-vm` in a different nodejs process than other critical infrastructure." The `onCatastrophicError` callback means "v8 has lost all control over the isolate, and all resources in use are totally unrecoverable. If you receive this error you should log the error, stop serving requests, finish outstanding work, and **end the process by calling `process.abort()`**."
- **Project status: maintenance mode.** "It will continue to be supported for as long as is technically feasible." Node 22 → 5.x/4.x, 24 → 6.x/5.x, 26 → 7.x; node 20+ requires `--no-node-snapshot`; a compiler is needed at install time (node-gyp). Its own comparison table is the clearest summary of the field: `isolated-vm` is the only listed option with Secure + Memory Limits + Isolated + Multithreaded — but it has **no Module Support**, and neither `vm` nor `vm2` has memory limits ([same README](https://github.com/laverdet/isolated-vm)).

#### 6.3 Deno permissions

Deno's model is the clearest *product-level* expression of capability security, and its documentation is unusually candid about the model's limits ([Security and permissions](https://docs.deno.com/runtime/fundamentals/security/)).

The grant side: "Deno is secure by default. Unless you specifically enable it, a program run with Deno has no access to sensitive APIs, such as file system access, network connectivity, or environment access." Permissions are scoped — `--allow-read=./data` "limits reads to one directory", `--allow-env=API_KEY` "to a single variable" — and `--deny-*` overrides `--allow-*`, so "you can grant a broad category and carve out the sensitive parts: `--allow-read --deny-read=/etc` reads anything except `/etc`." A refused operation throws a `NotCapable` error. At runtime, `Deno.permissions.query/request/revoke` let a program inspect and *tighten* its own grants — the documented pattern is to read a config file at startup and then "give up read access", shrinking the attack surface for the rest of the code including dependencies ([same page](https://docs.deno.com/runtime/fundamentals/security/)).

The **granularity critique**, in the manual's own words, is why this is not a sandbox for untrusted third-party code on its own:

- **"No limits on the execution of code at the same privilege level"** — `eval`, `new Function`, dynamic import, and web workers all execute arbitrary code at the caller's privilege.
- **"All code executing on the same thread shares the same privilege level. It is not possible for different modules to have different privilege levels within the same thread."**
- **"The initial static module graph can import modules without restrictions"** — no `--allow-read` is needed to load local files and no `--allow-net` to fetch remote modules, because the static graph is resolved without running code. (Runtime `import()` with a non-literal specifier *is* checked.)
- Two permissions bypass the sandbox entirely: `--allow-run` (a subprocess "runs as a separate program with its own permissions, not the restricted set you granted the Deno process"; `--allow-run=deno` is called out as "especially dangerous") and `--allow-ffi` (a native library "runs as compiled machine code in the same process and can issue system calls directly"). The doc's instruction: **"Treat both as equivalent to `--allow-all`."**

Deno's own guidance for running untrusted code is a four-layer stack, and it is a good model for PetDock's own threat model: (1) run with limited permissions and freeze what can load (`--frozen`, `--cached-only`); (2) **"Isolate the untrusted part in a Web Worker with a reduced permission set, so it cannot inherit everything the main program was granted"**; (3) use OS sandboxing (`chroot`, `cgroups`, `seccomp`); (4) use a sandboxed environment like a VM or MicroVM (gVisor, Firecracker) ([same page](https://docs.deno.com/runtime/fundamentals/security/)). There is also a **permission broker** (`DENO_PERMISSION_BROKER_PATH`, a Unix socket or Windows named pipe) that takes over all permission decisions and makes `--allow-*`/`--deny-*` flags inert; if brokering fails in any way, "Deno immediately terminates the process to preserve integrity and prevent permission escalation" ([same page](https://docs.deno.com/runtime/fundamentals/security/)).

**Transferable pattern for PetDock.** The `Deno.permissions.query/request/revoke` idea — **a plugin can inspect and voluntarily shrink its own capability set at runtime** — is the best product-level idea in this section and has no equivalent in the Wasm world. Pair it with the Deno manual's own warning: capability grants at the *process* level are useless if the plugin can spawn a subprocess or load native code, so `--allow-run` and `--allow-ffi` have no place in a pet plugin's grant vocabulary. From `isolated-vm`, take the naming discipline: every crossing is either a **copy** or a **handle**, they are different types, and a handle leaked into guest code is a full compromise.

---

### 7. Lua sandboxes — the cautionary tale

Lua is the classic in-process scripting extension language, and it is worth including *only* as evidence for why in-process scripting is the wrong default for PetDock.

The lua-users wiki's own framing is the finding: **"Sandboxing is tricky and generally speaking difficult to get right."** It recommends "whitelist rather than blacklist", notes that even a careful implementation "could be a bug in the Lua implementation" so you should "take additional steps for isolation in the event of such a breach, such as operating system level isolation mechanisms (like restricted user accounts or a chroot jail)", and warns that "restricting to a subset of the Lua language can mitigate some of these concerns" ([lua-users: Sand Boxes](http://lua-users.org/wiki/SandBoxes)).

The per-variable safety table is the concrete escape inventory, and it is long. Marked **UNSAFE**: `_G` ("by default this contains the global environment"); `getfenv` ("this can locate an environment outside of the sandbox, thereby breaking out of the sandbox"); `setfenv` ("can modify environments of functions that are up the call-chain and outside the sandbox"); `load`/`loadstring`/`loadfile`/`dofile` (the returned function "has the global environment, thereby breaking out of the sandbox" — and bytecode loading is separately dangerous); `getmetatable`/`setmetatable` (mutating the string metatable "can break code outside the sandbox"); `rawget`/`rawset`/`rawequal` (they bypass metatables, which is how you defeat a protected-object design); the `debug` table ("functions here can break out of the sandbox and access variables outside the sandbox"); `package.*` and `require`/`module`; and most of `os` and `io` ([same page](http://lua-users.org/wiki/SandBoxes)).

Two further lessons from that page generalise past Lua:

- **Even "SAFE" functions are denial-of-service vectors.** `string.find` is marked SAFE with the warning that "a number of functions like this can still lock up the CPU"; the simple sandbox example explicitly notes `while 1 do end` is accepted, "There is no limit to memory usage and computation, so the untrusted code could still severely impact system performance unless further restrictions are made." Capability safety and resource safety are independent problems, and a capability sandbox does nothing about the second.
- **The API itself moved out from under the sandbox.** `setfenv` "is no longer available as of Lua 5.2" (replaced by `_ENV`), so the wiki page's own examples are obsolete — a reminder that a sandbox built on a language's *internal* environment mechanism inherits that mechanism's churn.

Real escapes are not hypothetical: **CVE-2022-0543** was a Lua sandbox escape in Redis (Debian-specific, via a packaging issue) leading to remote code execution ([INE: CVE-2022-0543](https://ine.com/blog/cve-20220543-lua-sandbox-escape-in-redis)).

The mature answer in this family was to stop sandboxing general Lua and **build a different language**: **Luau**, Roblox's "fast, small, safe, gradually typed embeddable scripting language derived from Lua", is "almost fully syntax-compatible with Lua, with added type checking and sandboxing". Roblox "runs all Luau code inside a security sandbox" — scripts "cannot touch the filesystem, open network sockets directly, or load arbitrary native libraries — there is no `io`, no `os.execute`, and none of the dangerous parts of the Lua standard library", and instead the platform "exposes purpose-built APIs for controlled web requests and persistence" ([Roblox/luau](https://codeberg.org/Roblox/luau), [secondary summary](https://luablox.dev/learn/what-is-luau)). That is the same conclusion the WASI world reached: **remove the ambient-authority standard library and expose only purpose-built capabilities** — but Roblox had to fork the language to get there.

**Transferable pattern for PetDock.** The lesson is negative and cheap to apply: **never ship a general-purpose scripting language with its standard library intact and a "sandbox" bolted on top.** If PetDock supports user scripting at all, the language surface must be *designed* as a capability set (Luau's answer) or the plugin must be compiled to a substrate whose standard library is already capability-based (WASI's answer). And take the second lesson seriously: a capability sandbox without resource limits leaves the host trivially deniable.

---

## Comparison tables

### Table 1 — Group 1: plugin over a protocol

| | K8s CRDs + operators | Terraform providers |
|---|---|---|
| **What the plugin is** | A controller (separate Deployment) + a CRD schema registered with the apiserver | A separate binary launched as a subprocess |
| **Contract** | REST resource schema + `ConversionReview`/`AdmissionReview` webhooks | protobuf/gRPC service (`tfplugin5`/`tfplugin6`) over stdio |
| **Isolation mechanism** | **Process** (controller is an ordinary container) + **schema/type boundary**; webhooks are remote HTTP | **Process** (`go-plugin` subprocess) + **RPC boundary** |
| **ABI versioning** | API group versions `v1alpha1`→`v1beta1`→`v1`; conversion webhooks; `served`/`storage` flags; `status.storedVersions` | Major protocol version = compatibility (v5 = CLI 0.12+, v6 = CLI 1.0+); minor = additive; `tf5to6server`/`tf6to5server` translators |
| **Removal gate** | `served:false` (reversible) → migrate stored objects → verify `storedVersions` → remove from spec → drop conversion | Registry publishes protocol version as compatibility metadata; `required_providers` semver constraints on the consumer side |
| **Documented guarantee** | GA never removed within a major version; beta ≥9 months/3 minors after deprecation; alpha removable in any release; round-trip without information loss | A protocol-5 provider still works with newer Terraform — by construction |
| **Crash containment** | Process death; `failurePolicy: Fail\|Ignore`; 1–30 s webhook timeout (default 10 s) | "A panic in a plugin doesn't panic the plugin user" |
| **Capability model** | RBAC on the API; webhooks run as control-plane components | "Only has access to the interfaces and args given to it, not to the entire memory space" |
| **Governance scar** | Issue #52185: persisted API versions can never be removed, only unserved | BUSL 1.1 relicence (2023-08-10) → OpenTofu → CNCF Sandbox (2025-04-23) |

### Table 2 — Group 2: sandbox substrates

| Substrate | Isolation mechanism | ABI versioning | Timeout / preemption | Memory cap | Crash containment |
|---|---|---|---|---|---|
| **Wasm Component Model + WASI p2** | Wasm **instance**; shared-nothing linking; memories never exported; capabilities = WIT imports | `package@semver` in WIT; WASI **Developer Preview** releases (0.2.0 / 0.3.0 / 0.3.1); interfaces at **Phase 3, not standardized** | Provided by the runtime (Wasmtime), not the model | Provided by the runtime | Trap; instance is disposable |
| **Extism** | Wasm instance **in-process**; boundary = host functions | Host SDK version; plugin `.wasm` is a plain module; manifest is per-plugin | Inherited from the embedded engine | `memory.max_pages` × 64 KiB; `max_http_response_bytes`; `max_var_bytes` | Trap; plugin is reloadable |
| **Wasmtime** | Wasm **instance**; per-`Store` state; `WasiCtx` grants | Wasm core/component version + your own host-API version | `consume_fuel` (deterministic) **or** `epoch_interruption` (~10% overhead, unbypassable); neither covers host-blocked code | `ResourceLimiter::memory_growing` (+ tables, instances ≤10 000) | Trap; process survives |
| **Wasmer** | Wasm **instance**, shared read-only code pages | Wasm core/component version | Metering middleware (operator-count limit) | **[unverified]** | Trap |
| **QuickJS (native)** | **Interpreter** with capability handles (`JS_NewCFunction`, opaque `JSClassID` pointers) | Bytecode format is **version-locked and unchecked** — never load from plugins | `JS_SetInterruptHandler` callback | `JS_SetMemoryLimit`; `JS_SetMaxStackSize` | In-process; a bug is a host bug |
| **quickjs-emscripten** | Same, **inside a Wasm module** (QuickJS compiled to Wasm) | Same bytecode caveat; the Wasm module is the portable unit | `setInterruptHandler`; `shouldInterruptAfterDeadline(ms)` | `runtime.setMemoryLimit(bytes)` (aggregate across contexts) | Wasm trap; handles must be manually disposed |
| **isolated-vm** | **V8 isolate** (separate heap, separate thread possible) | Node↔isolated-vm version matrix (22→5.x/4.x, 24→6.x/5.x, 26→7.x) | `timeout` on eval/run/apply | `memoryLimit` MB — **"more of a guideline... 2-3 times this limit"** | `onCatastrophicError` → `process.abort()`; **maintenance mode** |
| **Deno permissions** | **Process**-level capability grants, per-permission scopes | Deno runtime version | None — "no limits on the execution of code at the same privilege level" | None (V8 flags only) | Process death; `--allow-run`/`--allow-ffi` void the sandbox |
| **Lua (`setfenv`/`_ENV`)** | **Interpreter** with a replaced global environment | None — and `setfenv` was removed in Lua 5.2 | None built in | None built in | None — a Lua bug is a host bug |

### Table 3 — Where each system puts its versioning burden

| Versioning approach | Examples | What breaks when you get it wrong |
|---|---|---|
| **Protocol major/minor** (major = compat break, minor = additive) | Terraform v5/v6, `go-plugin` `ProtocolVersion` | Old plugins refuse to load with a friendly error, or load and misbehave |
| **Named API versions + conversion** | Kubernetes `v1alpha1`→`v1`, CRD conversion webhooks | Persisted data becomes unreadable; the removal gate is what prevents it |
| **Semver package in the IDL** | WIT `package foo:bar@1.0.1` | Nothing automatic — the loader must enforce it |
| **In-band deprecation signal** | K8s `deprecated: true` + `deprecationWarning` header; `removed_release` metric label | Clients never learn they are about to break |
| **Version-locked opaque artifact** | QuickJS bytecode; isolated-vm `snapshot`/`cachedData` | **Arbitrary code execution** if accepted from a plugin |
| **No versioning at all** | Lua sandboxes | Silent divergence; escapes found in the wild (CVE-2022-0543) |

---

## Synthesis for PetDock

### What the two camps actually disagree about

"Plugin over a protocol" and "sandbox substrate" are not competing answers to one question; they are answers to **two different questions**, and PetDock needs both.

The protocol camp answers: *how do I let third parties extend a system I do not want to recompile, and how do I retire an interface over years without breaking anyone?* Its answer is a versioned data contract, a process boundary, and a written deprecation policy with numbers in it. Kubernetes and Terraform are the two best-documented instances of that answer, and they agree on the shape: **major version = compatibility break, minor = additive; a removal is gated on the data being migrated; the contract is published independently of the host.**

The sandbox camp answers a narrower and harder question: *what happens when the plugin is hostile, or merely broken, and it is running inside my process?* Every member of that camp converges on the same architecture: **deny-by-default capability handles, no ambient authority, a hard resource ceiling, and a preemption mechanism the guest cannot disable.** They differ only in what provides it — a Wasm instance, a V8 isolate, a purpose-built interpreter, or an OS process.

The trap is to think the sandbox camp's work subsumes the protocol camp's. It does not. A Wasm instance with a perfectly capability-scoped import list still has no answer for "how do I ship v2 of my pet-behaviour interface without breaking the pets already using v1" — and the answer to *that* is Kubernetes's `served: false` gate and Terraform's major/minor protocol split, not a better sandbox.

### The single biggest caveat

**The strongest isolation story on this list is also the least standardized one.** WASI preview 2's interfaces — the ones a plugin ABI would actually be written against — are at **Phase 3 of 5**, are versioned `0.x` by design until Phase 5, and the Component Model is documented as a *Developer Preview* mechanism whose stability is maintained "by producer and consumer tools" rather than by a standards body ([WASI Proposals](https://github.com/WebAssembly/WASI/blob/main/docs/Proposals.md), [component-model README](https://github.com/WebAssembly/component-model)). The blog literature that reports WASI 1.0 as imminent or the Component Model as "shipping everywhere" could not be corroborated against a primary source in this session and is contradicted by the phase tables.

The practical consequence for PetDock is specific: **build the plugin ABI on WIT-as-a-shape and on a pinned preview version, and treat `wasi:*` as a moving dependency you re-pin deliberately** — not as a frozen platform you can target once and forget. The capability *model* is sound and available today; the capability *standard* is not done.

### What I would copy, in order of value

1. **Terraform's protocol split** — plugin = separate process over a versioned RPC contract; major = compatibility break, minor = additive; ship a translator (`tf5to6server`-style) as a first-class artifact; publish the protocol definition so a fork could reimplement it. This is the highest-value, lowest-risk transfer, and the BUSL episode is the argument for it.
2. **Kubernetes's removal gate** — never delete a plugin ABI version in one step. Deprecate in-band (a warning the host actually surfaces, plus a field naming the release that will remove it), then make the version unservable *reversibly*, then migrate persisted state, then remove.
3. **WASI's capability model** — plugins receive handles and nothing else: no ambient filesystem, no ambient network, no environment, no argv, stdin closed. Filesystem access only through explicitly preopened directories; network only through an address check.
4. **Extism's declarative manifest** — one file per plugin declaring its wasm source, **mandatory** content hash, memory ceiling, allowed hosts, allowed path mappings, and a config bag. Extism's empty-vs-null ambiguity and optional hash are the two things to fix in the copy.
5. **Wasmtime's layered limits** — epoch deadline for wall-clock preemption, a hard memory cap that *also* bounds worst-case `memory.copy`, a bounded host-call timeout because neither of the first two covers a guest blocked in a host function, and output filtering for anything that reaches a terminal-like surface.
6. **Deno's self-tightening permissions** — a plugin can query and voluntarily revoke its own capabilities at runtime. No Wasm runtime offers this and it is a cheap, high-value addition to a manifest-driven model.

### What I would reject, and why

- **In-process scripting with a bolted-on sandbox** (Lua's `setfenv`/`_ENV` lineage). The lua-users wiki — the community's own reference page — opens by saying sandboxing "is generally speaking difficult to get right", then lists roughly twenty standard-library entry points as escape vectors, and the field has a real CVE from exactly this pattern ([lua-users](http://lua-users.org/wiki/SandBoxes), [CVE-2022-0543](https://ine.com/blog/cve-20220543-lua-sandbox-escape-in-redis)). The mature answer in that family was to build a different language (Luau) rather than to keep sandboxing Lua. PetDock should not spend its first year rediscovering that list.
- **`isolated-vm` as a primary substrate.** It is the most capable in-process JS sandbox in Node and its design (explicit `Reference`/`ExternalCopy` crossings, honest warnings, a `process.abort()` failure mode) is instructive — but it is in **maintenance mode**, its memory limit is documented as "more of a guideline" that a determined attacker can exceed by 2–3×, and its own README warns that leaking a single handle "yield[s] complete control over a process" ([isolated-vm](https://github.com/laverdet/isolated-vm)). Good to learn from; a poor thing to build a platform on.
- **Deno permissions as the isolation boundary for untrusted plugins.** The model is the best-designed *grant* UX in the field, but the manual is explicit that it is not a sandbox: "no limits on the execution of code at the same privilege level", all code on a thread shares one privilege level, and `--allow-run`/`--allow-ffi` are "equivalent to `--allow-all`" ([Deno manual](https://docs.deno.com/runtime/fundamentals/security/)). Adopt its permission *vocabulary*; do not adopt it as the boundary.
- **Kubernetes admission webhooks as a pattern for in-process extension.** `failurePolicy: Fail` is the default and the default timeout is 10 seconds ([Dynamic Admission Control](https://kubernetes.io/docs/reference/access-authn-authz/extensible-admission-controllers/)). That is the correct trade for a cluster control plane and the wrong one for a desktop pet: a slow or broken plugin must degrade the pet, never freeze it.

### The recommended shape

For a **Windows desktop host running untrusted third-party pet/behaviour plugins**, I would build the substrate as a **separate process per plugin group, running Wasm (Wasmtime) with a WASI-preview-2-pinned capability context, spoken to over a versioned RPC protocol**, and put the *policy* in Kubernetes's and Terraform's terms:

- **Process boundary** for crash containment and for the ability to kill a hung plugin (this is also what `go-plugin` and K8s controllers both do, and it is the one containment mechanism that works when the guest is blocked in a host call).
- **Wasm + `WasiCtxBuilder` defaults inside that process** for capability confinement: no preopens unless the manifest names them, all addresses denied unless the manifest names them, no env, no argv, stdin closed, `max_random_size` bounded. This is the model [wasi.dev/security](https://wasi.dev/security) describes and [Wasmtime's builder](https://docs.wasmtime.dev/api/wasmtime_wasi/struct.WasiCtxBuilder.html) implements.
- **Epoch interruption as the always-on preemption floor** plus a **memory cap via `ResourceLimiter`** (which is also what bounds the `memory.copy` time slice) plus an **outer host-call timeout** — the three-layer stack, because Wasmtime's own docs say no single layer suffices.
- **A WIT-shaped plugin ABI at a pinned preview version**, versioned with a Terraform-style major/minor rule and gated on a Kubernetes-style removal procedure over persisted pet state.
- **An Extism-style manifest** as the single declarative capability grant, with the content hash mandatory.

I would **not** claim this design is "sandboxed" in the absolute sense. I would claim, and be able to point at a source for each clause, that it is **deny-by-default at the capability boundary, resource-bounded at four independent layers, and crash-isolated by construction** — which is a stronger and more honest statement than the field's usual marketing.

---

## Claims I could not verify

Stated plainly, because each of these would change a decision if it were true:

1. **Extism billing itself as "a plugin system, not a sandbox."** Not found on extism.org. The docs I read describe a plugin system *with* sandboxing, and the accurate reading is that its isolation is inherited from Wasm plus the host functions you grant. **[unverified]** — treat the "not a sandbox" attribution as unconfirmed.
2. **The magic cookie as explicitly "not a security measure."** The framing appears in third-party restatements of `go-plugin`; I did not find it verbatim on the upstream page. The *fact* that `TF_PLUGIN_MAGIC_COOKIE` is a launch-intent check is well-supported by the SDK source comment "should NEVER be changed"; the *attribution* is **[unverified]**.
3. **WASI 1.0 timing, "Component Model phase 3" as a WebAssembly-spec milestone, and WASI 0.3 shipping dates.** Multiple 2026-dated secondary sources assert these; none could be corroborated against a primary standards document, and the primary phase tables contradict the strong form. **[unverified] — and treat as contradicted.**
4. **`controller-runtime` as the Go SDK named by Kubernetes' operator documentation.** The page lists kubebuilder and others but does not name `controller-runtime`. **[unverified]** against that page.
5. **Wasmer's equivalent of `ResourceLimiter`, and its current WASI preview 2 support.** The metering middleware is documented; the memory-cap and preview-2 status were not verified. **[unverified]**.
6. **Figma's use of QuickJS** is cited by `quickjs-emscripten`'s README as inspiration and by secondary summaries as a switch; I did not fetch Figma's own posts. **[unverified]**.

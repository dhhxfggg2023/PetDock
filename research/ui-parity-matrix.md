# 旧判据 = 新层规格书（parity matrix）

来源：`--uitest` 的实际输出（295 条，证据目录 `artifacts/verify/20261031-175300`）。
分类是**迁移方案**（v2 §4）的输入：KEEP 原样保留、PORT 换个契约重写、REWRITE 只属于旧壳。

| 类别 | 条数 | 含义 |
| --- | --- | --- |
| KEEP | 200 | 与界面壳无关（后端/合成器/协议），原样保留 |
| PORT | 50 | 界面行为：必须在新契约下重做等价判据，语义一条不许变 |
| REWRITE | 45 | 旧壳特有（导航/面板/主题/模态/托盘/引导/授权弹窗），用新壳等价判据取代 |
| **合计** | 295 | |

## PORT

### ui-auth（9）

- `ui-auth-batch-grouped`
- `ui-auth-batch-header`
- `ui-auth-batch-per-request-selection`
- `ui-auth-batch-once-disabled`
- `ui-auth-batch-selection-isolated`
- `ui-auth-batch-answer-per-request`
- `ui-auth-batch-single-unchanged`
- `ui-auth-batch-late-arrival`
- `ui-auth-batch-late-arrival-keeps-selection`

### ui-market（6）

- `ui-market-fixed-endpoint`
- `ui-market-fixed-download-prefix`
- `ui-market-index-parses`
- `ui-market-index-rejects-bad`
- `ui-market-sha256-verified`
- `ui-market-prefix-gate`

### ui-pets（6）

- `ui-pets-duplicate-delete`
- `ui-pets-per-pet-size`
- `ui-pets-detail`
- `ui-pets-desktop-stays-listed`
- `ui-pets-desktop-roundtrip`
- `ui-pets-desktop-delete-while-hidden`

### ui-plugin（4）

- `ui-plugin-trash-section`
- `ui-plugin-delete-affordance`
- `ui-plugin-entry-contained`
- `ui-plugin-entry-single-owner`

### ui-plugins（1）

- `ui-plugins-empty-state`

### ui-settings（15）

- `ui-settings-language-switch`
- `ui-settings-audit-entry`
- `ui-settings-update-entry`
- `ui-settings-update-offline-honest`
- `ui-settings-update-two-timings`
- `ui-settings-update-later-is-not-restart`
- `ui-settings-update-pending-restored`
- `ui-settings-pet-card`
- `ui-settings-pet-size`
- `ui-settings-update-atomic`
- `ui-settings-update-no-temp-leftover`
- `ui-settings-update-skips-unchanged-write`
- `ui-settings-pet-size-persisted`
- `ui-settings-pet-size-per-pet`
- `ui-settings-pet-size-not-overridden`

### ui-update（4）

- `ui-update-three-outcomes`
- `ui-update-relaunch-reported`
- `ui-update-relaunch-absent-is-normal`
- `ui-update-start-intent-unreadable`

### ui-work（5）

- `ui-work-progress-visible`
- `ui-work-progress-stage`
- `ui-work-cancel-once`
- `ui-work-progress-hidden`
- `ui-work-progress-determinate`

## REWRITE

### ui-auth（3）

- `ui-auth-dialog`
- `ui-auth-dialog-tier2-once-disabled`
- `ui-auth-dialog-plaintalk`

### ui-modal（6）

- `ui-modal-topmost-onboarding`
- `ui-modal-topmost-consent`
- `ui-modal-topmost-quit`
- `ui-modal-band-completeness`
- `ui-modal-above-topmost-pet`
- `ui-modal-band-control`

### ui-onboarding（14）

- `ui-onboarding-one-step`
- `ui-onboarding-no-language-choice`
- `ui-onboarding-through-caption`
- `ui-onboarding-keeps-language`
- `ui-onboarding-runs-once`
- `ui-onboarding-not-repeated`
- `ui-onboarding-marker-survives-settings-write`
- `ui-onboarding-state-audit-fields`
- `ui-onboarding-inline-error`
- `ui-onboarding-no-modal-error`
- `ui-onboarding-frame-alpha-transparent`
- `ui-onboarding-consent-not-pumped`
- `ui-onboarding-consent-request-kept`
- `ui-onboarding-consent-asker-released`

### ui-panel（1）

- `ui-panel-singleton`

### ui-settings（1）

- `ui-settings-a11y`

### ui-theme（2）

- `ui-theme-switch-keeps-panel`
- `ui-theme-high-contrast`

### ui-tray（8）

- `ui-tray-items`
- `ui-tray-pet-per-item`
- `ui-tray-pet-command-targets-own`
- `ui-tray-language-reload`
- `ui-tray-pet-menu-reopens`
- `ui-tray-restart-entry`
- `ui-tray-restart-optional`
- `ui-tray-pet-remove-drops-catalog`

### ui-workspace（10）

- `ui-workspace-page-order`
- `ui-workspace-page-order-tie`
- `ui-workspace-switch`
- `ui-workspace-status`
- `ui-workspace-status-survives-refresh`
- `ui-workspace-status-problem-kind`
- `ui-workspace-status-cleared-on-switch`
- `ui-workspace-unknown-page`
- `ui-workspace-render`
- `ui-workspace-empty`

## KEEP

### hit-surface（2）

- `hit-surface-wired-declared`
- `hit-surface-wired-forwards`

### hit-threshold（5）

- `hit-threshold-transparent-five-points`
- `hit-threshold-fringe-passes`
- `hit-threshold-at-threshold-hits`
- `hit-threshold-alpha-zero-still-passes`
- `hit-threshold-survives-bitmap-dispose`

### plugin-install（5）

- `plugin-install-directory`
- `plugin-install-replace`
- `plugin-install-backup-not-a-plugin`
- `plugin-install-zip-slip`
- `plugin-install-bad-manifest`

### plugin-inventory（6）

- `plugin-inventory-rows`
- `plugin-inventory-disable`
- `plugin-inventory-enable`
- `plugin-inventory-remove-essential`
- `plugin-inventory-remove-builtin`
- `plugin-inventory-remove-user`

### plugin-manifest（1）

- `plugin-manifest-default-enabled`

### plugin-policy（1）

- `plugin-policy-silent-grants`

### plugin-trash（6）

- `plugin-trash-list`
- `plugin-trash-path-guard`
- `plugin-trash-restore`
- `plugin-trash-restore-conflict`
- `plugin-trash-purge`
- `plugin-trash-prune`

### ui-agent（10）

- `ui-agent-loaded`
- `ui-agent-running`
- `ui-agent-refresh`
- `ui-agent-stale`
- `ui-agent-transition`
- `ui-agent-unreadable`
- `ui-agent-unknown-state`
- `ui-agent-language`
- `ui-agent-missing`
- `ui-agent-dataroot`

### ui-audio（6）

- `ui-audio-companion`
- `ui-audio-play`
- `ui-audio-mute`
- `ui-audio-missing`
- `ui-audio-format`
- `ui-audio-default-on`

### ui-audit（14）

- `ui-audit-view-rows`
- `ui-audit-view-empty`
- `ui-audit-view-unavailable`
- `ui-audit-trail-file-written`
- `ui-audit-trail-row-shape`
- `ui-audit-trail-grant-and-deny`
- `ui-audit-trail-use-not-persisted`
- `ui-audit-trail-survives-instance`
- `ui-audit-bundle-uses-trail`
- `ui-audit-bundle-not-only-recent`
- `ui-audit-trail-summary-honest`
- `ui-audit-trail-consent-recorded`
- `ui-audit-trail-write-failure-inert`
- `ui-audit-trail-not-in-repo`

### ui-catalog（8）

- `ui-catalog-all-packs`
- `ui-catalog-display-name`
- `ui-catalog-active-from-set`
- `ui-catalog-missing-pack`
- `ui-catalog-active-gone`
- `ui-catalog-multi-open`
- `ui-catalog-name-migration`
- `ui-catalog-pack-metadata`

### ui-celestial（6）

- `ui-celestial-attracts`
- `ui-celestial-no-collapse`
- `ui-celestial-bounded`
- `ui-celestial-zero-step`
- `ui-celestial-single-pet`
- `ui-celestial-default-off`

### ui-companion（12）

- `ui-companion-roster`
- `ui-companion-bound`
- `ui-companion-per-instance`
- `ui-companion-command`
- `ui-companion-missing-role`
- `ui-companion-unknown-command`
- `ui-companion-gesture`
- `ui-companion-snapshot`
- `ui-companion-offline-decay`
- `ui-companion-commit`
- `ui-companion-state-roundtrip`
- `ui-companion-faults`

### ui-compose（1）

- `ui-compose-read-before-start`

### ui-diag（8）

- `ui-diag-log-file-written`
- `ui-diag-log-line-has-agent-timestamp`
- `ui-diag-log-part-until-complete`
- `ui-diag-log-rolls-on-size`
- `ui-diag-log-write-never-throws`
- `ui-diag-redact-user-profile`
- `ui-diag-redact-machine-name`
- `ui-diag-redact-plugin-root`

### ui-frame（1）

- `ui-frame-pump-overruns`

### ui-hit（1）

- `ui-hit-tier1-consumes`

### ui-hitmap（2）

- `ui-hitmap-pierce-hit`
- `ui-hitmap-pierce-pass`

### ui-import（5）

- `ui-import-cancel-result`
- `ui-import-copy-progress`
- `ui-import-copy-cancel-midfile`
- `ui-import-convert-progress`
- `ui-import-convert-cancel`

### ui-install（2）

- `ui-install-cancel-clean`
- `ui-install-copy-progress`

### ui-keyhook（5）

- `ui-keyhook-not-installed-without-grantee`
- `ui-keyhook-install`
- `ui-keyhook-pause-resume`
- `ui-keyhook-uninstall`
- `ui-keyhook-no-synthetic-events`

### ui-net（5）

- `ui-net-allowed-hosts`
- `ui-net-redirect-status`
- `ui-net-redirect-policy`
- `ui-net-contents-address`
- `ui-net-failure-names-host`

### ui-pack（16）

- `ui-pack-lines-read`
- `ui-pack-lines-metadata`
- `ui-pack-lines-caps`
- `ui-pack-lines-absent`
- `ui-pack-lines-bad-shape`
- `ui-pack-lines-language`
- `ui-pack-lines-language-prefix`
- `ui-pack-lines-sample`
- `ui-pack-hits-read`
- `ui-pack-hits-clamp`
- `ui-pack-hits-caps`
- `ui-pack-hits-absent`
- `ui-pack-hits-bad-shape`
- `ui-pack-hits-covers`
- `ui-pack-hits-sample`
- `ui-pack-hits-window`

### ui-pet（6）

- `ui-pet-feedback`
- `ui-pet-throw`
- `ui-pet-position-persist-round-trip`
- `ui-pet-position-persist-missing-falls-back`
- `ui-pet-position-persist-unknown-ignored`
- `ui-pet-position-persist-restart-restores`

### ui-physics（7）

- `ui-physics-support-find`
- `ui-physics-support-skip-narrow`
- `ui-physics-support-skip-inside`
- `ui-physics-support-skip-top`
- `ui-physics-support-still-there`
- `ui-physics-support-none`
- `ui-physics-release-supported`

### ui-pointer（6）

- `ui-pointer-session-down`
- `ui-pointer-down-blocks-drag`
- `ui-pointer-session-move-up`
- `ui-pointer-cancel-on-deactivate`
- `ui-pointer-session-budget`
- `ui-pointer-drag-when-unclaimed`

### ui-resident（3）

- `ui-resident-window-visible`
- `ui-resident-frame-loop`
- `ui-resident-hidden-stops-pump`

### ui-roster（1）

- `ui-roster-readonly`

### ui-set（8）

- `ui-set-plan-extra`
- `ui-set-plan-no-shown`
- `ui-set-plan-empty`
- `ui-set-plan-hidden-skipped`
- `ui-set-plan-all-hidden`
- `ui-set-two-windows`
- `ui-set-toggle-all`
- `ui-set-hide-all-stops-pump`

### ui-single（3）

- `ui-single-instance-first`
- `ui-single-instance-refused`
- `ui-single-instance-released`

### ui-skip（1）

- `ui-skip-ledger-classify`

### ui-throttle（15）

- `ui-throttle-tier-default-active`
- `ui-throttle-probe-failure-inert`
- `ui-throttle-occluded-by-window`
- `ui-throttle-occluded-period`
- `ui-throttle-cloaked-not-occlusion`
- `ui-throttle-occluded-transparent-ignored`
- `ui-throttle-pump-self-heals`
- `ui-throttle-idle-skips-submit`
- `ui-throttle-idle-period-cap`
- `ui-throttle-idle-decided-idle`
- `ui-throttle-diagnostic-line`
- `ui-throttle-fullscreen-stops-pump`
- `ui-throttle-fullscreen-resume`
- `ui-throttle-single-decision-owner`
- `ui-throttle-no-new-events`

### ui-topology（4）

- `ui-topology-cache`
- `ui-topology-sane`
- `ui-topology-platform-land`
- `ui-topology-walk-off`

### ui-update（18）

- `ui-update-fixed-endpoint`
- `ui-update-version-compare`
- `ui-update-feed-parses`
- `ui-update-feed-rejects-bad`
- `ui-update-pending-roundtrip`
- `ui-update-pending-needs-zip`
- `ui-update-pending-inside-only`
- `ui-update-request-is-receipt`
- `ui-update-extract-trims-top`
- `ui-update-extract-needs-entry`
- `ui-update-helper-copy-unblocked`
- `ui-update-result-once`
- `ui-update-clear-keeps-backup`
- `ui-update-start-none`
- `ui-update-start-handoff`
- `ui-update-start-cancel-unfinished`
- `ui-update-start-rollback-interrupted`
- `ui-update-start-gate-off-probe`

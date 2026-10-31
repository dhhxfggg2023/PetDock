# 旧界面层教训档案（R0b）

用途：重写界面层、删除旧代码之前，把旧层用真机事故换来的取舍留成一份可查清单。  
怎么用：新层动到某类行为前，先在本档对应主题里找到那一条，按「判据」列验证；判据写「需新增」的，先补判据再写代码。  
不做什么：不收通用软件工程常识，不替代原规格与 RFC，也不复述已删代码的实现细节。

## 生命周期与资源

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| 显示窗口与起帧泵必须绑成同一个动作，只做一半就是静止首帧或白烧 CPU | `packages/core/PetDock.Core/src/Ui/ResidentFrameLoop.cs:5` | 隐藏即停泵，显示必起泵 | ui-resident-hidden-stops-pump |
| 泵线程会自行退出且不清标志，只读标志会把停帧的宠物报成在跑 | `packages/core/PetDock.Core/src/Ui/ResidentFrameLoop.cs:99` | 泵存活按 HasExited 判，且锁外读 | ui-throttle-pump-self-heals |
| 托盘图标要等首次滴答才注册；构造期没有消息泵，注册会静默失败 | `packages/core/PetDock.Core/src/Ui/TrayHost.cs:142` | 首次滴答才 NIM_ADD，失败留一行现场 | 需新增 |
| 换亮度别 Dispose 再开面板：用户会看到窗口跳回屏幕中央、回到第一页 | `packages/core/PetDock.Core/src/Ui/UiHost.Appearance.cs:15` | 原地换色板，窗口/当前页/滚动位置不动 | ui-theme-switch-keeps-panel |
| 模态窗自带消息循环，滴答器仍会触发，要守卫防同一次询问弹两遍 | `packages/core/PetDock.Core/src/Ui/UiConsentPort.cs:29` | _showing 期间不弹第二个窗 | ui-auth-batch-late-arrival |
| 一个槽位装配失败只许跳过这一只，不许连累已经在桌上的那几只 | `packages/core/PetDock.Core/src/Ui/PetWindowSet.cs:79` | 单只失败留日志，其余照常跑 | ui-set-two-windows |
| 面板同一时刻只跑一件长活，不排队也不并发写同一个数据区 | `packages/core/PetDock.Core/src/Ui/UiHost.Work.cs:243` | 第二次点击被直接拒，不并发执行 | 需新增 |

## 窗口与命中

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| 自有模态窗不置顶会被自己的宠物窗盖住，用户一个弹窗都看不见 | `packages/core/PetDock.Core/src/Ui/PetDockDialog.cs:6` | 所有自有模态窗进置顶档 | ui-modal-above-topmost-pet |
| 只写 TopMost 属性不够：原生 exstyle 的 TOPMOST 位会被系统抹掉 | `packages/core/PetDock.Core/src/Ui/PetDockDialog.cs:17` | 句柄建好与显示两个时点都设位，判据读位 | ui-modal-topmost-consent |
| ShowInTaskbar 为真会把窗提到普通档，顺手抹掉 TOPMOST | `packages/core/PetDock.Core/src/Ui/PetDockDialog.cs:33` | 模态窗一律不进任务栏 | ui-modal-topmost-quit |
| 系统 MessageBox 不进置顶档，确认框看不见等于「点了退出没反应」 | `packages/core/PetDock.Core/src/Ui/ConfirmDialog.cs:3` | 确认一律走自有置顶对话框 | ui-modal-topmost-quit |
| 宠物窗是逐像素分层窗、没有控件层，气泡必须另起一个窗 | `packages/core/PetDock.Core/src/Ui/PetSpeechBubble.cs:3` | 气泡独立窗：NOACTIVATE+TOOLWINDOW+TRANSPARENT | 需新增 |
| 右键菜单的归属由被点那只决定；认不出就退回无归属菜单，不猜 | `packages/core/PetDock.Core/src/Ui/UiHost.Tray.cs:39` | 认不出返回 null，不代选一只 | ui-tray-pet-command-targets-own |
| 「退出」必须与「只动这一只」视觉分开，否则被读成关掉这一只 | `packages/core/PetDock.Core/src/Ui/TrayHost.PetMenu.cs:17` | 分隔线把全局项隔开 | ui-tray-pet-per-item |
| 列表表头是独立子窗口，高度只能问它要，按字号估会盖住表头 | `packages/core/PetDock.Core/src/Ui/Design/ThemedList.EmptyArea.cs:59` | LVM_GETHEADER+GetWindowRect 取真实高度 | 需新增 |

## DPI 与缩放

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| 别开 AutoScaleMode.Dpi：尺寸已是设备像素，再乘一次就是两套缩放 | `packages/core/PetDock.Core/src/Ui/Design/Theme.cs:11` | AutoScaleMode 恒 None，缩放只有一个出处 | 需新增 |
| 字号按像素算、不按点算，点的渲染是第二套缩放 | `packages/core/PetDock.Core/src/Ui/Design/Theme.cs:132` | 字号显式换算成像素 | 需新增 |
| 自绘必须用控件所在 DPI 的字体，不能拿主屏那一份 | `packages/core/PetDock.Core/src/Ui/Design/Theme.cs:155` | FontFor(role, dpi) 取字体 | 需新增 |
| ListView 默认行高只有 17px，150% 下中文字形会被裁掉下半截 | `packages/core/PetDock.Core/src/Ui/Design/ThemedList.cs:6` | SmallImageList 按字体撑行高 | 需新增 |
| 说明行高钉死 14dip 会裁字形，要按字体行高量 | `packages/core/PetDock.Core/src/Ui/Design/FormGrid.cs:67` | 行高由测量决定，不写常量 | 需新增 |
| 显式口径下 DPI 变化框架不会动手，窗口得自己按比例缩一次 | `packages/core/PetDock.Core/src/Ui/Design/Theme.cs:206` | 比例真变才缩，并留一行诊断 | 需新增 |

## 焦点与模态

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| 别靠抢焦点：前台锁让后台进程的 SetForegroundWindow 失败 | `packages/core/PetDock.Core/src/Ui/PetDockDialog.cs:13` | 用置顶而不是抢焦点 | ui-modal-above-topmost-pet |
| ShowDialog 把同线程顶层窗置 WS_DISABLED，宠物可见却点不动 | `packages/core/PetDock.Core/src/Ui/PetDockDialog.cs:8` | 模态期间宠物窗不被禁用成「点不动」 | ui-modal-band-control |
| 模态期间滴答器仍在跑；引导窗开着时不许再泵授权询问 | `packages/core/PetDock.Core/src/Ui/OnboardingWindow.cs:10` | 询问留队列，引导关掉再弹 | ui-onboarding-consent-not-pumped |
| 授权一次取走全部待答请求，只开一个窗按插件分段 | `packages/core/PetDock.Core/src/Ui/UiConsentPort.cs:73` | 一窗多段、答案各自落账 | ui-auth-batch-grouped |
| 晚 0.4 秒到的请求要并进已开的窗，否则仍会连弹三个 | `packages/core/PetDock.Core/src/Ui/UiConsentPort.cs:75` | 模态期间 AbsorbPending | ui-auth-batch-late-arrival |
| 关窗与取消都按拒绝处理，默认拒绝比默认允许安全 | `packages/core/PetDock.Core/src/Ui/UiConsentPort.cs:105` | 无回答即拒绝 | ui-auth-batch-answer-per-request |
| 别在菜单点击链里开模态窗，两层消息泵会互相嵌套 | `packages/core/PetDock.Core/src/Ui/TrayHost.PetMenu.cs:100` | 动作投递回消息循环再执行 | ui-tray-pet-per-item |

## 语言与文案

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| 文案只有一个所有者：代码只持键，文案在 i18n 表里 | `packages/core/PetDock.Core/src/Ui/I18n.cs:1` | 界面代码不出现中文字面量 | 需新增 |
| 缺键就显示键名本身；回落中/英会把漏翻伪装成正常 | `packages/core/PetDock.Core/src/Ui/I18n.cs:2` | 缺键可见，并计一次 missing | 需新增 |
| 判据按 id/键断言，不按显示文字断言 | `packages/core/PetDock.Core/src/Ui/TrayHost.Checks.cs:7` | 菜单与导航断言键 | ui-tray-language-reload |
| 不支持的语言必须拒绝；静默退回中文比报错更难查 | `packages/core/PetDock.Core/src/Ui/I18n.cs:92` | 不支持返回 null 并给原因 | ui-settings-language-switch |
| 语言选项永远用各自语言写，用户切错还能走回来 | `packages/plugins/petdock.ui.default/PetDock.Ui.Default/SettingsPage.xaml.cs:29` | 选项不随当前语言翻译 | ui-settings-language-switch |
| 热切换要在构造面板之前换表，旧语言的窗口直接重建 | `packages/core/PetDock.Core/src/Ui/UiHost.Panel.cs:23` | 重开面板即生效，不留半中半英 | ui-settings-language-switch |
| 托盘换语言只换字不换入口，菜单对象不重建 | `packages/core/PetDock.Core/src/Ui/TrayHost.cs:207` | 菜单 id 稳定、文案重读 | ui-tray-language-reload |
| 引导不替用户选语言：语言是设置页的事实 | `packages/core/PetDock.Core/src/Ui/OnboardingWindow.cs:6` | 引导里 0 个语言单选 | ui-onboarding-no-language-choice |

## 状态与反馈

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| 状态条一 Refresh 就清，「先写结果再刷新」就一帧都看不见 | `packages/core/PetDock.Core/src/Ui/UiContracts.cs:170` | 状态条留到换页或页面自清 | ui-workspace-status-survives-refresh |
| 长任务期间换页不能把「正在做什么」擦掉 | `packages/core/PetDock.Core/src/Ui/UiWorkspaceView.xaml.cs:92` | 进度在跑时状态条保留阶段文案 | ui-work-progress-visible |
| 设置失败要说清哪一步没成，别只说「保存失败」 | `packages/core/PetDock.Core/src/Ui/UiHost.Panel.cs:106` | 部分失败也返回具体原因 | 需新增 |
| 「没查到」与「已是最新」是两件事，混了用户永远不再检查 | `packages/core/PetDock.Core/src/Ui/UiHost.Update.cs:85` | 三态分开报 | ui-update-three-outcomes |
| 一条全局尺寸滑块做不到「大猫小老鼠」，尺寸要按宠物记 | `packages/core/PetDock.Core/src/Ui/UiContracts.cs:259` | 每只各存各的边长 | ui-pets-per-pet-size |
| 按钮文案说「点下去会发生什么」，不是「现在是哪一档」 | `packages/plugins/petdock.ui.default/PetDock.Ui.Default/PetsPage.xaml.cs:112` | 收起/放回文案随状态反向 | ui-pets-desktop-roundtrip |
| 拖动中只更新读数、松手才落盘，否则拖拽会卡 | `packages/plugins/petdock.ui.default/PetDock.Ui.Default/SettingsPage.xaml.cs:102` | ValueChanged 落盘被 _binding 拦住 | ui-settings-pet-size |
| 取消不是失败，不能画成错误条 | `packages/core/PetDock.Core/src/Ui/UiContracts.cs:50` | 取消走中性出口 | ui-work-cancel-once |

## 删除与不可逆

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| 收起与删除是两件事，压进一个 bool 就是这一族 bug 的来源 | `packages/core/PetDock.Core/src/Ui/UiHost.cs:75` | 收起只藏窗、删除才移出清单 | ui-pets-desktop-delete-while-hidden |
| 收起后必须还在宠物页里，否则没有入口把它放回来 | `packages/core/PetDock.Core/src/Ui/UiHost.Tray.cs:144` | 按清单出行，OnDesktop 只是状态 | ui-pets-desktop-stays-listed |
| 身份不能带可见性条件，否则收起的行会被当成另一只 | `packages/core/PetDock.Core/src/Ui/UiHost.Tray.cs:316` | IsPrimary 只答身份，不答可见性 | ui-pets-desktop-delete-while-hidden |
| 摆位必须认「已收起」，否则重启后它们又自己回到桌面 | `packages/core/PetDock.Core/src/Ui/PetWindowSet.Catalog.cs:31` | 收起的清单条目不摆 | ui-set-plan-hidden-skipped |
| 多开不复制素材，最后一条引用消失才删目录 | `packages/core/PetDock.Core/src/Ui/UiHost.Pets.cs:4` | 删一只不动其余两只 | ui-pets-duplicate-delete |
| 清单删了但素材没删是部分失败，必须报出来 | `packages/core/PetDock.Core/src/Ui/UiHost.Pets.cs:132` | 返回原因，刷新后当问题报 | 需新增 |
| 托盘「移除」先问一句：托盘是最容易点错的地方 | `packages/core/PetDock.Core/src/Ui/UiHost.Tray.cs:247` | 移除有确认框 | ui-tray-pet-remove-drops-catalog |
| 重启不是「再开一个自己」：单实例锁会让新进程直接退出 | `packages/core/PetDock.Core/src/Ui/UiHost.Restart.cs:4` | 新进程带 --wait-pid 等老进程退出 | ui-tray-restart-entry |

## 线程与并发

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| 读租约不能在 UI 线程：调度线程一停就是 10 秒，界面跟着冻 | `packages/core/PetDock.Core/src/Ui/UiHost.Hook.cs:7` | 探测在后台、装钩子回 UI | ui-keyhook-install |
| 装钩子必须在 UI 线程：回调由安装线程的消息循环派发 | `packages/core/PetDock.Core/src/Ui/UiHost.Hook.cs:8` | 安装点固定在 UI 线程 | ui-keyhook-install |
| 在 UI 线程等调度线程就是自己等自己（死锁） | `packages/core/PetDock.Core/src/Ui/UiHost.Panel.cs:7` | 复核与启停都在后台线程 | 需新增 |
| 复制/解压/校验在后台，建实例与挂窗口回 UI 线程 | `packages/core/PetDock.Core/src/Ui/UiHost.Work.cs:7` | UI 只 await，不等后台 | ui-import-copy-progress |
| finally 可能落在非 UI 线程，必须按控件线程投递 | `packages/core/PetDock.Core/src/Ui/UiHost.Work.cs:319` | 结束回调经控件投递 | ui-work-progress-hidden |
| 计时器用 WinForms.Timer；Threading.Timer 会把窗弹在没有泵的线程 | `packages/core/PetDock.Core/src/Ui/UiHost.cs:56` | 滴答器靠消息循环驱动 | 需新增 |
| 持锁停泵会自造假死：取锁→改状态，Stop 放锁外 | `packages/core/PetDock.Core/src/Ui/ResidentFrameLoop.Throttle.cs:12` | 提交与停泵都在锁外 | 需新增 |
| tick、合成、静止三个计数必须在同一次锁内写完 | `packages/core/PetDock.Core/src/Ui/ResidentFrameLoop.Throttle.cs:82` | 判据只读一次原子读数 | ui-throttle-idle-decided-idle |
| 方法组转委托每帧一个垃圾，300 秒浸泡把稳态判据顶成 FAIL | `packages/core/PetDock.Core/src/Ui/ResidentFrameLoop.Throttle.cs:311` | 委托缓存成字段 | 需新增 |
| 已释放的菜单别再读 Visible/Bounds，计时器线程上抛就是异常框 | `packages/core/PetDock.Core/src/Ui/TrayHost.Checks.cs:79` | 先判 IsDisposed | ui-tray-pet-menu-reopens |

## 插件与信任

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| 只接受 tier 0：页面代码与内核同进程、同在 UI 线程 | `packages/core/PetDock.Core/src/Ui/UiPluginCatalog.cs:3` | tier≠0 不装载 | 需新增 |
| 坏插件不能让面板打不开：失败写日志，其余页照常 | `packages/core/PetDock.Core/src/Ui/UiPluginCatalog.cs:22` | 单插件失败不影响壳 | ui-plugins-empty-state |
| 壳只认清单 id，类型自报 Id 不参与身份，避免冒用 | `packages/core/PetDock.Core/src/Ui/UiPluginCatalog.cs:106` | 页面 id 取自清单 | ui-plugin-entry-single-owner |
| entry 必须落在插件目录内，绝对路径与 .. 一律拒绝 | `packages/core/PetDock.Core/src/Ui/UiPluginCatalog.cs:115` | 越界 entry 被拒 | ui-plugin-entry-contained |
| 市场地址只能来自索引，不接受页面传来的 URL | `packages/core/PetDock.Core/src/Ui/UiHost.Market.cs:83` | 下载前缀写死并校验 | ui-market-fixed-download-prefix |
| 插件根放数据区：发行目录可能只读，删数据区=卸载干净 | `packages/core/PetDock.Core/src/Ui/UiHost.cs:150` | 用户插件根在数据区 | ui-plugin-root |
| 功能说明没声明就显示「未声明」，不从 id 猜一句 | `packages/core/PetDock.Core/src/Ui/UiSnapshot.cs:63` | 不编造描述 | ui-plugins-empty-state |

## 外部环境（杀软、hosts、文件锁）

| 教训 | 出处 | 新层必须保住的行为 | 判据 |
| --- | --- | --- | --- |
| raw.githubusercontent 被 hosts 指到 127.0.0.1，检查更新永远 30 秒超时 | `packages/core/PetDock.Core/src/Host/UpdateService.cs:7` | 走 api.github.com contents API | ui-update-fixed-endpoint |
| 被 hosts/加速器劫持的主机是接了 TCP 不回话，要按单跳限时 | `packages/core/PetDock.Core/src/Host/GitHubHttp.cs:47` | 每跳 12 秒限时，不等整体超时 | 需新增 |
| 失败原因要点名主机，并区分指到本机/没回话/连不上 | `packages/core/PetDock.Core/src/Host/GitHubHttp.cs:238` | 错误串带 host 与类型 | ui-net-failure-names-host |
| GitHub 资产必然先回 302：不跟重定向就是功能整块不可用而判据全绿 | `packages/core/PetDock.Core/src/Host/GitHubHttp.cs:5` | 自己跟重定向，每跳校验主机 | ui-net-redirect-policy |
| Windows 不允许覆盖自己加载中的映像，边跑边替换会换一半 | `packages/core/PetDock.Core/src/Host/UpdateService.cs:12` | 只准备+交接，搬运在退出后 | ui-update-start-handoff |
| 搬运工要从副本启动，因为安装目录里那一份正被自己锁着 | `packages/core/PetDock.Core/src/Host/UpdateStage.cs:117` | helper 从 _update/helper 副本启动 | ui-update-helper-copy-unblocked |
| File.Copy 会把 Zone.Identifier 一起带走，SmartScreen 拦下搬运工 | `packages/core/PetDock.Core/src/Host/InternetMark.cs:9` | 拷完就地清「来自互联网」标记 | ui-update-helper-copy-unblocked |
| 半覆盖之后备份目录是唯一能把旧版本换回去的东西 | `packages/core/PetDock.Core/src/Host/UpdateStage.cs:103` | 覆盖前备份，失败可回滚 | ui-update-clear-keeps-backup |
## 附：20 条「需新增」判据该落在哪一片

> 这是 R0b 的直接产出：新层写代码之前，先把这些判据补上（R2–R6 各自片内完成）。
> **注意 3 条要「翻译」而不是照搬**：AutoScaleMode / 像素字号 / 手动缩放都是 WinForms 时代的解法，
> 新层是 WPF，判据应改判「单一缩放源」与「跨屏拖动不变形」，而不是复刻旧机制。

| # | 教训（简） | 落片 | 新判据要证明什么 |
| --- | --- | --- | --- |
| 1 | 托盘图标要等首次滴答才注册 | R6 | 启动 3 秒后注册成功；失败留一行现场 |
| 2 | 面板同一时刻只跑一件长活 | R3 | 第二个请求被拒并说明；不并发写同一数据区 |
| 3 | 气泡必须另起一个窗 | R6 | 气泡窗 NOACTIVATE+TOOLWINDOW+TRANSPARENT，不抢焦点 |
| 4 | 表头高度只能问它要 | R2 | 表格控件按真实表头高度布局（100%–200% 都不遮） |
| 5 | 别开 AutoScaleMode.Dpi（翻译） | R2 | 全应用只有一个缩放源；150% 下尺寸不出现 2.25 倍 |
| 6 | 字号按像素算（翻译） | R2 | 字号随 DPI 单调变化，同一角色在不同 DPI 下视觉等大 |
| 7 | 用控件所在 DPI 的字体（翻译） | R2 | 把窗口拖到另一块屏，字形不被裁、不突变 |
| 8 | 列表行高要按字体撑 | R2 | 150% 下中文不被裁下半截 |
| 9 | 说明行高按测量 | R2 | 行高由测量得出，无魔法常量 |
| 10 | DPI 变化要自己处理（翻译） | R2 | 跨屏拖动后布局不重叠、不失焦 |
| 11 | 代码只持键，不持文案 | R2 | 静态扫描：界面代码 0 条中文字面量 |
| 12 | 缺键显示键名 | R2 | 故意删一条键 → 界面显示键名并计一次 missing |
| 13 | 设置失败要说清哪一步 | R6 | 部分失败返回「哪一项没成」，不是一句「保存失败」 |
| 14 | 素材没删掉是部分失败 | R4 | 删宠物后素材被占用 → 如实报「已移出清单、素材未删」 |
| 15 | UI 线程等调度线程 = 死锁 | R3 | 服务调用不得在 UI 线程同步等；越界即判红（源码扫描 + 超时） |
| 16 | 计时器要挂在有泵的线程 | R3 | 新层只用 DispatcherTimer；源码扫描禁 WinForms.Timer |
| 17 | 持锁停泵会自造假死 | R3 | 停泵/改状态都在锁外；压测 60 秒无假死 |
| 18 | 方法组转委托每帧一个垃圾 | R3 | 300 秒浸泡后稳态分配不增长 |
| 19 | 只接受 tier 0 | R5 | tier≠0 的页面清单被拒并说明原因 |
| 20 | 劫持主机接了 TCP 不回话 | R6 | 单跳限时（≤12s），不靠整体超时 |

**审阅时发现的一处旧层漂移**：`Design/Theme.cs:15` 注释里引用的 `ui-text-fits-*` 判据**已经不存在**
（全仓只剩这一处注释）。这正好证明本档案「必须带出处」的价值：判据会漂、注释会留，两者对账才能发现。
该注释随 R8 一起删，不单独修。

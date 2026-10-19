using PetDock.Core.PetRuntime;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PetDock.Plugins.BongoCat
{
    /// <summary>
    /// BongoCat 交互重写（docs/33）：用 ABI 的 <see cref="IBrain"/> 重新表达原应用的三类交互，
    /// **不嵌入**原 Tauri 应用、不引用它的任何素材（docs/33 §6）。
    ///
    /// 三条交互各自的实现方式，以及**为什么是这样**：
    ///
    /// ① 眼睛/头部跟随鼠标（docs/33 §2 第一条）。docs/33 §4 原设想是
    ///    `kind=interaction` + `input.mousePosition` + 一个算朝向的 Interaction surface。
    ///    这条路在当前 ABI 下走不通，理由是结构性的而不是实现偷懒：
    ///      - `PetSurfaceLoader` 对 `entry` 不是 `.dll` 的包直接 `surface-skip`，不登记任何实现；
    ///      - 就算写成 `IPointerSurface`，该接口只收「窗口内的按下/移动/抬起」，拿不到全局光标，
    ///        **也没有任何动画输出通道** —— 它无法让宠物动起来。
    ///    而 `IBrain` 每 tick 都能拿到 `WorldSnapshot`，并且能发 `SetAnimation`。所以朝向用 Brain 表达。
    ///
    ///    光标的来源刻意是 `input.pointer.move` 事件而不是 `world.Cursor`：
    ///    `WorldSnapshot.Cursor` 按 docs/11 §7.1 在光标离开窗口时是 **null**，
    ///    而「猫看着远处移动的鼠标」恰恰要求光标在窗口外时也继续跟。
    ///    `input.pointer.move` 的 `pos` 是宿主采样的**原始屏幕坐标**（PetFrameSource.PublishPointerMove
    ///    用的是 WorldProbe.LastScreenCursor），不受窗口边界限制。两者都用：事件优先，没有事件时退回
    ///    `world.Cursor`（窗口内局部坐标，换算到屏幕要加窗口原点）。
    ///
    /// ② 敲键盘猫爪敲击（docs/33 §2 第二条）。docs/33 §4 设想 `input.keyboardHook`。
    ///    **2026-10-19 起这条事件有生产源了**：内核的 `KeyboardHook`（WH_KEYBOARD_LL）在
    ///    **有插件持有 `input.keyboardHook` 时**才安装（docs/16:56），本插件的 manifest 已声明
    ///    该能力。托盘上那个不可隐藏的「暂停全局输入钩子」固定项（docs/14:110 / docs/18:22）
    ///    是它的安全阀，暂停后钩子不再向外发事件。
    ///    仍然保留 `_pawSourceSeen`：**事件到达就响应，不到达就不假装** —— 钩子装不上
    ///    （无交互桌面会话、被安全软件拦、或用户已暂停）时，「爪子一次都没抬过」必须
    ///    在诊断行里可分辨，而不是看起来像实现坏了。
    ///
    /// ③ 闲置呼吸/眨眼（docs/33 §2 第三条）。素材侧已经有 idle 4 帧（第 4 帧是眨眼），
    ///    Brain 只要在没有任何输入时保持 `idle` 即可 —— 不需要额外代码。
    ///
    /// **素材**：本插件不自带素材，读的是 docs/31 Sample Pack 形态的
    /// `assets/bongocat-pack/`（由 `tools/gen-bongocat-pack.mjs` 程序化生成，CC0）。
    /// 片名表由 `ClipNames` 给出，因为 Brain 只能切到呈现层真的有的片段：
    /// `SetAnimation` 对不存在的片段返回 false，仲裁器记 refused，屏幕上什么都不变。
    /// </summary>
    public sealed class BongoCatBrain : IBrain
    {
        // —— 片段名（与 tools/gen-bongocat-pack.mjs 的 CLIPS 键逐字对应）——
        private const string ClipIdle = "idle";
        private const string ClipLookUp = "look_up";
        private const string ClipLookDown = "look_down";
        private const string ClipLookLeft = "look_left";
        private const string ClipLookRight = "look_right";
        private const string ClipPawLeft = "paw_l";
        private const string ClipPawRight = "paw_r";

        /// <summary>朝向判定的死区（像素）：光标离宠物中心这么近就不转向，避免原地抖动。</summary>
        private const double GazeDeadZonePx = 24;

        /// <summary>抬爪动画的持续时长（毫秒）。paw_l/paw_r 是 3 帧 @8fps = 375ms，留一点余量。</summary>
        private const int PawHoldMs = 400;

        /// <summary>一次抬爪结束后回到 idle 之前的冷却，避免连击时片段反复重启。</summary>
        private const int PawCooldownMs = 120;

        private string _current = ClipIdle;
        private PetPoint? _screenCursor;
        private PetRect _windowRect = new(0, 0, 0, 0);
        private long _pawUntilMs;
        private long _pawCooldownUntilMs;

        /// <summary>`input.key.down` 是否真的到达过。诊断行用它区分「没按过键」与「没有事件源」。</summary>
        private bool _pawSourceSeen;

        /// <summary>抬爪次数（诊断行可见；0 且 pawSourceSeen=false 就是「没有键盘事件源」）。</summary>
        private int _pawCount;

        public InterfaceMeta Meta => new("com.petdock.bongocat.interaction", "1.0.0");

        public Task InitAsync(BrainContext context)
        {
            Console.Error.WriteLine(
                $"[PetDock] BongoCatBrain instantiated (tier 0, docs/33 rewrite)."
                + $" clips={ClipIdle},{ClipLookUp},{ClipLookDown},{ClipLookLeft},{ClipLookRight},{ClipPawLeft},{ClipPawRight}"
                + $" instance={context.PetInstanceId}");
            return Task.CompletedTask;
        }

        public IReadOnlyList<BrainDirective> Tick(int dtMs, WorldSnapshot world)
        {
            var directives = new List<BrainDirective>();
            long now = PetEvent.NowMs();

            // 窗口矩形每 tick 刷新：朝向判定要的是「光标相对宠物中心」，窗口被拖走时这个中心会变。
            if (world.WindowRect.W > 0 && world.WindowRect.H > 0)
            {
                _windowRect = world.WindowRect;
            }

            // world.Cursor 是窗口内局部坐标（窗口外为 null）；把它换算成屏幕坐标作为**兜底**来源。
            // 事件来源优先，因为它窗口外也继续更新（见类注释 ①）。
            if (_screenCursor is null && world.Cursor is PetPoint local)
            {
                _screenCursor = new PetPoint(_windowRect.X + local.X, _windowRect.Y + local.Y);
            }

            // ① 抬爪窗口内：爪子优先于朝向（正在敲的时候不该被鼠标带跑）。
            if (now < _pawUntilMs)
            {
                Switch(directives, _pawClip);
                return directives;
            }

            if (now < _pawCooldownUntilMs)
            {
                Switch(directives, ClipIdle);
                return directives;
            }

            // ② 朝向：光标相对宠物中心的方位。
            Switch(directives, GazeClip());
            return directives;
        }

        /// <summary>当前抬爪窗口对应的片段名（在 OnEvent 里定，Tick 里复用）。</summary>
        private string _pawClip = ClipPawLeft;

        /// <summary>把「光标在哪」折算成 4 个朝向之一；死区内保持当前朝向（不抖）。</summary>
        private string GazeClip()
        {
            if (_screenCursor is not PetPoint cursor || _windowRect.W <= 0 || _windowRect.H <= 0)
            {
                return ClipIdle;
            }

            double cx = _windowRect.X + (_windowRect.W / 2.0);
            double cy = _windowRect.Y + (_windowRect.H / 2.0);
            double dx = cursor.X - cx;
            double dy = cursor.Y - cy;

            // 对角方向归到主轴：横向差更大就左右，否则上下。这个 1.0 的比值让对角
            // 恰好落在左右/上下之间不来回跳（1.2 会让「右上」在 45° 附近反复切两个片段）。
            if (Math.Abs(dx) <= GazeDeadZonePx && Math.Abs(dy) <= GazeDeadZonePx)
            {
                return _current;
            }

            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                return dx < 0 ? ClipLookLeft : ClipLookRight;
            }

            return dy < 0 ? ClipLookUp : ClipLookDown;
        }

        public void OnEvent(PetEvent evt)
        {
            if (evt.Type == EventCatalog.PointerMove)
            {
                // payload 的 pos 是 [x, y] 屏幕坐标（docs/16 §3 / PetFrameSource.PublishPointerMove）。
                if (evt.Payload?["pos"] is JsonArray pos && pos.Count >= 2)
                {
                    double? x = AsDouble(pos[0]);
                    double? y = AsDouble(pos[1]);
                    if (x is double px && y is double py)
                    {
                        _screenCursor = new PetPoint(px, py);
                    }
                }

                return;
            }

            if (evt.Type == EventCatalog.KeyDown)
            {
                _pawSourceSeen = true;

                // docs/33 §2「敲键盘猫爪敲击」+ §4 的 map {left: pawL, right: pawR}。
                // 「对侧」的判定：按键落在键盘左半区就抬右爪，反之抬左爪 —— 这是猫爪敲键盘的
                // 真实观感（左手按左区、右手抬起来敲）。vkey 的左右半区用两个已知分界：
                // 数字行/字母左半区（A..L, 0x41..0x4C）与 0x30..0x39 视为左，其余视为右。
                bool leftHalf = IsLeftHalfKey(evt.Payload?["vkey"]);
                _pawClip = leftHalf ? ClipPawRight : ClipPawLeft;
                long now = PetEvent.NowMs();
                _pawUntilMs = now + PawHoldMs;
                _pawCooldownUntilMs = _pawUntilMs + PawCooldownMs;
                _pawCount++;
                Console.Error.WriteLine(
                    $"[PetDock] BongoCatBrain <- key.down vkey={evt.Payload?["vkey"]} paw={_pawClip} n={_pawCount}");
            }
        }

        public Task DisposeAsync()
        {
            Console.Error.WriteLine(
                $"[PetDock] BongoCatBrain disposed. paws={_pawCount} keyEventsSeen={_pawSourceSeen}"
                + (_pawSourceSeen ? string.Empty : " (no key events received: the host installs the global hook only when a plugin holds input.keyboardHook)"));
            return Task.CompletedTask;
        }

        /// <summary>vkey 是否落在键盘左半区。未知/缺失一律按右半区处理（保守：抬左爪更醒目）。</summary>
        private static bool IsLeftHalfKey(JsonNode? node)
        {
            double? raw = AsDouble(node);
            if (raw is not double value)
            {
                return false;
            }

            int vkey = (int)value;
            if (vkey >= 0x30 && vkey <= 0x39)
            {
                return true; // 主键盘数字行
            }

            if (vkey >= 0x41 && vkey <= 0x4C)
            {
                return true; // A..L
            }

            return false;
        }

        /// <summary>
        /// 从 JSON 节点取数值，int/long/double 都收。
        /// 只写 <c>GetValue&lt;double&gt;()</c> 是不够的：<c>{"vkey": 65}</c> 里那个 65 是
        /// <c>JsonValue&lt;int&gt;</c>，取 double 会抛 InvalidOperationException，于是每个按键
        /// 都被当成「未知 vkey」而落到保守分支 —— 抬爪方向永远一样。
        /// </summary>
        private static double? AsDouble(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }

            if (value.TryGetValue(out double asDouble))
            {
                return asDouble;
            }

            if (value.TryGetValue(out int asInt))
            {
                return asInt;
            }

            if (value.TryGetValue(out long asLong))
            {
                return asLong;
            }

            return null;
        }

        /// <summary>
        /// 只在真的换片段时发指令 —— 同片段重复 setAnimation 是每帧一次的无用指令，
        /// 而且会让 restart 语义变成「每帧重播第一帧」（屏幕上就是抽帧）。
        /// </summary>
        private void Switch(List<BrainDirective> directives, string clip)
        {
            if (string.Equals(_current, clip, StringComparison.Ordinal))
            {
                return;
            }

            _current = clip;
            directives.Add(BrainDirective.SetAnimation(clip, restart: true));
            // 行为层自己的可见性（RFC-02 §4 第 4 条「不允许静默」）：常驻模式没有 KernelLog，
            // 这行裸 Console.Error 是「宠物真的反应了」在进程外唯一的证据。
            Console.Error.WriteLine($"[PetDock] BongoCatBrain -> setAnimation {clip}");
        }
    }
}

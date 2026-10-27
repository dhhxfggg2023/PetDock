using PetDock.Core.PetRuntime;
using System;
using System.Collections.Generic;

namespace PetDock.Plugins.ReactionBrain
{
    /// <summary>
    /// 对时间 / 鼠标 / 前台窗口三类事件做出反应的行为层（原规格 50:23 的 M4 退出标准）。
    ///
    /// **clip 名的来源**：本插件只能切到呈现层真的有的片段。首发验证物 `fixtures/sample-pet`
    /// 按 原规格 31 §2 声明了 `idle` / `walk` / `hover` 三个 —— 所以三类反应分别映射到这三个，
    /// 不另造 `wave` / `moving` / `surprise`（那些名字在示例包里不存在，`SetAnimation` 会返回
    /// false、仲裁器记 refused，屏幕上什么都不变）。
    ///
    /// **为什么用 world 而不是自己读时钟**：`WorldSnapshot.TimeOfDayMinutes` 与 `FocusedApp`
    /// 都是宿主每帧采样后交给 Brain 的（原规格 11 §4）；Brain 自己调 `DateTime.Now` 会让
    /// 「同一 tick 输入 → 同一指令序列」这条 ABI 要求失效（原规格 11:161）。
    /// </summary>
    public sealed class ReactionBrain : IBrain
    {
        private const string ClipIdle = "idle";
        private const string ClipWalk = "walk";
        private const string ClipHover = "hover";

        private readonly TimeSpan _toggleInterval = TimeSpan.FromSeconds(3);
        private readonly TimeSpan _mouseCooldown = TimeSpan.FromMilliseconds(800);

        private string _current = ClipIdle;
        private int _lastToggleMinute = -1;
        private DateTime _lastMouseMove = DateTime.MinValue;
        private string? _lastFocusedApp;

        public InterfaceMeta Meta => new("petdock.brain.reaction", "1.0.0");

        public Task InitAsync(BrainContext context)
        {
            Console.Error.WriteLine("[PetDock] ReactionBrain instantiated (tier 0).");
            return Task.CompletedTask;
        }

        public IReadOnlyList<BrainDirective> Tick(int dtMs, WorldSnapshot world)
        {
            var directives = new List<BrainDirective>();

            // ① 时间：每过一个 tick 间隔切一次 idle ⇄ hover。
            // 用 world 的分钟数做粗粒度判据（它由宿主采样，不是 Brain 自读），
            // 再用本地节流保证 3 秒一次的实际观感。
            var now = DateTime.UtcNow;
            if (now - _lastToggleAt >= _toggleInterval)
            {
                _lastToggleAt = now;
                _lastToggleMinute = world.TimeOfDayMinutes;
                Switch(directives, _current == ClipIdle ? ClipHover : ClipIdle);
            }

            // ② 鼠标：OnEvent 收到 input.pointer.move 会刷新时间戳，冷却窗口内显示 walk。
            if (now - _lastMouseMove < _mouseCooldown)
            {
                if (_current != ClipWalk)
                {
                    Switch(directives, ClipWalk);
                }
            }
            else if (_current == ClipWalk)
            {
                Switch(directives, ClipIdle);
            }

            // ③ 前台窗口：FocusedApp 变了就反应一次（离开时回 idle）。
            string? focused = world.FocusedApp;
            if (!string.IsNullOrEmpty(focused)
                && !string.Equals(focused, _lastFocusedApp, StringComparison.OrdinalIgnoreCase))
            {
                _lastFocusedApp = focused;
                Switch(directives, ClipHover);
            }
            else if (string.IsNullOrEmpty(focused) && _lastFocusedApp is not null)
            {
                _lastFocusedApp = null;
                Switch(directives, ClipIdle);
            }

            return directives;
        }

        public void OnEvent(PetEvent evt)
        {
            if (evt.Type == EventCatalog.PointerMove)
            {
                _lastMouseMove = DateTime.UtcNow;
                Console.Error.WriteLine($"[PetDock] ReactionBrain <- {evt.Type}");
            }
            else if (evt.Type == EventCatalog.WindowFocus)
            {
                Console.Error.WriteLine($"[PetDock] ReactionBrain <- {evt.Type}");
            }
        }

        public Task DisposeAsync() => Task.CompletedTask;

        private DateTime _lastToggleAt = DateTime.MinValue;

        /// <summary>只在真的换片段时发指令 —— 同片段重复 setAnimation 是每帧一次的无用指令。</summary>
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
            Console.Error.WriteLine($"[PetDock] ReactionBrain -> setAnimation {clip}");
        }
    }
}

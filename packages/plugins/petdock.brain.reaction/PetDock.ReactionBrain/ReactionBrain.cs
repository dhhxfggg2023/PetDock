using PetDock.Core.PetRuntime;
using System;
using System.Collections.Generic;

namespace PetDock.Plugins.ReactionBrain
{
    /// <summary>
    /// 示例宠物的默认行为层：在桌面上小范围来回走动，并对鼠标 / 前台窗口做轻反应。
    ///
    /// **为什么要走**：宿主已经会把 moveTo 真落到窗口上（FrameComposition.WireWindow），
    /// 但行为层从来不发这条指令，于是默认宠物只会原地换 idle/hover —— 看起来像在抽动，
    /// 不像一只会自己活动的桌宠。这里让它每次沿水平方向走一小段、停一会儿、再往回走。
    ///
    /// **clip 名的来源**：首发验证物 fixtures/sample-pet 按 原规格 31 §2 声明了 `idle` / `walk` / `hover`。
    /// 不另造 `wave` / `moving` 这类名字 —— 示例包里没有，SetAnimation 会 refused，屏幕什么都不变。
    ///
    /// **不许自读时钟**：原规格 11:161 要求「同一 tick 输入 → 同一指令序列」。
    /// 所有节流都用调用方给的 <c>dtMs</c> 累加，位置只来自 <see cref="WorldSnapshot.WindowRect"/>；
    /// 本类不碰 DateTime、不读屏幕、不做持久化。
    ///
    /// **不越界**：目标可能落在屏幕外（窗口本来就在边上），窗口宿主的 MoveTo 会把落点夹进工作区；
    /// 本类通过「窗口位置几帧没动」识别撞墙，掉头走回去。
    /// </summary>
    public sealed class ReactionBrain : IBrain
    {
        private const string ClipIdle = "idle";
        private const string ClipWalk = "walk";
        private const string ClipHover = "hover";

        /// <summary>走路速度（逻辑像素 / 秒）。慢一点才像散步，不像瞬移。</summary>
        private const double WalkSpeed = 58.0;

        /// <summary>一趟走多远（逻辑像素）。</summary>
        private const double TripDistance = 112.0;

        /// <summary>到达判定的容差：moveTo 的坐标是 double，窗口落点是整数。</summary>
        private const double ArriveEpsilon = 1.0;

        /// <summary>连续多久没有位移就认定撞到工作区边缘，掉头（毫秒）。</summary>
        private const int StuckMs = 500;

        /// <summary>休息时长按趟数轮换，免得节奏像节拍器。</summary>
        private static readonly int[] RestPattern = { 2400, 3800, 1800, 4600 };

        private string _current = ClipIdle;
        private int _restRemainingMs = 900;
        private int _trip;
        private double? _targetX;
        private double _lastObservedX = double.NaN;
        private int _stuckMs;
        private int _mouseCooldownMs;
        private int _hoverMs;
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

            // 负 dt / 调试器造成的巨大 dt 都不能变成一步跳半屏。
            int dt = Math.Clamp(dtMs, 0, 250);
            _mouseCooldownMs = Math.Max(0, _mouseCooldownMs - dt);
            _hoverMs = Math.Max(0, _hoverMs - dt);

            PetRect rect = world.WindowRect;
            if (rect.W <= 0 || rect.H <= 0)
            {
                // 窗口还没建好（启动首帧）：没有位置就没有可发的移动指令。
                return directives;
            }

            // 前台应用变了就抬头看一会儿。空字符串 / null 都表示「没有前台」。
            string? focused = world.FocusedApp;
            if (!string.Equals(focused, _lastFocusedApp, StringComparison.OrdinalIgnoreCase))
            {
                _lastFocusedApp = focused;
                if (!string.IsNullOrEmpty(focused))
                {
                    _hoverMs = 1500;
                }
            }

            if (_targetX is double target)
            {
                Walk(directives, rect, target, dt);
            }
            else
            {
                Rest(directives, rect, dt);
            }

            return directives;
        }

        public void OnEvent(PetEvent evt)
        {
            // 鼠标一动就进入 900ms 的 hover 窗口；不在这里读时钟，冷却由 Tick 的 dt 递减。
            if (evt.Type == EventCatalog.PointerMove)
            {
                _mouseCooldownMs = 900;
            }
        }

        public Task DisposeAsync() => Task.CompletedTask;

        private void Walk(List<BrainDirective> directives, PetRect rect, double target, int dt)
        {
            double dx = target - rect.X;
            if (Math.Abs(dx) <= ArriveEpsilon)
            {
                Arrive(directives);
                return;
            }

            Switch(directives, ClipWalk);

            double step = Math.Min(Math.Abs(dx), WalkSpeed * dt / 1000.0);
            if (step > 0)
            {
                double nextX = rect.X + Math.Sign(dx) * step;
                directives.Add(BrainDirective.MoveTo(new PetPoint(nextX, rect.Y), WalkSpeed));
            }

            // 位置没变 = 窗口宿主把落点夹在了工作区边缘。掉头，不要对着墙一直推。
            if (!double.IsNaN(_lastObservedX) && Math.Abs(rect.X - _lastObservedX) < 0.25)
            {
                _stuckMs += dt;
            }
            else
            {
                _stuckMs = 0;
            }

            _lastObservedX = rect.X;
            if (_stuckMs >= StuckMs)
            {
                _targetX = rect.X + (dx > 0 ? -TripDistance : TripDistance);
                _stuckMs = 0;
                _lastObservedX = double.NaN;
            }
        }

        private void Rest(List<BrainDirective> directives, PetRect rect, int dt)
        {
            _restRemainingMs -= dt;
            if (_restRemainingMs <= 0)
            {
                // 第一趟朝左：默认落点是屏幕右下角，向左才是桌面上有路的方向。
                _trip++;
                double direction = (_trip % 2 == 1) ? -1.0 : 1.0;
                _targetX = rect.X + direction * TripDistance;
                _stuckMs = 0;
                _lastObservedX = double.NaN;
                return;
            }

            bool reacting = _mouseCooldownMs > 0 || _hoverMs > 0;
            Switch(directives, reacting ? ClipHover : ClipIdle);
        }

        private void Arrive(List<BrainDirective> directives)
        {
            _targetX = null;
            _stuckMs = 0;
            _lastObservedX = double.NaN;
            _restRemainingMs = RestPattern[_trip % RestPattern.Length];
            Switch(directives, ClipIdle);
        }

        /// <summary>只在真的换片段时发指令 —— 同片段重复 setAnimation 是每帧一次的无用指令。</summary>
        private void Switch(List<BrainDirective> directives, string clip)
        {
            if (string.Equals(_current, clip, StringComparison.Ordinal))
            {
                return;
            }

            _current = clip;
            directives.Add(BrainDirective.SetAnimation(clip, restart: true));
            // 行为层自己的可见性：常驻模式没有 KernelLog，这行裸 Console.Error 是进程外唯一的证据。
            Console.Error.WriteLine($"[PetDock] ReactionBrain -> setAnimation {clip}");
        }
    }
}

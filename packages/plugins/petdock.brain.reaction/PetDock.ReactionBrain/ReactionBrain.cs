using PetDock.Core.PetRuntime;
using System;
using System.Collections.Generic;

namespace PetDock.Plugins.ReactionBrain
{
    /// <summary>
    /// A brain that reacts to time, mouse, and foreground window changes.
    /// </summary>
    public sealed class ReactionBrain : IBrain
    {
        private string _currentAnimation = "idle";
        private DateTime _lastToggleTime;
        private readonly TimeSpan _toggleInterval = TimeSpan.FromSeconds(2); // Faster toggle for visibility
        private string? _lastFocusedApp = null;
        private DateTime _lastMouseMoveTime;
        private readonly TimeSpan _mouseMoveCooldown = TimeSpan.FromMilliseconds(500); // Avoid spamming

        public InterfaceMeta Meta => new("petdock.brain.reaction", "1.0.0");

        public Task InitAsync(BrainContext context)
        {
            System.Diagnostics.Debug.WriteLine($"[PetDock] ReactionBrain instantiated (tier 0).");
            Console.Error.WriteLine($"[PetDock] ReactionBrain instantiated (tier 0).");
            return Task.CompletedTask;
        }

        public IReadOnlyList<BrainDirective> Tick(int dtMs, WorldSnapshot world)
        {
            var directives = new List<BrainDirective>();

            // Time-based reaction: toggle animation every 2 seconds
            var now = DateTime.Now;
            if (now - _lastToggleTime >= _toggleInterval)
            {
                _lastToggleTime = now;
                _currentAnimation = _currentAnimation == "idle" ? "wave" : "idle";
                directives.Add(BrainDirective.SetAnimation(_currentAnimation, restart: true));
            }

            // Mouse reaction: if we have received a mouse move event recently, show moving animation
            if ((now - _lastMouseMoveTime) < _mouseMoveCooldown)
            {
                // Show moving animation if we are not already showing it (to avoid spam)
                if (_currentAnimation != "moving")
                {
                    directives.Add(BrainDirective.SetAnimation("moving", restart: true));
                    _currentAnimation = "moving"; // Update our state to avoid repeated directives
                }
            }
            else
            {
                // If we are showing moving animation and cooldown passed, we can go back to idle in Tick?
                // But we want to go back to idle only when no mouse move for a while.
                // We'll handle that by not setting moving animation when cooldown passed.
                // Actually, we want to show moving only when there is recent mouse move.
                // So if cooldown passed and we are currently moving, we should go back to idle.
                if (_currentAnimation == "moving")
                {
                    directives.Add(BrainDirective.SetAnimation("idle", restart: true));
                    _currentAnimation = "idle";
                }
            }

            // Foreground window reaction: if the focused app changes to a target app, react
            if (!string.IsNullOrEmpty(world.FocusedApp) && 
                !world.FocusedApp.Equals(_lastFocusedApp, StringComparison.OrdinalIgnoreCase))
            {
                _lastFocusedApp = world.FocusedApp;
                // Example: if the foreground app is notepad, show a surprise animation
                if (world.FocusedApp.Equals("notepad", StringComparison.OrdinalIgnoreCase))
                {
                    directives.Add(BrainDirective.SetAnimation("surprise", restart: true));
                }
                else if (_lastFocusedApp?.Equals("notepad", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // When leaving notepad, go back to idle
                    directives.Add(BrainDirective.SetAnimation("idle", restart: true));
                }
            }

            return directives;
        }

        public void OnEvent(PetEvent evt)
        {
            // React to mouse move events by updating the timestamp
            if (evt.Type == EventCatalog.PointerMove)
            {
                _lastMouseMoveTime = DateTime.Now;
            }
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }
}
// RFC-21 §4 的判据体（第四块：日志取证口）。
//
// 为什么单拆一份：`CapturingSink` 只服务一条判据（泵自退的取证），却要占 50 行 ——
// 而 `ThrottleChecks.Frames.cs` 正卡在 D-03c 的 400 行上限上（判据变长就要么砍注释要么拆文件，
// 这里选拆文件：读日志的口子和读帧循环的口子本来就不是一件事）。
using System;
using System.Collections.Generic;
using PetDock.Core.Compositor;

namespace PetDock.Hosting;

internal static partial class ThrottleChecks
{
    /// <summary>`throttle-pump-exited` 那行日志的取证口（走 KernelLog 的真出口，不伪造）。</summary>
    private sealed class CapturingSink : IKernelLogSink
    {
        private readonly List<string> _lines = new();

        public void Write(string message)
        {
            lock (_lines)
            {
                _lines.Add(message);
            }
        }

        public bool Contains(string fragment)
        {
            lock (_lines)
            {
                return _lines.Exists(line => line.Contains(fragment, StringComparison.Ordinal));
            }
        }

        /// <summary>从 `throttle-pump-exited reason=&lt;取值&gt; …` 这行里取出 reason 值。</summary>
        public string LastReason()
        {
            lock (_lines)
            {
                for (int i = _lines.Count - 1; i >= 0; i--)
                {
                    string line = _lines[i];
                    int at = line.IndexOf("throttle-pump-exited", StringComparison.Ordinal);
                    if (at < 0)
                    {
                        continue;
                    }

                    int start = line.IndexOf("reason=", at, StringComparison.Ordinal);
                    if (start < 0)
                    {
                        continue;
                    }

                    start += "reason=".Length;
                    int end = line.IndexOf(' ', start);
                    return end < 0 ? line.Substring(start) : line.Substring(start, end - start);
                }

                return string.Empty;
            }
        }
    }
}

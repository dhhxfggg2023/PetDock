// 宿主侧音效的判据（2026-10-29，P4 音频第一层）。命令形态 `--uitest`，判据行 `ui-audio-*`。
//
// 判的是「宿主这一侧的决定」：静音、缺文件、格式不支持、不阻塞，以及设置里的默认值。
// 真的出声没法在无人值守里判（那需要音频设备与耳朵），所以播放器是注入的桩件 ——
// 判据验的是「该不该播、播的是什么」，不是「扬声器响没响」。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class SoundChecks
{
    internal delegate void Report(string name, bool ok, string detail);

    public static void Run(Report check)
    {
        bool saved = SoundBoard.Enabled;
        string dir = Path.Combine(Path.GetTempPath(), "petdock-soundcheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string wav = WriteWav(dir, "tone.wav");
            string mp3 = Path.Combine(dir, "tone.mp3");
            File.WriteAllBytes(mp3, Array.Empty<byte>());

            var played = new List<string>();
            using (var board = new SoundBoard { PlayerForCheck = played.Add })
            {
                SoundBoard.Enabled = true;
                bool ok = board.Play(wav);
                check(
                    "ui-audio-play",
                    ok && board.Plays == 1 && board.LastPath == wav && played.Count == 1,
                    $"ok={ok} plays={board.Plays} played={played.Count} last={board.LastPath}");

                SoundBoard.Enabled = false;
                int before = board.Plays;
                bool muted = board.Play(wav);
                check(
                    "ui-audio-mute",
                    !muted && board.Plays == before && played.Count == 1,
                    $"played={muted} plays={board.Plays} (静音时一条都不该交给播放器)");
                SoundBoard.Enabled = true;

                bool missing = board.Play(Path.Combine(dir, "absent.wav"));
                check(
                    "ui-audio-missing",
                    !missing && board.LastFailure == "missing-file",
                    $"played={missing} failure={board.LastFailure}");

                bool wrong = board.Play(mp3);
                check(
                    "ui-audio-format",
                    !wrong && board.LastFailure == "unsupported-format",
                    $"played={wrong} failure={board.LastFailure} (只支持 wav，不假装播了)");

                board.Dispose();
                board.Dispose();
            }

            // 设置里的默认值：SoundEnabled 为 null（旧文件没这个字段）= 开。
            check(
                "ui-audio-default-on",
                UserSettings.Default.SoundEnabled is null && SettingsView.Default.SoundEnabled,
                $"settings={(UserSettings.Default.SoundEnabled?.ToString() ?? "(unset)")}"
                + $" view={SettingsView.Default.SoundEnabled}");
        }
        catch (Exception error)
        {
            check("ui-audio-crash", false, error.GetType().Name + ": " + error.Message);
        }
        finally
        {
            SoundBoard.Enabled = saved;
            TryDelete(dir);
        }
    }

    /// <summary>写一个最小可解析的 WAV（44 字节头 + 一个静音样本）。</summary>
    private static string WriteWav(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        const int rate = 8000;
        const int samples = 2;
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + (samples * 2));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(samples * 2);
        writer.Write(0);
        writer.Write(0);
        return path;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响判据结论。
        }
    }
}

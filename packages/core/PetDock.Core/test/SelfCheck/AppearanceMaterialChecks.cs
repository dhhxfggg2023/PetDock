// 材质判据（docs/18 §2.7，2026-10-28）：从 AppearanceChecks 拆出（D-03c 单文件 400 行）。
//
// 两条读数：
//   ① ui-material-tokens：三档材质的绘制 alpha 必须 255 → 224 → 178 严格递减，
//      名字往返稳定，认不出的名字一律回落到不透明；
//   ② ui-material-apply：真拿一个窗套材质，不抛，回报的 requested 与请求一致，
//      不透明档能回到 100% 不透明。
// 模糊在不在、好不好看，机器判不了（系统合成 + 壁纸），留给截图与人眼。
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using PetDock.Core.Ui.Design;

namespace PetDock.Hosting;

internal static partial class AppearanceChecks
{
    /// <summary>⑤：材质令牌与落地。</summary>
    private static void Material(Report check)
    {
        MaterialKind saved = Theme.Material;
        try
        {
            var alphas = new List<int>();
            var bad = new List<string>();
            foreach (MaterialKind kind in new[] { MaterialKind.Opaque, MaterialKind.Frosted, MaterialKind.Glass })
            {
                Theme.Material = kind;
                alphas.Add(Theme.Paint(Theme.Bg).A);
                string token = Theme.MaterialToken(kind);
                if (Theme.MaterialToken(Theme.ParseMaterial(token)) != token)
                {
                    bad.Add(token);
                }

                double opacity = MaterialHost.OpacityOf(kind);
                if (opacity <= 0.0 || opacity > 1.0)
                {
                    bad.Add($"opacity:{token}");
                }
            }

            check(
                "ui-material-tokens",
                alphas[0] == 255 && alphas[1] < 255 && alphas[2] < alphas[1]
                    && bad.Count == 0
                    && Theme.ParseMaterial("bogus") == MaterialKind.Opaque,
                $"alpha={string.Join("/", alphas)} bad={string.Join(",", bad)}");
        }
        finally
        {
            Theme.Material = saved;
        }

        // 落地：真拿一个窗去套材质，不抛；结果里的 requested 必须是请求的那一档。
        // 这里能证的只有「接口通了、没抛、不透明档能回得来」——模糊在不在是系统与壁纸的事。
        using var form = new Form { Text = "material-probe" };
        Theme.Apply(form);
        bool ok;
        string probe;
        try
        {
            Theme.Material = MaterialKind.Frosted;
            MaterialReport frosted = Theme.ApplyMaterial(form);
            Theme.Material = MaterialKind.Glass;
            MaterialReport glass = Theme.ApplyMaterial(form);
            Theme.Material = MaterialKind.Opaque;
            MaterialReport opaque = Theme.ApplyMaterial(form);
            ok = frosted.Requested == MaterialKind.Frosted
                && glass.Requested == MaterialKind.Glass
                && opaque.Applied == MaterialKind.Opaque
                && Math.Abs(form.Opacity - 1.0) < 0.001;
            probe = $"frosted=[{frosted.Describe()}] glass=[{glass.Describe()}] opaque=[{opaque.Describe()}]";
        }
        catch (Exception error)
        {
            ok = false;
            probe = $"{error.GetType().Name}: {error.Message}";
        }
        finally
        {
            Theme.Material = saved;
        }

        check("ui-material-apply", ok, probe);
    }
}
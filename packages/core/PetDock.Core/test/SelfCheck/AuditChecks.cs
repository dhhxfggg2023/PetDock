// 审计流水视图的判据（原规格 18 §2.8，2026-10-28）。
//
// 这一层机器能判的只有三件事：
//   ① ui-audit-view-rows：有记录时，行数与列数对得上（列 = 时间/插件/类别/详情）；
//   ② ui-audit-view-empty：没有记录时，底部说的是「还没有」而不是「读不到」——
//      这两句话在数据层是 Available=false 与 Rows.Count==0 的差别，界面不能把它俩说成一句；
//   ③ ui-audit-view-unavailable：读不到时，底部把原因原样带出来（不吞成空窗）。
// 「好不好看、读起来顺不顺」留给截图与人眼。
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using PetDock.Core.Ui;

namespace PetDock.Hosting;

internal static class AuditChecks
{
    public static void Run(AuditTrailChecks.Check check)
    {
        Rows(check);
        EmptyAndUnavailable(check);
    }

    private static void Rows(AuditTrailChecks.Check check)
    {
        string probe;
        bool ok;
        try
        {
            var rows = new List<AuditTrailRow>
            {
                new("2026-10-28 10:00:00", "petdock.example.demo", AuditTrail.KindGrant, "input.pointer", false),
                new("2026-10-28 10:00:01", "petdock.example.demo", AuditTrail.KindRevoke, "input.pointer", false),
            };
            using AuditWindow window = AuditWindow.Create(new AuditTrailRead(rows, 1, 0, true, "ok"));
            int columns = window.RowColumnCount;
            ok = window.RowCount == 2 && columns == 4;
            probe = $"rows={window.RowCount} columns={columns}";
        }
        catch (Exception error)
        {
            ok = false;
            probe = $"{error.GetType().Name}: {error.Message}";
        }

        check("ui-audit-view-rows", ok, probe);
    }

    private static void EmptyAndUnavailable(AuditTrailChecks.Check check)
    {
        string emptyText;
        string unavailableText;
        try
        {
            using AuditWindow empty = AuditWindow.Create(
                new AuditTrailRead(Array.Empty<AuditTrailRow>(), 0, 0, true, "none"));
            emptyText = empty.StatusText;

            using AuditWindow unavailable = AuditWindow.Create(
                new AuditTrailRead(Array.Empty<AuditTrailRow>(), 0, 0, false, "unreadable: denied"));
            unavailableText = unavailable.StatusText;
        }
        catch (Exception error)
        {
            string fail = $"{error.GetType().Name}: {error.Message}";
            check("ui-audit-view-empty", false, fail);
            check("ui-audit-view-unavailable", false, fail);
            return;
        }

        // 判据断的是「两个窗说的是两句不同的话」，不是断具体文案（文案会跟着语言变）。
        check(
            "ui-audit-view-empty",
            emptyText.Length > 0 && !string.Equals(emptyText, unavailableText, StringComparison.Ordinal),
            $"empty={Shorten(emptyText)}");
        check(
            "ui-audit-view-unavailable",
            unavailableText.Contains("denied", StringComparison.Ordinal),
            $"unavailable={Shorten(unavailableText)}");
    }

    private static string Shorten(string text) => text.Length <= 60 ? text : text.Substring(0, 60) + "…";
}
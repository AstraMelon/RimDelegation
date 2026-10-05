using System.Collections.Generic;
using RimDelegation;
using RadiusUI.Framework;
using UnityEngine;
using Verse;

namespace RimDelegationRadiusUI
{
    /// <summary>
    /// 委派报告（**历史详情**与**收工签核窗口**）的 Radius 画法 —— 只有这一份。
    ///
    /// RIM-9（2026-10-05，用户原话「完成后的UI（图二）需要写RadiusUI」）：
    ///   · 图二那个签核窗口原来整个是原版画法（原版窗口底 + `Widgets.DrawMenuSection` + 两颗原版按钮），
    ///     主控台早就半径化了，只剩它还是"两种质感拼在一个界面里"；
    ///   · 而它的正文与主控台右栏的**历史详情**共用 `DelegationReportUI`（原版画法）——
    ///     所以本轮把正文排版抽到这里（Radius 画法），两处都调它：
    ///     历史详情走 <see cref="DelegationConsoleSkin.RecordMain" /> → 本类，
    ///     `Dialog_DelegationReport` 也走本类。**排版只有一份**，不会再分叉。
    ///   · 原版画法的那份（`DelegationReportUI.Draw`）**保留**：主控台皮肤不可用时签核窗口落回它，
    ///     这是"界面绝不空窗"的最后一道。
    ///
    /// 数据来源只有 `DelegationReportData`（`DelegationRecord.ToReportData()` /
    /// `DelegationReportData.Build(...)`），本类**不碰任何逻辑**。
    /// </summary>
    internal static class ReportSkin
    {
        /// <summary>
        /// 画一份报告（标题 → 概要 → 可滚动的事件明细）。<paramref name="scroll" /> 由调用方持有
        /// （历史详情与签核窗口各一份滚动位置）。
        /// </summary>
        public static void Draw(Rect r, DelegationReportData data, ref Vector2 scroll)
        {
            if (data == null)
            {
                return;
            }
            Rect inner = r.ContractedBy(10f);
            float w = Mathf.Max(80f, inner.width - 16f);
            float lhTitle = RadiusFont.LineHAt(RadiusFont.Scale.Title, true);
            float lhBody = RadiusFont.LineHAt(RadiusFont.Scale.Body, false);
            float lhMeta = RadiusFont.LineHAt(RadiusFont.Scale.Meta, false);

            string summary = data.summary ?? "";
            float summaryH = Mathf.Max(lhBody, RadiusFont.HeightAt(summary, w, RadiusFont.Scale.Body, false));
            List<string> lines = data.eventLines;
            int n = lines?.Count ?? 0;
            float linesH = 0f;
            for (int i = 0; i < n; i++)
            {
                linesH += Mathf.Max(lhMeta, RadiusFont.HeightAt(lines[i], w, RadiusFont.Scale.Meta, false));
            }
            float contentH = lhTitle + 6f + summaryH + 10f + lhMeta + linesH + 20f;

            Rect view = new Rect(0f, 0f, w, Mathf.Max(contentH, inner.height));
            int depth = FlatScroll.Depth;
            bool opened = false;
            try
            {
                FlatScroll.Begin(inner, ref scroll, view);
                opened = true;
                float y = 0f;
                RadiusFont.LabelAt(new Rect(0f, y, w, lhTitle), data.title ?? "委派报告",
                    RadiusFont.Scale.Title, data.aborted ? Palette.Warn : Palette.Flat.Ink,
                    TextAnchor.MiddleLeft, true, false);
                y += lhTitle + 6f;
                RadiusFont.LabelAt(new Rect(0f, y, w, summaryH), summary,
                    RadiusFont.Scale.Body, Palette.Flat.InkMid, TextAnchor.UpperLeft, false, true);
                y += summaryH + 10f;
                RadiusFont.LabelAt(new Rect(0f, y, w, lhMeta),
                    n > 0
                        ? string.Format("RimDelegationReportEvents".Translate(), n)
                        : "RimDelegationReportEventsNone".Translate(),
                    RadiusFont.Scale.Meta, Palette.Flat.InkMid, TextAnchor.MiddleLeft, false, false);
                y += lhMeta;
                if (n == 0)
                {
                    RadiusFont.LabelAt(new Rect(0f, y, w, lhMeta), "RimDelegationReportNoEvents".Translate(),
                        RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.MiddleLeft, false, false);
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        float lh = Mathf.Max(lhMeta, RadiusFont.HeightAt(lines[i], w, RadiusFont.Scale.Meta, false));
                        RadiusFont.LabelAt(new Rect(0f, y, w, lh), lines[i],
                            RadiusFont.Scale.Meta, Palette.Flat.InkLow, TextAnchor.UpperLeft, false, true);
                        y += lh;
                    }
                }
            }
            finally
            {
                if (opened)
                {
                    FlatScroll.EndOrUnwind(depth);
                }
            }
        }

        /// <summary>
        /// 签核窗口的底栏（RIM-9）：右端一颗 `确认`；返回 true = 这一帧被点了。
        ///
        /// 用 `SkinButtons` 的同款画法（`CardChrome.Rounded` + `RadiusFont.Label` + `ButtonInvisible`），
        /// 但**不用红色** —— 确认只是"我知道了"，破坏性的红色留给"中止委派"。
        ///
        /// ⚠️ 关窗动作**不在这里做**：`Dialog_DelegationReport` 的 `Close()` 会触发 `PostClose()`
        /// （报告排队器靠它弹下一条），由窗口自己调才不会被皮肤绕过去。
        /// </summary>
        public static bool DrawConfirmBar(Rect r, string label)
        {
            const float BtnW = 116f;
            const float BtnH = 30f;
            Rect btn = new Rect(r.xMax - BtnW, r.y + (r.height - BtnH) * 0.5f, BtnW, BtnH);
            bool hover = Mouse.IsOver(btn);
            CardChrome.Rounded(btn, hover ? RadiusTheme.Accent : Palette.Surface2, 8f);
            if (hover)
            {
                CardChrome.Hover(btn, 8f);
            }
            RadiusFont.Label(btn, label, GameFont.Small, false, Palette.Ink, TextAnchor.MiddleCenter, false);
            return Widgets.ButtonInvisible(btn, true);
        }
    }
}

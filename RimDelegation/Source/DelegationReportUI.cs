using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 委派报告块的**唯一画法**（S15 第二期）：
    /// 结束时的签核窗口（<see cref="Dialog_DelegationReport" />）与主控台右栏的
    /// **历史详情**共用它 —— 两处各画一份，迟早出现"窗口里有、历史里没有"。
    ///
    /// 调用方负责按钮与窗口壳；这里只管"标题 + 概要 + 可滚动的事件明细"。
    /// </summary>
    public static class DelegationReportUI
    {
        /// <summary>画一整块报告；<paramref name="scroll" /> 由调用方持有（每个视图各一份滚动位置）。</summary>
        public static void Draw(Rect inRect, DelegationReportData data, ref Vector2 scroll)
        {
            if (data == null)
            {
                return;
            }
            float y = inRect.y;
            Color old = GUI.color;

            // ---- 标题（收工 = 偏绿、中断 = 偏红）
            Text.Font = GameFont.Medium;
            GUI.color = data.aborted ? new Color(1f, 0.62f, 0.56f) : new Color(0.78f, 1f, 0.78f);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 34f), data.title ?? "委派报告");
            GUI.color = old;
            Text.Font = GameFont.Small;
            y += 38f;

            // ---- 概要（结束理由 / 时长 / 产出）
            string summary = data.summary ?? "";
            float summaryH = Mathf.Min(150f, Text.CalcHeight(summary, inRect.width));
            Widgets.Label(new Rect(inRect.x, y, inRect.width, summaryH), summary);
            y += summaryH + 8f;

            // ---- 事件明细（可滚动）
            Rect listRect = new Rect(inRect.x, y, inRect.width, Mathf.Max(56f, inRect.yMax - y));
            Widgets.DrawMenuSection(listRect);
            if (data.eventLines.NullOrEmpty())
            {
                GUI.color = new Color(1f, 1f, 1f, 0.6f);
                Widgets.Label(listRect.ContractedBy(8f), "期间没有发生随机事件。");
                GUI.color = old;
                return;
            }

            Rect inner = listRect.ContractedBy(6f);
            float lh = Text.LineHeight;
            Rect view = new Rect(0f, 0f, inner.width - 20f, Mathf.Max(inner.height, data.eventLines.Count * lh + 4f));
            Widgets.BeginScrollView(inner, ref scroll, view);
            float ly = 0f;
            for (int i = 0; i < data.eventLines.Count; i++)
            {
                Widgets.Label(new Rect(0f, ly, view.width, lh), data.eventLines[i]);
                ly += lh;
            }
            Widgets.EndScrollView();
        }
    }
}

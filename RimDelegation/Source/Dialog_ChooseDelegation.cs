using System;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 委派对话框（S18 起是**薄壳**）。
    ///
    /// 它曾经是"下单"的唯一界面（727 行）；S18 把状态抽进 <see cref="DelegationDraft" />、
    /// 画法抽进 <see cref="DelegationDraftUI" />，于是这里只剩下三件事：
    ///   ① 一个 640×780 的**模态**窗口（`forcePause` + `absorbInputAroundWindow`）；
    ///   ② 顶上一行标题（def @ 地点 + 地点信息卡）；
    ///   ③ 底下三颗出口按钮（确认 / 延后决定 / 取消）。
    ///
    /// 为什么还留着它：Mod 设置里的「使用旧版委派对话框」是**逃生门**
    /// （见 <see cref="RimDelegationSettings.legacyDelegationDialog" /> 与
    /// <see cref="DelegationDraft.BeginOrDialog" />）—— 万一主控台的「待下达」草稿出了怪事，
    /// 玩家勾一下就能回到这条走了一路的老流程，不必等我们发新 dll。
    ///
    /// 三个出口的语义一字未改：
    ///   确认委派 → 立刻带着选择出发，抵达即开工
    ///   延后决定 → 先让车队过去（不携带选择），抵达后再决定
    ///   取消     → 什么都不做
    /// </summary>
    public class Dialog_ChooseDelegation : Window
    {
        private readonly DelegationDraft draft;

        public override Vector2 InitialSize => new Vector2(640f, 780f);

        public Dialog_ChooseDelegation(Caravan caravan, Site site, DelegationDef def,
            Action<DelegationRequest> onConfirm, DelegationRequest preRequest = null, Action onDefer = null)
        {
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            onlyOneOfTypeAllowed = true;

            draft = new DelegationDraft(caravan, site, def, preRequest)
            {
                onConfirm = onConfirm,
                onDefer = onDefer
            };
        }

        public override void DoWindowContents(Rect inRect)
        {
            DelegationDef def = draft.def;
            Site site = draft.site;
            if (def == null || site == null || site.Destroyed)
            {
                // 打开期间地点被销毁（例如别的入口把它采空了）—— 自己关掉，别画一屏空
                Close(true);
                return;
            }

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            // ---- 标题行（右侧是"地点本体"的信息卡按钮）
            Rect titleRect = new Rect(inRect.x, inRect.y, inRect.width, 26f);
            Widgets.Label(titleRect, (def.commandLabel ?? def.label).Formatted(site.Label).CapitalizeFirst());
            float titleInfoSize = Widgets.InfoCardButtonSize;
            Widgets.InfoCardButton(titleRect.xMax - titleInfoSize, titleRect.y + 1f, site);

            Rect content = new Rect(inRect.x, inRect.y + 28f, inRect.width, inRect.height - 28f);
            DelegationDraftUI.Draw(draft, content);
            DrawExitButtons(content);
        }

        /// <summary>三个出口。位置与画法与 S5 起的一模一样（右下角三颗）。</summary>
        private void DrawExitButtons(Rect content)
        {
            const float ConfirmWidth = 150f;
            const float DeferWidth = 140f;
            const float CancelWidth = 100f;
            const float Gap = 8f;
            float buttonY = DelegationDraftUI.ButtonY(content);
            Rect cancelRect = new Rect(content.xMax - CancelWidth, buttonY, CancelWidth, 30f);
            Rect deferRect = new Rect(cancelRect.x - Gap - DeferWidth, buttonY, DeferWidth, 30f);
            Rect confirmRect = new Rect(deferRect.x - Gap - ConfirmWidth, buttonY, ConfirmWidth, 30f);

            if (Widgets.ButtonText(confirmRect, "确认委派"))
            {
                // 先自己校验一次：原版的顺序是"先关窗、再回调"，所以不能把校验留给
                // draft.Confirm()（那时窗已经关了，报错消息会显得莫名其妙）。
                if (!draft.CanConfirm(out string reason))
                {
                    Messages.Message(reason, MessageTypeDefOf.RejectInput, false);
                }
                else
                {
                    Close();
                    draft.Confirm();
                }
            }

            // 已经站在目标格上时，"延后"没有意义（等于取消），所以不显示
            if (draft.onDefer != null && !draft.onTile)
            {
                if (Widgets.ButtonText(deferRect, "延后决定"))
                {
                    Close();
                    draft.Defer();
                }
            }

            if (Widgets.ButtonText(cancelRect, "取消"))
            {
                Close();
            }
        }
    }
}

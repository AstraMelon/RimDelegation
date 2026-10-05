using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimDelegation
{
    /// <summary>把「锁定」按钮挂到 Caravan 这个 WorldObjectDef 上（见 Patches/RimDelegation_CaravanComps.xml）。</summary>
    public class WorldObjectCompProperties_CaravanLock : WorldObjectCompProperties
    {
        public WorldObjectCompProperties_CaravanLock()
        {
            compClass = typeof(WorldObjectComp_CaravanLock);
        }
    }

    /// <summary>
    /// 车队 gizmo 栏里的「锁定 / 已锁定」按钮（S16 用户要求：和"定居 / 拆分"这些按钮排在一起）。
    ///
    /// 为什么挂在 **Caravan 自己的 WorldObjectDef** 上，而不是 Site 的 comp 上：
    /// `WorldObjectComp.GetGizmos()` 给的是"这个 WorldObject 自己的 gizmo"，
    /// 挂在 Caravan def 上才会出现在**每一支**车队上（不管它停在哪、有没有委派）。
    ///
    /// 图标用原版 `TexCommand.ForbidOn / ForbidOff`：语义正好（禁止 / 解除），而且**必须给图标** ——
    /// `Verse.Command.DrawIcon` 在 `icon == null` 时会画 `BaseContent.BadTex`（洋红叉，见 S16 截图）。
    /// </summary>
    public class WorldObjectComp_CaravanLock : WorldObjectComp
    {
        private Caravan Caravan => parent as Caravan;

        public override IEnumerable<Gizmo> GetGizmos()
        {
            Caravan caravan = Caravan;
            if (caravan == null || caravan.Destroyed)
            {
                yield break;
            }
            bool locked = RimDelegationCaravanState.IsLocked(caravan);
            string activity = DelegationRegistry.For(caravan)?.active?.Worker?.ActivityName;

            Command_Action cmd = new Command_Action();
            cmd.defaultLabel = locked ? "已锁定" : "解锁";
            cmd.icon = locked ? TexCommand.ForbidOn : TexCommand.ForbidOff;
            cmd.defaultIconColor = locked ? new Color(1f, 0.72f, 0.35f) : Color.white;
            cmd.defaultDesc = locked
                ? "该远行队已**锁定**：右击其他地图块不会让它移动（防止误操作把正在委派的队伍带走）。\n\n"
                  + "只对「有在途委派」的车队生效 —— 闲着或只在路上的车队照常能走。\n点击解除锁定。"
                : "该远行队已解锁：右击地图块可以正常移动。\n\n"
                  + (activity.NullOrEmpty()
                      ? "（当前没有在途委派，锁不锁都不影响移动。）"
                      : "⚠ 它正在委派（" + activity + "）：一走开就会中断委派。");
            cmd.action = () => RimDelegationCaravanState.Toggle(caravan);
            yield return cmd;
        }
    }
}

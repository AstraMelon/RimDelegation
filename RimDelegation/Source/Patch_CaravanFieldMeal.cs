using HarmonyLib;
using RimWorld;
using Verse;

namespace RimDelegation
{
    /// <summary>
    /// 「野外伙食」的挂载点（§19.25）。
    ///
    /// 为什么打在这里而不是 `Caravan_NeedsTracker.TrySatisfyFoodNeed`：
    /// 那个方法里吃的东西是**局部变量**（`CaravanInventoryUtility.TryGetBestFood(caravan, pawn, out var food2, out _)`），
    /// 后缀拿不到。而它一定会调 `food2.Ingested(pawn, food.NutritionWanted)`，
    /// 所以 `Thing.Ingested` 是唯一能同时拿到"谁吃的 + 吃了什么"的公共入口。
    ///
    /// 这条补丁在**全地图吃饭**时也会被调用，所以第一件事就是挡住非委派的人
    /// （`DelegationRegistry.AnyActiveFor` 查不到就立刻返回）。
    /// 原版自己也是在这个方法里挂 `tasteThought`（奢华 +12 / 精致 +5 / 生食 -7），
    /// 我们只是**再加一层**委派专属的余味，不替代它。
    /// </summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.Ingested))]
    public static class Patch_Thing_Ingested_FieldMeal
    {
        public static void Postfix(Thing __instance, Pawn ingester)
        {
            DelegationUtility.GrantFieldMeal(__instance, ingester);
        }
    }
}

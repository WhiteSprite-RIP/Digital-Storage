using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 防御 patch（原版 null 缺陷）：<c>PawnCanAutomaticallyHaul</c> 对未 Spawned 物品
    /// （<c>t.Map == null</c>）会在 <c>t.Position.Fogged(t.Map)</c> 处直接 NRE——
    /// GridsUtility.Fogged(IntVec3, Map) 无 null 检查（HaulAIUtility.cs:48）。
    /// 触发者：本 mod 的 GhostThing 不 Spawn 却挂进 listerThings.listsByDef
    /// （供其他 mod 扫描核心库存），原版 WorkGiver_CookFillHopper.HopperFillFoodJob
    /// 走 ThingsOfDef 遍历时拿到 ghost → PawnCanAutomaticallyHaul(ghost) → NRE。
    /// 语义：不在地图上的物品本来就不能 haul，提前返回 false 与原版意图一致，
    /// 顺带保护所有会把 despawned thing 放进索引的 mod。
    ///
    /// 守卫写成 t.Map == null（而不是 !t.Spawned）：两者等价（Thing.Map 仅在
    /// Spawned 时非 null，见 Thing.cs:233），但 t.Map == null 正是原版那行 NRE 的
    /// 充要条件，语义更准。**不要**改成 t.MapHeld == null —— 非 Fast 版读的是裸
    /// t.Map，容器内容物（未 Spawn 但 MapHeld 非 null）在这里本就走不通。
    /// </summary>
    [HarmonyPatch(typeof(HaulAIUtility), "PawnCanAutomaticallyHaul")]
    static class Patch_HaulAIUtility_PawnCanAutomaticallyHaul
    {
        static bool Prefix(Pawn p, Thing t, bool forced, ref bool __result)
        {
            if (p == null || t == null || t.Map == null)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 同款防御：<c>PawnCanAutomaticallyHaulFast_NewTemp</c> 行 82 的 <c>t.Fogged()</c> 展开即
    /// <c>t.MapHeld.fogGrid.IsFogged(t.PositionHeld)</c>（GridsUtility.cs:86-89，无 null 检查），
    /// MapHeld 为 null 时 NRE。
    ///
    /// ⚠️ 守卫必须是 <b>t.MapHeld == null</b>，<b>不能</b>是 !t.Spawned。
    /// 容器内容物未 Spawn，但 `ParentHolder =&gt; Map` 数据修复让 MapHeld 非 null，
    /// 原版这条路径对它们完全安全 —— 而搬出路径正是走这里：
    ///   ListerHaulables.HaulSourcesCheckTick → Check(内容物) → ShouldBeHaulable 通过
    ///   → WorkGiver_Haul.JobOnThing:26 → PawnCanAutomaticallyHaulFast
    /// 用 !t.Spawned 会把内容物一律挡掉：容器里被过滤器排除的物品永远搬不出来
    /// （甲-1 静默失效，且只在主仓补丁与 4.0 数据层同时加载时才暴露）。
    ///
    /// <para><b>⚠️ 2026-10-06 修正挂点：必须挂 4 参的 <c>_NewTemp</c>，不能挂 3 参壳。</b>
    /// 原版 1.6.4871 的实际形状（反编译 Assembly-CSharp.dll 核实）：
    /// <code>
    /// public static bool PawnCanAutomaticallyHaulFast(Pawn p, Thing t, bool forced)
    ///     =&gt; PawnCanAutomaticallyHaulFast_NewTemp(p, t, forced);            // 3 参转发壳
    /// public static bool PawnCanAutomaticallyHaulFast_NewTemp(Pawn p, Thing t, bool forced,
    ///                                                         bool checkReachability = true)
    /// {
    ///     if (t.Fogged()) return false;                                      // ← 崩溃点（行 82）
    ///     ...
    /// }
    /// </code>
    /// 全程序集的调用点分布（IL 扫描，共 6 处）：
    /// <list type="bullet">
    /// <item><c>HaulAIUtility.PawnCanAutomaticallyHaul</c> → <c>_NewTemp</c>（绕过）</item>
    /// <item><c>HaulAIUtility.PawnCanAutomaticallyHaulFast</c> → <c>_NewTemp</c>（转发壳本体）</item>
    /// <item><c>Pawn_JobTracker.TryOpportunisticJob:700</c> → <c>_NewTemp</c>（<b>顺路搬运，实际崩的就是这条</b>）</item>
    /// <item><c>JobGiver_Haul.TryGiveJob</c> 内联校验 → <c>_NewTemp</c>（绕过）</item>
    /// <item><c>WorkGiver_ConstructDeliverResources.ResourceValidator_NewTemp</c> → <c>_NewTemp</c>（绕过）</item>
    /// <item><c>WorkGiver_Haul.JobOnThing:26</c> → 3 参壳（**唯一**走壳的调用点）</item>
    /// </list>
    /// 也就是说：挂 3 参壳时，这个守卫对**六分之五**的调用点完全无效，其中就包括
    /// <c>TryOpportunisticJob</c>——它遍历 <c>listerHaulables.ThingsPotentiallyNeedingHauling()</c>，
    /// 而本 mod 的 <c>Building_StorageCore</c> 是 <c>IHaulSource</c>，核心内容物会被原版
    /// 登记进那张表；一旦其中有 <c>MapHeld == null</c> 的坏件，<b>每次给小人派任何新任务的瞬间</b>
    /// （<c>StartJob</c> 早期就调 <c>TryOpportunisticJob</c>）都会 NRE ⇒ 派活中断 ⇒ 小人原地反复
    /// 在「站立中 ↔ 取食/睡觉/清洁」之间跳、最终饿死。<b>用户实测日志</b>：
    /// <c>at Verse.GridsUtility.Fogged</c> + <c>at HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp [0x00000]</c>
    /// + <c>at Pawn_JobTracker.TryOpportunisticJob</c>，栈上**没有**本 mod 的 PREFIX
    /// （同栈 Nanosuit 的 PREFIX 正常打印）⇒ 守卫确实没拦到。
    ///
    /// <para><b>为什么这不是"文件漏编"</b>：3 参 <c>PawnCanAutomaticallyHaulFast</c> 在 1.6.4871 里
    /// <b>确实存在</b>，所以 Harmony 挂载**成功、零报错**，自检行照常显示满员 —— 属于最难发现的一类
    /// 静默失效。改挂 <c>_NewTemp</c> 后 6 个调用点全部覆盖（转发壳内部也调 <c>_NewTemp</c>）。</para>
    /// </summary>
    [HarmonyPatch(typeof(HaulAIUtility), "PawnCanAutomaticallyHaulFast_NewTemp")]
    static class Patch_HaulAIUtility_PawnCanAutomaticallyHaulFast
    {
        static bool Prefix(Pawn p, Thing t, bool forced, bool checkReachability, ref bool __result)
        {
            if (p == null || t == null || t.MapHeld == null)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
    // 作业排队后可能断电, 实际取出前仍需检查
    [HarmonyPatch(typeof(Toils_Haul), nameof(Toils_Haul.StartCarryThing))]
    internal static class Patch_StartCarryThing_CorePower
    {
        private static void Postfix(TargetIndex haulableInd, Toil __result)
        {
            __result.AddFailCondition(() =>
            {
                Thing target = __result.actor.CurJob.GetTarget(haulableInd).Thing;
                return target?.ParentHolder is Building_StorageCore core && !core.HaulSourceEnabled;
            });
        }
    }

    [HarmonyPatch(typeof(JobDriver_Wear), "MakeNewToils")]
    internal static class Patch_JobDriver_Wear_CorePower
    {
        private static IEnumerable<Toil> Postfix(IEnumerable<Toil> __result, JobDriver_Wear __instance)
        {
            foreach (Toil toil in __result)
            {
                toil.AddFailCondition(() =>
                {
                    Thing apparel = __instance.job.GetTarget(TargetIndex.A).Thing;
                    return apparel?.ParentHolder is Building_StorageCore core && !core.ApparelSourceEnabled;
                });
                yield return toil;
            }
        }
    }
}

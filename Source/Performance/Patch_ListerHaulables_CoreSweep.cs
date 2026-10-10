using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Performance
{
    /// <summary>
    /// <b>核心内容物不再被原版搬运列表每 tick 全量重算。</b>
    ///
    /// <para><b>链条</b>：<c>ListerHaulables.HaulSourcesCheckTick</c>（每 tick，轮转窗口里最多 4 个源）
    /// → 源只要"有内容"就调 <c>RecalculateAllInHaulSource</c> → 对**每一件**直接内容物调
    /// <c>Check</c> → <c>ShouldBeHaulable</c> 里那句 <c>t.IsInValidBestStorage()</c>
    /// → <c>StoreUtility.TryFindBestBetterStorageFor</c>：一次**完整储存搜索**，两条腿
    /// （<c>TryFindBestBetterStoreCellFor</c> 遍历全图 SlotGroup 的格子、
    /// <c>TryFindBestBetterNonSlotGroupStorageFor</c> 遍历全图 <see cref="IHaulDestination"/>）。</para>
    ///
    /// <para>4.0 的核心是**物理容器**且实现 <see cref="IHaulSource"/> —— 这个接口不能去掉：
    /// <c>WorkGiver_DoBill.cs:481</c> 正是遍历 <c>AllHaulSourcesListForReading</c> 从容器里找原料，
    /// 交易/轨道信标/第三方扫描器也都认它。于是装着几百堆货的核心，每 tick 要跑几百次完整储存搜索。
    /// 源的个数 ≤ 4 时窗口覆盖全部源（<c>num = ceil(count/4)</c>），核心必然每 tick 中招。</para>
    ///
    /// <para><b>关键观察：这些搜索对核心而言答案是个常量。</b>
    /// 只要地图上**没有任何优先级严格高于核心**的储存目的地，
    /// <c>TryFindBestBetterStorageFor</c> 两条腿都会在第一项就 <c>break</c>
    /// （<c>StoreUtility.cs:189</c> / <c>:257</c>，两个列表都按优先级降序维护），返回 false
    /// ⇒ <c>IsInValidBestStorage</c> 恒真 ⇒ <c>ShouldBeHaulable</c> 恒假
    /// ⇒ 每件物品的结果就是 <c>haulables.Remove(t)</c>。</para>
    ///
    /// <para><b>做法：把常量算一次，而不是算 N 次。</b>
    /// <list type="number">
    /// <item>读优先级降序列表的**首项**判断"有没有更高的目的地"（有序 ⇒ 摊还 O(1)，
    ///   与 <c>StoreUtility</c> 自己那两处 <c>break</c> 用的是同一个假设）；</item>
    /// <item>没有 ⇒ 逐件走**廉价等价路径**：<c>Accepts(t)</c> 为真就 <c>Remove</c>
    ///   （与原版 <c>Check</c> 的 else 分支同结果，幂等）；为假则 <c>return true</c>
    ///   **退回原版全量** —— 那正是「过滤器不再收它 ⇒ 原版该把它搬出去」那条语义，
    ///   不能自己替原版回答；</item>
    /// <item>有 ⇒ 原版这条链是**有意义的**（真会把货搬去更高级储存区），语义上必须算，
    ///   但按 <see cref="FallbackInterval"/> 采一次：答案在"搬走之前"不会变，
    ///   玩家能看到的差别最多是"几秒后才开始往外搬"。</item>
    /// </list></para>
    ///
    /// <para><b>与原版逐字等价的三条前提</b>（都作为守卫写在 <see cref="Prefix"/> 里，
    /// 任一不成立就 <c>return true</c> 走原版）：① 源确实是本 mod 的核心；
    /// ② 核心属于玩家阵营（否则 <c>IsInValidBestStorage</c> 的阵营闸门会让**所有**内容物变成"可搬运"）；
    /// ③ 没有更高优先级的目的地。<b>本补丁只做减法，不做加法。</b></para>
    ///
    /// <para><b>第四种情形（2026-10-10 加）</b>：源被关掉或断电时 <c>HaulSourceEnabled == false</c>
    /// （<c>Building_StorageCore.HaulSourceEnabled = 开关 &amp;&amp; IsUsableNow</c>，而后者含
    /// <c>Powered</c>），原版 <c>ShouldBeHaulable</c> 的最后一道门
    /// —— <c>t.ParentHolder is IHaulSource { HaulSourceEnabled: false }</c>（<c>ListerHaulables.cs:215</c>）
    /// —— 对**每一件**内容物都返回 false，与目的地、与过滤器都无关 ⇒ 正确答案是"全部 Remove"。
    /// 不单独处理它的话，断电的核心会落进下面 <c>!core.Accepts(t)</c> 那条"退回原版"分支
    /// （断电后 <c>Accepts</c> 恒假），于是每 tick 对满核心逐件跑一次完整储存搜索
    /// —— 恰好是这个补丁要消掉的那笔开销。</para>
    ///
    /// <para>挂点选在 <c>RecalculateAllInHaulSource</c>（而不是 <c>HaulSourcesCheckTick</c>）：
    /// 后者是本轮轮转的私有方法，前者是"重算一个源的**全部**内容物"的唯一入口，语义更清楚，
    /// 而且事件路径（<c>StorageSettings.Priority</c> setter）与轮转路径共用它 —— 两条都覆盖到了。
    /// 过滤器变更走的是 <c>Notify_HaulSourceChanged</c>（另一个方法），**不受本补丁影响**。</para>
    ///
    /// <para>开关：并入「性能优化」总开关（<see cref="DigitalStorageSettings.perfOptimizationsEnabled"/>）。</para>
    /// </summary>
    [HarmonyPatch(typeof(ListerHaulables), nameof(ListerHaulables.RecalculateAllInHaulSource),
        new[] { typeof(IHaulSource) })]
    internal static class Patch_ListerHaulables_CoreSweep
    {
        /// <summary>
        /// 「地图上真有更高优先级目的地」那条路径的采样周期（tick）。
        /// 那条路径每件内容物约 1.8µs（一次完整储存搜索），几百堆就是 1~2ms/tick；
        /// 250 tick ≈ 4 秒采一次足够 —— 搬运动作本身也要走好几秒。
        /// </summary>
        private const int FallbackInterval = 250;

        private static long t0;

        private static bool Prefix(IHaulSource source, ListerHaulables __instance)
        {
            t0 = DevDrawProfiler.Stamp(); // 探针关闭时返回 0，整段零开销

            Building_StorageCore core = source as Building_StorageCore;
            if (core == null) return true;
            if (!DigitalStorageSettings.perfOptimizationsEnabled) return true;

            try
            {
                if (__instance == null) return true;
                if (!core.Spawned || core.Map == null) return true;
                if (core.Faction != Faction.OfPlayer) return true;

                // 源被关掉 / 断电：`HaulSourceEnabled = 开关 && IsUsableNow`（含 Powered），
                // 而 `ShouldBeHaulable` 的最后一道门是
                // `t.ParentHolder is IHaulSource { HaulSourceEnabled: false }`（ListerHaulables.cs:215）
                // —— 它对**每一件**内容物都返回 false，与目的地、与过滤器都无关
                // ⇒ 正确答案就是"全部 Remove"，不需要问 HasStrictlyBetterDestination，
                // 也不能走下面那条 `!core.Accepts(t)` 的退回原版分支（断电后 Accepts 恒假，
                // 那样每 tick 会对满核心逐件跑一次完整储存搜索）。
                bool sourceOff = !core.HaulSourceEnabled;

                if (!sourceOff && HasStrictlyBetterDestination(core))
                {
                    int now = GenTicks.TicksGame;
                    if (now - core.lastHaulSweepTick < FallbackInterval) return false;
                    core.lastHaulSweepTick = now;
                    return true;
                }

                ThingOwner held = source.GetDirectlyHeldThings();
                if (held == null || held.Count == 0) return false;

                ICollection<Thing> haulables = __instance.ThingsPotentiallyNeedingHauling();
                int saved = 0;
                for (int i = 0; i < held.Count; i++)
                {
                    Thing t = held[i];
                    if (t == null) continue;
                    // 过滤器不再收它 ⇒ 原版会把它标成"该搬出去"（这正是容器能靠原版搬运工取空的那条路），
                    // 只有原版能回答，交给它。（源已关掉时这条路本来就被原版那道门堵死了，见上。）
                    if (!sourceOff && !core.Accepts(t)) return true;
                    haulables.Remove(t);
                    saved++;
                }

                DevDrawProfiler.Bump("核心免算件", saved);
                return false;
            }
            catch (Exception ex)
            {
                // 这是每 tick 都会走的路：绝不能让我们的异常改变原版行为 —— 本次退回原版即可。
                Log.ErrorOnce("[DigitalStorage] 核心搬运源重算跳过失败，本次退回原版：" + ex, 0x44534255);
                return true;
            }
        }

        private static void Postfix()
        {
            DevDrawProfiler.Mark("HaulSweep", t0);
            t0 = 0L;
        }

        /// <summary>
        /// 地图上有没有"优先级**严格高于**核心、且启用中"的储存目的地。
        ///
        /// <para>两个列表都按优先级降序维护（<c>HaulDestinationManager.cs:65</c> / <c>:124</c> 的
        /// <c>InsertionSort</c>，改优先级时 <c>Notify_HaulDestinationChangedPriority</c> 重排），
        /// 所以读到 ≤ 核心优先级就可以停。常见情形（核心「偏好」、储存区「普通」）这里只跑 1~2 次比较。</para>
        ///
        /// <para><b>宁可多报，不可漏报</b>：返回 true 只是"走原版、慢一点"，返回 false 是
        /// "我们替原版回答了一个语义问题"。所以这里只判存在性，**不**查 <c>Accepts</c>、**不**查空格子
        /// —— 那些恰恰是原版昂贵的那部分，交给它自己算。</para>
        /// </summary>
        private static bool HasStrictlyBetterDestination(Building_StorageCore core)
        {
            Map map = core.Map;
            HaulDestinationManager mgr = (map == null) ? null : map.haulDestinationManager;
            if (mgr == null) return true;

            StoragePriority mine = StoragePriority.Unstored;
            StorageSettings mineSettings = core.GetStoreSettings();
            if (mineSettings != null) mine = mineSettings.Priority;

            // ① 格子型储存（储存区 / 货架）
            List<SlotGroup> groups = mgr.AllGroupsListInPriorityOrder;
            if (groups != null)
            {
                for (int i = 0; i < groups.Count; i++)
                {
                    SlotGroup g = groups[i];
                    if (g == null || g.parent == null) continue;
                    StorageSettings gs = g.Settings;
                    StoragePriority p = (gs == null) ? StoragePriority.Unstored : gs.Priority;
                    if (p <= mine) break;
                    if (g.parent.HaulDestinationEnabled) return true;
                }
            }

            // ② 容器型目的地（含别的核心）
            List<IHaulDestination> dests = mgr.AllHaulDestinationsListInPriorityOrder;
            if (dests != null)
            {
                for (int i = 0; i < dests.Count; i++)
                {
                    IHaulDestination d = dests[i];
                    if (d == null || ReferenceEquals(d, core)) continue;
                    StorageSettings ds = d.GetStoreSettings();
                    StoragePriority p = (ds == null) ? StoragePriority.Unstored : ds.Priority;
                    if (p <= mine) break;
                    if (d.HaulDestinationEnabled) return true;
                }
            }

            return false;
        }
    }
}

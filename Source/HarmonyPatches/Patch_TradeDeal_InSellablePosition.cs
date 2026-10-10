using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 让交易界面真正**收下**存储核心里的内容物（含白银）。
    ///
    /// <para><b>这是交易链路上最后一道、也是最隐蔽的一道闸门。</b>
    /// <c>TradeDeal.AddAllTradeables:46</c> 逐个消费 <c>ITrader.ColonyThingsWillingToBuy</c>，
    /// 然后对每一件调用**私有方法** <c>TradeDeal.InSellablePosition(t, out reason)</c>；
    /// 该方法第 85 行对"不在场上的东西"有一张**硬编码类型白名单**：</para>
    ///
    /// <code>
    /// if (!t.Spawned
    ///     &amp;&amp; (!ModsConfig.BiotechActive || t.def != ThingDefOf.Genepack || !(t.ParentHolder is CompGenepackContainer))
    ///     &amp;&amp; (!(t is Book) || !(t.ParentHolder is Building_Bookcase))
    ///     &amp;&amp; !(t.ParentHolder is Building_OutfitStand))
    /// { reason = null; return false; }        // ← 我们的内容物全部死在这里
    /// </code>
    ///
    /// <para><b>症状因此极具误导性</b>：原版<b>看得见</b>我们的内容物
    /// （<c>Pawn_TraderTracker.cs:124</c> 自己就在枚举 <c>AllColonistBuildingsOfType&lt;IHaulSource&gt;()</c> 并
    /// yield <c>GetDirectlyHeldThings()</c>，轨道那条更有我们的 Postfix 追加），
    /// <c>PlayerSellableNow</c> 也**全部通过**（实测 16/16），却在最后一步被静默丢弃 ⇒
    /// <b>交易窗口玩家侧整片空白、连白银都没有</b>；而 <c>TradeDeal.cs:71-76</c> 找不到
    /// <c>IsCurrency</c> 时会给**商人侧**补一个 0 银的合成 tradeable，于是"商人侧正常、玩家侧空"。
    /// 越是上游的诊断（枚举计数、PlayerSellableNow 计数）越是全绿，真凶越看不见。</para>
    ///
    /// <para><b>修法与 3.0 的注入层无关</b>：4.0 的内容物是**真 Thing**，
    /// 原版自己就能枚举到它们，唯一缺的就是"允许它们站在货架上"。所以这里只做一件事 ——
    /// **把我们的容器排进那张白名单**，语义与 <c>Building_Bookcase</c> / <c>Building_OutfitStand</c> 完全一致。</para>
    ///
    /// <para><b>为什么是 Prefix 而不是"只翻一个条件"</b>：<c>InSellablePosition</c> 是私有实例方法，
    /// 放行之后后面还有**有真实语义**的判定（雾、房间、以及
    /// <c>PreventPlayerSellingThingsNearby</c> —— 它只有 <c>Hive</c> 和 <c>Pawn</c>（敌对方未倒地）
    /// 两个覆写会拦截，构成"身边有敌人就不能卖"的原版规则）。这些必须原样保留，
    /// 所以下面在本方法内**逐行照搬原版第 90-118 行**，只在入口换掉白名单；
    /// 非我方容器一律 <c>return true</c> 交还原版，绝不改变原版行为。
    /// 代价是原版若改动该方法需要同步（该类自 1.1 起形状稳定）。</para>
    ///
    /// <para><b>覆盖范围</b>：<c>AddAllTradeables</c> 是所有交易类型的**唯一**汇合点
    /// （商队 / 轨道 / 定居点 / 世界商队），所以这一处补丁同时修好全部交易路径。
    /// 世界商队（<c>playerNegotiator.IsWorldPawn()</c>）本来就会跳过本方法，不受影响。</para>
    /// </summary>
    [HarmonyPatch(typeof(TradeDeal), "InSellablePosition")]
    internal static class Patch_TradeDeal_InSellablePosition
    {
        private static bool Prefix(Thing t, out string reason, ref bool __result)
        {
            Building_StorageCore core = t.ParentHolder as Building_StorageCore;
            if (t.Spawned || core == null)
            {
                reason = null;
                return true;
            }

            if (!core.IsUsableNow || !core.HaulSourceEnabled)
            {
                reason = null;
                __result = false;
                return false;
            }

            IntVec3 positionHeld = t.PositionHeld;
            Map mapHeld = t.MapHeld;
            Room room = t.SpawnedParentOrMe.GetRoom();

            if (mapHeld == null || positionHeld.Fogged(mapHeld))
            {
                reason = null;
                __result = false;
                return false;
            }

            if (room != null)
            {
                int num = GenRadial.NumCellsInRadius(6.9f);
                for (int i = 0; i < num; i++)
                {
                    IntVec3 intVec = positionHeld + GenRadial.RadialPattern[i];
                    if (!intVec.InBounds(mapHeld) || intVec.GetRoom(mapHeld) != room)
                    {
                        continue;
                    }
                    List<Thing> thingList = intVec.GetThingList(mapHeld);
                    for (int j = 0; j < thingList.Count; j++)
                    {
                        if (thingList[j].PreventPlayerSellingThingsNearby(out reason))
                        {
                            __result = false;
                            return false;
                        }
                    }
                }
            }

            reason = null;
            __result = true;
            return false;
        }
    }
}

using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 铺「地基（Foundation）」地形时的**最后一道网**：真要撞车时先把这一格的上层地形清掉。
    ///
    /// <para><b>原版把这一步放在哪</b>（三处，唯独建造收尾没有）：</para>
    /// <list type="number">
    /// <item><c>Designator_Build.DesignateSingleCell</c>（god mode / 零工时分支，<c>Designator_Build.cs:479-486</c>）；</item>
    /// <item><c>WorkGiver_ConstructDeliverResourcesToBlueprints.JobOnThing</c>（<c>:89-93</c>）——
    /// 送料**之前**先派一个 <c>JobDefOf.RemoveFloor</c>（<c>ignoreDesignations:true</c>），
    /// 由 <c>JobDriver_RemoveFloor.DoEffect</c> 清掉旧地板；下一趟才 <c>ResourceDeliverJobFor</c> 送料。
    /// 这才是"在地板上铺地基"的正常路径；</item>
    /// <item><c>GravshipPlacementUtility.SpawnTerrain</c>（飞船落地，<c>:209-215</c>）。</item>
    /// </list>
    ///
    /// <para><b>为什么会撞</b>：<c>TerrainGrid.SetFoundation</c>（<c>TerrainGrid.cs:337-341</c>）在
    /// <c>underGrid != null</c> 时拒绝并报 "there is an under terrain there"。
    /// 而 <c>underGrid</c> 正是**铺地板**造出来的：原版所有地板都继承 <c>FloorBase</c>，
    /// 而 <c>FloorBase.layerable = true</c>（<c>XML/Core/Defs/TerrainDefs/Terrain_Floors.xml:5</c>，
    /// 且 <c>TerrainDef.Removable =&gt; layerable</c>，<c>TerrainDef.cs:304</c>）
    /// ⇒ <c>SetTerrain</c> 会把旧地形挪进 <c>underGrid</c>（<c>TerrainGrid.cs:215-229</c>）。
    /// 于是「在已有地板的格子上铺逆重飞船基架」（<c>TerrainDef Substructure</c>，<c>isFoundation=true</c>，
    /// <c>XML/Odyssey/Defs/TerrainDefs/Terrain_Foundation.xml:4-41</c>）必然需要第 2 条那一步。</para>
    ///
    /// <para><b>代价</b>：<c>Frame.CompleteConstruction</c> 在铺地形**之前**就把框 <c>Destroy()</c> 了
    /// （<c>Frame.cs:277-280</c>）⇒ 拒绝之后框没了、料没了、工没了，只剩一行红字，
    /// 玩家看到的是「造完了但地基没出现」。</para>
    ///
    /// <para><b>本 mod 的两层处理</b>：正路是
    /// <c>DSConstructionDelivery.RemoveExistingFloorFirstIfNeeded</c>（数字工人的供料路径 +
    /// 殖民者的 <c>JobDriver_DS_Withdraw</c> 各调用一次，等价于原版第 2 条），本前缀是**兜底**：
    /// 框不是经我们供料建完的（第三方路径 / 供料到完成之间这一格被别人加了地板 / 顺序竞争）时，
    /// 至少别让框白白消失。撞不上的格子（<c>underGrid == null</c>）一行都不做，零行为变化。</para>
    /// </summary>
    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    internal static class Patch_Frame_CompleteConstruction_Foundation
    {
        private static void Prefix(Frame __instance)
        {
            // 整段自吞：这里在"建造收尾"里动手，抛异常会连着原版的 CompleteConstruction 一起炸，
            // 那是框已经 Destroy 之后的现场，画面会很难看。失败就原样退回原版行为。
            try
            {
                var terrain = __instance.def.entityDefToBuild as TerrainDef;
                if (terrain == null || !terrain.isFoundation) return;   // 只关心"地基"地形

                Map map = __instance.Map;
                if (map == null) return;
                TerrainGrid grid = map.terrainGrid;
                IntVec3 pos = __instance.Position;

                if (grid.UnderTerrainAt(pos) == null) return;           // 没有下层地形：原版自己就能铺
                if (grid.TempTerrainAt(pos) != null) grid.RemoveTempTerrain(pos);   // 原版也会清，但它清在拒绝之后
                if (!grid.CanRemoveTopLayerAt(pos)) return;             // 上层拆不动（不可移除的天然地形）⇒ 维持原版行为
                grid.RemoveTopLayer(pos, doLeavings: true);             // 与设计器即时分支同义：上层清掉、下层顶上来
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 地基铺设前置清理失败（本格退回原版行为，地基可能铺不上）：" + e, 0x5D52E);
            }
        }
    }
}

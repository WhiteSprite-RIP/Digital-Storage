using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>种植代理的活</b>：播种 + 自动收割（外加把"伐木 / 割除"共用的收割产出段收在这里）。
    ///
    /// <para><b>为什么这两种活要单独走一条管线</b>：原版 <c>WorkGiver_GrowerSow</c> /
    /// <c>WorkGiver_GrowerHarvest</c> 是 <b>scanCells 型</b> —— 候选是<b>格子</b>不是 Thing，
    /// 闸门与取活都在 <c>JobOnCell</c>/<c>HasJobOnCell</c> 里，而且
    /// <c>WorkGiver_Scanner.HasJobOnThing</c> 的实现是 <c>JobOnThing(...) != null</c>
    /// （<c>WorkGiver_Scanner.cs</c>），这两类 giver 都没重写 <c>JobOnThing</c>
    /// ⇒ 用 Thing 那套去问，答案<b>恒为 false</b>。所以管线多了一条"格子目标"的路：
    /// <see cref="DigitalTask.TargetCell"/> / <see cref="DigitalTaskAdapter.CellBased"/>。</para>
    ///
    /// <para><b>范围严格照原版</b>：候选格子 = 原版 <c>WorkGiver_Grower.PotentialWorkCellsGlobal</c>
    /// （种植区 + 种植盆），闸门 = 原版 <c>JobOnCell</c>/<c>HasJobOnCell</c>。
    /// 也就是说：<b>野生成熟植物不打标记不会被自动收</b>（那条路是
    /// <see cref="DigitalTask_PlantCut"/> —— <c>WorkGiver_PlantsCut</c> 同时认
    /// <c>CutPlant</c> 与 <c>HarvestPlant</c> 两枚标记）；<b>播什么由玩家在种植区里设</b>，
    /// 不由我们挑。用户拍板的四条：只收种植区/种植盆、可达性照原版（以代理建筑所在格为准）、
    /// 格子上有挡路物就跳过这一格、苗在完工那一刻才生成。</para>
    /// </summary>
    internal static class DigitalPlantWork
    {
        /// <summary>
        /// 把原版那个 <c>protected static</c> 的 <c>wantedPlantDef</c> 清成 null。
        ///
        /// <para><b>为什么必须清</b>：原版 <c>WorkGiver_Grower</c> 靠"枚举
        /// <c>PotentialWorkCellsGlobal</c> 时，对每个 settable 顺手把想要的作物写进这个静态字段"
        /// 来给后面的 <c>JobOnCell</c>/<c>HasJobOnCell</c> 传值（它们只在该字段为 null 时才
        /// 自己算 <c>CalculateWantedPlantDef(c, map)</c>）。我们物化候选集之后才逐个格子去问，
        /// 这时枚举早已结束 ⇒ 字段要么是 null、要么是<b>上一个 settable 的脏值</b>，
        /// 脏值会让原版按"别的种植区的作物"来判定（表现：某片种植区永远播不上 / 按错的作物算门槛）。
        /// 清成 null 就把判定交回"按当前格子自己算"，那正是我们要的。</para>
        /// </summary>
        public static void ResetWantedPlantDef()
        {
            GrowerStaticAccess.SetWantedPlantDef(null);
        }

        /// <summary>原版 <c>JobDriver_PlantWork.WorkDonePerTick</c>：<c>PlantWorkSpeed × Lerp(3.3, 1, Growth)</c>。
        /// 这里把 <c>PlantWorkSpeed</c> 换成建筑倍率（拍板：速度只认倍率），Growth 那一项照抄。</summary>
        public static float WorkPerTick(float speedMult, Plant plant)
        {
            return speedMult * Mathf.Lerp(3.3f, 1f, plant.Growth);
        }

        /// <summary>
        /// 收割产出段 —— 原样搬 <c>JobDriver_PlantWork.MakeNewToils</c> 里"该收成了"那一段，
        /// 落点从 <c>actor.Position</c> 换成植株格（隔空干活没有"人站在哪"这回事）。
        ///
        /// <para>伐木/割除（<see cref="DigitalTask_PlantCut"/>）与自动收割
        /// （<see cref="DigitalTask_PlantHarvest"/>）共用本方法：两者只有
        /// <paramref name="asHarvest"/>（= 原版 driver 的 <c>PlantDestructionMode</c> 与收尾那一步）不同。</para>
        /// </summary>
        public static void Harvest(Pawn pawn, Plant plant, bool asHarvest)
        {
            if (pawn == null || plant == null || plant.Destroyed || !plant.Spawned) return;
            Map map = plant.Map;
            if (map == null) return;

            if (plant.def.plant.harvestedThingDef != null)
            {
                StatDef yieldStat = (plant.def.plant.harvestedThingDef.IsDrug || plant.def.plant.drugForHarvestPurposes)
                    ? StatDefOf.DrugHarvestYield
                    : StatDefOf.PlantHarvestYield;
                float statValue = pawn.GetStatValue(yieldStat);

                // ⚠️ 这里**没有**原版那条"收获失败"掷骰（JobDriver_PlantWork.cs:70：
                //    `Rand.Value > statValue` ⇒ 整株作物白收、只留一个红字气泡）。
                //    代理是机器："活干完了就该有产出"；高技能 statValue > 1 的多产奖励照旧保留。
                //    全局口径（含殖民者）见 Settings.WorkFailureTuner。
                int num = plant.YieldNow();
                if (statValue > 1f)
                {
                    num = GenMath.RoundRandom(num * statValue);
                }
                if (num > 0)
                {
                    Thing thing = ThingMaker.MakeThing(plant.def.plant.harvestedThingDef);
                    thing.stackCount = num;
                    Find.QuestManager.Notify_PlantHarvested(pawn, thing);
                    // 落点走 GenPlace ⇒ 代理建筑 tick 期间的 DigitalDropRedirect 会把它直塞最近的核心
                    GenPlace.TryPlaceThing(thing, plant.Position, map, ThingPlaceMode.Near);
                    if (pawn.records != null)
                    {
                        pawn.records.Increment(RecordDefOf.PlantsHarvested);
                    }
                }
                if (plant.HarvestableNow)
                {
                    List<ThingComp> comps = plant.AllComps;
                    for (int i = 0; i < comps.Count; i++)
                    {
                        foreach (ThingDefCountClass extra in comps[i].GetAdditionalHarvestYield())
                        {
                            Thing extraThing = ThingMaker.MakeThing(extra.thingDef);
                            extraThing.stackCount = extra.count;
                            GenPlace.TryPlaceThing(extraThing, plant.Position, map, ThingPlaceMode.Near);
                        }
                    }
                }
            }

            if (plant.def.plant.soundHarvestFinish != null)
            {
                // 声音源用植株本身（隔空干活没有"人"在场，也避开假 pawn 的 TargetInfo）
                plant.def.plant.soundHarvestFinish.PlayOneShot(plant);
            }

            plant.PlantCollected(pawn, asHarvest ? PlantDestructionMode.Chop : PlantDestructionMode.Cut);

            // ---- 收尾：照抄原版两条 driver 各自的 PlantWorkDoneToil ----
            if (asHarvest)
            {
                // JobDriver_PlantHarvest → Toils_General.RemoveDesignationsOnThing(HarvestPlant)
                Designation d = map.designationManager.DesignationOn(plant, DesignationDefOf.HarvestPlant);
                if (d != null)
                {
                    map.designationManager.RemoveDesignation(d);
                }
            }
            else if (!plant.Destroyed && plant.Spawned)
            {
                // JobDriver_PlantCut → Toils_Interact.DestroyThing（它自己带 !Destroyed 守卫）
                plant.Destroy();
            }
        }
    }

    /// <summary>
    /// 免反射读写原版 <c>WorkGiver_Grower.wantedPlantDef</c>（<b>protected static</b>）的访问器。
    ///
    /// <para><c>protected</c> 对派生类是可见的（静态成员也一样）⇒ 在自己程序集里派生一个
    /// 只用来当访问器的类即可，不用 <c>AccessTools</c> 写字符串字段名
    /// （写错字段名的反射是**静默失效**，而这里写错连编译都过不去）。
    /// 本类永不实例化：<c>WorkGiverDef.Worker</c> 只按 XML 里的 <c>giverClass</c> 建实例，
    /// 没有任何 def 指向它。</para>
    /// </summary>
    internal class GrowerStaticAccess : WorkGiver_Grower
    {
        public static void SetWantedPlantDef(ThingDef def)
        {
            wantedPlantDef = def;
        }

        public static ThingDef GetWantedPlantDef()
        {
            return wantedPlantDef;
        }
    }

    /// <summary>
    /// <b>格子型适配器的公共基类</b>：只管格子（<c>CellBased = true</c>），没有 Thing 入口。
    ///
    /// <para>为什么不干脆给 <c>DigitalTaskAdapter.MakeTask</c> 一个默认实现：那条路是 Thing 型的
    /// **唯一**入口，让它有默认空实现等于"某个适配器忘了写 MakeTask ⇒ 一整天一件活都不干、
    /// 却什么都不报"（正是本 mod 踩过的那类静默失败）。所以抽象成员照旧，
    /// 由这个基类显式地把 Thing 那一半封死并写清"永远不该被调到"。</para>
    /// </summary>
    public abstract class DigitalTaskAdapter_CellOnly : DigitalTaskAdapter
    {
        /// <summary>格子型：闸门在 <c>JobOnCell</c>/<c>HasJobOnCell</c> 里，comp 只走格子分支。</summary>
        public override bool CellBased
        {
            get { return true; }
        }

        /// <summary>不信任 Thing 那套：cell 型 giver 不重写 <c>JobOnThing</c>
        /// ⇒ <c>HasJobOnThing</c>（= <c>JobOnThing(...) != null</c>）恒为 false。</summary>
        public override bool TrustWorkGiver
        {
            get { return false; }
        }

        /// <summary>格子型适配器没有 Thing 入口。真被调到就说明 <c>CellBased</c> 被改错了；
        /// 返回 null 而不是抛异常（tick 路径里不抛）。</summary>
        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return null;
        }
    }

    // ==========================================================================================
    // 播种 —— 原版 WorkGiver_GrowerSow / JobDriver_PlantSow（scanCells 型）
    // ==========================================================================================
    public class DigitalTaskAdapter_GrowerSow : DigitalTaskAdapter_CellOnly
    {
        /// <summary>只认 Growing（见 <see cref="DigitalTaskAdapter.HomeWorkTypes"/>）。</summary>
        private static readonly string[] Homes = { "Growing" };

        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_GrowerSow); }
        }

        public override string[] HomeWorkTypes
        {
            get { return Homes; }
        }

        public override DigitalTask MakeCellTask(IntVec3 cell, Map map, Pawn pawn, WorkGiver_Scanner scanner, CompDigitalWorker comp)
        {
            DigitalPlantWork.ResetWantedPlantDef();

            Job job;
            try
            {
                job = scanner.JobOnCell(pawn, cell, false);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 问播种 WorkGiver 时出错：" + e, 7731);
                return null;
            }

            // 原版一个 JobOnCell 会给出三类结果：
            //   Sow（该种了）/ CutPlant（格子上有挡路的苗，得先砍）/ HaulAside（格子上有可搬运物）。
            // **只接 Sow**：其余两类分别属于伐木（我们自己的伐木代理）与搬运工作类型，
            // 顺手做掉会变成"种植代理偷偷接搬运"，范围失控（用户拍板：跳过这一格）。
            if (job == null || job.def != JobDefOf.Sow || job.plantDefToSow == null) return null;

            return new DigitalTask_PlantSow { comp = comp, cell = cell, plantDef = job.plantDefToSow };
        }
    }

    /// <summary>
    /// 播种。目标植物**不由我们挑**：原版 <c>JobOnCell</c> 从格子的 <c>IPlantToGrowSettable</c>
    /// （种植区 / 水培盆 / 花盆）取玩家设的作物，我们只认得出来的那件事。
    ///
    /// <para><b>苗在完工那一刻才生成</b>（原版是进入 toil 就先 spawn 一棵幼苗、中断就 <c>Destroy</c>）。
    /// 我们的进行中任务**不进存档**（见 <c>CompDigitalWorker.PostExposeData</c>）：
    /// 若照原版先 spawn，存读档后会留下一棵"未播种的孤儿苗"—— 它没有 job 去清理，
    /// 却会命中 <c>JobOnCell</c> 里"格子上已有同种植物 ⇒ 不播"那道门，<b>永久占住这一格</b>。
    /// 完工才落苗则完全没有这个窗口，且对外可见结果一致（终态就是
    /// <c>sown = true</c> + <c>Growth = 0.0001f</c>）。</para>
    /// </summary>
    public class DigitalTask_PlantSow : DigitalTask
    {
        public IntVec3 cell;
        public ThingDef plantDef;

        private float workDone;
        private bool done;

        public override string Label
        {
            get { return "DS_Task_Sow".Translate().ToString(); }
        }

        public override IntVec3 TargetCell
        {
            get { return cell; }
        }

        public override bool Finished
        {
            get { return done || Abort; }
        }

        public override string TargetLabel
        {
            get
            {
                string what = (plantDef == null) ? "?" : plantDef.LabelCap.ToString();
                return what + " (" + cell.x + ", " + cell.z + ")";
            }
        }

        public override float Progress01
        {
            get
            {
                if (plantDef == null || plantDef.plant == null || plantDef.plant.sowWork <= 0f) return -1f;
                return Mathf.Clamp01(workDone / plantDef.plant.sowWork);
            }
        }

        /// <summary>苗还不存在 ⇒ 只能占格子（见 <see cref="DigitalWorkerClaims"/> 的格子认领表）。</summary>
        public override void Claim(Map map, CompDigitalWorker me)
        {
            DigitalWorkerClaims.TryClaimCell(map, cell, me);
        }

        public override void Release(Map map, CompDigitalWorker me)
        {
            DigitalWorkerClaims.ReleaseCell(map, cell, me);
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            if (Abort || done) return false;
            if (plantDef == null || !cell.IsValid || !cell.InBounds(map)) return false;

            // ① 这一格上已经有同种植物了（原版 JobOnCell 的头一道门）⇒ 不播
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].def == plantDef) return false;
            }

            // ② 这一格还算不算"玩家让它种这个"的地方：种植区被删掉 / 作物被改 ⇒ 立刻放手
            IPlantToGrowSettable settable = cell.GetPlantToGrowSettable(map);
            return settable != null && settable.GetPlantDefToGrow() == plantDef;
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            if (Abort || done || plantDef == null) return;

            // 原版 JobDriver_PlantSow.tickIntervalAction：sowWorkDone += PlantWorkSpeed * delta
            // （播种不吃 Growth 那一项）
            workDone += speedMult;
            StrikeCount++;
            if (workDone < plantDef.plant.sowWork) return;
            if (!DigitalWorkBudget.AllowCompletion()) return;   // 超预算：下一 tick 再落苗

            // ---- 落苗前的最后一道复查（照抄原版 goto 那道 toil 的三条 FailOn）----
            if (PlantUtility.AdjacentSowBlocker(plantDef, cell, map) != null
                || !plantDef.CanNowPlantAt(cell, map)
                || !PlantUtility.GrowthSeasonNow(cell, map, plantDef))
            {
                Abort = true;
                return;
            }

            Plant plant = GenSpawn.Spawn(plantDef, cell, map) as Plant;
            if (plant == null)
            {
                Abort = true;
                return;
            }

            // 顺序照抄原版 JobDriver_PlantSow：spawn ⇒ Growth = 0 ⇒ sown = true ⇒
            // 干完才 Growth = BaseSownGrowthPercent(0.0001f)。我们在"干完"这一刻落苗，
            // 所以三步连着落到终态 —— 但顺序不能反：先 sown 再给 Growth，中途任何读
            // LifeStage / BlightableNow 的代码看到的才是"人工种的苗"而不是"野生的苗"。
            plant.Growth = 0f;
            plant.sown = true;
            plant.Growth = Plant.BaseSownGrowthPercent;
            map.mapDrawer.MapMeshDirty(cell, MapMeshFlagDefOf.Things);

            // 记录照抄原版；**不记 HistoryEvent**（那是"殖民者史"，而我们的工人是假 pawn，
            // 会把一个不存在的名字写进历史图）
            if (pawn.records != null)
            {
                pawn.records.Increment(RecordDefOf.PlantsSown);
            }

            done = true;
        }
    }

    // ==========================================================================================
    // 自动收割（无标记）—— 原版 WorkGiver_GrowerHarvest（scanCells 型）
    // ==========================================================================================
    public class DigitalTaskAdapter_GrowerHarvest : DigitalTaskAdapter_CellOnly
    {
        /// <summary>Growing —— 外加 Complex Jobs 把 <c>GrowerHarvest</c> 搬去的 <c>FSFHarvesting</c>
        /// （见 <see cref="DigitalTaskAdapter.HomeWorkTypes"/>；不装那个 mod 时这个 defName 不存在）。</summary>
        private static readonly string[] Homes = { "Growing", "FSFHarvesting" };

        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_GrowerHarvest); }
        }

        public override string[] HomeWorkTypes
        {
            get { return Homes; }
        }

        public override DigitalTask MakeCellTask(IntVec3 cell, Map map, Pawn pawn, WorkGiver_Scanner scanner, CompDigitalWorker comp)
        {
            DigitalPlantWork.ResetWantedPlantDef();

            bool ok;
            try
            {
                ok = scanner.HasJobOnCell(pawn, cell, false);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 问收割 WorkGiver 时出错：" + e, 7733);
                return null;
            }
            if (!ok) return null;

            // 这件活的目标仍然是一个 Thing（植株）⇒ 认领/表现/失效判定走既有那套，
            // 不需要格子认领（我们直接占住植株）。
            Plant p = cell.GetPlant(map);
            if (p == null || p.Destroyed || !p.Spawned) return null;

            return new DigitalTask_PlantHarvest { target = p, comp = comp };
        }
    }

    /// <summary>
    /// 自动收割（<b>无标记</b>）。闸门完全交给原版 <c>WorkGiver_GrowerHarvest.HasJobOnCell</c>
    /// （成熟 / <c>autoHarvestable</c> / 种植区 <c>allowCut</c> / <c>CompPlantPreventCutting</c> /
    /// <c>CanYieldNow</c> / <c>PawnWillingToCutPlant_Job</c> / <c>CanReserve</c>），我们只负责干活。
    /// </summary>
    public class DigitalTask_PlantHarvest : DigitalTask
    {
        private float workDone;

        public override string Label
        {
            get { return "DS_Task_Reap".Translate().ToString(); }
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            Plant p = target as Plant;
            if (p == null || p.Destroyed || !p.Spawned) return false;
            // 熟没熟是这件事唯一的状态：被别人先收了 / 被火烧了 / 生长倒退了 ⇒ 立刻放手。
            // （原版 JobDriver_PlantWork 的 cut toil 也是靠 FailOnDespawnedNullOrForbidden 收）
            return p.HarvestableNow && p.LifeStage == PlantLifeStage.Mature;
        }

        public override float Progress01
        {
            get
            {
                Plant p = target as Plant;
                if (p == null || p.def.plant.harvestWork <= 0f) return -1f;
                return Mathf.Clamp01(workDone / p.def.plant.harvestWork);
            }
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            Plant p = target as Plant;
            if (p == null || p.Destroyed) return;

            workDone += DigitalPlantWork.WorkPerTick(speedMult, p);
            StrikeCount++;
            if (workDone < p.def.plant.harvestWork) return;
            if (!DigitalWorkBudget.AllowCompletion()) return;   // 超预算：下一 tick 再收

            DigitalPlantWork.Harvest(pawn, p, true);
            workDone = 0f;
        }
    }
}

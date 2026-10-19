using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    /// <summary>
    /// 「工作不会失败」设置的实际施加者：把原版三处"干完才发现白干"的掷骰变成必成。
    ///
    /// <para><b>原版那三处</b>（都是 <c>Rand.Value</c> 对一条 stat 作比较）：</para>
    /// <list type="number">
    /// <item><b>收割</b>：<c>JobDriver_PlantWork.cs:70</c> ——
    /// <c>Rand.Value &gt; GetStatValue(PlantHarvestYield | DrugHarvestYield)</c> ⇒ 「收获失败」，
    /// 整株作物白收（只留一个红字气泡，产出为 0）；</item>
    /// <item><b>建造</b>：<c>JobDriver_ConstructFinishFrame.cs:59-67</c> ——
    /// <c>Rand.Value &lt; 1 - Pow(ConstructSuccessChance, 本次进度/总工时)</c> ⇒ <c>Frame.FailConstruction</c>，
    /// 框里的材料白费、还要重新取料；</item>
    /// <item><b>修故障</b>：<c>JobDriver_FixBrokenDownBuilding.cs:45</c> ——
    /// <c>Rand.Value &gt; GetStatValue(FixBrokenDownBuildingSuccessChance)</c> ⇒「修复失败」，零件白费。</item>
    /// </list>
    ///
    /// <para><b>为什么改 Def 而不是打补丁</b>：这三条掷骰都写在匿名闭包里
    /// （<c>tickIntervalAction</c> / <c>initAction</c> 的编译器生成方法），Harmony 没有稳定接缝；
    /// 唯一稳定的接缝就是它们查的那条 stat。而 stat 的值 =
    /// <c>defaultBaseValue × Π(skillNeedFactors) × Π(capacityFactors)</c>
    /// （<c>StatWorker.cs:368-392</c>），所以只要让这两段因子都 ≥ 1，掷骰就永远落在"成功"那一侧：</para>
    /// <list type="bullet">
    /// <item><c>skillNeedFactors</c>（<c>SkillNeed_Direct.valuesPerLevel</c>）：逐档夹到 ≥ 1。
    /// 原版是 0 级 0.6 / 0.75（收割 40% 失败、建造 25% 失败），8 级才到 1.0；</item>
    /// <item><c>capacityFactors</c>（手 / 眼）：把 <c>weight</c> 清零 —— 原版是
    /// <c>num = Mathf.Lerp(num, num × factor, weight)</c>（<c>StatWorker.cs:390</c>），
    /// <c>weight = 0</c> 即"这条容量完全不参与"。</item>
    /// </list>
    ///
    /// <para><b>高技能的多产奖励保留</b>：夹的是"≥ 1"，不是"= 1" ——
    /// <c>PlantHarvestYield</c> 在 20 级仍是 1.13 ⇒ <c>JobDriver_PlantWork</c> 里
    /// <c>statValue &gt; 1f</c> 那条产量乘算照旧生效。</para>
    ///
    /// <para><b>为什么必须有基线快照</b>：同 <see cref="DefMultipliers"/> —— 改的是 Def 自己的字段，
    /// 反复开关设置必须幂等（直接"当前值夹一遍"会在关→开→关之间漂移）。所以第一次施加前把 XML 原值抄一份，
    /// 之后每次都是"原值 → 按开关夹一遍"。</para>
    ///
    /// <para><b>范围</b>：全局（含原版与其它 mod 的小人）。只碰上面这几条"失败判定"stat，
    /// 速度 / 产量 / 品质类 stat 一律不动。</para>
    ///
    /// <para><b>不覆盖</b>（2026-10-11 拍板：不做 —— 本 mod 没有这三类工作的代理，且机制不同）：
    /// 手术（<c>MedicalSurgerySuccessChance</c>，走 <c>SurgeryOutcomeEffectDef</c> 那套独立机制，失败可能致命）、
    /// 逮捕（<c>ArrestSuccessChance</c>）、驯服 / 训练。采矿 / 挤奶 / 伐木只有产量没有失败判定，无需处理。</para>
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class WorkFailureTuner
    {
        private sealed class SkillSnapshot
        {
            public SkillNeed_Direct need;
            public float[] values;
        }

        private sealed class StatSnapshot
        {
            public readonly List<SkillSnapshot> skills = new List<SkillSnapshot>();
            public readonly List<PawnCapacityFactor> capacities = new List<PawnCapacityFactor>();
            public readonly List<float> capacityWeights = new List<float>();

            public bool IsEmpty
            {
                get { return skills.Count == 0 && capacities.Count == 0; }
            }
        }

        private static readonly Dictionary<StatDef, StatSnapshot> snapshots
            = new Dictionary<StatDef, StatSnapshot>();

        private static bool captured;

        static WorkFailureTuner()
        {
            Apply();
        }

        /// <summary>抄基线 → 按当前开关施加。幂等，设置里开关一变就调一次。</summary>
        public static void Apply()
        {
            try
            {
                Capture();
                if (!captured) return;

                bool on = DigitalStorageSettings.noWorkFailure;
                foreach (KeyValuePair<StatDef, StatSnapshot> kv in snapshots)
                {
                    ApplyTo(kv.Value, on);
                }

                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Warning("[DigitalStorage] 工作失败率设置 = " + (on ? "0（不会失败）" : "原版")
                        + "，已施加 " + snapshots.Count + " 条 stat。");
                }
            }
            catch (Exception e)
            {
                Log.Error("[DigitalStorage] 工作失败率设置施加失败（本次改动未生效）：" + e);
            }
        }

        // ===================================================================
        // 基线
        // ===================================================================

        /// <summary>
        /// 抄一份原值。**Def 还没加载完时直接返回、什么都不锁**：Mod 构造（读设置文件）可能早于 XML 解析，
        /// 那时 <c>StatDefOf</c> 全是 null，若在这里把 <c>captured</c> 钉死，后面就永远抄不到基线了。
        /// （同 <see cref="DefMultipliers.Capture"/> 的坑。）
        /// </summary>
        private static void Capture()
        {
            if (captured) return;
            if (StatDefOf.PlantHarvestYield == null) return;

            CaptureStat(StatDefOf.PlantHarvestYield);
            CaptureStat(StatDefOf.DrugHarvestYield);
            CaptureStat(StatDefOf.ConstructSuccessChance);
            CaptureStat(StatDefOf.FixBrokenDownBuildingSuccessChance);

            captured = snapshots.Count > 0;
            if (!captured)
            {
                Log.Warning("[DigitalStorage] 找不到任何工作失败类 stat，工作失败率设置本次无效。");
            }
        }

        private static void CaptureStat(StatDef def)
        {
            if (def == null || snapshots.ContainsKey(def)) return;

            StatSnapshot snap = new StatSnapshot();

            if (def.skillNeedFactors != null)
            {
                for (int i = 0; i < def.skillNeedFactors.Count; i++)
                {
                    // 只认 SkillNeed_Direct（本游戏里这几条 stat 全是它）。别的类型直接不动：
                    // SkillNeed_BaseBonus 的 baseValue/bonusPerLevel 是 private，夹它要反射，不值得。
                    SkillNeed_Direct direct = def.skillNeedFactors[i] as SkillNeed_Direct;
                    if (direct == null || direct.valuesPerLevel == null || direct.valuesPerLevel.Count == 0) continue;

                    float[] copy = new float[direct.valuesPerLevel.Count];
                    for (int j = 0; j < copy.Length; j++) copy[j] = direct.valuesPerLevel[j];
                    snap.skills.Add(new SkillSnapshot { need = direct, values = copy });
                }
            }

            if (def.capacityFactors != null)
            {
                for (int i = 0; i < def.capacityFactors.Count; i++)
                {
                    PawnCapacityFactor f = def.capacityFactors[i];
                    if (f == null) continue;
                    snap.capacities.Add(f);
                    snap.capacityWeights.Add(f.weight);
                }
            }

            // DrugHarvestYield 是"statFactor ← PlantHarvestYield"的空壳（没有自己的因子），
            // 它的值由内层 PlantHarvestYield 决定 —— 快照为空也照样登记，保持清单完整。
            if (!snap.IsEmpty || def == StatDefOf.DrugHarvestYield) snapshots[def] = snap;
        }

        // ===================================================================
        // 施加
        // ===================================================================

        private static void ApplyTo(StatSnapshot snap, bool on)
        {
            for (int i = 0; i < snap.skills.Count; i++)
            {
                SkillSnapshot s = snap.skills[i];
                if (s.need == null || s.need.valuesPerLevel == null) continue;

                int n = Mathf.Min(s.need.valuesPerLevel.Count, s.values.Length);
                for (int j = 0; j < n; j++)
                {
                    s.need.valuesPerLevel[j] = on ? Mathf.Max(1f, s.values[j]) : s.values[j];
                }
            }

            for (int i = 0; i < snap.capacities.Count; i++)
            {
                PawnCapacityFactor f = snap.capacities[i];
                if (f == null) continue;
                f.weight = on ? 0f : snap.capacityWeights[i];
            }
        }
    }
}

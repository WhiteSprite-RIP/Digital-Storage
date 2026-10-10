using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>假 pawn（"资质载体"）工厂</b> —— 从 <see cref="DigitalStorage.Components.CompDigitalWorker.EnsureWorker"/>
    /// 原样抽出，供两个 comp 共用：<c>CompDigitalWorker</c>（挖掘/建造/清洁/种植）与
    /// <c>CompBillAutomation</c>（制作代理）。
    ///
    /// <para>抽出而不是复制：这段里有四条**踩过坑才写对**的东西（组件补齐 / 不进注册表 / 清特质 / 固定资质），
    /// 两处各写一份必然漂移 —— 而漂移的表现是"某个代理类型偶尔 NRE"，最难查。</para>
    ///
    /// <para><b>为什么不 spawn 真 pawn</b>：见 obsidian <c>代码Wiki/rimworld/代理工人-脱离job直调产出.md</c>。
    /// 假 pawn 只是"原版 API 要求的参数形状"，从不进 TickManager、不进存档。</para>
    /// </summary>
    internal static class DigitalWorkerFactory
    {
        /// <summary>
        /// 造一个资质固定为 <paramref name="skillLevel"/> 的假殖民者。
        /// 失败返回 null（调用方按"没有工人"处理，绝不抛进 tick）。
        /// </summary>
        public static Pawn Create(int skillLevel, string nameShort, string nameNick)
        {
            try
            {
                // 先例：god hand MapComponent_GodAssistant.cs:18-33
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                p.Name = new NameTriple("", nameShort, nameNick);

                // ★ 补上"生成时不需要、被 spawn 时才建"的那批组件（pather / rotationTracker / natives /
                //   filth / roping…）。不补的话，**任何读 pawn.DrawPos 的原版代码都会 NRE** ——
                //   PawnTweener.TweenedPosRoot 直接解引用 pawn.pather（Verse\PawnTweener.cs:104），
                //   而 pather 是 Pawn.SpawnSetup → PawnComponentsUtility.AddComponentsForSpawn 才建的。
                //   实测踩过：原版挖掘特效的 sprayer 取 TargetInfo.CenterVector3（→ Pawn.DrawPos）时炸掉。
                //   AddComponentsForSpawn 内部对"还没真的 spawn"的 pawn 是安全的
                //   （它给 AddAndRemoveDynamicComponents 传 actAsIfSpawned: true，PawnComponentsUtility.cs:206）。
                PawnComponentsUtility.AddComponentsForSpawn(p);

                // ① 不进地图注册表：即便作用域期间 Spawned 为 true，RegisterPawn 也会早退
                //    （MapPawns.cs:847 `if (!p.mindState.Active) return;`）
                p.mindState.Active = false;

                // ② 清掉随机特质 —— WorkTypeIsDisabled 会吃背景/特质，机器不该因抽到
                //    "不能做熟练劳动"而罢工（Notify_DisabledWorkTypesChanged 会清 Pawn 侧缓存）
                if (p.story != null && p.story.traits != null && p.story.traits.allTraits != null)
                {
                    p.story.traits.allTraits.Clear();
                }
                // ②b 随机**背景**同样会禁技能，而且是更隐蔽的那一半：见 NeutralizeBackstories
                NeutralizeBackstories(p);
                if (p.relations != null)
                {
                    p.relations.ClearAllRelations();
                }

                // ③ 固定资质：技能只进品质/门槛，且**永不成长**（任何地方都不调 skills.Learn）
                if (p.skills != null)
                {
                    for (int i = 0; i < p.skills.skills.Count; i++)
                    {
                        p.skills.skills[i].Level = skillLevel;
                    }
                }
                p.Notify_DisabledWorkTypesChanged();

                if (p.workSettings != null)
                {
                    p.workSettings.EnableAndInitialize();
                }

                WarnIfSkillsStillDisabled(p);
                return p;
            }
            catch (Exception e)
            {
                Log.Error("[DigitalStorage] 生成数字工人失败：" + e);
                return null;
            }
        }

        // ===================================================================
        // ②b 背景：随机背景同样会禁技能（而且是隐蔽的那一半）
        // ===================================================================

        private static BackstoryDef safeChildhood;
        private static BackstoryDef safeAdulthood;
        private static bool backstoriesResolved;

        /// <summary>
        /// 把两个背景槽位换成**不禁任何工作标签**的背景。
        ///
        /// <para><b>为什么非换不可</b>：工人是 <c>PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, ...)</c>
        /// 随机生成的，背景随之随机。而 <c>SkillRecord.GetLevel()</c> 在 <c>TotallyDisabled</c> 时
        /// **直接 return 0**（<c>SkillRecord.cs:336-339</c>）；<c>TotallyDisabled</c> ←
        /// <c>SkillDef.IsDisabled(pawn.CombinedDisabledWorkTags, pawn.GetDisabledWorkTypes())</c>
        /// （<c>SkillRecord.cs:419-422</c>），而 <c>CombinedDisabledWorkTags</c> 含**背景**的
        /// <c>workDisables</c>（<c>Pawn_StoryTracker.cs:264-283</c>）。
        /// ⇒ 抽到"禁手工 / 禁采矿"的背景时，下面把 <c>SkillRecord.Level</c> 写成多少都白写：
        /// 读出来仍是 <b>0</b> ⇒ 账单报「资质不够」（<c>RecipeDef.FirstSkillRequirementPawnDoesntSatisfy</c>），
        /// 采矿 yield 则走 disabled 分支。而假工人**不进存档**，每次读档重新随机 ⇒ 症状"时好时坏"。</para>
        ///
        /// <para>光清特质（<see cref="Create"/> 里上一段）盖不住这条来源。背景也**不能置 null**：
        /// <c>Pawn_StoryTracker.Childhood</c> 的 setter 直接解引用 <c>value.spawnCategories</c>
        /// （<c>Pawn_StoryTracker.cs:55</c>）⇒ 只有"换成一个安全的 def"这一条路。</para>
        ///
        /// <para><b>什么算安全</b>：<c>workDisables == WorkTags.None</c> 即够。技能被判禁有两条路
        /// （<c>SkillDef.cs:44-67</c>）：① <c>disablingWorkTags</c> 命中组合标签；② 该技能关联的
        /// <b>全部</b>工作类型都被禁。背景一个标签都不禁 ⇒ 两条都不成立。成年 pawn 再排掉
        /// <c>spawnCategories</c> 带 "Child" 的（挂了原版会 <c>Log.Warning</c>）。</para>
        ///
        /// <para>取最小 <c>defName</c> 只为**确定性**（同一存档每次读档落在同一个背景），
        /// 与"哪个背景更好"无关 —— 技能等级由下面统一写死。</para>
        /// </summary>
        private static void NeutralizeBackstories(Pawn p)
        {
            try
            {
                if (p == null || p.story == null) return;

                if (!backstoriesResolved)
                {
                    backstoriesResolved = true;   // 先置位：即便下面抛了也不反复重扫 def 表
                    safeChildhood = SafeBackstory(BackstorySlot.Childhood);
                    safeAdulthood = SafeBackstory(BackstorySlot.Adulthood);
                }

                if (safeChildhood != null && !ReferenceEquals(p.story.Childhood, safeChildhood))
                    p.story.Childhood = safeChildhood;
                if (safeAdulthood != null && !ReferenceEquals(p.story.Adulthood, safeAdulthood))
                    p.story.Adulthood = safeAdulthood;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 数字工人背景替换失败（沿用随机背景继续）：" + e, 0x44534256);
            }
        }

        /// <summary>def 表里挑一个不禁任何工作标签的背景；没有（极端 mod 集）返回 null。</summary>
        private static BackstoryDef SafeBackstory(BackstorySlot slot)
        {
            List<BackstoryDef> all = DefDatabase<BackstoryDef>.AllDefsListForReading;
            BackstoryDef best = null;

            for (int i = 0; i < all.Count; i++)
            {
                BackstoryDef b = all[i];
                if (b == null || b.slot != slot) continue;
                if (b.workDisables != WorkTags.None) continue;
                if (slot == BackstorySlot.Childhood && b.IsPlayerColonyChildBackstory) continue;
                if (best == null || string.CompareOrdinal(b.defName, best.defName) < 0) best = b;
            }

            if (best == null)
            {
                Log.WarningOnce("[DigitalStorage] 找不到不禁任何工作标签的 " + slot
                    + " 背景：数字工人会沿用随机背景，技能可能被读成 0。", 0x44534257);
            }
            return best;
        }

        /// <summary>
        /// 自证：背景（②b）+ 特质（②）都清完之后，技能不该还剩 <c>TotallyDisabled</c> 的。
        /// 还剩就说明来源是**基因 / hediff**（<c>Pawn.CombinedDisabledWorkTags</c> 的另外两路），
        /// 这条日志是"资质不够"复发时的第一现场。
        /// </summary>
        private static void WarnIfSkillsStillDisabled(Pawn p)
        {
            if (!DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog) return;
            if (p == null || p.skills == null || p.skills.skills == null) return;

            string names = null;
            for (int i = 0; i < p.skills.skills.Count; i++)
            {
                SkillRecord s = p.skills.skills[i];
                if (s == null || s.def == null || !s.TotallyDisabled) continue;
                names = (names == null) ? s.def.defName : (names + ", " + s.def.defName);
            }
            if (names != null)
            {
                Log.WarningOnce("[DigitalStorage] 数字工人仍有被禁的技能（" + names
                    + "）：背景已换成不禁工作标签的，来源应在基因或 hediff。", 0x44534258);
            }
        }
    }
}

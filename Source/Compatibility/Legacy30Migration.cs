using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using Verse;

namespace DigitalStorage.Compatibility
{
    /// <summary>
    /// 3.0 → 4.0 存档迁移：把旧账本里的库存变成核心容器里的**真实物品**。
    ///
    /// <para><b>3.0 的库存存在哪</b>：<c>Building_StorageCore.ExposeData</c> 里
    /// <c>Scribe_Deep.Look(ref ledger, "ledger")</c>，而 <c>CoreLedger</c> 存的是**两条平行列表**：</para>
    /// <code>
    /// Scribe_Collections.Look(ref entries, "stockKeys", LookMode.Value);   // "defName|stuffDefName"（stuff 为 null 写作 "-"）
    /// Scribe_Collections.Look(ref counts, "stockCounts", LookMode.Value);  // long
    /// </code>
    /// <para>所以**不需要复活任何旧类**：核心类名 4.0 没变（还是 <c>Building_StorageCore</c>），
    /// 我们只要在它身上留一个同键（<c>"ledger"</c>）的墓碑字段，把这两条列表读回来即可。</para>
    ///
    /// <para><b>为什么必须做</b>：4.0 的容器里是真实 Thing，而 3.0 的账本数据没有任何东西去读它 ——
    /// 不做迁移，玩家更新后核心里的货就凭空消失了（读档时既不报错也不提示）。</para>
    ///
    /// <para><b>不丢物</b>：装不下的（容器栈数上限 + 原版堆叠上限）**落地到核心脚下**，绝不销毁；
    /// 连落地都失败的才记 error。</para>
    /// </summary>
    public static class Legacy30Migration
    {
        /// <summary>旧 <c>CoreLedger</c> 的墓碑：字段名/键名与 3.0 的 Scribe 逐字对齐。</summary>
        public class LegacyLedger : IExposable
        {
            public List<string> keys;
            public List<long> counts;

            public void ExposeData()
            {
                Scribe_Collections.Look(ref keys, "stockKeys", LookMode.Value);
                Scribe_Collections.Look(ref counts, "stockCounts", LookMode.Value);
            }

            public bool HasStock => keys != null && counts != null && keys.Count > 0;
        }

        /// <summary>
        /// 把旧账本逐项变成真实 Thing 塞进容器；装不下的落地。返回迁移进容器的单位总数。
        /// </summary>
        public static int Migrate(Building_StorageCore core, LegacyLedger ledger)
        {
            if (core == null || ledger == null || ledger.keys == null || ledger.counts == null) return 0;

            ThingOwner target = core.GetDirectlyHeldThings();
            Map map = core.Map;
            IntVec3 cell = core.PositionHeld;

            int stacks = 0, units = 0, spilled = 0, skipped = 0;
            int n = Math.Min(ledger.keys.Count, ledger.counts.Count);

            for (int i = 0; i < n; i++)
            {
                long count = ledger.counts[i];
                if (count <= 0) continue;
                if (!TryParseKey(ledger.keys[i], out ThingDef def, out ThingDef stuff)) { skipped++; continue; }

                int stackLimit = def.stackLimit > 0 ? def.stackLimit : 1;
                long remaining = count;

                while (remaining > 0)
                {
                    int take = (int)Math.Min(remaining, stackLimit);
                    Thing t;
                    try
                    {
                        t = ThingMaker.MakeThing(def, stuff);
                    }
                    catch (Exception e)
                    {
                        Log.Warning("[DigitalStorage] 3.0 迁移：无法生成 " + def.defName + "：" + e.Message);
                        skipped += take;
                        break;
                    }
                    if (t == null) { skipped += take; break; }

                    t.stackCount = take;
                    remaining -= take;

                    if (target.Count < core.maxStacks)
                    {
                        // ★ 走"恢复性插入"：迁移不是玩家在入库，必须绕开筛选/电力/入库开关那套规则
                        //   （容量由上面这句自己判）。走 TryAdd 的话，未通电的旧档会被整套倒在地上。
                        core.AddRestored(t);
                        stacks++;
                        units += take;
                    }
                    else if (map != null && GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near))
                    {
                        spilled += take; // 容器满了 ⇒ 落地，绝不销毁
                    }
                    else
                    {
                        Log.Error("[DigitalStorage] 3.0 迁移：既塞不进核心也落不了地，销毁 " + take + " x " + def.defName);
                        t.Destroy();
                        skipped += take;
                        break;
                    }
                }
            }

            if (stacks > 0 || spilled > 0 || skipped > 0)
            {
                Log.Warning("[DigitalStorage] 3.0 存档迁移完成（" + core.LabelShort + "）："
                    + units + " 单位 / " + stacks + " 堆进入核心"
                    + (spilled > 0 ? "；容器已满，" + spilled + " 单位落在核心脚下" : "")
                    + (skipped > 0 ? "；跳过 " + skipped + " 单位（def 已不存在或无法生成）" : ""));
            }
            return units;
        }

        /// <summary>
        /// 3.0 的 <c>storageFilter</c> 迁到 4.0 的存储设置。**只复制"被禁"的那一侧**：
        /// 3.0 的过滤器默认是"全允许"，原样复制会把尸体之类也放进来；
        /// 只保留玩家的限制意图，不放宽任何东西。
        /// </summary>
        public static void ApplyLegacyFilter(Building_StorageCore core, ThingFilter legacy)
        {
            if (core == null || legacy == null) return;

            ThingFilter current = core.GetStoreSettings()?.filter;
            if (current == null) return;

            int blocked = 0;
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def == null) continue;
                if (legacy.Allows(def)) continue;
                if (!current.Allows(def)) continue;

                current.SetAllow(def, false);
                blocked++;
            }

            if (blocked > 0)
                Log.Warning("[DigitalStorage] 3.0 迁移：" + core.LabelShort + " 沿用旧存储筛选，禁用了 " + blocked + " 个 def。");
        }

        /// <summary>解析旧键 <c>"defName|stuffDefName"</c>（stuff 为 null 写作 "-"）。</summary>
        private static bool TryParseKey(string raw, out ThingDef def, out ThingDef stuff)
        {
            def = null;
            stuff = null;
            if (string.IsNullOrEmpty(raw)) return false;

            int bar = raw.IndexOf('|');
            string defName = (bar < 0) ? raw : raw.Substring(0, bar);
            string stuffName = (bar < 0) ? "-" : raw.Substring(bar + 1);

            def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null) return false;

            if (stuffName != "-")
            {
                stuff = DefDatabase<ThingDef>.GetNamedSilentFail(stuffName);
                // stuff 丢了也照样还原（MakeThing 会当无材质处理），不整条丢弃
            }
            return true;
        }
    }
}

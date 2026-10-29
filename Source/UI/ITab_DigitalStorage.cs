// =====================================================================================
//  【本地新增文件／整块替换】ITab_DigitalStorage —— 存储核心内容物页签（AE2 材质版）
// -------------------------------------------------------------------------------------
//  这是**整块替换**：逻辑（按 def+材质+品质 聚合、分类折叠、搜索、取出、搬运优先级、容量条）
//  与文案 key 全部保留，只把绘制换成 AE2 材质（AE2Draw）：
//    · 容量条 → AE2 凹槽 + 青色填充条（替掉 Widgets.FillableBar）
//    · 优先级 / 取出 → AE2 凸起按钮
//    · 搜索框 → AE2 输入框
//    · 列表 → AE2 行（分组头 + 行），**不再用 BeginScrollView**，改用 AE2Draw.DragBar 自绘滚动
//      （虚拟列表：先算每行的 y 位置，再只画落在可视区里的那些）
//
//  ★ 2026-10-29 分类重做（用户要求：「原版物品在存储区怎么被分类的，我们也加上武器这些类别，
//    还有 mod 自定义的我们也可以显示」）：
//    旧的 6 组硬编码（ItemGroup 枚举 + defName 白名单）换成**原版 ThingCategoryDef 分类树的层级**，
//    数据来源见 Core\ItemGroup.cs。这里只负责「把路径渲染成可折叠的树」：
//      大类（Weapons / Apparel / Foods / Manufactured / …，含 mod 自建大类）
//        └ 子类（WeaponsRanged / Headgear / FoodRaw / …，含 mod 自建子类）
//             └ 物品行（def + 材质 + 品质 聚合）
//    **默认全展开**：进页签先看到东西；嫌长就点分类头折叠（折叠状态按页签实例记忆）。
//    搜索时不看折叠状态（强制定位到命中项），否则「搜得到但看不见」又会变成一次排障。
// =====================================================================================
using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    public class ITab_DigitalStorage : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(470f, 560f);

        // ---- 分类树的折叠状态（按 ThingCategoryDef 记；换一台核心也沿用，玩家不用每台都折一次）----
        private readonly Dictionary<ThingCategoryDef, bool> expanded = new Dictionary<ThingCategoryDef, bool>();
        private bool otherExpanded = true;

        private float scrollFrac;          // ★ AE2：0~1 的滚动比例（DragBar 用）
        private string search = "";

        private const float HeaderRowH = 26f;
        private const float ItemRowH = 24f;
        private const float IndentPerDepth = 14f;

        private Building_StorageCore Core { get { return SelThing as Building_StorageCore; } }

        public ITab_DigitalStorage()
        {
            this.size = WinSize;
            this.labelKey = "DS_TabDigitalStorage";
        }

        public override bool IsVisible { get { return Core != null; } }

        // ===================================================================
        // 行快照：按 真实身份（def + 材质 + 品质）聚合
        // ===================================================================
        private struct Row
        {
            public ThingDef def;
            public ThingDef stuff;
            public int qualityOrdinal;   // -1 = 无品质
            public string label;
            public int count;
            public ThingCategoryDef[] path;   // 原版分类树路径（空 = 没分类 ⇒ 「其他」）
        }

        /// <summary>分类树节点。叶子上挂物品行；每层都带「子树合计」用于折叠时显示总量。</summary>
        private sealed class CatNode
        {
            public ThingCategoryDef cat;     // null = 合成节点（根 或「其他」桶）
            public bool isOther;             // 「其他」桶：没有分类信息的物品
            public int depth;                // 0 = 顶层大类（原版 Root 的直接子类）
            public readonly List<CatNode> children = new List<CatNode>();
            public readonly List<int> rows = new List<int>();
            public long count;               // 子树物品总数
            public int kinds;                // 子树行数（聚合种类数）
            public int matchedKinds;         // 搜索命中后的行数
            public long matchedCount;

            public bool IsExpanded(ITab_DigitalStorage tab, bool searching)
            {
                if (searching) return true;   // 搜索时强制展开，保证命中项一定看得见
                if (isOther) return tab.otherExpanded;
                bool v;
                return tab.expanded.TryGetValue(cat, out v) ? v : true;   // 默认全展开
            }
        }

        private struct Line
        {
            public bool isHeader;
            public CatNode node;
            public int row;      // 物品行的 rows 下标（仅 isHeader == false 时有效）
            public float y;
        }

        private readonly List<Row> rows = new List<Row>();
        private int cachedTick = -1;
        private int cachedCount = -1;

        private CatNode treeRoot = new CatNode { depth = -1, isOther = false };

        private static int QualityOrdinalOf(Thing t)
        {
            CompQuality q = t.TryGetComp<CompQuality>();
            return (q != null) ? (int)q.Quality : -1;
        }

        private void RebuildRowsIfNeeded(Building_StorageCore core, bool force = false)
        {
            int tick = Find.TickManager.TicksGame;
            int count = core.innerContainer.Count;
            if (!force && tick == cachedTick && count == cachedCount) return;
            cachedTick = tick;
            cachedCount = count;

            List<ValueTuple<ThingDef, ThingDef, int>> order = new List<ValueTuple<ThingDef, ThingDef, int>>();
            Dictionary<ValueTuple<ThingDef, ThingDef, int>, int> counts = new Dictionary<ValueTuple<ThingDef, ThingDef, int>, int>();
            Dictionary<ValueTuple<ThingDef, ThingDef, int>, string> labels = new Dictionary<ValueTuple<ThingDef, ThingDef, int>, string>();

            for (int i = 0; i < core.innerContainer.Count; i++)
            {
                Thing t = core.innerContainer[i];
                if (t == null || t.def == null || t.Destroyed) continue;

                ValueTuple<ThingDef, ThingDef, int> key = new ValueTuple<ThingDef, ThingDef, int>(t.def, t.Stuff, QualityOrdinalOf(t));
                int cur;
                if (counts.TryGetValue(key, out cur))
                {
                    counts[key] = cur + t.stackCount;
                }
                else
                {
                    counts[key] = t.stackCount;
                    labels[key] = BuildLabel(t);
                    order.Add(key);
                }
            }

            rows.Clear();
            for (int i = 0; i < order.Count; i++)
            {
                ValueTuple<ThingDef, ThingDef, int> key = order[i];
                Row r = new Row();
                r.def = key.Item1;
                r.stuff = key.Item2;
                r.qualityOrdinal = key.Item3;
                r.label = labels[key];
                r.count = counts[key];
                r.path = ItemGrouping.PathOf(key.Item1);
                rows.Add(r);
            }
            rows.Sort(delegate (Row a, Row b) { return string.Compare(a.label, b.label, StringComparison.Ordinal); });
            BuildTree();
        }

        // ===================================================================
        // 分类树构建
        // ===================================================================

        private void BuildTree()
        {
            CatNode root = new CatNode { depth = -1 };
            Dictionary<ThingCategoryDef, CatNode> byCat = new Dictionary<ThingCategoryDef, CatNode>();
            CatNode other = null;

            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.path == null || r.path.Length == 0)
                {
                    if (other == null)
                    {
                        other = new CatNode { depth = 0, isOther = true };
                        root.children.Add(other);
                    }
                    other.rows.Add(i);
                    other.count += r.count;
                    other.kinds++;
                    continue;
                }

                CatNode parentNode = root;
                for (int d = 0; d < r.path.Length; d++)
                {
                    ThingCategoryDef cat = r.path[d];
                    CatNode node;
                    if (!byCat.TryGetValue(cat, out node))
                    {
                        node = new CatNode { cat = cat, depth = d };
                        byCat[cat] = node;
                        parentNode.children.Add(node);
                    }
                    node.count += r.count;
                    node.kinds++;
                    parentNode = node;
                }
                // 物品挂在路径最深处（最细分类）；中间层同时累计，折叠时也能看到总量。
                parentNode.rows.Add(i);
            }

            SortTree(root);
            treeRoot = root;
        }

        private static void SortTree(CatNode node)
        {
            node.children.Sort(CompareNode);
            for (int i = 0; i < node.children.Count; i++) SortTree(node.children[i]);
        }

        /// <summary>
        /// 同层排序：先按原版分类树的前序序号（<see cref="ItemGrouping.OrderOf"/>），再按 label 兜底。
        /// 「其他」桶永远排最后（它不是原版分类，不该混进武器/服装之间）。
        /// </summary>
        private static int CompareNode(CatNode a, CatNode b)
        {
            if (a.isOther != b.isOther) return a.isOther ? 1 : -1;
            int oa = ItemGrouping.OrderOf(a.cat);
            int ob = ItemGrouping.OrderOf(b.cat);
            if (oa != ob) return oa.CompareTo(ob);
            return string.Compare(a.cat != null ? a.cat.label : "", b.cat != null ? b.cat.label : "",
                                 StringComparison.OrdinalIgnoreCase);
        }

        // ===================================================================

        private static string BuildLabel(Thing t)
        {
            string label;
            try { label = t.LabelNoCount; }
            catch { label = t.def.LabelCap; }

            if (t.def.useHitPoints && t.MaxHitPoints > 0)
                label += "  " + ((float)t.HitPoints / t.MaxHitPoints).ToStringPercent();
            return label;
        }

        // ===================================================================

        protected override void FillTab()
        {
            try
            {
            Building_StorageCore core = Core;
            if (core == null) return;
            RebuildRowsIfNeeded(core);

            AE2Draw.HandlePauseHotkey();   // ★ 用户要求：界面开着时空格也能暂停/继续
            Rect rect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(4f);
            AE2Draw.PanelBox(rect);
            Rect ri = rect.ContractedBy(6f);
            float y = ri.y;

            // ---- 容量条（AE2：凹槽 + 青色填充）----
            Rect barRect = new Rect(ri.x, y, ri.width - 100f, 22f);
            Rect craftBtn = new Rect(ri.xMax - 96f, y, 96f, 22f);
            if (AE2Draw.TextButton(craftBtn, "DS_AE2_OpenCraft".Translate().ToString()))   // ★ 主操作：常态按钮
            {
                // 找不到代理就传 null ⇒ 新面板自己会挑一台；一台都没有会显示提示（原版文案）
                Find.WindowStack.Add(new Window_AE2CraftPanel(NearestProxy(core)));
            }
            // ★ 用户要求：右上角让出 100px 给"打开制作代理界面"按钮
            int used = core.innerContainer.Count;
            int cap = core.maxStacks;
            AE2Draw.Bar(barRect, (cap > 0) ? Mathf.Clamp01((float)used / cap) : 0f, false);
            AE2Draw.TextIn(new Rect(barRect.x, barRect.y + 3f, barRect.width, 16f), "DS_CapacityBarLv".Translate(CoreTier.Level, used, cap, rows.Count).ToString(), 1f, AE2Draw.Hi, TextAnchor.MiddleLeft);
            y += 26f;

            // ---- 搬运优先级（AE2 按钮 + 浮窗菜单）----
            AE2Draw.TextIn(new Rect(ri.x, y + 3f, 54f, 16f), "DS_Priority".Translate().ToString() + ":", 1f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            Rect prioRect = new Rect(ri.x + 56f, y, 150f, 22f);
            if (AE2Draw.TextButton(prioRect, PriorityLabel(core.storagePriority)))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (StoragePriority p in Enum.GetValues(typeof(StoragePriority)))
                {
                    StoragePriority priority = p;
                    if (priority == StoragePriority.Unstored) continue;
                    options.Add(new FloatMenuOption(PriorityLabel(priority), delegate { core.storagePriority = priority; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            y += 26f;

            // ---- 搜索框（AE2 输入框）----
            search = AE2Draw.TextField(new Rect(ri.x, y, ri.width, 24f), search);
            y += 28f;

            // ---- 列表（虚拟列表 + AE2 侧面拖动条；不再用 BeginScrollView）----
            Rect list = new Rect(ri.x, y, ri.width, ri.yMax - y);
            DrawList(list, core);
            }
            catch (Exception __uiEx) { Log.ErrorOnce("[DigitalStorage] AE2 界面绘制异常（只记一次，界面不会卡死）：" + __uiEx, 771005); }
        }

        /// <summary>把分类树 + 物品行铺成一条虚拟列表，只画落在可视区里的部分。</summary>
        private void DrawList(Rect list, Building_StorageCore core)
        {
            bool searching = !string.IsNullOrEmpty(search);

            // ① 统计（搜索时只算命中项；折叠头显示命中的件数/种数）
            CountMatches(treeRoot);

            // ② 排版：分类头 + （展开时）子分类 + 物品行
            List<Line> lines = new List<Line>();
            float total = 0f;
            for (int i = 0; i < treeRoot.children.Count; i++)
                Layout(treeRoot.children[i], searching, lines, ref total);

            if (total <= 0f)
            {
                AE2Draw.TextIn(new Rect(list.x + 4f, list.y + 4f, list.width - 8f, 18f), (rows.Count == 0) ? "DS_EmptyCoreText".Translate().ToString() : "DS_KindsEmpty".Translate().ToString(), 1f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
                return;
            }

            // ③ 可视区 + 滚动偏移
            float viewH = list.height;
            float maxOffset = Mathf.Max(0f, total - viewH);
            float offset = Mathf.Round(scrollFrac * maxOffset);

            for (int i = 0; i < lines.Count; i++)
            {
                Line line = lines[i];
                float ly = line.y - offset;
                if (ly + HeaderRowH < 0f) continue;
                if (ly > viewH) break;

                if (line.isHeader) DrawHeader(list, line, ly, searching);
                else DrawItemRow(list, line, ly);
            }

            AE2Draw.DragBar(new Rect(list.xMax - 12f, list.y, 12f, list.height), ref scrollFrac,
                Mathf.Clamp01(viewH / Mathf.Max(1f, total)));
            AE2Draw.WheelScroll(list, ref scrollFrac);   // ★ 滚轮在列表里也能滚
        }

        /// <summary>递归统计搜索命中：命中的行数/件数向上累加（折叠头据此显示）。</summary>
        private void CountMatches(CatNode node)
        {
            node.matchedKinds = 0;
            node.matchedCount = 0;
            for (int i = 0; i < node.rows.Count; i++)
            {
                Row r = rows[node.rows[i]];
                if (!RowMatchesSearch(r)) continue;
                node.matchedKinds++;
                node.matchedCount += r.count;
            }
            for (int i = 0; i < node.children.Count; i++)
            {
                CatNode c = node.children[i];
                CountMatches(c);
                node.matchedKinds += c.matchedKinds;
                node.matchedCount += c.matchedCount;
            }
        }

        private void Layout(CatNode node, bool searching, List<Line> lines, ref float total)
        {
            if (node.matchedKinds <= 0) return;   // 搜索没命中（或空分类）⇒ 整支不占高度

            Line header = new Line();
            header.isHeader = true;
            header.node = node;
            header.row = -1;
            header.y = total;
            lines.Add(header);
            total += HeaderRowH + 2f;

            if (!node.IsExpanded(this, searching)) return;

            for (int i = 0; i < node.children.Count; i++)
                Layout(node.children[i], searching, lines, ref total);

            for (int i = 0; i < node.rows.Count; i++)
            {
                int ri = node.rows[i];
                if (searching && !RowMatchesSearch(rows[ri])) continue;
                Line item = new Line();
                item.isHeader = false;
                item.node = node;
                item.row = ri;
                item.y = total;
                lines.Add(item);
                total += ItemRowH + 1f;
            }
        }

        private void DrawHeader(Rect list, Line line, float ly, bool searching)
        {
            CatNode node = line.node;
            float indent = 4f + node.depth * IndentPerDepth;
            Rect header = new Rect(list.x + indent, list.y + ly, list.width - 14f - indent, HeaderRowH);

            bool hover = Mouse.IsOver(header);
            AE2Draw.Sunken(header, hover ? AE2Draw.SlotHover : AE2Draw.Slot);

            // 原版分类自带的图标（mod 分类没写 iconPath 就是 BadTex ⇒ 不画，留白给文字）
            float textX = header.x + 5f;
            Texture2D icon = (node.cat != null) ? node.cat.icon : null;
            if (!node.isOther && icon != null && icon != BaseContent.BadTex)
            {
                Rect iconRect = new Rect(header.x + 4f, header.y + 4f, 18f, 18f);
                Widgets.DrawTextureFitted(iconRect, icon, 1f);
                textX = iconRect.xMax + 4f;
            }

            string arrow = node.IsExpanded(this, searching) ? "▼" : "▶";
            string label = node.isOther ? "DS_Group_Other".Translate().ToString() : node.cat.LabelCap.ToString();
            string text = arrow + " " + label + "   ×" + node.matchedCount + "   (" + node.matchedKinds + " " + "DS_Kinds".Translate().ToString() + ")";

            AE2Draw.TextIn(new Rect(textX, header.y + 5f, header.xMax - textX - 4f, 16f), text, 1f, AE2Draw.Hi, TextAnchor.MiddleLeft);

            if (Widgets.ButtonInvisible(header)) Toggle(node);
        }

        private void Toggle(CatNode node)
        {
            if (node.isOther)
            {
                otherExpanded = !otherExpanded;
                return;
            }
            bool now;
            bool cur = expanded.TryGetValue(node.cat, out now) ? now : true;
            expanded[node.cat] = !cur;
        }

        private void DrawItemRow(Rect list, Line line, float ly)
        {
            CatNode node = line.node;
            Row row = rows[line.row];

            float indent = 4f + node.depth * IndentPerDepth;
            Rect rowRect = new Rect(list.x + indent + IndentPerDepth, list.y + ly, list.width - 14f - indent - IndentPerDepth, ItemRowH);
            bool hov = Mouse.IsOver(rowRect);
            AE2Draw.Row(rowRect, hov, false);

            Rect icon = new Rect(rowRect.x + 2f, rowRect.y + 1f, 22f, 22f);
            AE2Draw.SlotBox(icon, false);
            Widgets.ThingIcon(icon.ContractedBy(1f), row.def, row.stuff);

            AE2Draw.TextIn(new Rect(rowRect.x + 28f, rowRect.y + 4f, rowRect.width - 140f, 16f), row.label + "   ×" + row.count, 1f, AE2Draw.TextCol, TextAnchor.MiddleLeft);

            if (AE2Draw.TextButton(new Rect(rowRect.xMax - 92f, rowRect.y + 1f, 90f, 22f), "DS_WithdrawBtn".Translate().ToString()))
            {
                Row captured = row;
                Find.WindowStack.Add(new Dialog_WithdrawAmount(captured.label, captured.count, 0,
                    delegate (int amount) { ExtractRow(row, amount); }));
            }
        }

        private bool RowMatchesSearch(Row row)
        {
            if (string.IsNullOrEmpty(search)) return true;
            return row.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>把某一行（def + 材质 + 品质）取出 amount 个，落到核心旁边。</summary>
        private void ExtractRow(Row row, int amount)
        {
            Building_StorageCore core = Core;
            if (core == null) return;
            Map map = core.Map;
            if (map == null) return;

            // ★ 适配上游 2026-10-12 的重构：ExtractMatchingTo 签名已变
            //   旧：ExtractMatchingTo(int count, IntVec3 pos, Map map, Predicate<Thing> filter)     4 参
            //   新：ExtractMatchingTo(Building_StorageCore core, int count, IntVec3 pos, Map map,
            //                        Predicate<Thing> filter, bool forbid = true)                     6 参
            //   forbid: true ⇒ 面板取物**置禁止**。4.1 起自动收纳尊重禁止
            //   （CompAutoIngest.Reject.Forbidden），否则 300 tick 的"刚被取出"窗口一过期，
            //   刚掏出来的东西就被瞬移吸回核心 —— 就是社区反复反馈的"取出来又塞回去"。
            int got = HaulSourceContents.ExtractMatchingTo(core, amount, core.Position, map,
                delegate (Thing t) { return t.def == row.def && t.Stuff == row.stuff && QualityOrdinalOf(t) == row.qualityOrdinal; },
                forbid: true);

            if (got <= 0)
                Messages.Message("DS_NoSpaceNearCore".Translate(), core, MessageTypeDefOf.RejectInput);
            else
                RebuildRowsIfNeeded(core, true);   // 取出后立刻刷新（容器 Count 可能不变：碎堆合并）
        }

        /// <summary>离这个核心最近的制作代理（找不到返回 null ⇒ 新面板会自己挑一台或给提示）。</summary>
        private static CompBillAutomation NearestProxy(Building_StorageCore core)
        {
            Map map = (core != null && core.Map != null) ? core.Map : Find.CurrentMap;
            if (map == null) return null;
            CompBillAutomation best = null;
            float bestD = float.MaxValue;
            List<Thing> all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
            {
                Thing th = all[i];
                if (th == null || !th.Spawned) continue;
                CompBillAutomation c = th.TryGetComp<CompBillAutomation>();
                if (c == null) continue;
                float d = (core != null) ? th.Position.DistanceTo(core.Position) : 0f;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private static string PriorityLabel(StoragePriority p)
        {
            // 键名必须与 vanilla Enums.xml 一致（StoragePriorityXxx），错键名会显示成泰南语
            switch (p)
            {
                case StoragePriority.Low: return "StoragePriorityLow".Translate();
                case StoragePriority.Normal: return "StoragePriorityNormal".Translate();
                case StoragePriority.Preferred: return "StoragePriorityPreferred".Translate();
                case StoragePriority.Important: return "StoragePriorityImportant".Translate();
                case StoragePriority.Critical: return "StoragePriorityCritical".Translate();
                default: return p.ToString();
            }
        }
    }
}

using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 面板分类 = <b>原版 ThingCategoryDef 分类树</b>（与储存区筛选窗口、原版「资源读数」同一套 def）。
    ///
    /// <para><b>为什么重写（2026-10-29 用户要求）</b>：旧实现把分类硬编码成 6 组
    /// （原料 / 食物 / 制成品 / 药品耗材 / 义体植入 / 其他），并且靠 <c>defName</c> 白名单认类别
    /// （<c>"Manufactured"</c> / <c>"Foods"</c> / <c>"Medicine"</c> …）。两个必然后果：</para>
    /// <list type="number">
    /// <item>原版在 <c>Root</c> 下另有 <c>Weapons</c>（武器）/ <c>Apparel</c>（服装）/ <c>Chunks</c> / <c>Corpses</c>
    /// 这几支大类，白名单里没有 ⇒ 它们的物品全被塞进「其他」。用户实测：枪、衣服都堆在「其他」。</item>
    /// <item>mod 自定义的 <c>ThingCategoryDef</c> 一个也进不来 —— 白名单只认原版那几个 defName。</item>
    /// </list>
    ///
    /// <para><b>新实现只做一件事</b>：把 <c>def.thingCategories</c> 沿 <c>parent</c> 走到顶，
    /// 得到「该物品在原版分类树里的那条路径」，面板按这条路径渲染层级。
    /// ⇒ 原版加了什么分类、mod 加了什么分类，<b>全部自动出现，零维护</b>。
    /// 判定依据是 def 的<b>真实数据</b>（<c>ThingCategoryDef</c> 树），不写任何 defName 名单。</para>
    ///
    /// <para><b>为什么不是「取 def.FirstThingCategory 一个名字」</b>：那样只能得到最细的叶子类别
    /// （<c>MeatRaw</c> / <c>WeaponsRanged</c> / <c>Headgear</c> …），层级关系丢了；
    /// 而原版储存区筛选窗口展示的正是层级（食物 → 生食 → 肉类）。这里保留整条路径。</para>
    /// </summary>
    public static class ItemGrouping
    {
        private static readonly ThingCategoryDef[] EmptyPath = new ThingCategoryDef[0];

        /// <summary>def → 分类路径（Root 的直接子类 … 最细类别）。分类不随运行时变化，可永久缓存。</summary>
        private static readonly Dictionary<ThingDef, ThingCategoryDef[]> pathCache =
            new Dictionary<ThingDef, ThingCategoryDef[]>();

        /// <summary>分类 → 原版树里的前序序号（同层排序用）。</summary>
        private static Dictionary<ThingCategoryDef, int> orderIndex;

        /// <summary>orderIndex 是按哪一份节点表建的 —— 开发者「重载 def」会把表换掉，必须能自愈。</summary>
        private static List<TreeNode_ThingCategory> indexedFrom;

        // ===================================================================
        // 对外：路径 + 排序
        // ===================================================================

        /// <summary>
        /// 物品在分类树里的路径：<c>[Root 的直接子类, …, 它最具体的类别]</c>。
        ///
        /// <para>返回空数组 = 该 def 没有可用分类（<c>thingCategories</c> 为 null，
        /// 或只挂在 <c>Root</c> 上）⇒ 面板把它放进「其他」。</para>
        ///
        /// <para><b>多分类</b>：原版筛选树会把一个 def 同时挂在它的每个 <c>thingCategories</c> 下
        /// （<c>ThingCategoryNodeDatabase.FinalizeInit</c> 逐个 append）。面板是「物品清单」不是筛选树，
        /// 同一件东西出现两次只会让玩家困惑 ⇒ 这里只取第一条能走通的路径。</para>
        /// </summary>
        public static ThingCategoryDef[] PathOf(ThingDef def)
        {
            if (def == null) return EmptyPath;
            ThingCategoryDef[] cached;
            if (pathCache.TryGetValue(def, out cached)) return cached;

            ThingCategoryDef[] path = ComputePath(def);
            pathCache[def] = path;
            return path;
        }

        /// <summary>
        /// 同一层分类的排序键 = 原版树的前序遍历序号。
        /// <para>为什么不用 defName 字典序：原版把 <c>Foods</c> / <c>Manufactured</c> / <c>ResourcesRaw</c> /
        /// <c>Items</c> / <c>Weapons</c> / <c>Apparel</c> / … 按「资源读数优先、然后常用度」排在 XML 里，
        /// 玩家在原版筛选窗口里看到的顺序就是它；按字母排会把「武器」丢到最前面。</para>
        /// <para>树外分类（<c>parent == null</c> 的孤儿根，例如原版 <c>BookEffects</c>、mod 自建根）
        /// 不在节点表里 ⇒ 返回 <see cref="int.MaxValue"/>，调用方再用 label 兜底排序。</para>
        /// </summary>
        public static int OrderOf(ThingCategoryDef cat)
        {
            if (cat == null) return int.MaxValue;
            EnsureOrderIndex();

            int idx;
            if (orderIndex != null && orderIndex.TryGetValue(cat, out idx)) return idx;
            return int.MaxValue;
        }

        // ===================================================================
        // 内部
        // ===================================================================

        private static ThingCategoryDef[] ComputePath(ThingDef def)
        {
            List<ThingCategoryDef> cats = def.thingCategories;
            if (cats == null || cats.Count == 0) return EmptyPath;

            ThingCategoryDef root = ThingCategoryDefOf.Root;

            for (int i = 0; i < cats.Count; i++)
            {
                ThingCategoryDef leaf = cats[i];
                if (leaf == null) continue;

                List<ThingCategoryDef> chain = new List<ThingCategoryDef>();
                for (ThingCategoryDef c = leaf; c != null && c != root; c = c.parent)
                {
                    chain.Add(c);
                    // 防御：mod 把分类 parent 写成环（A→B→A）时不要死循环。
                    if (chain.Count > 64) break;
                }

                if (chain.Count == 0) continue;   // 只挂在 Root 上 ⇒ 算「其他」
                chain.Reverse();                  // 变成 顶层 → 叶子
                return chain.ToArray();
            }
            return EmptyPath;
        }

        private static void EnsureOrderIndex()
        {
            List<TreeNode_ThingCategory> nodes = ThingCategoryNodeDatabase.allThingCategoryNodes;

            // 已经按当前这张表建过索引 ⇒ 直接用（开发者「重载 def」会把 nodes 换成新表）
            // 注意：节点库还没 FinalizeInit 时表可能非 null 但为空 —— 不能把这个空结果缓存成永久答案。
            bool stale = orderIndex == null || !ReferenceEquals(indexedFrom, nodes)
                         || (orderIndex.Count == 0 && nodes != null && nodes.Count > 0);
            if (!stale) return;

            orderIndex = new Dictionary<ThingCategoryDef, int>();
            indexedFrom = nodes;

            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    TreeNode_ThingCategory n = nodes[i];
                    if (n == null || n.catDef == null) continue;
                    if (!orderIndex.ContainsKey(n.catDef)) orderIndex[n.catDef] = i;
                }
                return;
            }

            // 兜底（节点库还没 FinalizeInit 的极端情况）：自己前序遍历 Root 子树
            ThingCategoryDef root = ThingCategoryDefOf.Root;
            if (root == null) return;
            int counter = 0;
            WalkRoot(root, ref counter);
        }

        private static void WalkRoot(ThingCategoryDef cat, ref int counter)
        {
            if (cat == null || orderIndex == null) return;
            if (!orderIndex.ContainsKey(cat)) orderIndex[cat] = counter;
            counter++;
            if (cat.childCategories == null) return;
            for (int i = 0; i < cat.childCategories.Count; i++)
                WalkRoot(cat.childCategories[i], ref counter);
        }
    }
}

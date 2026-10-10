using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using Verse;

namespace DigitalStorage.Compatibility
{
    /// <summary>
    /// 兼容层的**惰性替身**：只为了让核心出现在 <c>haulDestinationManager.AllGroups</c>，
    /// 从而被 Phinix / 红包 之类"遍历 SlotGroup → HeldThings"的第三方扫描器看见。
    ///
    /// <para><b>为什么必须是替身，而不是让核心自己实现 <c>ISlotGroupParent</c></b>
    /// （2026-10-02 实测事故，血账）：核心一旦自己实现该接口，原版**容器腿**的第一道判断
    /// 就会把它当成格子型储存跳过：</para>
    /// <code>
    /// // StoreUtility.cs:252
    /// if (haulDestination2 is ISlotGroupParent || (haulDestination2 is Building_Grave &amp;&amp; !t.CanBeBuried()) || !haulDestination2.HaulDestinationEnabled)
    ///     continue;
    /// </code>
    /// <para>连带 <c>HaulAIUtility.cs:129</c> 与 <c>Pawn_JobTracker.cs:710/733</c> 都会把它当
    /// **格子目的地**去建 <c>HaulToCell</c> 作业（而它是零格子）。症状：自动收纳停摆、
    /// pawn 不往核心搬货、右键物品报"无可用存储区"。要修就得 patch 至少三处 IL ——
    /// 正是本项目一直避免的形态。</para>
    ///
    /// <para><b>替身的做法</b>：核心**保持纯容器**（只实现 <c>IHaulDestination</c>），
    /// 替身实现 <c>ISlotGroupParent</c> 但
    /// <list type="bullet">
    /// <item><c>HaulDestinationEnabled =&gt; false</c> ⇒ 容器腿 <c>:252</c> 跳过</item>
    /// <item><c>Accepts =&gt; false</c> ⇒ 即便走到 <c>:262</c> 也跳过</item>
    /// <item><c>AllSlotCellsList() =&gt; 空表</c> ⇒ 格子腿永不选中它</item>
    /// </list>
    /// ⇒ 对原版搬运/收货管线**完全透明**，只贡献一个"零格子 SlotGroup"给
    /// <c>AllGroups</c>，内容物由 <c>Patch_SlotGroup_HeldThings</c> 追加进枚举。</para>
    ///
    /// <para>注册/注销由 <c>Building_StorageCore</c> 在 <c>SpawnSetup</c>/<c>DeSpawn</c> 里做
    /// （<c>HaulDestinationManager.AddHaulDestination</c>/<c>RemoveHaulDestination</c> 是 public，
    /// 且内部会对 <c>ISlotGroupParent</c> 调用 <c>GetSlotGroup()</c> 并加进 <c>allGroupsInOrder</c>）。</para>
    /// </summary>
    internal class CoreSlotGroupAdapter : ISlotGroupParent
    {
        private static readonly List<IntVec3> NoCells = new List<IntVec3>();

        private readonly Building_StorageCore core;
        private SlotGroup slotGroup;

        public CoreSlotGroupAdapter(Building_StorageCore core)
        {
            this.core = core;
        }

        public Building_StorageCore Core => core;

        // ===== IHaulDestination：故意全惰性（见类注释）=====

        public bool HaulDestinationEnabled => false;

        public bool Accepts(Thing t) => false;

        public IntVec3 Position => core.PositionHeld;

        public Map Map => core.Map;

        // ===== IStoreSettingsParent：借核心的（只被排序/优先级显示读）=====

        public StorageSettings GetStoreSettings() => core.GetStoreSettings();

        public StorageSettings GetParentStoreSettings() => core.GetParentStoreSettings();

        public void Notify_SettingsChanged() { }

        public bool StorageTabVisible => false;

        // ===== ISlotGroupParent =====

        public bool IgnoreStoredThingsBeauty => true;

        public string GroupingLabel => core.Label;

        public int GroupingOrder => 0;

        public IEnumerable<IntVec3> AllSlotCells() => NoCells;

        public List<IntVec3> AllSlotCellsList() => NoCells; // 零格子：不占任何格子，格子腿永不选中

        public void Notify_ReceivedThing(Thing newItem) { }

        public void Notify_LostThing(Thing newItem) { }

        public string SlotYielderLabel() => core.Label;

        public SlotGroup GetSlotGroup()
        {
            if (slotGroup == null) slotGroup = new SlotGroup(this);
            return slotGroup;
        }
    }
}

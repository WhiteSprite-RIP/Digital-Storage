using System;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.Core
{
    public sealed class CoreStorageContainer : ThingOwner<Thing>
    {
        public CoreStorageContainer(IThingHolder owner) : base(owner)
        {
            dontTickContents = true;
        }

        private void UpdateCapacity()
        {
            // 旧档超额库存保留, 但不能再新增堆
            maxStacks = Math.Max(Count, ((Building_StorageCore)Owner).maxStacks);
        }

        public override int GetCountCanAccept(Thing item, bool canMergeWithExistingStacks = true)
        {
            UpdateCapacity();
            return base.GetCountCanAccept(item, canMergeWithExistingStacks);
        }

        /// <summary>
        /// 插入漏斗上的**玩家规则**（筛选 / 入库开关 / 电力）。
        ///
        /// <para>容量之外还要管这三样，是因为 <c>IHaulDestination.Accepts</c> 只在**选目的地**时被问
        /// （<c>StoreUtility.TryFindBestBetterStorageFor</c>）；真正的插入走 <c>ThingOwner.TryAdd</c>：
        /// 原版 <c>Toils_Haul.DepositHauledThingInContainer</c> 如此，
        /// "先组批、再一件件塞"的搬运 mod（Pick Up And Haul 的 <c>JobDriver_UnloadYourHauledInventory</c>）
        /// 更是如此 —— 它组批时只问 <c>CanAcceptAnyOf</c>（= <c>GetCountCanAccept &gt; 0</c>，只看容量）。
        /// 不过滤的话，玩家明确不收的东西会从这条路进核心。</para>
        ///
        /// <para><c>Spawned == false</c> 一律放行：读档 / 迁移期电力与开关都还没定论，
        /// 那时走 <see cref="AddLoaded"/> 与 <c>Building_StorageCore.AddRestored</c>。</para>
        /// </summary>
        private bool OwnerAllowsInsert(Thing item)
        {
            Building_StorageCore core = Owner as Building_StorageCore;
            if (core == null || !core.Spawned) return true;
            return core.AllowsInsert(item);
        }

        public override bool TryAdd(Thing item, bool canMergeWithExistingStacks = true)
        {
            // 整堆接口必须全部可收, 避免失败前已部分合堆
            if (item == null || GetCountCanAccept(item, canMergeWithExistingStacks) < item.stackCount) return false;
            if (!OwnerAllowsInsert(item)) return false;
            return base.TryAdd(item, canMergeWithExistingStacks);
        }

        public override int TryAdd(Thing item, int count, bool canMergeWithExistingStacks = true)
        {
            if (item == null || count <= 0) return 0;
            if (!OwnerAllowsInsert(item)) return 0;
            int accepted = Math.Min(count, GetCountCanAccept(item, canMergeWithExistingStacks));
            return base.TryAdd(item, accepted, canMergeWithExistingStacks);
        }

        internal void AddLoaded(Thing item)
        {
            item.holdingOwner = this;
            InnerListForReading.Add(item);
        }
    }
}

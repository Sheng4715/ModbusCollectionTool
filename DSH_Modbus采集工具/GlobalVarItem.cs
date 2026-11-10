using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DSH_Modbus采集工具.Drivers;
using DSH_Modbus采集工具.Modeling;

namespace DSH_Modbus采集工具
{
    /// <summary>
    /// 全局变量列表里的一行 —— <see cref="GlobalVariable"/> 的界面包装。
    /// <para>
    /// 与自由监控的 <c>MonitorItem</c> 同一思路：模型要能 JSON 往返（故不含 INPC 与界面态），
    /// 界面需要「解析错误」这类随校验变化、要通知绑定的属性。
    /// </para>
    /// </summary>
    public sealed class GlobalVarItem : INotifyPropertyChanged
    {
        private string _resolveError = string.Empty;
        private int _id;
        private bool _isSelected;

        public GlobalVarItem(GlobalVariable model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public GlobalVariable Model { get; }

        /// <summary>序号（界面左侧显示，1 起）</summary>
        /// <remarks>
        /// ⚠️ 必须通知：拖动排序后 <c>Reindex()</c> 会重排一批行的序号，
        /// 不通知的话画面上的 # 会停在旧值，与真实顺序对不上。
        /// </remarks>
        public int Id
        {
            get => _id;
            set
            {
                if (_id == value)
                {
                    return;
                }

                _id = value;
                Raise();
            }
        }

        public string Name
        {
            get => Model.Name;
            set
            {
                if (Model.Name == value)
                {
                    return;
                }

                Model.Name = value;
                Raise();
            }
        }


        /// <summary>映射地址原文（元件名，如 <c>HD100</c>）；空串 = 内存变量</summary>
        public string Address
        {
            get => Model.Address;
            set
            {
                if (Model.Address == value)
                {
                    return;
                }

                Model.Address = value ?? string.Empty;
                Raise();
            }
        }

        public string Comment
        {
            get => Model.Comment;
            set
            {
                if (Model.Comment == value)
                {
                    return;
                }

                Model.Comment = value ?? string.Empty;
                Raise();
            }
        }

        /// <summary>解析 / 校验失败原因（红色）。空 = 正常。</summary>
        public string ResolveError
        {
            get => _resolveError;
            set
            {
                if (_resolveError == value)
                {
                    return;
                }

                _resolveError = value ?? string.Empty;
                Raise();
            }
        }

        /// <summary>
        /// 是否是当前选中行（只影响底色，纯界面态，不落盘）。
        /// <para>表头的 ↑↓ 排序作用于选中行，故需要一个可见的「当前行」。</para>
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                Raise();
            }
        }


        /// <summary>
        /// 类型显示名（如 <c>STRING[9]</c>）—— <c>MonitorTypeCombo</c> 的闭合态模板
        /// 直接绑数据上下文的这个属性，缺了它下拉框会显示成空白。
        /// </summary>
        public string TypeDisplayName => Model.TypeDisplayName;


        /// <summary>类型变更后通知下拉框闭合态重读显示名。</summary>
        public void RaiseTypeChanged() => Raise(nameof(TypeDisplayName));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 全局变量集合的界面侧编排：增删改、校验、落盘。
    /// <para>底层数据与规则在 <see cref="GlobalVariableSet"/>，本类只加界面所需的包装。</para>
    /// </summary>
    public sealed class GlobalVarItemSet
    {
        private readonly ObservableCollection<GlobalVarItem> _items = new();
        private readonly GlobalVariableSet _set = new();

        public ObservableCollection<GlobalVarItem> Items => _items;

        public CommDriver? Driver => _set.Driver;

        public void SetDriver(CommDriver? driver)
        {
            _set.SetDriver(driver);
            Reindex();
        }

        /// <summary>载入定义并重置所有值为初始值</summary>
        public void Load()
        {
            _set.Load();
            Rebuild();
        }


        /// <summary>新增一条变量；失败返回 null 并给出原因</summary>
        public GlobalVarItem? Add(string name, out string error)
        {
            var model = new GlobalVariable
            {
                Name = name,
                Type = DataType.Int,
                Format = GlobalVariable.DefaultFormatFor(DataType.Int),
                Address = string.Empty,
                Comment = string.Empty,
            };

            GlobalVariable? added = _set.Add(model, out error);
            if (added == null)
            {
                return null;
            }

            var item = new GlobalVarItem(added);
            _items.Add(item);
            Reindex();
            Save();
            return item;
        }

        public void Remove(GlobalVarItem item)
        {
            _items.Remove(item);
            _set.Remove(item.Model);
            Reindex();
            Save();
        }

        /// <summary>
        /// 拖动排序：把 <paramref name="item"/> 放到 <paramref name="newIndex"/>。
        /// <para>
        /// ⚠️ 必须**同时**改两边：界面的 <c>_items</c>（ObservableCollection，决定画面顺序）
        /// 与模型的 <c>_set</c>（List，决定落盘顺序）。只改一边的话，画面动了但重启就变回原样；
        /// 或者存对了但界面不刷新。
        /// </para>
        /// </summary>
        public void Move(GlobalVarItem item, int newIndex)
        {
            int oldIndex = _items.IndexOf(item);
            if (oldIndex < 0)
            {
                return;
            }

            if (newIndex < 0)
            {
                newIndex = 0;
            }

            if (newIndex > _items.Count - 1)
            {
                newIndex = _items.Count - 1;
            }

            if (newIndex == oldIndex)
            {
                return;
            }

            _items.Move(oldIndex, newIndex);

            if (_set.Move(item.Model, newIndex))
            {
                Reindex();
                Save();
            }
        }

        public void Clear()
        {
            _items.Clear();
            _set.Clear();
            Save();
        }

        /// <summary>
        /// 按名称查找变量定义（大小写敏感 —— C 标识符语义）。
        /// <para>
        /// 自由监控用它判断「输入框里打的是不是一个已定义的变量」，
        /// 故返回的是**模型**而非界面包装：那边只需要类型 / 地址这些定义字段。
        /// </para>
        /// </summary>
        public GlobalVariable? FindByName(string name) => _set.FindByName(name);

        /// <summary>
        /// 查找已占用该地址的**另一条**变量（用户定案 2026-11-05：全局变量不可重复映射同一地址）。
        /// <para>转发给 <see cref="GlobalVariableSet.FindByAddress"/>，比较的是归一化后的地址。</para>
        /// </summary>
        /// <param name="address">已归一的地址（调用方应在 <c>ResolveOnly</c> 之后传）</param>
        /// <param name="exclude">排除自己（改自己的地址时不算冲突）</param>
        public GlobalVariable? FindByAddress(string address, GlobalVariable? exclude) =>
            _set.FindByAddress(address, exclude);

        /// <summary>
        /// 查找与之**占用区间重叠**的另一条变量（2026-11-09，数组引入后必需）。
        /// <para>数组占一段连续单元，只比起点会漏判「D11 落在 D10 起的 ARRAY&lt;INT&gt;[3] 里」。</para>
        /// </summary>
        public GlobalVariable? FindByAddressOverlap(GlobalVariable candidate) =>
            _set.FindByAddressOverlap(candidate);

        /// <summary>改名（含合法性与重名检查）</summary>
        public bool Rename(GlobalVarItem item, string newName, out string error)
        {
            bool ok = _set.Rename(item.Model, newName, out error);
            if (ok)
            {
                item.Name = item.Model.Name;
            }

            return ok;
        }

        /// <summary>重新解析一条（改类型 / 改地址后调用）</summary>
        public void ResolveOnly(GlobalVarItem item) => ResolveOnly(item, out _);

        /// <summary>
        /// 同上，并返回失败**类别** —— 界面据此决定弹哪句提示。
        /// </summary>
        public bool ResolveOnly(GlobalVarItem item, out AddressErrorKind kind)
        {
            bool ok = _set.ResolveOnly(item.Model, out kind);
            item.ResolveError = item.Model.ResolveError;
            return ok;
        }

        /// <summary>重新解析全部（换设备类型后调用）</summary>
        public void ReResolveAll()
        {
            _set.ReResolveAll();
            foreach (GlobalVarItem item in _items)
            {
                item.ResolveError = item.Model.ResolveError;
            }
        }

        /// <summary>
        /// 清空**所有**变量的映射地址（切换设备类型前用，2026-11-08 用户定案）。
        /// <para>
        /// 变量本身保留（名称 / 类型 / 注释都不动），只把 <c>Address</c> 置空、
        /// 连带把解析状态清干净 —— 否则会留下「地址空了、ResolvedAddress 还指着旧品牌」的鬼状态。
        /// </para>
        /// <returns>实际清掉了几个（0 = 本来就没有映射地址）。</returns>
        /// </summary>
        public int ClearAllAddresses()
        {
            int cleared = 0;

            foreach (GlobalVariable v in _set.Items)
            {
                if (!v.HasAddress)
                {
                    continue;
                }

                v.Address = string.Empty;

                // 解析产物必须一起清：它们指向的是**旧品牌**的绝对地址
                v.ResolvedAddress = -1;
                v.ResolveError = string.Empty;
                v.IsReadOnly = false;
                cleared++;
            }

            if (cleared > 0)
            {
                // 界面包装的 ResolveError 也刷一遍，别留着上次的红字
                foreach (GlobalVarItem item in _items)
                {
                    item.ResolveError = string.Empty;
                }

                Save();
            }

            return cleared;
        }

        /// <summary>落盘（只写定义）</summary>
        public bool Save() => _set.Save();

        private void Rebuild()
        {
            _items.Clear();
            foreach (GlobalVariable v in _set.Items)
            {
                _items.Add(new GlobalVarItem(v));
            }

            Reindex();
        }

        private void Reindex()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Id = i + 1;
            }
        }
    }

}

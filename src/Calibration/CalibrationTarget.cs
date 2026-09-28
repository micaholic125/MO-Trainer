using System;
using System.Collections.Generic;
using System.Globalization;
using MoTrainer.Core;

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 校准所需的目标环境抽象。
    /// 把"从哪块内存里找"与向导逻辑解耦，自检程序即可用合成内存驱动整套校准流程。
    /// </summary>
    public interface ICalibrationTarget
    {
        bool IsReady { get; }

        MemoryIO Memory { get; }

        uint ModuleBase { get; }
        uint ModuleSize { get; }

        /// <summary>数值搜索的区域：私有可写页，噪声最低。</summary>
        List<MemoryRegion> HeapRegions { get; }

        /// <summary>指针回溯的区域：所有可写页（堆 + 模块 .data）。</summary>
        List<MemoryRegion> PointerRegions { get; }

        string DescribeEnvironment();
    }

    /// <summary>真实游戏进程的校准目标。</summary>
    public sealed class LiveCalibrationTarget : ICalibrationTarget
    {
        private readonly ProcessAttach _attach;
        private readonly MemoryIO _memory;

        private List<MemoryRegion> _heapRegions;
        private List<MemoryRegion> _pointerRegions;

        public LiveCalibrationTarget(ProcessAttach attach, MemoryIO memory)
        {
            if (attach == null) { throw new ArgumentNullException("attach"); }
            if (memory == null) { throw new ArgumentNullException("memory"); }
            _attach = attach;
            _memory = memory;
        }

        public bool IsReady { get { return _attach.IsReady; } }
        public MemoryIO Memory { get { return _memory; } }
        public uint ModuleBase { get { return _attach.ModuleBase; } }
        public uint ModuleSize { get { return _attach.ModuleSize; } }

        public List<MemoryRegion> HeapRegions
        {
            get
            {
                if (_heapRegions == null) { Refresh(); }
                return _heapRegions;
            }
        }

        public List<MemoryRegion> PointerRegions
        {
            get
            {
                if (_pointerRegions == null) { Refresh(); }
                return _pointerRegions;
            }
        }

        /// <summary>强制重新枚举区域（重新附加游戏后调用）。</summary>
        public void Refresh()
        {
            _heapRegions = null;
            _pointerRegions = null;
            if (!IsReady) { return; }

            List<MemoryRegion> all = _memory.EnumRegions(0x00010000ul, Win32Api.MaxUserAddress32, true, false, 0);
            _heapRegions = RegionSelector.ForHeapData().Apply(all);

            RegionSelector pointerSelector = new RegionSelector();
            pointerSelector.IncludePrivate = true;
            pointerSelector.IncludeImage = true;   // 模块的 .data 里放着全局指针
            pointerSelector.IncludeMapped = false;
            pointerSelector.RequireWritable = true;
            pointerSelector.RequireReadable = true;
            pointerSelector.MinimumSize = 0x1000;
            _pointerRegions = pointerSelector.Apply(all);
        }

        public string DescribeEnvironment()
        {
            if (!IsReady) { return "未连接游戏进程"; }

            ulong heapBytes = 0;
            List<MemoryRegion> heap = HeapRegions;
            for (int i = 0; i < heap.Count; i++) { heapBytes += heap[i].Size; }

            ulong pointerBytes = 0;
            List<MemoryRegion> pointer = PointerRegions;
            for (int i = 0; i < pointer.Count; i++) { pointerBytes += pointer[i].Size; }

            return string.Format(CultureInfo.InvariantCulture,
                "pid={0} 模块={1} 基址=0x{2:X8} 大小=0x{3:X}\r\n"
                + "数值搜索区：{4} 个区域 / {5:N0} MB\r\n"
                + "指针回溯区：{6} 个区域 / {7:N0} MB",
                _attach.ProcessId, _attach.ModuleName, _attach.ModuleBase, _attach.ModuleSize,
                heap.Count, heapBytes / 1048576.0,
                pointer.Count, pointerBytes / 1048576.0);
        }
    }
}

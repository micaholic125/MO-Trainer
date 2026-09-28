using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using MoTrainer.Util;

namespace MoTrainer.Core
{
    /// <summary>一段已提交的内存区域。</summary>
    public sealed class MemoryRegion
    {
        public ulong Base;
        public ulong Size;
        public uint Protect;
        public uint Type;
        public uint State;

        public ulong End
        {
            get
            {
                ulong end = Base + Size;
                return end < Base ? ulong.MaxValue : end;
            }
        }

        public bool Writable { get { return Win32Api.IsWritableProtect(Protect); } }

        /// <summary>页是否可读（排除 NOACCESS 与 GUARD）。</summary>
        public bool Readable
        {
            get
            {
                if ((Protect & Win32Api.PAGE_GUARD) != 0) { return false; }
                return (Protect & 0xFF) != Win32Api.PAGE_NOACCESS;
            }
        }

        public string TypeText
        {
            get
            {
                if (Type == Win32Api.MEM_IMAGE) { return "image"; }
                if (Type == Win32Api.MEM_MAPPED) { return "mapped"; }
                if (Type == Win32Api.MEM_PRIVATE) { return "private"; }
                return "0x" + Type.ToString("X", CultureInfo.InvariantCulture);
            }
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "0x{0:X8}-0x{1:X8} size=0x{2:X} protect=0x{3:X2} {4}",
                Base, End, Size, Protect, TypeText);
        }
    }

    /// <summary>
    /// 内存读写内核。业务代码访问进程内存的**唯一入口**。
    /// 提供重试、可写页校验、区域枚举；失败只记录日志，不抛异常。
    /// </summary>
    public sealed class MemoryIO
    {
        private const int MaxRetry = 2;
        private const int RegionEnumHardLimit = 400000;

        private readonly IProcessHandle _target;
        private int _consecutiveFailures;
        private DateTime _lastFailureLog = DateTime.MinValue;

        public MemoryIO(IProcessHandle target)
        {
            if (target == null) { throw new ArgumentNullException("target"); }
            _target = target;
        }

        public IProcessHandle Target { get { return _target; } }

        public bool IsReady { get { return _target.IsReady; } }

        /// <summary>连续失败次数。上层可用它判断"连接已失效"。</summary>
        public int ConsecutiveFailures { get { return _consecutiveFailures; } }

        // ============================================================ 读

        public bool ReadRaw(IntPtr address, byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) { return false; }
            if (offset < 0 || offset + count > buffer.Length) { return false; }

            IntPtr handle = _target.Handle;
            if (handle == IntPtr.Zero) { return false; }

            IntPtr read;
            for (int attempt = 0; attempt < MaxRetry; attempt++)
            {
                bool ok;
                if (offset == 0)
                {
                    ok = Win32Api.ReadProcessMemory(handle, address, buffer, (IntPtr)count, out read)
                        && read.ToInt64() == count;
                }
                else
                {
                    byte[] tmp = new byte[count];
                    ok = Win32Api.ReadProcessMemory(handle, address, tmp, (IntPtr)count, out read)
                        && read.ToInt64() == count;
                    if (ok) { Buffer.BlockCopy(tmp, 0, buffer, offset, count); }
                }

                if (ok)
                {
                    _consecutiveFailures = 0;
                    return true;
                }
                if (attempt == 0) { continue; }

                int err = Win32Api.LastErrorCode();
                NoteFailure("ReadProcessMemory", (ulong)address.ToInt64(), count, err);
            }
            return false;
        }

        public byte[] ReadRawBytes(IntPtr address, int count)
        {
            if (count <= 0) { return null; }
            byte[] buffer = new byte[count];
            return ReadRaw(address, buffer, 0, count) ? buffer : null;
        }

        public bool Read(uint address, byte[] buffer, int offset, int count)
        {
            return ReadRaw(ToIntPtr(address), buffer, offset, count);
        }

        public byte[] ReadBytes(uint address, int count)
        {
            return ReadRawBytes(ToIntPtr(address), count);
        }

        /// <summary>
        /// 安静读取：失败时不写日志、不重试。
        ///
        /// 用于**试探性**读取 —— 典型场景是在对象里挑出"哪条指针指向自己的阵营对象"，
        /// 这需要把对象里所有像指针的字段挨个试一遍，按设计就会大量失败。
        /// 走普通读取的话，每条野指针都会在日志里刷一条失败记录；
        /// 实测把真正的错误原因整个淹掉了（日志里十几条 ReadProcessMemory 失败，
        /// 而唯一有用的那一行夹在中间）。失败在这里是**预期结果**，不是异常。
        /// </summary>
        public byte[] ReadBytesQuiet(uint address, int count)
        {
            if (count <= 0) { return null; }

            IntPtr handle = _target.Handle;
            if (handle == IntPtr.Zero) { return null; }

            byte[] buffer = new byte[count];
            IntPtr read;
            bool ok = Win32Api.ReadProcessMemory(handle, ToIntPtr(address), buffer, (IntPtr)count, out read);
            return ok && read.ToInt64() == count ? buffer : null;
        }

        public bool ReadUInt32Raw(IntPtr address, out uint value)
        {
            value = 0;
            byte[] buffer = ReadRawBytes(address, 4);
            if (buffer == null) { return false; }
            value = HexUtil.ReadUInt32(buffer, 0);
            return true;
        }

        public bool ReadUInt32(uint address, out uint value)
        {
            return ReadUInt32Raw(ToIntPtr(address), out value);
        }

        public uint ReadUInt32Or(uint address, uint fallback)
        {
            uint value;
            return ReadUInt32(address, out value) ? value : fallback;
        }

        public bool ReadInt32(uint address, out int value)
        {
            value = 0;
            uint raw;
            if (!ReadUInt32(address, out raw)) { return false; }
            value = unchecked((int)raw);
            return true;
        }

        public bool ReadUInt16(uint address, out ushort value)
        {
            value = 0;
            byte[] buffer = ReadBytes(address, 2);
            if (buffer == null) { return false; }
            value = HexUtil.ReadUInt16(buffer, 0);
            return true;
        }

        public bool ReadSingle(uint address, out float value)
        {
            value = 0f;
            byte[] buffer = ReadBytes(address, 4);
            if (buffer == null) { return false; }
            value = BitConverter.ToSingle(buffer, 0);
            return true;
        }

        /// <summary>读取 32 位指针。失败返回 0（调用方必须把 0 视为无效地址）。</summary>
        public uint ReadPointer(uint address)
        {
            return ReadUInt32Or(address, 0);
        }

        // ============================================================ 写

        public bool WriteRaw(IntPtr address, byte[] buffer, int offset, int count)
        {
            if (buffer == null || count <= 0) { return false; }
            if (offset < 0 || offset + count > buffer.Length) { return false; }

            IntPtr handle = _target.Handle;
            if (handle == IntPtr.Zero) { return false; }

            IntPtr written;
            for (int attempt = 0; attempt < MaxRetry; attempt++)
            {
                bool ok;
                if (offset == 0)
                {
                    ok = Win32Api.WriteProcessMemory(handle, address, buffer, (IntPtr)count, out written)
                        && written.ToInt64() == count;
                }
                else
                {
                    byte[] tmp = new byte[count];
                    Buffer.BlockCopy(buffer, offset, tmp, 0, count);
                    ok = Win32Api.WriteProcessMemory(handle, address, tmp, (IntPtr)count, out written)
                        && written.ToInt64() == count;
                }

                if (ok)
                {
                    _consecutiveFailures = 0;
                    return true;
                }
                if (attempt == 0) { continue; }

                int err = Win32Api.LastErrorCode();
                NoteFailure("WriteProcessMemory", (ulong)address.ToInt64(), count, err);
            }
            return false;
        }

        public bool Write(uint address, byte[] buffer, int offset, int count)
        {
            return WriteRaw(ToIntPtr(address), buffer, offset, count);
        }

        public bool WriteUInt32Raw(IntPtr address, uint value)
        {
            byte[] buffer = new byte[4];
            HexUtil.WriteUInt32(buffer, 0, value);
            return WriteRaw(address, buffer, 0, 4);
        }

        public bool WriteUInt32(uint address, uint value)
        {
            return WriteUInt32Raw(ToIntPtr(address), value);
        }

        public bool WriteInt32(uint address, int value)
        {
            return WriteUInt32(address, unchecked((uint)value));
        }

        public bool WriteUInt16(uint address, ushort value)
        {
            byte[] buffer = new byte[2];
            HexUtil.WriteUInt16(buffer, 0, value);
            return Write(address, buffer, 0, 2);
        }

        public bool WriteSingle(uint address, float value)
        {
            return Write(address, BitConverter.GetBytes(value), 0, 4);
        }

        /// <summary>
        /// 写入后立即回读校验。用于血量、生产进度等"静默无效"代价高的字段。
        /// </summary>
        public bool WriteUInt32Verified(uint address, uint value)
        {
            if (!WriteUInt32(address, value)) { return false; }
            uint readBack;
            if (!ReadUInt32(address, out readBack)) { return false; }
            if (readBack == value) { return true; }

            Logger.Warn("MemoryIO", string.Format(CultureInfo.InvariantCulture,
                "写入校验不一致 @0x{0:X8}：期望 0x{1:X8}，读回 0x{2:X8}，重试一次",
                address, value, readBack));

            if (!WriteUInt32(address, value)) { return false; }
            return ReadUInt32(address, out readBack) && readBack == value;
        }

        // ============================================================ 区域

        /// <summary>查询某地址所属内存区域。不可用时返回 null。</summary>
        public MemoryRegion Describe(ulong address)
        {
            IntPtr handle = _target.Handle;
            if (handle == IntPtr.Zero) { return null; }

            MEMORY_BASIC_INFORMATION info;
            IntPtr result = Win32Api.VirtualQueryEx(
                handle, (IntPtr)(long)address, out info, (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)));
            if (result == IntPtr.Zero) { return null; }

            MemoryRegion region = new MemoryRegion();
            region.Base = (ulong)info.BaseAddress.ToInt64();
            region.Size = info.RegionSize.ToUInt64();
            region.Protect = info.Protect;
            region.Type = info.Type;
            region.State = info.State;
            return region;
        }

        public bool IsWritable(ulong address)
        {
            MemoryRegion region = Describe(address);
            if (region == null) { return false; }
            if (region.State != Win32Api.MEM_COMMIT) { return false; }
            return region.Writable;
        }

        /// <summary>枚举 [start, end) 范围内的内存区域。</summary>
        public List<MemoryRegion> EnumRegions(ulong start, ulong end, bool committedOnly, bool writableOnly, int maxRegions)
        {
            List<MemoryRegion> list = new List<MemoryRegion>();
            IntPtr handle = _target.Handle;
            if (handle == IntPtr.Zero) { return list; }
            if (end <= start) { return list; }

            int structSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
            ulong cursor = start;
            int guard = 0;

            while (cursor < end && guard < RegionEnumHardLimit)
            {
                guard++;
                MEMORY_BASIC_INFORMATION info;
                IntPtr result = Win32Api.VirtualQueryEx(handle, (IntPtr)(long)cursor, out info, (IntPtr)structSize);
                if (result == IntPtr.Zero) { break; }

                ulong baseAddr = (ulong)info.BaseAddress.ToInt64();
                ulong size = info.RegionSize.ToUInt64();
                if (size == 0) { break; }

                MemoryRegion region = new MemoryRegion();
                region.Base = baseAddr;
                region.Size = size;
                region.Protect = info.Protect;
                region.Type = info.Type;
                region.State = info.State;

                bool accept = true;
                if (committedOnly && region.State != Win32Api.MEM_COMMIT) { accept = false; }
                if (accept && writableOnly && !region.Writable) { accept = false; }
                if (accept) { list.Add(region); }

                if (maxRegions > 0 && list.Count >= maxRegions) { break; }

                ulong next = baseAddr + size;
                if (next <= cursor) { break; }
                cursor = next;
            }
            return list;
        }

        // ============================================================ 内部

        private static IntPtr ToIntPtr(uint address)
        {
            return new IntPtr((long)address);
        }

        private void NoteFailure(string api, ulong address, int count, int win32Error)
        {
            _consecutiveFailures++;

            // 高频调用下失败会刷屏，故按时间节流，最多每 3 秒记一条
            DateTime now = DateTime.Now;
            if ((now - _lastFailureLog).TotalSeconds < 3.0) { return; }
            _lastFailureLog = now;

            Logger.Warn("MemoryIO", string.Format(CultureInfo.InvariantCulture,
                "{0} 失败 @0x{1:X8} len={2}，{3}，连续失败 {4} 次",
                api, address, count, Win32Api.FormatWin32Error(win32Error), _consecutiveFailures));
        }
    }
}

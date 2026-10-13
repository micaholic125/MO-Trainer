// One-off dev helper (read-only): look inside the player's house object and find
// the field that ticks while INFANTRY / VEHICLES are being produced.
//
// Why this shape: the proven "instant build" path is
//     house + 0x53BC  ->  the object currently under construction  ->  +0x24 ticks
// That works for buildings. Infantry and vehicles have NO object yet while they
// are being built, so the ticking value must live somewhere else. The cheapest
// hypothesis to test first is that +0x53BC is one slot of a small array of
// production pointers -- in which case its siblings light up for the other
// categories and the whole thing is solved by reading one more slot.
//
// So: sample the house object (and everything it points at) and report what moves.
//
//   HousePeek resolve
//   HousePeek dump    <addr> <len>
//   HousePeek ptrs    <base> <lo> <hi>
//   HousePeek watch   <base> <lo> <hi> <seconds> <hz>
//   HousePeek map
//
// Read-only. Nothing is ever written to the game.
//
// `map` prints the current shroud state of the whole map, using the layout
// from the Ares team's YRpp headers (archived at
// <repo>\证据归档\2026-10-13-网络方向\YRpp\), verified offline against two
// independent samples on 2026-10-13 (the Ares crash dump and the spy-satellite
// before/after snapshots) -- see docs\07-全图视野交接说明.md section 10:
//     MapClass instance : 0x0087F7E8  (a global object in .data, NOT a pointer)
//       +0x13C          : Cells.Items    -> CellClass* array on the heap
//       +0xEC           : MapRect (left, top, right, bottom)
//     CellClass (one heap object per cell, size 0x148):
//       +0x24 X (short)   +0x26 Y (short)
//       +0x120 Shroudedness: -2(0xFE)=shrouded  -1(0xFF)=visible  0..48=fade anim
//       +0x121 Foggedness  (same value set)
//     index formula: idx = (Y << 9) + X   (MapClass::GetCellIndex)
// Every printed line of the map is guarded by two checks that random data
// cannot pass: the cell vtable must be inside the game module, and the cell's
// own X/Y must reproduce its slot index.  Anything failing is counted and
// skipped, never guessed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MoTools
{
    internal static class HousePeek
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const uint ModuleBase = 0x00400000;
        private const uint ModuleSize = 0x00793000;

        // profile.json: money chain = static root 0x683D4C (module-relative) + 0x30C.
        // Absolute root = 0x400000 + 0x683D4C = 0x00A83D4C.
        private const uint HouseRoot = 0x00A83D4C;
        private const uint MoneyOffset = 0x30C;

        private const int TargetBytes = 0x120;   // how much of each pointed-to object we watch
        private const int MaxTargets = 2000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        private static IntPtr _process = IntPtr.Zero;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0)
            {
                Console.WriteLine("usage: HousePeek resolve|dump|ptrs|watch ...");
                return 2;
            }

            Process[] found = Process.GetProcessesByName("gamemd");
            if (found.Length == 0)
            {
                Console.WriteLine("gamemd.exe is not running.");
                return 3;
            }
            int pid = found[0].Id;
            _process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (_process == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess failed, Win32=" + Marshal.GetLastWin32Error());
                return 4;
            }
            Console.WriteLine("gamemd.exe pid=" + pid);

            try
            {
                string cmd = args[0].ToLowerInvariant();
                if (cmd == "resolve") { return Resolve(); }
                if (cmd == "map") { return MapPrint(); }
                if (cmd == "dump")
                {
                    return Dump(ParseHex(args[1]), (int)ParseHex(args[2]));
                }
                if (cmd == "ptrs")
                {
                    return Ptrs(ParseHex(args[1]), ParseHex(args[2]), ParseHex(args[3]));
                }
                if (cmd == "watch")
                {
                    return Watch(ParseHex(args[1]), ParseHex(args[2]), ParseHex(args[3]),
                        double.Parse(args[4], CultureInfo.InvariantCulture),
                        double.Parse(args[5], CultureInfo.InvariantCulture));
                }
                if (cmd == "slots")
                {
                    return Slots(ParseHex(args[1]),
                        double.Parse(args[2], CultureInfo.InvariantCulture),
                        double.Parse(args[3], CultureInfo.InvariantCulture));
                }
                Console.WriteLine("unknown command: " + cmd);
                return 2;
            }
            finally
            {
                CloseHandle(_process);
            }
        }

        private static uint ParseHex(string text)
        {
            string s = text.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(2); }
            return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- reading

        /// <summary>Chunked read: a range that straddles an unreadable page returns what it can.</summary>
        private static bool TryRead(uint address, int length, out byte[] buffer)
        {
            buffer = new byte[length];
            bool any = false;
            int done = 0;
            while (done < length)
            {
                int chunk = length - done;
                if (chunk > 256) { chunk = 256; }
                byte[] temp = new byte[chunk];
                IntPtr got;
                if (ReadProcessMemory(_process, (IntPtr)(ulong)(address + (uint)done), temp, (IntPtr)chunk, out got)
                    && (long)got == chunk)
                {
                    Buffer.BlockCopy(temp, 0, buffer, done, chunk);
                    any = true;
                }
                else
                {
                    // leave zeros; caller decides what an all-zero page means
                }
                done += chunk;
            }
            return any;
        }

        private static uint ReadU32(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }

        private static bool PlausiblePointer(uint v)
        {
            return v >= 0x00010000u && v < 0x80000000u;
        }

        private static bool InModule(uint v)
        {
            return v >= ModuleBase && v < ModuleBase + ModuleSize;
        }

        /// <summary>
        /// Read an "identifier-style" string (GAPOWR / HARV / E1 ...) at an address.
        /// Same shape rule the project already uses in ProgressHunter.ScanTypeIds:
        /// 4..24 chars, alphanumeric plus _ . -, at least one uppercase letter.
        /// </summary>
        private static string ReadIdentifier(uint address, out string raw)
        {
            raw = "";
            if (address < 0x10000) { return ""; }
            byte[] buf;
            if (!TryRead(address, 24, out buf)) { return ""; }

            int length = 0;
            bool sawUpper = false;
            while (length < buf.Length && buf[length] != 0)
            {
                byte b = buf[length];
                bool ok = (b >= (byte)'0' && b <= (byte)'9')
                    || (b >= (byte)'A' && b <= (byte)'Z')
                    || (b >= (byte)'a' && b <= (byte)'z')
                    || b == (byte)'_' || b == (byte)'.' || b == (byte)'-';
                if (!ok) { return ""; }
                if (b >= (byte)'A' && b <= (byte)'Z') { sawUpper = true; }
                length++;
            }
            if (length < 4 || !sawUpper) { return ""; }

            StringBuilder text = new StringBuilder();
            for (int i = 0; i < length; i++) { text.Append((char)buf[i]); }
            string id = text.ToString();
            string upper = id.ToUpperInvariant();
            if (upper == "NORTH" || upper == "SOUTH" || upper == "EAST" || upper == "WEST") { return ""; }
            if (upper == "NORTHEAST" || upper == "NORTHWEST" || upper == "SOUTHEAST" || upper == "SOUTHWEST") { return ""; }
            if (upper == "NONE" || upper == "TRUE" || upper == "FALSE" || upper == "NULL") { return ""; }
            raw = id;
            return id;
        }

        /// <summary>
        /// Look for the object's TYPE NAME: walk every plausible pointer and see
        /// whether it lands on an identifier string. That is how a "what is this
        /// thing" question gets answered without reading any game docs.
        /// </summary>
        private static string GuessTypeName(uint address, int scanBytes, int maxDepth)
        {
            List<string> found = new List<string>();
            byte[] buf;
            if (!TryRead(address, scanBytes, out buf)) { return ""; }

            for (int off = 0; off + 4 <= scanBytes; off += 4)
            {
                uint p = ReadU32(buf, off);
                if (!PlausiblePointer(p)) { continue; }
                string raw;
                string id = ReadIdentifier(p, out raw);
                if (id.Length == 0) { continue; }
                if (!found.Contains(id)) { found.Add(id); }
                if (found.Count >= maxDepth) { break; }
            }
            if (found.Count == 0) { return ""; }
            return string.Join(", ", found.ToArray());
        }

        // ---------------------------------------------------------------- commands

        private static int Resolve()
        {
            byte[] head;
            if (!TryRead(HouseRoot, 4, out head))
            {
                Console.WriteLine("cannot read static root 0x" + HouseRoot.ToString("X8"));
                return 5;
            }
            uint house = ReadU32(head, 0);
            Console.WriteLine("static root 0x" + HouseRoot.ToString("X8") + " -> house = 0x" + house.ToString("X8"));
            if (!PlausiblePointer(house))
            {
                Console.WriteLine("  (that does not look like a pointer -- maybe no game in progress)");
                return 5;
            }

            byte[] money;
            if (TryRead(house + MoneyOffset, 4, out money))
            {
                Console.WriteLine("  money  (+0x" + MoneyOffset.ToString("X") + ") = "
                    + ReadU32(money, 0).ToString(CultureInfo.InvariantCulture));
            }
            byte[] vt;
            if (TryRead(house, 4, out vt))
            {
                Console.WriteLine("  vtable = 0x" + ReadU32(vt, 0).ToString("X8"));
            }
            return 0;
        }

        private static int Dump(uint address, int length)
        {
            byte[] buf;
            if (!TryRead(address, length, out buf))
            {
                Console.WriteLine("read failed at 0x" + address.ToString("X8"));
                return 5;
            }
            for (int row = 0; row < length; row += 16)
            {
                StringBuilder hex = new StringBuilder();
                StringBuilder txt = new StringBuilder();
                for (int i = 0; i < 16 && row + i < length; i++)
                {
                    byte b = buf[row + i];
                    hex.Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                    txt.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8}  {1,-48} {2}", address + (uint)row, hex.ToString(), txt.ToString()));
            }
            return 0;
        }

        private static int Ptrs(uint baseAddress, uint lo, uint hi)
        {
            int length = (int)(hi - lo);
            byte[] buf;
            if (!TryRead(baseAddress + lo, length, out buf))
            {
                Console.WriteLine("read failed");
                return 5;
            }
            Console.WriteLine("=== pointer-looking dwords in 0x" + baseAddress.ToString("X8")
                + " + 0x" + lo.ToString("X") + "..0x" + hi.ToString("X") + " ===");
            int count = 0;
            for (int off = 0; off + 4 <= length; off += 4)
            {
                uint v = ReadU32(buf, off);
                if (!PlausiblePointer(v)) { continue; }
                byte[] head;
                string vtableText = "unreadable";
                if (TryRead(v, 4, out head))
                {
                    uint vt = ReadU32(head, 0);
                    vtableText = "vtable 0x" + vt.ToString("X8") + (InModule(vt) ? "  <-- module!" : "");
                }
                string typeName = GuessTypeName(v, 0x200, 6);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  +0x{0:X4} -> 0x{1:X8}   {2}{3}", lo + (uint)off, v, vtableText,
                    typeName.Length > 0 ? "   ids: " + typeName : ""));
                count++;
            }
            Console.WriteLine("total: " + count);
            return 0;
        }

        /// <summary>
        /// Sample the house and everything it points at.
        ///
        /// Two questions get answered at once:
        ///   A) which dwords INSIDE the house move, and how;
        ///   B) which pointed-to objects contain moving dwords (the factory would
        ///      be one of those).
        /// Money is printed every second so production windows are visible in the
        /// same timeline.
        /// </summary>
        private static int Watch(uint baseAddress, uint lo, uint hi, double seconds, double hz)
        {
            int length = (int)(hi - lo);
            int slots = length / 4;
            int samples = (int)Math.Round(seconds * hz);
            if (samples < 2) { samples = 2; }
            int sleepMs = (int)Math.Round(1000.0 / hz);

            uint[] first = new uint[slots];
            uint[] last = new uint[slots];
            int[] changes = new int[slots];
            int[] ups = new int[slots];
            int[] downs = new int[slots];
            uint[] minV = new uint[slots];
            uint[] maxV = new uint[slots];
            bool[] seen = new bool[slots];

            Dictionary<uint, TargetTrack> targets = new Dictionary<uint, TargetTrack>();

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "=== watch base=0x{0:X8} +0x{1:X}..+0x{2:X}  {3} samples @ {4} Hz ===",
                baseAddress, lo, hi, samples, hz));
            Console.WriteLine("  (money is printed once a second: drops = something is being paid for)");

            Stopwatch clock = Stopwatch.StartNew();
            int nextMoneyLine = 0;

            for (int s = 0; s < samples; s++)
            {
                byte[] buf;
                if (!TryRead(baseAddress + lo, length, out buf))
                {
                    Console.WriteLine("read failed at sample " + s);
                    break;
                }

                for (int i = 0; i < slots; i++)
                {
                    uint v = ReadU32(buf, i * 4);
                    if (!seen[i])
                    {
                        seen[i] = true;
                        first[i] = v;
                        last[i] = v;
                        minV[i] = v;
                        maxV[i] = v;
                    }
                    else if (v != last[i])
                    {
                        changes[i]++;
                        if (v > last[i]) { ups[i]++; } else { downs[i]++; }
                        last[i] = v;
                        if (v < minV[i]) { minV[i] = v; }
                        if (v > maxV[i]) { maxV[i] = v; }
                    }
                }

                // follow pointers
                for (int i = 0; i < slots; i++)
                {
                    uint v = ReadU32(buf, i * 4);
                    if (!PlausiblePointer(v)) { continue; }
                    TargetTrack track;
                    if (!targets.TryGetValue(v, out track))
                    {
                        if (targets.Count >= MaxTargets) { continue; }
                        byte[] probe;
                        if (!TryRead(v, 8, out probe)) { continue; }
                        track = new TargetTrack(v);
                        targets[v] = track;
                    }
                    track.Sample(_process, s);
                }

                double elapsed = clock.Elapsed.TotalSeconds;
                if (elapsed >= nextMoneyLine)
                {
                    nextMoneyLine = (int)elapsed + 1;
                    uint money = 0;
                    string moneyText = "?";
                    byte[] m;
                    if (TryRead(baseAddress + MoneyOffset, 4, out m)) { money = ReadU32(m, 0); moneyText = money.ToString(CultureInfo.InvariantCulture); }
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  [t={0,5:0.0}s] sample {1}/{2}  money={3}", elapsed, s + 1, samples, moneyText));
                }

                Thread.Sleep(sleepMs);
            }

            // ------------------------------------------------------------ report
            Console.WriteLine();
            Console.WriteLine("--- A. dwords INSIDE the house that moved ---");
            List<int> moved = new List<int>();
            for (int i = 0; i < slots; i++) { if (changes[i] > 0) { moved.Add(i); } }
            moved.Sort(delegate(int a, int b) { return changes[b].CompareTo(changes[a]); });
            if (moved.Count == 0) { Console.WriteLine("  (nothing moved)"); }
            for (int k = 0; k < moved.Count && k < 80; k++)
            {
                int i = moved[k];
                uint off = lo + (uint)(i * 4);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  +0x{0:X4}  n={1,-4} {2} -> {3}   up={4} down={5}  min=0x{6:X8} max=0x{7:X8}{8}",
                    off, changes[i], first[i], last[i], ups[i], downs[i], minV[i], maxV[i],
                    IsKnown(off) ? "   <- known" : ""));
            }
            if (moved.Count > 80) { Console.WriteLine("  ... total " + moved.Count); }

            Console.WriteLine();
            Console.WriteLine("--- B. pointed-to objects with moving dwords ---");
            List<TargetTrack> ordered = new List<TargetTrack>(targets.Values);
            ordered.Sort(delegate(TargetTrack a, TargetTrack b)
            {
                int am = a.MovingCount(); int bm = b.MovingCount();
                if (am != bm) { return bm.CompareTo(am); }
                return a.Address.CompareTo(b.Address);
            });
            int shown = 0;
            for (int k = 0; k < ordered.Count && shown < 40; k++)
            {
                TargetTrack t = ordered[k];
                if (t.MovingCount() == 0 && t.VTable == 0) { continue; }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  0x{0:X8}  vtable=0x{1:X8}{2}  seen {3}/{4}{5}",
                    t.Address, t.VTable, InModule(t.VTable) ? " <-- module!" : "", t.Seen, samples,
                    t.TypeName.Length > 0 ? "   ids: " + t.TypeName : ""));
                List<int> tm = t.MovingOffsets();
                for (int j = 0; j < tm.Count && j < 12; j++)
                {
                    int o = tm[j];
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "        +0x{0:X4}  n={1,-4} {2} -> {3}  up={4} down={5}",
                        o, t.Changes[o / 4], t.First[o / 4], t.Last[o / 4], t.Ups[o / 4], t.Downs[o / 4]));
                }
                if (tm.Count > 12) { Console.WriteLine("        ... " + tm.Count + " moving dwords"); }
                shown++;
            }
            if (shown == 0) { Console.WriteLine("  (none)"); }
            Console.WriteLine();
            Console.WriteLine("targets followed: " + targets.Count);

            // ------------------------------------------------------------ appendix
            Console.WriteLine();
            Console.WriteLine("--- C. the slots next to the known production pointer (+0x53BC) ---");
            for (uint off = 0x5390; off <= 0x53F0; off += 4)
            {
                int i = (int)((off - lo) / 4);
                if (i < 0 || i >= slots) { continue; }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  +0x{0:X4}  first=0x{1:X8} last=0x{2:X8} n={3}{4}",
                    off, first[i], last[i], changes[i],
                    off == 0x53BC ? "   <- the known one" : ""));
            }
            return 0;
        }

        /// <summary>
        /// The five production slots, stride 8, starting at house+0x53AC.
        /// Slot 2 (+0x53BC) is the one the shipped "instant build" already uses.
        /// </summary>
        private static readonly uint[] SlotOffsets = new uint[] { 0x53AC, 0x53B4, 0x53BC, 0x53C4, 0x53CC };

        /// <summary>
        /// Follow the five production slots and print a line whenever anything about
        /// one of them changes: the pointer itself, or the tick counter / money owed
        /// inside the object it points at. This is what maps "sidebar tab" to "slot".
        /// </summary>
        private static int Slots(uint house, double seconds, double hz)
        {
            int samples = (int)Math.Round(seconds * hz);
            int sleepMs = (int)Math.Round(1000.0 / hz);
            string[] last = new string[SlotOffsets.Length];
            int lastMoney = -1;

            Console.WriteLine("house = 0x" + house.ToString("X8"));
            Console.WriteLine("slots: " + string.Join(", ", Array.ConvertAll(SlotOffsets,
                delegate(uint o) { return "+0x" + o.ToString("X"); })));
            Console.WriteLine("watching for " + seconds + "s @ " + hz + " Hz. Just build things;");
            Console.WriteLine("a line appears whenever a slot changes.");
            Console.WriteLine();

            Stopwatch clock = Stopwatch.StartNew();
            for (int s = 0; s < samples; s++)
            {
                for (int i = 0; i < SlotOffsets.Length; i++)
                {
                    string text = DescribeSlot(house, SlotOffsets[i]);
                    if (text != last[i])
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "[t={0,6:0.0}s] slot{1} (+0x{2:X}) {3}", clock.Elapsed.TotalSeconds, i, SlotOffsets[i], text));
                        last[i] = text;
                    }
                }

                byte[] m;
                if (TryRead(house + MoneyOffset, 4, out m))
                {
                    int money = (int)ReadU32(m, 0);
                    if (lastMoney >= 0 && money != lastMoney)
                    {
                        // only report meaningful jumps, the per-tick drain would flood
                        if (Math.Abs(money - lastMoney) >= 200)
                        {
                            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "[t={0,6:0.0}s]                money {1} -> {2}",
                                clock.Elapsed.TotalSeconds, lastMoney, money));
                        }
                    }
                    lastMoney = money;
                }

                Thread.Sleep(sleepMs);
            }
            Console.WriteLine();
            Console.WriteLine("done. final state:");
            for (int i = 0; i < SlotOffsets.Length; i++)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  slot{0} (+0x{1:X}) {2}", i, SlotOffsets[i], DescribeSlot(house, SlotOffsets[i])));
            }
            return 0;
        }

        private static string DescribeSlot(uint house, uint offset)
        {
            byte[] raw;
            if (!TryRead(house + offset, 4, out raw)) { return "<unreadable>"; }
            uint ptr = ReadU32(raw, 0);
            if (ptr == 0) { return "0"; }
            if (!PlausiblePointer(ptr)) { return "0x" + ptr.ToString("X8") + " (not a pointer)"; }

            byte[] head;
            if (!TryRead(ptr, 0x80, out head)) { return "0x" + ptr.ToString("X8") + " -> unreadable"; }
            uint vt = ReadU32(head, 0);
            return string.Format(CultureInfo.InvariantCulture,
                "0x{0:X8}  vt=0x{1:X8}  +0x24={2}  +0x28={3}  +0x2C={4}  +0x5C={5}  +0x60={6}",
                ptr, vt, ReadU32(head, 0x24), ReadU32(head, 0x28), ReadU32(head, 0x2C),
                ReadU32(head, 0x5C), ReadU32(head, 0x60));
        }

        private static bool IsKnown(uint offset)
        {
            return offset == 0x30C || offset == 0x53A4 || offset == 0x53A8 || offset == 0x53BC;
        }

        // ================================================================
        // map -- print the current shroud state of the whole map.
        // Read-only, and it stays read-only: this file has no
        // WriteProcessMemory declaration at all.  (The write experiment is
        // option B and belongs to a separate, deliberately-written tool.)
        //
        // Layout source: Ares team's YRpp headers, archived at
        // 证据归档\2026-10-13-网络方向\YRpp\, verified 2026-10-13 against two
        // independent samples (Ares crash dump 10527/10527 index equation,
        // spy-satellite snapshots 349 cells FE->FF).  See docs\07 section 10.
        // ================================================================

        private const uint MapClassInstance = 0x0087F7E8; // global object, not a pointer
        private const int MapRectOffset = 0xEC;           // left, top, right, bottom (ints)
        private const int CellsItemsOffset = 0x13C;       // VectorClass<CellClass*> Items
        private const int CellsCapacityOffset = 0x140;    // ... Capacity (= 0x40000, constant)
        private const int CellXOffset = 0x24;             // short
        private const int CellYOffset = 0x26;             // short
        private const int CellShroudOffset = 0x120;       // -2 black / -1 visible / 0..48 anim
        private const int CellFogOffset = 0x121;
        private const int CellProbeBytes = 0x124;         // enough to cover +0x120/+0x121
        private const int MaxCells = 0x40000;

        private static int MapPrint()
        {
            byte[] head;
            if (!TryRead(MapClassInstance, 0x160, out head))
            {
                Console.WriteLine("cannot read MapClass instance at 0x"
                    + MapClassInstance.ToString("X8") + " -- is the game in a match?");
                return 5;
            }

            int left = (int)ReadU32(head, MapRectOffset);
            int top = (int)ReadU32(head, MapRectOffset + 4);
            int right = (int)ReadU32(head, MapRectOffset + 8);
            int bottom = (int)ReadU32(head, MapRectOffset + 12);
            int capacity = (int)ReadU32(head, CellsCapacityOffset);

            uint items = ReadU32(head, CellsItemsOffset);
            uint mapVt = ReadU32(head, 0);

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "MapClass @ 0x{0:X8}  vt=0x{1:X8}{2}", MapClassInstance, mapVt,
                InModule(mapVt) ? " (in module)" : "  <-- NOT in module, suspicious"));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "MapRect = ({0},{1})-({2},{3})  {4}x{5}   Cells capacity = 0x{6:X}",
                left, top, right, bottom, right - left, bottom - top, capacity));
            if (items < 0x10000 || capacity != MaxCells)
            {
                Console.WriteLine("Cells chain looks wrong (items=0x" + items.ToString("X8")
                    + ", capacity=0x" + capacity.ToString("X")
                    + ") -- not in a match? Refusing to print a guessed map.");
                return 5;
            }
            Console.WriteLine("Cells.Items = 0x" + items.ToString("X8"));
            Console.WriteLine();

            // Pass 1: walk all 0x40000 slots, keep only cells that pass BOTH gates
            // (vtable inside module, own X/Y reproduces slot index).  Measure the
            // real coordinate range from what survives -- never assume it.
            int passed = 0, failedEq = 0, failedVt = 0, nullSlots = 0, unreadable = 0;
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            Dictionary<int, byte[]> cells = new Dictionary<int, byte[]>();
            for (int idx = 0; idx < capacity; idx++)
            {
                byte[] slot;
                if (!TryRead(items + (uint)(idx * 4), 4, out slot)) { unreadable++; break; }
                uint cell = ReadU32(slot, 0);
                if (cell < 0x10000) { nullSlots++; continue; }

                byte[] body;
                if (!TryRead(cell, CellProbeBytes, out body)) { unreadable++; continue; }
                if (!InModule(ReadU32(body, 0))) { failedVt++; continue; }

                int x = (short)(body[CellXOffset] | (body[CellXOffset + 1] << 8));
                int y = (short)(body[CellYOffset] | (body[CellYOffset + 1] << 8));
                if (((y << 9) + x) != idx) { failedEq++; continue; }

                passed++;
                cells[idx] = body;
                if (x < minX) { minX = x; }
                if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; }
                if (y > maxY) { maxY = y; }
            }

            if (passed == 0)
            {
                Console.WriteLine("no cell passed the index equation -- structure mismatch, print nothing.");
                return 5;
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "cells: passed {0} | null {1} | index-eq failed {2} | vtable failed {3} | unreadable {4}",
                passed, nullSlots, failedEq, failedVt, unreadable));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "cell coordinate range: X {0}..{1} ({2} cols)  Y {3}..{4} ({5} rows)",
                minX, maxX, maxX - minX + 1, minY, maxY, maxY - minY + 1));
            Console.WriteLine();

            int nShroud = 0, nVisible = 0, nAnim = 0;
            int fShroud = 0, fVisible = 0, fAnim = 0;
            foreach (byte[] body in cells.Values)
            {
                int s = (sbyte)body[CellShroudOffset];
                if (s == -2) { nShroud++; } else if (s == -1) { nVisible++; } else { nAnim++; }
                int f = (sbyte)body[CellFogOffset];
                if (f == -2) { fShroud++; } else if (f == -1) { fVisible++; } else { fAnim++; }
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Shroudedness: black {0} | visible {1} | anim {2}",
                nShroud, nVisible, nAnim));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Foggedness  : black {0} | visible {1} | anim {2}",
                fShroud, fVisible, fAnim));
            Console.WriteLine();
            Console.WriteLine("legend:  # black   . visible   0-9/a-z anim frame   (blank = no cell)");
            Console.WriteLine("         X -> right, Y -> down");
            Console.WriteLine();

            // ruler line so the user can eyeball coordinates
            StringBuilder ruler = new StringBuilder("     ");
            for (int x = 0; x <= maxX - minX; x++)
            {
                ruler.Append((x + minX) % 10 == 0 ? (char)('0' + ((x + minX) / 10) % 10) : ' ');
            }
            Console.WriteLine(ruler.ToString());

            for (int y = minY; y <= maxY; y++)
            {
                StringBuilder line = new StringBuilder();
                line.Append(y.ToString("D3", CultureInfo.InvariantCulture)).Append("  ");
                for (int x = minX; x <= maxX; x++)
                {
                    byte[] body;
                    if (!cells.TryGetValue((y << 9) + x, out body))
                    {
                        line.Append(' ');
                        continue;
                    }
                    int s = (sbyte)body[CellShroudOffset];
                    if (s == -2) { line.Append('#'); }
                    else if (s == -1) { line.Append('.'); }
                    else if (s >= 0 && s <= 9) { line.Append((char)('0' + s)); }
                    else if (s >= 10 && s <= 35) { line.Append((char)('a' + s - 10)); }
                    else { line.Append('*'); }
                }
                Console.WriteLine(line.ToString());
            }
            return 0;
        }

        /// <summary>One pointed-to object we are following.</summary>
        private sealed class TargetTrack
        {
            public readonly uint Address;
            public uint VTable;
            public int Seen;
            public string TypeName = "";

            public readonly uint[] First = new uint[TargetBytes / 4];
            public readonly uint[] Last = new uint[TargetBytes / 4];
            public readonly int[] Changes = new int[TargetBytes / 4];
            public readonly int[] Ups = new int[TargetBytes / 4];
            public readonly int[] Downs = new int[TargetBytes / 4];
            private readonly bool[] _seen = new bool[TargetBytes / 4];

            private IntPtr _process;

            public TargetTrack(uint address) { Address = address; }

            public void Sample(IntPtr process, int sampleIndex)
            {
                _process = process;
                byte[] buf = new byte[TargetBytes];
                IntPtr got;
                if (!ReadProcessMemory(process, (IntPtr)(ulong)Address, buf, (IntPtr)TargetBytes, out got)
                    || (long)got != TargetBytes)
                {
                    return;
                }
                if (Seen == 0)
                {
                    VTable = ReadU32(buf, 0);
                    TypeName = GuessTypeName(Address, 0x200, 6);
                }
                Seen++;

                for (int i = 0; i < TargetBytes / 4; i++)
                {
                    uint v = ReadU32(buf, i * 4);
                    if (!_seen[i]) { _seen[i] = true; First[i] = v; Last[i] = v; continue; }
                    if (v != Last[i])
                    {
                        Changes[i]++;
                        if (v > Last[i]) { Ups[i]++; } else { Downs[i]++; }
                        Last[i] = v;
                    }
                }
            }

            public int MovingCount()
            {
                int n = 0;
                for (int i = 0; i < Changes.Length; i++) { if (Changes[i] > 0) { n++; } }
                return n;
            }

            public List<int> MovingOffsets()
            {
                List<int> list = new List<int>();
                for (int i = 0; i < Changes.Length; i++) { if (Changes[i] > 0) { list.Add(i * 4); } }
                list.Sort(delegate(int a, int b) { return Changes[b / 4].CompareTo(Changes[a / 4]); });
                return list;
            }
        }
    }
}

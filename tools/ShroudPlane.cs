// ShroudPlane (read-only): find the per-house shroud "book" by comparing every
// writable region against the KNOWN-TRUE cell-layer shroud.
//
// Why this shape of test: the cell layer (CellClass +0x120 Shroudedness) is
// verified ground truth -- 10527/10527 index equation on two independent
// samples (Ares crash dump + spy-satellite snapshots), see docs\07 section 10.
// Whatever the renderer actually reads must carry the SAME black/visible mix
// as that layer.  So: slide a window of a few plausible plane shapes over
// every writable committed region and keep the windows whose composition
// matches the cell layer's.  A real plane matches almost exactly; unrelated
// memory (screen buffers, object heaps, tile caches) does not.
//
// 2026-10-16 history:
//   pass 1  tried only 0xFE/0xFF byte planes -> one hit, and its picture was a
//           repeating tile pattern (a graphics cache), i.e. a false positive.
//   pass 2  (this version) also tries the other likely encodings
//           (0x00/0xFF, 0x00/0x01, 0x01/0x00, 0x00/0xFE) and 1-bit-per-cell
//           planes (10527/8 and 21609/8 bytes), because the renderer may well
//           store "seen" as a bit rather than a byte.
//
// Read-only: OpenProcess(PROCESS_VM_READ) only, ReadProcessMemory only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MoTools
{
    internal static class ShroudPlane
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const uint ModuleBase = 0x00400000;
        private const uint ModuleSize = 0x00793000;

        // Cell-layer chain -- verified, see docs\07 section 10.
        private const uint MapClassInstance = 0x0087F7E8; // global object, not a pointer
        private const int CellsItemsOffset = 0x13C;
        private const int CellsCapacityOffset = 0x140;
        private const int CellXOffset = 0x24;
        private const int CellYOffset = 0x26;
        private const int CellShroudOffset = 0x120;
        private const int CellProbeBytes = 0x124;
        private const int MaxCells = 0x40000;

        private const byte BLACK = 0xFE;
        private const byte VISIBLE = 0xFF;

        /// <summary>Byte-plane shapes worth trying: the real cell count, the square
        /// of the measured coordinate span, and a couple of neighbours.</summary>
        private static readonly int[] PlaneSizes = new int[] { 10527, 21609, 5307, 23409, 40000 };

        /// <summary>1-bit-per-cell shapes (one bit per cell, packed).</summary>
        private static readonly int[] BitPlaneSizes = new int[] { 1316, 2702, 661, 2927, 5000 };

        /// <summary>A "black" code and a "visible" code the renderer might use.</summary>
        private sealed class ValuePair
        {
            public byte BlackCode;
            public byte VisibleCode;
            public string Name;

            public ValuePair(byte blackCode, byte visibleCode, string name)
            {
                BlackCode = blackCode;
                VisibleCode = visibleCode;
                Name = name;
            }
        }

        private static readonly ValuePair[] Pairs = new ValuePair[]
        {
            new ValuePair(0xFE, 0xFF, "FE/FF"),
            new ValuePair(0x00, 0xFF, "00/FF"),
            new ValuePair(0x00, 0x01, "00/01"),
            new ValuePair(0x01, 0x00, "01/00"),
            new ValuePair(0x00, 0xFE, "00/FE")
        };

        /// <summary>A window whose composition looks like the cell layer's.</summary>
        private sealed class Hit
        {
            public uint Base;
            public int Offset;
            public int Size;
            public string Kind;
            public int CountA;      // "black" under this encoding
            public int CountB;      // "visible" under this encoding
            public double Distance;

            /// <summary>Share of the window that is black or visible at all.
            /// A real shroud plane is ~1.0: every cell holds one of the two
            /// values (plus the 0..48 animation frames).  This is the
            /// discriminator the first version of this tool lacked -- with a
            /// loose ratio test alone, millions of windows "match" to 1e-5 and
            /// the test says nothing.</summary>
            public double Live;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(
            IntPtr handle, IntPtr address, out MEMORY_BASIC_INFORMATION64 info, IntPtr length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION64
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public uint AllocationProtect;
            public uint Alignment1;
            public ulong RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Alignment2;
        }

        private static IntPtr _process = IntPtr.Zero;
        private static uint _pid;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                return RealMain(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex.Message);
                return 9;
            }
            finally
            {
                if (_process != IntPtr.Zero) { CloseHandle(_process); }
            }
        }

        private static int RealMain(string[] args)
        {
            System.Diagnostics.Process[] found =
                System.Diagnostics.Process.GetProcessesByName("gamemd");
            if (found.Length == 0)
            {
                Console.WriteLine("gamemd.exe is not running.");
                return 3;
            }
            _pid = (uint)found[0].Id;
            _process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, (int)_pid);
            if (_process == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess failed, Win32=" + Marshal.GetLastWin32Error());
                return 4;
            }
            Console.WriteLine("gamemd.exe pid=" + _pid);

            if (args.Length > 0 && args[0].ToLowerInvariant() == "show")
            {
                return Show(ParseHex(args[1]), (int)ParseHex(args[2]), (int)ParseHex(args[3]));
            }
            if (args.Length > 0 && args[0].ToLowerInvariant() == "cells")
            {
                int want = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;
                return PrintCells(want);
            }
            if (args.Length > 0 && args[0].ToLowerInvariant() == "frontier")
            {
                int want = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 3;
                return PrintFrontier(want);
            }
            return Scan();
        }

        // ======================================================== ground truth

        private static bool ReadTruth(out int countFE, out int countFF, out int countOther,
            out Dictionary<int, byte> grid, out string note)
        {
            countFE = 0; countFF = 0; countOther = 0;
            grid = new Dictionary<int, byte>();
            note = "";

            byte[] head;
            if (!TryRead(MapClassInstance, 0x160, out head))
            {
                note = "cannot read the MapClass instance -- is the game in a match?";
                return false;
            }
            int capacity = (int)ReadU32(head, CellsCapacityOffset);
            uint items = ReadU32(head, CellsItemsOffset);
            if (items < 0x10000 || capacity != MaxCells)
            {
                note = "Cells chain looks wrong (items=0x" + items.ToString("X8")
                    + ", capacity=0x" + capacity.ToString("X") + ") -- not in a match?";
                return false;
            }

            int passed = 0, failed = 0;
            for (int idx = 0; idx < capacity; idx++)
            {
                byte[] slot;
                if (!TryRead(items + (uint)(idx * 4), 4, out slot)) { break; }
                uint cell = ReadU32(slot, 0);
                if (cell < 0x10000) { continue; }

                byte[] body;
                if (!TryRead(cell, CellProbeBytes, out body)) { failed++; continue; }
                if (!InModule(ReadU32(body, 0))) { failed++; continue; }

                int x = (short)(body[CellXOffset] | (body[CellXOffset + 1] << 8));
                int y = (short)(body[CellYOffset] | (body[CellYOffset + 1] << 8));
                if (((y << 9) + x) != idx) { failed++; continue; }

                passed++;
                byte s = body[CellShroudOffset];
                grid[idx] = s;
                if (s == BLACK) { countFE++; }
                else if (s == VISIBLE) { countFF++; }
                else { countOther++; }
            }
            note = "cells passed " + passed + " | gate-failed " + failed;
            return passed > 0;
        }

        // ======================================================== scan

        private static int Scan()
        {
            int fe, ff, other;
            Dictionary<int, byte> grid;
            string note;
            if (!ReadTruth(out fe, out ff, out other, out grid, out note))
            {
                Console.WriteLine("truth unavailable: " + note);
                return 5;
            }
            double truthRatio = (double)fe / (double)(fe + ff);
            Console.WriteLine("TRUTH (cell layer): black=" + fe + " visible=" + ff
                + " anim=" + other + " total=" + (fe + ff + other));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "truth black share among black+visible = {0:F4}", truthRatio));
            Console.WriteLine();

            List<Region> regions = EnumerateRegions();
            long totalBytes = 0;
            foreach (Region r in regions) { totalBytes += r.Size; }
            Console.WriteLine("scanning " + regions.Count + " writable regions, ~"
                + (totalBytes >> 20) + " MB");
            Console.WriteLine("byte-plane encodings: " + PairNames() + "  sizes: " + SizesText(PlaneSizes));
            Console.WriteLine("bit-plane sizes: " + SizesText(BitPlaneSizes));
            Console.WriteLine();

            List<Hit> hits = new List<Hit>();
            for (int i = 0; i < regions.Count; i++)
            {
                Region r = regions[i];
                byte[] buf = new byte[r.Size];
                if (!ReadWhole(r.Base, buf)) { continue; }

                Hit best = null;
                for (int p = 0; p < Pairs.Length; p++)
                {
                    for (int s = 0; s < PlaneSizes.Length; s++)
                    {
                        int n = PlaneSizes[s];
                        if (n > buf.Length) { continue; }
                        SlideBytePlane(r.Base, buf, n, Pairs[p], truthRatio, ref best);
                    }
                }
                for (int s = 0; s < BitPlaneSizes.Length; s++)
                {
                    int n = BitPlaneSizes[s];
                    if (n > buf.Length) { continue; }
                    SlideBitPlane(r.Base, buf, n, truthRatio, ref best);
                }
                if (best != null) { hits.Add(best); }
            }

            // Most-dominated first: a real plane is ~100% black/visible.
            hits.Sort(delegate(Hit a, Hit b)
            {
                int c = b.Live.CompareTo(a.Live);
                if (c != 0) { return c; }
                return a.Distance.CompareTo(b.Distance);
            });
            Console.WriteLine("=== TOP WINDOWS (most dominated by black/visible, with the cell layer's mix) ===");
            Console.WriteLine("addr        offset     size   kind    live   black  visible  share   distance");
            int shown = 0;
            for (int i = 0; i < hits.Count && shown < 25; i++)
            {
                Hit h = hits[i];
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8}  0x{1:X6}  {2,6}  {3,-6} {4:F3} {5,6} {6,7}  {7:F4}  {8:F4}",
                    h.Base, h.Offset, h.Size, h.Kind, h.Live, h.CountA, h.CountB,
                    (double)h.CountA / (double)(h.CountA + h.CountB), h.Distance));
                shown++;
            }
            if (shown == 0)
            {
                Console.WriteLine("(none -- no window is dominated by a black/visible pair in the cell layer's mix)");
            }

            Console.WriteLine();
            Console.WriteLine("=== TRUTH: cell-layer shroud (rows Y, cols X) ===");
            Console.WriteLine("    # = black(0xFE)   . = visible(0xFF)   digit = animation frame");
            RenderTruth(grid);

            Console.WriteLine();
            Console.WriteLine("=== TOP CANDIDATE WINDOWS ===");
            int drawn = 0;
            for (int i = 0; i < hits.Count && drawn < 5; i++)
            {
                Hit h = hits[i];
                Console.WriteLine();
                Console.WriteLine("--- 0x" + h.Base.ToString("X8") + " + 0x" + h.Offset.ToString("X")
                    + "  size=" + h.Size + "  kind=" + h.Kind
                    + "  stride=" + StrideFor(h.Size)
                    + "  black=" + h.CountA + " visible=" + h.CountB
                    + string.Format(CultureInfo.InvariantCulture, "  distance={0:F4}", h.Distance));
                Show(h.Base + (uint)h.Offset, h.Size, StrideFor(h.Size));
                drawn++;
            }
            return 0;
        }

        /// <summary>
        /// Print the addresses of a few BLACK cells, so a debugger can be pointed
        /// at "cell + 0x120" and asked who writes it.  The engine rewrites that
        /// byte every frame (experiment #1, 2026-10-14), so the writing code is
        /// reachable at any moment -- and that code must reference the per-house
        /// book directly.  This is the one measurement no amount of shape-guessing
        /// can replace.  Read-only.
        /// </summary>
        private static int PrintCells(int want)
        {
            int fe, ff, other;
            Dictionary<int, byte> grid;
            string note;
            if (!ReadTruth(out fe, out ff, out other, out grid, out note))
            {
                Console.WriteLine("truth unavailable: " + note);
                return 5;
            }
            Console.WriteLine(note);
            Console.WriteLine("cell layer: black=" + fe + " visible=" + ff + " anim=" + other);
            Console.WriteLine();
            Console.WriteLine("MapClass = 0x" + MapClassInstance.ToString("X8")
                + "   Cells.Items = (see HousePeek map)");

            // Walk the slot array again, this time remembering where each cell lives.
            byte[] head;
            TryRead(MapClassInstance, 0x160, out head);
            uint items = ReadU32(head, CellsItemsOffset);
            int capacity = (int)ReadU32(head, CellsCapacityOffset);

            int found = 0;
            Console.WriteLine();
            Console.WriteLine("=== BLACK cells to breakpoint (address to use = cell + 0x120) ===");
            for (int idx = 0; idx < capacity && found < want; idx++)
            {
                byte[] slot;
                if (!TryRead(items + (uint)(idx * 4), 4, out slot)) { break; }
                uint cell = ReadU32(slot, 0);
                if (cell < 0x10000) { continue; }
                byte[] body;
                if (!TryRead(cell, CellProbeBytes, out body)) { continue; }
                if (!InModule(ReadU32(body, 0))) { continue; }
                int x = (short)(body[CellXOffset] | (body[CellXOffset + 1] << 8));
                int y = (short)(body[CellYOffset] | (body[CellYOffset + 1] << 8));
                if (((y << 9) + x) != idx) { continue; }
                if (body[CellShroudOffset] != BLACK) { continue; }
                found++;
                Console.WriteLine("  cell 0x" + cell.ToString("X8")
                    + "   X=" + x + " Y=" + y
                    + "   >>> breakpoint address: 0x" + (cell + 0x120).ToString("X8")
                    + "   (now = 0x" + body[CellShroudOffset].ToString("X2") + ")");
            }
            if (found == 0) { Console.WriteLine("(no black cell found -- is the map fully explored?)"); }
            return 0;
        }

        /// <summary>
        /// Print BLACK cells that sit right at the edge of the explored area --
        /// the cells a player is most likely to reveal next, and therefore the
        /// cells whose Shroudedness the engine will actually write.
        ///
        /// Why this matters: the engine only writes the byte when its own
        /// computed value DIFFERS from what is stored (that is why watching a
        /// plain black cell produced no hits at all).  A frontier cell is the
        /// one most likely to get revealed by simply walking outward.
        ///
        /// Also prints the direction in screen terms, using the isometric
        /// mapping (screen x ~ X-Y, screen y ~ X+Y), so it can be described to
        /// a player as "walk toward the upper-left of your base".
        /// Read-only.
        /// </summary>
        private static int PrintFrontier(int want)
        {
            int fe, ff, other;
            Dictionary<int, byte> grid;
            string note;
            if (!ReadTruth(out fe, out ff, out other, out grid, out note))
            {
                Console.WriteLine("truth unavailable: " + note);
                return 5;
            }
            Console.WriteLine(note);
            Console.WriteLine("black=" + fe + " visible=" + ff + " anim=" + other);

            // Cell object addresses, so we can hand out "cell + 0x120".
            Dictionary<int, uint> cellAt = new Dictionary<int, uint>();
            byte[] head;
            TryRead(MapClassInstance, 0x160, out head);
            uint items = ReadU32(head, CellsItemsOffset);
            int capacity = (int)ReadU32(head, CellsCapacityOffset);
            for (int idx = 0; idx < capacity; idx++)
            {
                byte[] slot;
                if (!TryRead(items + (uint)(idx * 4), 4, out slot)) { break; }
                uint cell = ReadU32(slot, 0);
                if (cell < 0x10000) { continue; }
                byte[] body;
                if (!TryRead(cell, CellProbeBytes, out body)) { continue; }
                if (!InModule(ReadU32(body, 0))) { continue; }
                int x = (short)(body[CellXOffset] | (body[CellXOffset + 1] << 8));
                int y = (short)(body[CellYOffset] | (body[CellYOffset + 1] << 8));
                if (((y << 9) + x) != idx) { continue; }
                cellAt[idx] = cell;
            }

            // Visible centroid, in screen space, for the direction hints.
            double sx = 0, sy = 0;
            int vis = 0;
            foreach (KeyValuePair<int, byte> kv in grid)
            {
                if (kv.Value != VISIBLE) { continue; }
                int x = kv.Key & 511;
                int y = kv.Key >> 9;
                sx += (x - y);
                sy += (x + y);
                vis++;
            }
            if (vis == 0) { Console.WriteLine("no visible cells -- cannot compute the base position"); }
            else { sx /= vis; sy /= vis; }

            Console.WriteLine();
            Console.WriteLine("=== FRONTIER BLACK CELLS (adjacent to the explored area) ===");
            // NOTE: black and visible cells are separated by ANIMATION cells
            // (Shroudedness 1..48) -- the fade-in/fade-out band.  So "black next
            // to visible" finds nothing; the frontier must count animation cells
            // as explored too.  (Learned the hard way, 2026-10-17.)
            List<int> frontier = new List<int>();
            List<int> animating = new List<int>();
            foreach (KeyValuePair<int, byte> kv in grid)
            {
                byte v = kv.Value;
                if (v != BLACK && v != VISIBLE && v != 0) { animating.Add(kv.Key); }
                if (v != BLACK) { continue; }
                int x = kv.Key & 511;
                int y = kv.Key >> 9;
                bool touch = false;
                for (int dx = -1; dx <= 1 && !touch; dx++)
                {
                    for (int dy = -1; dy <= 1 && !touch; dy++)
                    {
                        if (dx == 0 && dy == 0) { continue; }
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0) { continue; }
                        byte nv;
                        if (grid.TryGetValue((ny << 9) + nx, out nv) && nv != BLACK) { touch = true; }
                    }
                }
                if (touch) { frontier.Add(kv.Key); }
            }
            // Closest to the base centre first: most likely to be walked over.
            frontier.Sort(delegate(int a, int b)
            {
                double da = Math.Abs((a & 511) - (a >> 9) - sx) + Math.Abs((a & 511) + (a >> 9) - sy);
                double db = Math.Abs((b & 511) - (b >> 9) - sx) + Math.Abs((b & 511) + (b >> 9) - sy);
                return da.CompareTo(db);
            });
            Console.WriteLine("frontier cells: " + frontier.Count
                + "   animating cells: " + animating.Count);
            Console.WriteLine();
            Console.WriteLine("### BEST TARGET: cells that are animating RIGHT NOW");
            Console.WriteLine("    (the engine is writing these every frame, so a write");
            Console.WriteLine("     breakpoint on one of them fires immediately)");
            for (int i = 0; i < animating.Count && i < 3; i++)
            {
                int idx = animating[i];
                uint cell;
                if (!cellAt.TryGetValue(idx, out cell)) { continue; }
                Console.WriteLine("  cell 0x" + cell.ToString("X8") + "  X=" + (idx & 511)
                    + " Y=" + (idx >> 9) + "  now=0x" + grid[idx].ToString("X2")
                    + "   >>> breakpoint: 0x" + (cell + 0x120).ToString("X8"));
            }
            Console.WriteLine();
            Console.WriteLine("### FRONTIER (walk outward from your base to reveal these)");
            for (int i = 0; i < frontier.Count && i < want; i++)
            {
                int idx = frontier[i];
                int x = idx & 511;
                int y = idx >> 9;
                uint cell;
                if (!cellAt.TryGetValue(idx, out cell)) { continue; }
                double ddx = (x - y) - sx;
                double ddy = (x + y) - sy;
                Console.WriteLine("  cell 0x" + cell.ToString("X8") + "  X=" + x + " Y=" + y
                    + "   >>> breakpoint: 0x" + (cell + 0x120).ToString("X8")
                    + string.Format(CultureInfo.InvariantCulture,
                        "   screen offset from base: dx={0:F0} dy={1:F0}", ddx, ddy));
            }

            // Side effect for the debugger step: drop the best target into a file
            // so a .bat can hand it straight to WriteSpy.exe -- no human copying
            // hex around, and only one UAC prompt for the whole thing.
            try
            {
                string pick = "";
                for (int i = 0; i < animating.Count && pick.Length == 0; i++)
                {
                    uint c;
                    if (cellAt.TryGetValue(animating[i], out c)) { pick = (c + 0x120).ToString("X8"); }
                }
                for (int i = 0; i < frontier.Count && pick.Length == 0; i++)
                {
                    uint c;
                    if (cellAt.TryGetValue(frontier[i], out c)) { pick = (c + 0x120).ToString("X8"); }
                }
                if (pick.Length == 0 && cellAt.Count > 0)
                {
                    // Fully explored map: no frontier left, so any cell will do --
                    // the fog byte (+0x121) still changes as units move.
                    foreach (KeyValuePair<int, uint> kv in cellAt)
                    {
                        pick = (kv.Value + 0x120).ToString("X8");
                        break;
                    }
                }
                if (pick.Length > 0)
                {
                    System.IO.File.WriteAllText("spy-target.txt", pick);
                    Console.WriteLine();
                    Console.WriteLine("spy-target.txt = " + pick);
                }
            }
            catch (Exception) { }
            return 0;
        }

        private static int StrideFor(int size)
        {
            if (size == 21609) { return 147; }
            if (size == 23409) { return 153; }
            if (size == 40000) { return 200; }
            if (size == 5307) { return 61; }
            if (size == 10527) { return 103; }
            if (size == 2702) { return 82; }
            if (size == 1316) { return 47; }
            return 147;
        }

        private static void RenderTruth(Dictionary<int, byte> grid)
        {
            int maxRow = 0, maxCol = 0;
            foreach (KeyValuePair<int, byte> kv in grid)
            {
                int row = kv.Key >> 9;
                int col = kv.Key & 511;
                if (row > maxRow) { maxRow = row; }
                if (col > maxCol) { maxCol = col; }
            }
            if (maxCol > 200) { maxCol = 200; }
            if (maxRow > 200) { maxRow = 200; }
            StringBuilder line = new StringBuilder();
            for (int y = 0; y <= maxRow; y++)
            {
                line.Length = 0;
                for (int x = 0; x <= maxCol; x++)
                {
                    byte s;
                    if (!grid.TryGetValue((y << 9) + x, out s)) { line.Append(' '); continue; }
                    if (s == BLACK) { line.Append('#'); }
                    else if (s == VISIBLE) { line.Append('.'); }
                    else { line.Append((char)('0' + (s % 10))); }
                }
                Console.WriteLine(line.ToString());
            }
        }

        // ======================================================== sliding

        /// <summary>
        /// Slide an n-byte window, keeping the window whose (black,visible)
        /// composition under this encoding is closest to the truth.  Running
        /// counts, so O(bytes) per (encoding, size) -- regions reach tens of MB
        /// and prefix arrays would not fit comfortably.
        /// </summary>
        private static void SlideBytePlane(uint baseAddress, byte[] buf, int n,
            ValuePair pair, double truthRatio, ref Hit best)
        {
            if (buf.Length < n) { return; }
            int ca = 0, cb = 0;
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (b == pair.BlackCode) { ca++; }
                else if (b == pair.VisibleCode) { cb++; }
            }
            for (int off = 0; off + n <= buf.Length; off++)
            {
                int live = ca + cb;
                // Keep the window that is MOST dominated by these two values
                // (a real plane is ~100% black/visible); among comparable ones,
                // the closest mix wins.
                if (live * 10 >= n * 9)
                {
                    double ratio = (double)ca / (double)live;
                    double d = ratio - truthRatio;
                    if (d < 0) { d = -d; }
                    if (d <= 0.05)
                    {
                        double dom = (double)live / (double)n;
                        if (best == null || dom > best.Live + 1e-9
                            || (dom > best.Live - 1e-9 && d < best.Distance))
                        {
                            Hit h = new Hit();
                            h.Base = baseAddress;
                            h.Offset = off;
                            h.Size = n;
                            h.Kind = pair.Name;
                            h.CountA = ca;
                            h.CountB = cb;
                            h.Distance = d;
                            h.Live = dom;
                            best = h;
                        }
                    }
                }
                byte outByte = buf[off];
                if (outByte == pair.BlackCode) { ca--; }
                else if (outByte == pair.VisibleCode) { cb--; }
                if (off + n < buf.Length)
                {
                    byte inByte = buf[off + n];
                    if (inByte == pair.BlackCode) { ca++; }
                    else if (inByte == pair.VisibleCode) { cb++; }
                }
            }
        }

        /// <summary>
        /// Same idea for a 1-bit-per-cell plane: running popcount of the
        /// window, compared with the truth's black share (either polarity).
        /// </summary>
        private static void SlideBitPlane(uint baseAddress, byte[] buf, int n,
            double truthRatio, ref Hit best)
        {
            if (buf.Length < n) { return; }
            int ones = 0;
            for (int i = 0; i < n; i++) { ones += PopCount(buf[i]); }
            int bits = n * 8;
            for (int off = 0; off + n <= buf.Length; off++)
            {
                double ratio = (double)ones / (double)bits;
                double d1 = ratio - truthRatio; if (d1 < 0) { d1 = -d1; }
                double d2 = (1.0 - ratio) - truthRatio; if (d2 < 0) { d2 = -d2; }
                double d = d1 < d2 ? d1 : d2;
                if (d <= 0.03 && (best == null || d < best.Distance))
                {
                    Hit h = new Hit();
                    h.Base = baseAddress;
                    h.Offset = off;
                    h.Size = n;
                    h.Kind = "bits";
                    h.CountA = ones;
                    h.CountB = bits - ones;
                    h.Distance = d;
                    h.Live = 0;      // dominance is meaningless for a bit plane
                    best = h;
                }
                ones -= PopCount(buf[off]);
                if (off + n < buf.Length) { ones += PopCount(buf[off + n]); }
            }
        }

        private static int PopCount(byte b)
        {
            int c = 0;
            while (b != 0) { c += (b & 1); b >>= 1; }
            return c;
        }

        // ======================================================== render

        /// <summary>
        /// Render a window as ASCII art: '#' = black code, '.' = visible code,
        /// digit = 0..48 (animation frame), ' ' = anything else.
        /// </summary>
        private static int Show(uint address, int length, int stride)
        {
            if (stride <= 0) { stride = 147; }
            byte[] buf;
            if (!TryRead(address, length, out buf))
            {
                Console.WriteLine("read failed at 0x" + address.ToString("X8"));
                return 5;
            }
            int fe = 0, ff = 0, other = 0;
            StringBuilder line = new StringBuilder();
            Console.WriteLine("=== 0x" + address.ToString("X8") + "  len=" + length
                + "  stride=" + stride + " ===");
            for (int i = 0; i < length; i++)
            {
                byte b = buf[i];
                char c;
                if (b == BLACK) { c = '#'; fe++; }
                else if (b == VISIBLE) { c = '.'; ff++; }
                else if (b <= 48) { c = (char)('0' + (b % 10)); other++; }
                else { c = ' '; other++; }
                line.Append(c);
                if ((i + 1) % stride == 0)
                {
                    Console.WriteLine(line.ToString());
                    line.Length = 0;
                }
            }
            if (line.Length > 0) { Console.WriteLine(line.ToString()); }
            Console.WriteLine("counts: FE=" + fe + " FF=" + ff + " other=" + other);
            return 0;
        }

        // ======================================================== helpers

        private sealed class Region
        {
            public uint Base;
            public int Size;
        }

        private static string PairNames()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < Pairs.Length; i++)
            {
                if (i > 0) { sb.Append(", "); }
                sb.Append(Pairs[i].Name);
            }
            return sb.ToString();
        }

        private static string SizesText(int[] sizes)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < sizes.Length; i++)
            {
                if (i > 0) { sb.Append(", "); }
                sb.Append(sizes[i]);
            }
            return sb.ToString();
        }

        private static List<Region> EnumerateRegions()
        {
            List<Region> regions = new List<Region>();
            ulong addr = 0x10000;
            ulong max = 0x7FFEFFFF;
            while (addr < max)
            {
                MEMORY_BASIC_INFORMATION64 info;
                if (VirtualQueryEx(_process, (IntPtr)addr, out info,
                    (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64))) == IntPtr.Zero)
                { break; }
                ulong size = info.RegionSize;
                if (size == 0) { break; }
                bool writable = (info.State == 0x1000) && ((info.Protect & 0xF0) == 0)
                    && ((info.Protect & 0x04) != 0 || (info.Protect & 0x08) != 0);
                if (writable && info.BaseAddress < 0xFFFFFFFFul && size <= 0x2000000)
                {
                    Region r = new Region();
                    r.Base = (uint)info.BaseAddress;
                    r.Size = (int)size;
                    regions.Add(r);
                }
                addr = info.BaseAddress + size;
            }
            return regions;
        }

        private static bool ReadWhole(uint address, byte[] buffer)
        {
            int done = 0;
            while (done < buffer.Length)
            {
                int chunk = buffer.Length - done;
                if (chunk > 65536) { chunk = 65536; }
                IntPtr got;
                byte[] temp = new byte[chunk];
                if (!ReadProcessMemory(_process, (IntPtr)(ulong)(address + (uint)done),
                    temp, (IntPtr)chunk, out got) || (long)got != chunk)
                {
                    return false;
                }
                Buffer.BlockCopy(temp, 0, buffer, done, chunk);
                done += chunk;
            }
            return true;
        }

        private static bool TryRead(uint address, int length, out byte[] buffer)
        {
            buffer = new byte[length];
            int done = 0;
            while (done < length)
            {
                int chunk = length - done;
                if (chunk > 256) { chunk = 256; }
                byte[] temp = new byte[chunk];
                IntPtr got;
                if (!ReadProcessMemory(_process, (IntPtr)(ulong)(address + (uint)done),
                    temp, (IntPtr)chunk, out got) || (long)got != chunk)
                {
                    return false;
                }
                Buffer.BlockCopy(temp, 0, buffer, done, chunk);
                done += chunk;
            }
            return true;
        }

        private static uint ReadU32(byte[] buf, int offset)
        {
            return (uint)(buf[offset] | (buf[offset + 1] << 8)
                | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }

        private static bool InModule(uint address)
        {
            return address >= ModuleBase && address < ModuleBase + ModuleSize;
        }

        private static uint ParseHex(string text)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { text = text.Substring(2); }
            return uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
    }
}

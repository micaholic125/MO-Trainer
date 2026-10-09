# -*- coding: utf-8 -*-
"""只读探针（第二轮）：顺着上一轮发现的"同型号共用指针"找到电厂的产出值。

上一轮（building-compare.txt）的结果：
  · 三座电厂（HP 750 / 造价 600）+ 两座核电站（HP 1400 / 造价 2000）
  · 只有两个偏移**恰好按 3/2 分裂且指向堆地址**：
        +0x4E4  电厂 = 0x1F307280   核电站 = 0x1F303040
        +0x520  电厂 = 0x232CF130   核电站 = 0x232DE180
    "同型号完全相同、不同型号不同" —— 这就是"类型对象"（说明书）的特征。
  · 建筑自己身上**没有**任何字段等于 150（电厂的真实产出）
    → 产出大概率记在说明书里，不在每个建筑身上。

本轮：读那两个指针指向的内容；只有当里面找不到 150/2000 时，才退而做一次
全内存查找（用 C 速度的字节匹配，有上限、不会跑很久）。

**全程只读。**
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_PATH = os.path.join(HERE, '..', 'dist', 'reports', 'building-compare2.txt')

MODULE_BASE = 0x00400000
SIZE = 0x1000

BUILDINGS = [0x2CD0A680, 0x234E4E68, 0x234E55B0, 0x234E5CF8, 0x234E72D0]
LABELS = ['plant#1', 'plant#2', 'plant#3', 'nuclear#1', 'nuclear#2']
CANDIDATE_PTR_OFFSETS = [0x4E4, 0x520]

out_lines = []


def say(text):
    out_lines.append(text)
    try:
        print(text)
    except Exception:
        pass


def find_pid():
    try:
        r = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq gamemd.exe', '/FO', 'CSV', '/NH'],
                           capture_output=True, text=True)
        for line in r.stdout.splitlines():
            parts = [p.strip('"') for p in line.split('","')]
            if len(parts) >= 2 and parts[1].isdigit():
                return int(parts[1])
    except Exception:
        pass
    return 0


def main():
    k32 = ctypes.WinDLL('kernel32', use_last_error=True)
    k32.OpenProcess.restype = wt.HANDLE
    k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]
    k32.ReadProcessMemory.restype = wt.BOOL
    k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                      ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    k32.VirtualQueryEx.restype = ctypes.c_size_t
    k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p,
                                   ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]

    pid = int(sys.argv[1]) if len(sys.argv) > 1 else find_pid()
    if not pid:
        say('gamemd.exe is not running.')
        return

    h = k32.OpenProcess(0x0010 | 0x0400, False, pid)
    if not h:
        say('OpenProcess failed, error %d (need administrator).' % ctypes.get_last_error())
        return

    def read(addr, size):
        buf = ctypes.create_string_buffer(size)
        got = ctypes.c_size_t(0)
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(got)):
            return None
        if got.value != size:
            return None
        return buf.raw

    def u32(buf, off):
        return int.from_bytes(buf[off:off + 4], 'little')

    def i32(buf, off):
        v = u32(buf, off)
        return v - 0x100000000 if v >= 0x80000000 else v

    say('MO-Trainer building compare, round 2 (read-only)  pid=%d' % pid)
    say('')

    house_head = read(MODULE_BASE + 0x683D4C, 4)
    if house_head:
        house = u32(house_head, 0)
        hb = read(house, 0x5400)
        if hb:
            say('house 0x%08X: money=%d  power_output=%d  power_drain=%d'
                % (house, u32(hb, 0x30C), u32(hb, 0x53A4), u32(hb, 0x53A8)))
    say('')

    objs = []
    for i, addr in enumerate(BUILDINGS):
        b = read(addr, SIZE)
        objs.append(b)
        if b is None:
            say('  %s 0x%08X unreadable (destroyed?)' % (LABELS[i], addr))
    if any(b is None for b in objs):
        say('Some objects unreadable - aborting.')
        return

    found_output_anywhere = False

    # ---- 1. 顺着候选指针读"说明书" ----
    for off in CANDIDATE_PTR_OFFSETS:
        targets = [u32(b, off) for b in objs]
        say('=== object+0x%X  ->  %s ==='
            % (off, '  '.join('%s:0x%08X' % (LABELS[i], targets[i]) for i in range(5))))
        uniq = []
        for t in targets:
            if t not in uniq:
                uniq.append(t)

        for t in uniq:
            tb = read(t, 0x800)
            if tb is None:
                say('  target 0x%08X unreadable' % t)
                continue
            say('  --- target 0x%08X  (vtable 0x%08X) ---' % (t, u32(tb, 0)))

            small = []
            negs = []
            for o in range(0, 0x800 - 3, 4):
                v = u32(tb, o)
                if v in (60, 150, 200, 300, 600, 750, 1000, 1400, 2000, 3000, 4000, 6000):
                    small.append('+0x%X=%d' % (o, v))
                iv = i32(tb, o)
                if -6000 <= iv < 0:
                    negs.append('+0x%X=%d' % (o, iv))
            say('      小整数（电力/血量/造价的样子）: %s' % (', '.join(small) if small else '无'))
            say('      负值（耗电建筑的指纹）: %s' % (', '.join(negs[:24]) if negs else '无'))

            if 150 in [u32(tb, o) for o in range(0, 0x800 - 3, 4)]:
                found_output_anywhere = True
            if 2000 in [u32(tb, o) for o in range(0, 0x800 - 3, 4)]:
                found_output_anywhere = True

            for row in range(0, 0x140, 16):
                words = ' '.join('%08X' % u32(tb, row + k) for k in (0, 4, 8, 12))
                say('      +0x%03X  %s' % (row, words))
        say('')

    # ---- 2. 后备：全内存找"150 与 600 挨在一起"的块 ----
    if found_output_anywhere:
        say('=== 说明书里已经看到 150/2000，跳过全内存查找 ===')
    else:
        say('=== 说明书里没有 150/2000，改为全内存查找 ===')
        say('    (找 150，且其前后 0x100 内同时出现 600 与 750)')
        try:
            pat150 = struct.pack('<I', 150)
            pat600 = struct.pack('<I', 600)
            pat750 = struct.pack('<I', 750)
            addr = 0x10000
            scanned = 0
            hits = 0
            while addr < 0x7FFF0000 and hits < 30:
                mbi = ctypes.create_string_buffer(64)
                got = ctypes.c_size_t(0)
                if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), mbi, 48, ctypes.byref(got)):
                    break
                base = int.from_bytes(mbi.raw[0:8], 'little')
                region = int.from_bytes(mbi.raw[24:32], 'little')
                protect = int.from_bytes(mbi.raw[32:36], 'little')
                state = int.from_bytes(mbi.raw[36:40], 'little')
                if region == 0 or base == 0:
                    break
                nxt = base + region
                if nxt <= addr:
                    break
                addr = nxt
                if state != 0x1000 or (protect & 0xFF) not in (0x02, 0x04, 0x20, 0x40, 0x08):
                    continue
                if region > 0x4000000:
                    continue
                block = read(base, region)
                if block is None:
                    continue
                scanned += region
                pos = block.find(pat150)
                while pos >= 0 and hits < 30:
                    if pos % 4 == 0:
                        win = block[max(0, pos - 0x100):pos + 0x100]
                        if pat600 in win and pat750 in win:
                            hits += 1
                            say('  HIT 0x%08X  (150 at +0x%X)' % (base + pos, pos))
                            ctx = block[max(0, pos - 0x30):pos + 0x50]
                            for row in range(0, len(ctx) - 15, 16):
                                say('        %08X  %s'
                                    % (base + pos - 0x30 + row,
                                       ' '.join('%08X' % u32(ctx, row + k) for k in (0, 4, 8, 12))))
                    pos = block.find(pat150, pos + 4)
            say('  scanned %d MB, hits=%d' % (scanned // 1048576, hits))
        except Exception as exc:
            say('  scan error: %s' % exc)

    k32.CloseHandle(h)


if __name__ == '__main__':
    try:
        main()
    except Exception as exc:
        say('ERROR: %s' % exc)
    finally:
        try:
            os.makedirs(os.path.dirname(OUT_PATH), exist_ok=True)
            with open(OUT_PATH, 'w', encoding='utf-8') as f:
                f.write('\n'.join(out_lines) + '\n')
        except Exception:
            pass

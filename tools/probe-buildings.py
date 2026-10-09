# -*- coding: utf-8 -*-
"""只读探针：找出「电力产出」到底存在建筑的哪个字段。

**全程只读**：只用 OpenProcess(PROCESS_VM_READ) + ReadProcessMemory，
不调用 WriteProcessMemory，不注入、不改游戏一个字节。

依据（2026-10-09 采集报告 harvest-20261009-150108）：
  电力产出的每一次跳变都恰好伴随一个**新建的 0x007E3EBC 对象**出现：
    采样#20 +150  → 0x2CD0A680
    采样#30 +150  → 0x234E4E68
    采样#33 +150  → 0x234E55B0
    采样#39 +2000 → 0x234E5CF8
    采样#43 +2000 → 0x234E72D0
  用户确认共建了 3 座电厂 + 2 座核电站，且 60+150×3+2000×2 = 4510 与读数完全吻合。
  所以：**电厂每座产出 150、核电站每座产出 2000**。
  找出"前三个一个值、后两个另一个值"（或恰好 =150/2000）的字段，就是产出的所在。
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_PATH = os.path.join(HERE, '..', 'dist', 'reports', 'building-compare.txt')

MODULE_BASE = 0x00400000

# 采集报告里"电力一涨就冒出来"的那 5 个对象（类型表都是 0x007E3EBC）
BUILDINGS = [
    (0x2CD0A680, 'jump +150 @sample20  (power plant)'),
    (0x234E4E68, 'jump +150 @sample30  (power plant)'),
    (0x234E55B0, 'jump +150 @sample33  (power plant)'),
    (0x234E5CF8, 'jump +2000 @sample39 (nuclear plant)'),
    (0x234E72D0, 'jump +2000 @sample43 (nuclear plant)'),
]
EXPECT = [150, 150, 150, 2000, 2000]

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

    pid = int(sys.argv[1]) if len(sys.argv) > 1 else find_pid()
    if not pid:
        say('gamemd.exe is not running. Start the game and run this again.')
        return

    h = k32.OpenProcess(0x0010 | 0x0400, False, pid)   # VM_READ | QUERY_INFORMATION
    if not h:
        say('OpenProcess failed, error %d (need administrator).' % ctypes.get_last_error())
        return

    say('MO-Trainer building compare (read-only)  pid=%d' % pid)
    say('')

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

    # ---- 0. 先确认这一局还活着：走金钱主链 ----
    head = read(MODULE_BASE + 0x683D4C, 4)
    if head is None:
        say('Cannot read the static root - the game may have exited.')
    else:
        house = u32(head, 0)
        hb = read(house, 0x5400)
        if hb:
            say('Money chain: module+0x683D4C -> house 0x%08X (direct, no intermediate offset)' % house)
            say('  house +0x30C  money        = %d' % u32(hb, 0x30C))
            say('  house +0x53A4 power output = %d' % u32(hb, 0x53A4))
            say('  house +0x53A8 power drain  = %d' % u32(hb, 0x53A8))
            say('  (if power output is not around 4510, this session is a different game)')
        else:
            say('Cannot read house 0x%08X' % house)
    say('')

    # ---- 1. 读 5 座建筑 ----
    SIZE = 0x1000
    objs = []
    for (addr, note) in BUILDINGS:
        b = read(addr, SIZE)
        objs.append(b)
        head4 = ('0x%08X' % u32(b, 0)) if b else 'unreadable'
        say('  0x%08X  vtable %s  %s' % (addr, head4, note))
    say('')

    if any(b is None for b in objs):
        say('Some objects unreadable (destroyed or address changed). Comparison skipped.')
        return

    # ---- 2. 找"3 个电厂一个值 / 2 个核电站另一个值"的字段 ----
    say('=== fields that split exactly as 150x3 / 2000x2 ===')
    hits = 0
    for off in range(0, SIZE - 3, 4):
        vals = [u32(b, off) for b in objs]
        if vals == EXPECT:
            say('  *** +0x%03X  int exactly [150,150,150,2000,2000]' % off)
            hits += 1
            continue
        a, c = vals[:3], vals[3:]
        if len(set(a)) == 1 and len(set(c)) == 1 and a[0] != c[0]:
            extra = ''
            if a[0] in (150, 2000) or c[0] in (150, 2000):
                extra = '   <-- matches the known outputs'
            say('  +0x%03X  plant=%d  nuclear=%d  0x%X / 0x%X%s'
                % (off, a[0], c[0], a[0], c[0], extra))
            hits += 1

    if hits == 0:
        say('  (none - so the output value is not on the building objects themselves;')
        say('   it is probably in the shared per-model "type object" instead)')
    say('')

    # ---- 3. 各对象里等于 60/150/600/2000 的字段 ----
    say('=== dwords equal to 60 / 150 / 600 / 2000 (first 0x1000 bytes) ===')
    for i, (addr, note) in enumerate(BUILDINGS):
        found = []
        for off in range(0, SIZE - 3, 4):
            v = u32(objs[i], off)
            if v in (60, 150, 600, 2000):
                found.append('+0x%X=%d' % (off, v))
        say('  0x%08X: %s' % (addr, ', '.join(found) if found else 'none'))
    say('')

    # ---- 4. 顺带：把 5 个对象的头部按 16 进制摊开（看清结构） ----
    say('=== first 0x120 bytes of each object ===')
    for i, (addr, note) in enumerate(BUILDINGS):
        say('  -- 0x%08X  %s' % (addr, note))
        for row in range(0, 0x120, 16):
            words = ' '.join('%08X' % u32(objs[i], row + k) for k in (0, 4, 8, 12))
            say('     +0x%03X  %s' % (row, words))

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

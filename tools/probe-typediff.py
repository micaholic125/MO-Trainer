# -*- coding: utf-8 -*-
"""只读探针（收官轮）：把"电力产出记在哪里"这件事一次问到底。

已经确定的事实（前两轮 + 采集报告）：
  · 建筑对象 +0x520 = 指向"同型号共用一份的说明书"（类型对象）的指针；
  · 电厂说明书：ID「NAPOWR」名称「Tesla Reactor」，血量 750、造价 600；
  · 核电站说明书：ID「NANRCT」名称「Nuclear Reactor」，血量 1400、造价 2000；
  · 3 座电厂各产出 150、2 座核电站各产出 2000（由电力读数 4510 = 60+150×3+2000×2 反推，
    且与原版《尤里的复仇》里这两个建筑的数据完全吻合）。
  · 但建筑自己身上没有任何字段等于 150 → 产出记在说明书里。

本轮做三件事，保证不会空手而归：
  1. 两块说明书**整段**（0x20000）逐格对比，列出所有"两型号不同"的小数值；
  2. 全内存查找作后备（**上一轮我写错判断把这一步跳过了，这里补上**）；
  3. **零写入的定案判据**：把场上所有建筑的类型对象都读出来，按"我方"归类计数，
     把候选字段按 发电/耗电 求和 —— 若它真是发电量，必须正好等于游戏显示的 4510 与 350。
     这一条不需要往游戏里写任何东西就能定案。

**全程只读，一个字节都不写。**
"""
import ctypes
import ctypes.wintypes as wt
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_PATH = os.path.join(HERE, '..', 'dist', 'reports', 'building-compare3.txt')

MODULE_BASE = 0x00400000
MODULE_SIZE = 0x00793000

TECHNO_ARRAY_SLOT = MODULE_BASE + 0x47F77C     # 全对象名单的"格子"（配置里那个静态地址）
TYPE_PTR_OFFSET = 0x520                        # 建筑对象 → 说明书
OWNER_OFFSET = 0x14C                           # 阵营指针（金钱锚定推出的那个，本轮顺带验证）
SCAN = 0x20000                                 # 说明书整段读

BUILDINGS = [0x2CD0A680, 0x234E4E68, 0x234E55B0, 0x234E5CF8, 0x234E72D0]
LABELS = ['plant#1', 'plant#2', 'plant#3', 'nuclear#1', 'nuclear#2']

INTERESTING = (60, 100, 150, 200, 300, 500, 600, 750, 1000, 1400, 2000, 3000, 4000, 4510)

out_lines = []
diag = []          # "为什么读不到"——绝不允许静默返回空


def parse_mbi(raw):
    """把 VirtualQueryEx 回的 48 字节解析成字段。

    字段偏移按项目自己的 C# 定义（`src/Core/Win32Api.cs` 的 MEMORY_BASIC_INFORMATION，x64）：
      0 BaseAddress / 8 AllocationBase / 16 AllocationProtect /
      24 RegionSize / 32 State / 36 Protect / 40 Type
    **State 在 32，不是 36**（36 是 Protect）—— 第一版写错成 36，
    于是每次都判成"不是已提交内存"、静默返回空，白让用户点了两次。

    另外：VirtualQueryEx 只有 **4 个参数**，长度是**返回值**而不是出参；
    第一版多传了个出参又去读它，读到的永远是 0。

    两处都是实现写错，不是方向问题。现在有 `diag` 兜底：**读不到必须说得出为什么**。
    """
    return dict(
        base=int.from_bytes(raw[0:8], 'little'),
        region=int.from_bytes(raw[24:32], 'little'),
        state=int.from_bytes(raw[32:36], 'little'),
        protect=int.from_bytes(raw[36:40], 'little'),
    )


def say(text=''):
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
                                   ctypes.c_size_t]

    pid = int(sys.argv[1]) if len(sys.argv) > 1 else find_pid()
    if not pid:
        say('gamemd.exe 没有在运行。')
        return
    h = k32.OpenProcess(0x0010 | 0x0400, False, pid)
    if not h:
        say('OpenProcess 失败，错误码 %d（需要管理员权限）' % ctypes.get_last_error())
        return

    def read(addr, size):
        buf = ctypes.create_string_buffer(size)
        got = ctypes.c_size_t(0)
        if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, size, ctypes.byref(got)):
            return None
        if got.value != size:
            return None
        return buf.raw

    def query(addr, tag=''):
        mbi = ctypes.create_string_buffer(64)
        written = k32.VirtualQueryEx(h, ctypes.c_void_p(addr), mbi, 48)
        if written != 48:
            diag.append('%s VirtualQueryEx @0x%X 返回 %s（应为 48），错误码 %d'
                        % (tag, addr, written, ctypes.get_last_error()))
            return None
        return parse_mbi(mbi.raw)

    # ---- 先自我验算：拿模块基址试一次（它必然是可读的已提交内存）----
    probe = query(MODULE_BASE, '自检')
    if probe is None:
        say('！！ 结构解析自检失败（VirtualQueryEx 没成功）—— 见下方诊断')
    elif probe['state'] != 0x1000 or probe['region'] == 0:
        say('！！ 结构解析自检失败：模块基址读出来 state=0x%X region=0x%X（应当 state=0x1000）'
            % (probe['state'], probe['region']))
    else:
        say('结构解析自检通过：模块 0x%08X 已提交、region=0x%X 可读=0x%X'
            % (MODULE_BASE, probe['region'], probe['protect']))
    say()

    def read_chunks(addr, size, chunk=0x1000, tag=''):
        """读"这段内存实际能读到的部分"，绝不越界，且**空手而归时必须说明原因**。"""
        info = query(addr, tag)
        if info is None:
            return b''
        base, region, state, protect = info['base'], info['region'], info['state'], info['protect']
        if state != 0x1000:
            diag.append('%s 0x%08X 不是已提交内存（state=0x%X）' % (tag, addr, state))
            return b''
        if not (base <= addr < base + region):
            diag.append('%s 0x%08X 落在 region [0x%08X,0x%08X) 之外'
                        % (tag, addr, base, base + region))
            return b''
        want = min(size, base + region - addr)

        parts = []
        off = 0
        while off < want:
            n = min(chunk, want - off)
            b = read(addr + off, n)
            if b is None:
                break
            parts.append(b)
            off += n
        if not parts:
            diag.append('%s 0x%08X 头一块 %d 字节读不动（protect=0x%X region=0x%X）'
                        % (tag, addr, chunk, protect, region))
        return b''.join(parts)

    def u32(buf, off):
        return int.from_bytes(buf[off:off + 4], 'little')

    def i32(buf, off):
        v = u32(buf, off)
        return v - 0x100000000 if v >= 0x80000000 else v

    def cstr(buf, off, maxlen=40):
        out = bytearray()
        p = off
        while p < len(buf) and len(out) < maxlen and 32 <= buf[p] < 127:
            out.append(buf[p])
            p += 1
        return out.decode('ascii')

    say('MO-Trainer 电力产出定位 · 收官轮（只读）  pid=%d' % pid)
    say()

    # ================= 0. 确认这一局 =================
    house = 0
    head = read(MODULE_BASE + 0x683D4C, 4)
    if head:
        house = u32(head, 0)
        hb = read(house, 0x5400)
        if hb:
            say('房子 0x%08X：金钱=%d  电力产出=%d  电力消耗=%d'
                % (house, u32(hb, 0x30C), u32(hb, 0x53A4), u32(hb, 0x53A8)))
    say()

    # ================= 1. 两块说明书整段读 + 逐格对比 =================
    types = []
    for i, addr in enumerate(BUILDINGS):
        b = read(addr, TYPE_PTR_OFFSET + 4)
        if b is None:
            say('%s 0x%08X 读不到' % (LABELS[i], addr))
            return
        types.append(u32(b, TYPE_PTR_OFFSET))
    say('类型指针：' + '  '.join('%s:0x%08X' % (LABELS[i], types[i]) for i in range(5)))
    tesla_addr, nuclear_addr = types[0], types[3]
    if tesla_addr == nuclear_addr:
        say('！！ 电厂与核电站指向同一份说明书，与预期不符')
        return
    say()

    tesla = read_chunks(tesla_addr, SCAN)
    nuclear = read_chunks(nuclear_addr, SCAN)
    if not tesla or not nuclear:
        say('读说明书一个字节都没读到（地址可能已释放）')
        return
    say('说明书长度：电厂 %d 字节，核电站 %d 字节（读到读不动为止）'
        % (len(tesla), len(nuclear)))
    say()
    # 后续一律按实际读到的长度来扫，不再假设固定长度
    tlen = len(tesla) - 3
    nlen = len(nuclear) - 3

    say('=== 两块说明书的身份 ===')
    say('  电厂  0x%08X  类型表 0x%08X  ID「%s」 名称「%s」'
        % (tesla_addr, u32(tesla, 0), cstr(tesla, 0x24), cstr(tesla, 0x64)))
    say('  核电站 0x%08X  类型表 0x%08X  ID「%s」 名称「%s」'
        % (nuclear_addr, u32(nuclear, 0), cstr(nuclear, 0x24), cstr(nuclear, 0x64)))
    say()

    say('=== ★ 电厂=150 且 核电站=2000 的字段 ===')
    exact = []
    for o in range(0, tlen, 4):
        if u32(tesla, o) == 150 and o < nlen and u32(nuclear, o) == 2000:
            exact.append(o)
            say('  ★★★ +0x%X' % o)
    if not exact:
        say('  （没有）')
    say()

    say('=== 电厂说明书里等于 150 的字段（不管核电站是什么）===')
    t150 = [o for o in range(0, tlen, 4) if u32(tesla, o) == 150]
    for o in t150:
        nv = u32(nuclear, o) if o < nlen else -1
        say('  +0x%-6X 电厂=150  核电站=%d (0x%X)' % (o, nv, nv if nv >= 0 else 0))
    if not t150:
        say('  （没有）')
    say()

    say('=== 两型号取值不同、且都像"小数值"的字段（全部列出）===')
    shown = 0
    for o in range(0, min(tlen, nlen), 4):
        a, b = u32(tesla, o), u32(nuclear, o)
        if a == b:
            continue
        if a > 200000 or b > 200000:
            continue
        mark = ''
        if a == 150 or b == 2000 or a == 60 or b == 0 or a == 0:
            mark = '   <<<'
        say('  +0x%-6X 电厂=%-8d 核电站=%-8d (0x%X / 0x%X)%s' % (o, a, b, a, b, mark))
        shown += 1
        if shown >= 400:
            say('  …（超过 400 条，已截断）')
            break
    if shown == 0:
        say('  （没有）')
    say()

    for name, buf in (('电厂', tesla), ('核电站', nuclear)):
        vals = []
        for o in range(0, len(buf) - 3, 4):
            v = u32(buf, o)
            neg = i32(buf, o)
            if v in INTERESTING:
                vals.append('+0x%X=%d' % (o, v))
            elif -6000 <= neg < 0:
                vals.append('+0x%X=%d' % (o, neg))
        say('=== %s 说明书里"像电力/血量/造价"的值（含负值）===' % name)
        for i in range(0, len(vals), 8):
            say('  ' + ', '.join(vals[i:i + 8]))
        if not vals:
            say('  （没有）')
        say()

    # ================= 2. 后备：全内存查找（补上上一轮被跳过的）=================
    if exact or t150:
        say('=== 说明书里已经看到 150，跳过全内存查找 ===')
        say()
    else:
        say('=== 说明书里没有 150 —— 执行全内存查找 ===')
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
                if k32.VirtualQueryEx(h, ctypes.c_void_p(addr), mbi, 48) != 48:
                    break
                base = int.from_bytes(mbi.raw[0:8], 'little')
                region = int.from_bytes(mbi.raw[24:32], 'little')
                state = int.from_bytes(mbi.raw[32:36], 'little')
                protect = int.from_bytes(mbi.raw[36:40], 'little')
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
                            say('  命中 0x%08X  (150 在 +0x%X)' % (base + pos, pos))
                            ctx = block[max(0, pos - 0x30):pos + 0x50]
                            for row in range(0, len(ctx) - 15, 16):
                                say('        %08X  %s'
                                    % (base + pos - 0x30 + row,
                                       ' '.join('%08X' % u32(ctx, row + k) for k in (0, 4, 8, 12))))
                    pos = block.find(pat150, pos + 4)
            say('  扫描 %d MB，命中 %d 处' % (scanned // 1048576, hits))
        except Exception as exc:
            say('  扫描出错：%s' % exc)
        say()

    # ================= 3. 零写入定案判据 =================
    say('=== 定案判据：把场上所有建筑的类型对象读出来，按我方归类求和 ===')
    roster_head = read(TECHNO_ARRAY_SLOT, 4)
    if not roster_head:
        say('  读不到全对象名单的格子')
    else:
        array_addr = u32(roster_head, 0)
        say('  名单格子 0x%08X → 数组 0x%08X' % (TECHNO_ARRAY_SLOT, array_addr))
        objects = []
        if array_addr >= 0x10000:
            step = 4
            for i in range(4096):
                word = read(array_addr + i * step, 4)
                if word is None:
                    break
                p = u32(word, 0)
                if p < 0x10000 or p > 0x7FFFFFFF:
                    break
                headb = read(p, 4)
                if headb is None:
                    break
                vt = u32(headb, 0)
                if vt < MODULE_BASE or vt >= MODULE_BASE + MODULE_SIZE:
                    break
                objects.append(p)
        say('  名单里数到 %d 个有效对象' % len(objects))

        # 阵营分布（顺带验证 +0x14C 这个偏移对不对）
        owners = {}
        mine = []
        for p in objects:
            ob = read(p, OWNER_OFFSET + 4)
            if ob is None:
                continue
            ow = u32(ob, OWNER_OFFSET)
            owners[ow] = owners.get(ow, 0) + 1
            if house and ow == house:
                mine.append(p)
        top = sorted(owners.items(), key=lambda kv: -kv[1])[:6]
        say('  阵营分布（前 6）：' + ', '.join('0x%08X:%d' % kv for kv in top))
        say('  判为「我方」（阵营 == 房子 0x%08X）的对象：%d 个' % (house, len(mine)))

        # 我方对象的类型对象归类
        type_count = {}
        for p in mine:
            tb = read(p, TYPE_PTR_OFFSET + 4)
            if tb is None:
                continue
            t = u32(tb, TYPE_PTR_OFFSET)
            if t < 0x10000:
                continue
            vtb = read(t, 4)
            if vtb is None:
                continue
            v = u32(vtb, 0)
            if v < MODULE_BASE or v >= MODULE_BASE + MODULE_SIZE:
                continue        # 不是真说明书（类型表应在模块内）
            type_count[t] = type_count.get(t, 0) + 1

        say('  我方对象共归到 %d 种型号' % len(type_count))
        say()
        say('  型号说明书        个数  ID           名称                    候选字段的值')
        cand_offsets = exact if exact else t150
        rows = sorted(type_count.items(), key=lambda kv: -kv[1])
        detail = []
        for (t, n) in rows[:60]:
            tb = read_chunks(t, SCAN)
            if not tb:
                say('  0x%08X  %-5d (读不到)' % (t, n))
                continue
            ident = cstr(tb, 0x24)
            name = cstr(tb, 0x64)
            cvals = ', '.join('+0x%X=%d' % (o, u32(tb, o)) for o in cand_offsets[:6]
                              if o + 4 <= len(tb))
            say('  0x%08X  %-5d %-13s %-24s %s' % (t, n, ident, name, cvals))
            detail.append((t, n, ident, name, tb))

        # 对每个候选字段做求和
        say()
        for o in (cand_offsets[:8] if cand_offsets else []):
            pos = neg = 0
            covered = 0
            for (t, n, ident, name, tb) in detail:
                if o + 4 > len(tb):
                    continue        # 这份说明书没读到那么远，跳过（并如实记数）
                covered += 1
                v = i32(tb, o)
                if v > 0:
                    pos += v * n
                elif v < 0:
                    neg += v * n
            say('  若「+0x%X」就是发电量：我方建筑合计 发电=%d  耗电=%d  '
                '（游戏显示 4510 / 350）%s'
                % (o, pos, neg,
                   '  ★★★ 完全吻合' if pos == 4510 and neg == -350 else
                   '  （%d/%d 份说明书参与求和）' % (covered, len(detail))))

    if diag:
        say('=== 读取诊断（为什么有东西没读到）===')
        for d in diag[:40]:
            say('  ' + d)
        say()

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

#!/usr/bin/env python3
"""解码 script 录制的 TUI 字节流：重放成网格，标出「黑格 / 从未写过的格」及其来源序列。

用法：python3 scripts/analyze_tui_capture.py /tmp/myth-tui.txt

输入为 `script -q /tmp/myth-tui.txt dotnet run --project src/Host` 录制的真实终端流
（或它的 gzip+base64）。输出：逐帧「未设置背景的格」统计 + 背景字符地图 + 可疑序列片段。
"""
from __future__ import annotations

import base64
import gzip
import sys
import unicodedata
from pathlib import Path


def maybe_ungzip(data: bytes) -> bytes:
    if data[:2] == b"\x1f\x8b":
        return gzip.decompress(data)
    return data


def rune_width(ch: str) -> int:
    if unicodedata.combining(ch):
        return 0
    if unicodedata.east_asian_width(ch) in ("W", "F"):
        return 2
    if ord(ch) >= 0x1F300:
        return 2
    return 1


def parse_sgr(pars: list[str], fg, bg):
    i = 0
    while i < len(pars):
        p = pars[i]
        if not p:
            i += 1
            continue
        v = int(p) if p.isdigit() else -1
        if v == 0:
            fg = bg = None
        elif v == 39:
            fg = None
        elif v == 49:
            bg = None
        elif v == 38 and i + 1 < len(pars):
            if pars[i + 1] == "2" and i + 4 < len(pars):
                fg = (int(pars[i + 2]), int(pars[i + 3]), int(pars[i + 4]))
                i += 4
            elif pars[i + 1] == "5" and i + 2 < len(pars):
                fg = ("idx", int(pars[i + 2]))
                i += 2
        elif v == 48 and i + 1 < len(pars):
            if pars[i + 1] == "2" and i + 4 < len(pars):
                bg = (int(pars[i + 2]), int(pars[i + 3]), int(pars[i + 4]))
                i += 4
            elif pars[i + 1] == "5" and i + 2 < len(pars):
                bg = ("idx", int(pars[i + 2]))
                i += 2
        elif 40 <= v <= 47:
            bg = ("idx", v - 40)
        elif 100 <= v <= 107:
            bg = ("idx", v - 100 + 8)
        elif 30 <= v <= 37:
            fg = ("idx", v - 30)
        elif 90 <= v <= 97:
            fg = ("idx", v - 90 + 8)
        i += 1
    return fg, bg


def detect_continuation_clobber(text: str, W: int = 400, H: int = 200) -> list[tuple[int, int, int, str, str]]:
    """找出「写在宽字形续格上的写入」——这是我们/框架把宽字形的第二格覆盖掉的直接证据。

    返回 (off, x, y, 覆盖前该格归属的字符, 本次写入的字符)。
    """
    grid = [[" "] * W for _ in range(H)]
    owner = [[None] * W for _ in range(H)]  # 宽字形首格坐标 -> 续格标记
    hits: list[tuple[int, int, int, str, str]] = []
    fg = bg = None
    x = y = 0
    i = 0
    while i < len(text):
        c = text[i]
        if c == "\x1b":
            if i + 1 < len(text) and text[i + 1] == "[":
                j = i + 2
                while j < len(text) and (text[j].isdigit() or text[j] in ";:?<>=!"):
                    j += 1
                fin = text[j] if j < len(text) else ""
                ps = text[i + 2 : j].replace(":", ";").split(";")
                if fin in "Hf":
                    y = (int(ps[0]) if ps and ps[0] else 1) - 1
                    x = (int(ps[1]) if len(ps) > 1 and ps[1] else 1) - 1
                elif fin == "G":
                    x = (int(ps[0]) if ps and ps[0] else 1) - 1
                elif fin == "K":
                    for c2 in range(x, W):
                        grid[y][c2] = " "
                        owner[y][c2] = None
                elif fin == "J":
                    p0 = int(ps[0]) if ps and ps[0] else 0
                    if p0 == 2:
                        for r in range(H):
                            for c2 in range(W):
                                grid[r][c2] = " "
                                owner[r][c2] = None
                i = j + 1
                continue
            if i + 1 < len(text) and text[i + 1] == "]":
                j = i + 2
                while j < len(text) and text[j] != "\x07":
                    if text[j] == "\x1b" and j + 1 < len(text) and text[j + 1] == "\\":
                        break
                    j += 1
                i = j + 1
                continue
            i += 2
            continue
        if c == "\r":
            x = 0
            i += 1
            continue
        if c == "\n":
            y += 1
            i += 1
            continue

        w = rune_width(c)
        if 0 <= x < W and 0 <= y < H:
            if owner[y][x] is not None:
                # 这一格是前一宽字形的续格 —— 本次写入把它覆盖了
                head_x, head_y = owner[y][x]
                hits.append((i, x, y, grid[head_y][head_x], c))
            grid[y][x] = c
            owner[y][x] = None
            for k in range(1, w):
                if x + k < W:
                    grid[y][x + k] = ""
                    owner[y][x + k] = (x, y)
        x += w
        i += 1
    return hits


def main() -> None:
    raw = Path(sys.argv[1]).read_bytes()
    text = maybe_ungzip(raw).decode("utf-8", errors="replace")
    print(f"流长度: {len(text)} 字符")

    # 视口（未知，按最大行列）
    W, H = 400, 200
    grid: list[list[str]] = [[" "] * W for _ in range(H)]
    bg: list[list[object]] = [[None] * W for _ in range(H)]
    origin: list[list[int]] = [[-1] * W for _ in range(H)]  # 写入该格的字节偏移
    fg, curbg = None, None
    x = y = 0
    i = 0
    stats = {"sgr_bg_true": 0, "sgr_bg_256": 0, "erase_J": 0, "erase_K": 0, "osc11": 0, "writes": 0}

    while i < len(text):
        c = text[i]
        if c == "\x1b":
            if i + 1 < len(text) and text[i + 1] == "[":
                j = i + 2
                while j < len(text) and (text[j].isdigit() or text[j] in ";:?<>=!"):
                    j += 1
                final = text[j] if j < len(text) else ""
                pars = [p for p in text[i + 2 : j].replace(":", ";").split(";")]
                if final in "Hf":
                    y = (int(pars[0]) if pars and pars[0] else 1) - 1
                    x = (int(pars[1]) if len(pars) > 1 and pars[1] else 1) - 1
                elif final == "G":
                    x = (int(pars[0]) if pars and pars[0] else 1) - 1
                elif final == "m":
                    fg, curbg = parse_sgr(pars, fg, curbg)
                    if curbg and curbg[0] == "idx":
                        stats["sgr_bg_256"] += 1
                    elif curbg:
                        stats["sgr_bg_true"] += 1
                elif final == "J":
                    p0 = int(pars[0]) if pars and pars[0] else 0
                    stats["erase_J"] += 1
                    for yy in range(H):
                        for xx in range(W):
                            if p0 == 2 or (p0 == 0 and (yy > y or (yy == y and xx >= x))):
                                grid[yy][xx] = " "
                                bg[yy][xx] = curbg
                                origin[yy][xx] = i
                elif final == "K":
                    stats["erase_K"] += 1
                    p0 = int(pars[0]) if pars and pars[0] else 0
                    for xx in range(x, W):
                        grid[y][xx] = " "
                        bg[y][xx] = curbg
                        origin[y][xx] = i
                i = j + 1
                continue
            if i + 1 < len(text) and text[i + 1] == "]":
                j = i + 2
                while j < len(text) and text[j] not in "\x07":
                    if text[j] == "\x1b" and j + 1 < len(text) and text[j + 1] == "\\":
                        break
                    j += 1
                osc = text[i + 2 : j]
                if osc.startswith("11"):
                    stats["osc11"] += 1
                i = j + 1
                continue
            i += 2
            continue
        if c == "\r":
            x = 0
            i += 1
            continue
        if c == "\n":
            y += 1
            i += 1
            continue
        w = rune_width(c)
        if 0 <= x < W and 0 <= y < H:
            grid[y][x] = c
            bg[y][x] = curbg
            origin[y][x] = i
            for k in range(1, w):
                if x + k < W:
                    grid[y][x + k] = ""
                    bg[y][x + k] = curbg
                    origin[y][x + k] = i
            stats["writes"] += 1
        x += w
        i += 1

    # ── 宽字形续格被覆盖检测（写坏宽字形的第二格 ⇒ 那一格显示底色 = 用户看到的黑块/白块）──
    clobber = detect_continuation_clobber(text)
    print(f"\n== 写在宽字形续格上的写入：{len(clobber)} 次 ==")
    for off, x, y, prev, ch in clobber[:8]:
        frag = text[max(0, off - 60) : off + 12]
        print(f"  off={off:7} ({x},{y}) 覆盖宽字形续格，写入 '{ch}'  片段={frag!r}")
    # 统计被覆盖的「宽字形」到底是哪些字符（用其首格的字符）
    from collections import Counter
    cnt = Counter(prev for *_ , prev, _ch in clobber)
    print("  被覆盖的宽字形分布 top:", cnt.most_common(8))
    if clobber:
        off0 = clobber[0][0]
        print("首个样本前后 160 字符：")
        print(repr(text[max(0, off0 - 120) : off0 + 40]))

    # ── 逐帧分析：框架用同步输出 ESC[?2026h … ESC[?2026l 包帧，按此切帧 ──
    print("\n== 逐帧黑格/默认底格统计（只在光标位置附近写过的格） ==")
    frames = text.split("\x1b[?2026l")
    # 重新按帧重放，记录每帧结束时「背景为 None 且非空格」以及「显式黑底」的格子
    def replay(stream: str, upto: int):
        g = [[" "] * 400 for _ in range(200)]
        b = [[None] * 400 for _ in range(200)]
        f, cb = None, None
        xx = yy = 0
        k = 0
        while k < upto and k < len(stream):
            ch = stream[k]
            if ch == "\x1b":
                if k + 1 < len(stream) and stream[k + 1] == "[":
                    jj = k + 2
                    while jj < len(stream) and (stream[jj].isdigit() or stream[jj] in ";:?<>=!"):
                        jj += 1
                    fin = stream[jj] if jj < len(stream) else ""
                    ps = stream[k + 2 : jj].replace(":", ";").split(";")
                    if fin in "Hf":
                        yy = (int(ps[0]) if ps and ps[0] else 1) - 1
                        xx = (int(ps[1]) if len(ps) > 1 and ps[1] else 1) - 1
                    elif fin == "G":
                        xx = (int(ps[0]) if ps and ps[0] else 1) - 1
                    elif fin == "m":
                        f, cb = parse_sgr(ps, f, cb)
                    elif fin == "J":
                        p0 = int(ps[0]) if ps and ps[0] else 0
                        for r in range(200):
                            for c2 in range(400):
                                if p0 == 2 or (p0 == 0 and (r > yy or (r == yy and c2 >= xx))):
                                    g[r][c2] = " "
                                    b[r][c2] = cb
                    elif fin == "K":
                        p0 = int(ps[0]) if ps and ps[0] else 0
                        for c2 in range(xx, 400):
                            g[yy][c2] = " "
                            b[yy][c2] = cb
                    k = jj + 1
                    continue
                if k + 1 < len(stream) and stream[k + 1] == "]":
                    jj = k + 2
                    while jj < len(stream) and stream[jj] != "\x07":
                        if stream[jj] == "\x1b" and jj + 1 < len(stream) and stream[jj + 1] == "\\":
                            break
                        jj += 1
                    k = jj + 1
                    continue
                k += 2
                continue
            if ch == "\r":
                xx = 0
                k += 1
                continue
            if ch == "\n":
                yy += 1
                k += 1
                continue
            w = rune_width(ch)
            if 0 <= xx < 400 and 0 <= yy < 200:
                g[yy][xx] = ch
                b[yy][xx] = cb
                for kk in range(1, w):
                    if xx + kk < 400:
                        g[yy][xx + kk] = ""
                        b[yy][xx + kk] = cb
            xx += w
            k += 1
        return g, b

    offsets = []
    acc = 0
    for fr in frames:
        acc += len(fr) + len("\x1b[?2026l")
        offsets.append(acc)

    worst = None
    for idx, off in enumerate(offsets):
        g, b = replay(text, off)
        dead = [
            (c2, r)
            for r in range(42)
            for c2 in range(154)
            if b[r][c2] is None
        ]
        if not dead:
            continue
        xs = [d[0] for d in dead]
        ys = [d[1] for d in dead]
        if worst is None or len(dead) > worst[1]:
            worst = (idx, len(dead), off)
        if idx % 10 == 0 or len(dead) > 50:
            print(
                f"  帧#{idx} (off={off}): 默认底格 {len(dead)}, "
                f"范围 x{min(xs)}..{max(xs)} y{min(ys)}..{max(ys)}"
            )

    # ── 对话框区域内的「串层格」：对话框中应全是 e(#2D2424)/其它对话面色，
    #    若混进 a(#1F1F1F 夜空) 或 b(#5B3A29 沙丘) 就是叠层串写（肉眼即黑块）──
    print("\n== 对话框矩形内的夜空格（#1F1F1F 不该出现在对话框内部） ==")
    for idx, off in enumerate(offsets):
        g, b = replay(text, off)
        # 对话框面 = #2D2424 (e) 或 #3A3A3A (w)；用 e 的连通行区间估计矩形
        rows_e = [r for r in range(42) if any(b[r][c2] == (0x2D, 0x24, 0x24) for c2 in range(154))]
        if not rows_e:
            continue
        r0, r1 = min(rows_e), max(rows_e)
        if r1 - r0 < 5:
            continue
        # 矩形左右界：以 r0+1 行上 e 的最左/最右列
        cols = [c2 for c2 in range(154) if b[r0 + 1][c2] == (0x2D, 0x24, 0x24)]
        if not cols:
            continue
        c0, c1 = min(cols), max(cols)
        strays = [
            (c2, r)
            for r in range(r0, r1 + 1)
            for c2 in range(c0, c1 + 1)
            if b[r][c2] == (0x1F, 0x1F, 0x1F)
        ]
        if strays:
            xs = [s2[0] for s2 in strays]
            ys = [s2[1] for s2 in strays]
            print(
                f"  帧#{idx} (off={off}): 对话框 x{c0}..{c1} y{r0}..{r1} 内夜空格 {len(strays)} 个,"
                f" x{min(xs)}..{max(xs)} y{min(ys)}..{max(ys)}, 样本 {strays[:8]}"
            )

    if worst:
        idx, n, off = worst
        print(f"\n== 最糟帧 #{idx}: 默认底格 {n} 个（off={off}）==")
        g, b = replay(text, off)
        for r in range(42):
            row = "".join("·" if b[r][c2] is None else " " for c2 in range(154))
            if "·" in row:
                print(f"{r:2} {row}")
        seg_start = max(0, off - 400)
        print("\n该帧结束前 400 字符原始序列：")
        print(repr(text[seg_start:off]))

    # ── 背景地图（末尾帧）：· = 终端默认底（黑）, # = 夜空, B = 沙丘, W = 提亮夜空, 其他字母 = 其它色 ──
    palette_named = {}
    def tag(b):
        if b is None:
            return "·"
        if b[0] == "idx":
            return f"{b[1]:X}"
        r, g_, b_ = b
        key = (r, g_, b_)
        if key not in palette_named:
            palette_named[key] = "abcdefghijklmnopqrstuvwxyz"[len(palette_named) % 26]
        return palette_named[key]

    print("\n== 背景地图（x0..153, y0..41）· = 终端默认底 ==")
    legend = []
    for yy in range(42):
        row = "".join(tag(bg[yy][xx]) for xx in range(154))
        print(f"{yy:2} {row}")
    for key, ch in palette_named.items():
        legend.append(f"{ch}=#{key[0]:02X}{key[1]:02X}{key[2]:02X}")
    print("图例:", " ".join(legend))

    # 打印第一个黑格/默认底格附近的原始片段
    target = (black or default_bg)[:1]
    if target:
        off = target[0][2]
        seg = text[max(0, off - 120) : off + 60]
        print("\n== 首个异常格附近的原始序列 ==")
        print(repr(seg))


if __name__ == "__main__":
    main()

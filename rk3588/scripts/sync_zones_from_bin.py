"""把 bin 重写版里的标定区域坐标合回源码版配方。

背景：Windows 程序保存配置时会把 YAML 重写成程序内部格式
      （steps 与步骤同缩进、transitions 变列表、regions 挪到文件末尾），
      该格式不适合 RK3588 侧解析器与人工查看。
      故：保留源码版（人工可读、解析器已支持），仅把标定后的坐标覆盖进去。

用法：python sync_zones_from_bin.py <源码版yaml> <bin重写版yaml>
"""
import io
import re
import sys

src_path = sys.argv[1]
bin_path = sys.argv[2]

# ---- 1. 从 bin 版提取区域坐标 ----
bin_txt = io.open(bin_path, encoding='utf-8').read()
zones = {}
pat = re.compile(
    r'\n    (\w+):\s*\n\s*x1:\s*(\d+)\s*\n\s*y1:\s*(\d+)\s*\n'
    r'\s*x2:\s*(\d+)\s*\n\s*y2:\s*(\d+)')
for m in pat.finditer(bin_txt):
    zones[m.group(1)] = (int(m.group(2)), int(m.group(3)),
                        int(m.group(4)), int(m.group(5)))

if not zones:
    print('[warn] 未从 bin 版提取到区域坐标，源文件保持不变')
    sys.exit(0)

# ---- 2. 覆盖源码版的区域坐标 ----
lines = io.open(src_path, encoding='utf-8').read().split('\n')
out = []
cur = None
changed = []

for ln in lines:
    m = re.match(r'^    (\w+):\s*$', ln)
    if m:
        cur = m.group(1)
    elif re.match(r'^      (x1|y1|x2|y2):', ln):
        if cur in zones:
            key = ln.strip().split(':')[0]
            old = ln.strip().split(':')[1].strip()
            idx = {'x1': 0, 'y1': 1, 'x2': 2, 'y2': 3}[key]
            new = zones[cur][idx]
            if old != str(new):
                changed.append('%s.%s %s -> %d' % (cur, key, old, new))
            ln = '      %s: %d' % (key, new)
    out.append(ln)

io.open(src_path, 'w', encoding='utf-8', newline='\n').write('\n'.join(out))

print('[sync] 同步 %d 个区域到 %s' % (len(zones), src_path))
for c in changed:
    print('        ' + c)
if not changed:
    print('        （坐标无变化）')
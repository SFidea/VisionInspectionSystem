#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
黄金数据集校验 / 比对工具（P0 配套）

用途：
  1. inspect  —— 体检单个 case：帧完整性、步骤时间轴、违规明细、手部/物体统计
  2. compare  —— 并排比对两批数据（Windows 基线 vs RK3588），算 G4 一致率

为什么需要它：
  - 录完立刻知道数据合不合格，不用等到 P1 才发现问题
  - RK3588 对拍前，先在 PC 上把比对逻辑跑通

用法：
  python inspect_golden.py inspect <case_dir>
  python inspect_golden.py compare <windows_case_dir> <rk3588_case_dir> [--verbose]

依赖：仅 Python 标准库（跨平台，板子上也能跑）
"""

import argparse
import collections
import io
import json
import os
import sys

# ============================================================ 读取


def load_jsonl(path):
    """逐行读 jsonl，返回 list。坏行不中断（计数后告警）。"""
    rows, bad = [], 0
    if not os.path.exists(path):
        raise FileNotFoundError(path)
    with io.open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                rows.append(json.loads(line))
            except Exception:
                bad += 1
    if bad:
        print("  [警告] %s 有 %d 行无法解析" % (os.path.basename(path), bad))
    return rows


def load_case(case_dir):
    case_dir = case_dir.rstrip("/\\")
    d = {}
    d["dir"] = case_dir
    d["decisions"] = load_jsonl(os.path.join(case_dir, "decisions.jsonl"))
    d["inputs"] = load_jsonl(os.path.join(case_dir, "inputs.jsonl"))
    fdir = os.path.join(case_dir, "frames")
    d["frame_files"] = sorted(os.listdir(fdir)) if os.path.isdir(fdir) else []
    d["meta"] = {}
    mpath = os.path.join(case_dir, "meta.json")
    if os.path.exists(mpath):
        with io.open(mpath, encoding="utf-8") as f:
            d["meta"] = json.load(f)
    d["summary"] = {}
    spath = os.path.join(case_dir, "summary.json")
    if os.path.exists(spath):
        with io.open(spath, encoding="utf-8") as f:
            d["summary"] = json.load(f)
    return d


def merge(case):
    """decisions 与 inputs 按 frame 序号对齐成时间轴。"""
    dec_by_f = {r["frame"]: r for r in case["decisions"]}
    inp_by_f = {r["frame"]: r for r in case["inputs"]}
    frames = sorted(set(dec_by_f) | set(inp_by_f))
    out = []
    for fr in frames:
        d = dec_by_f.get(fr, {})
        i = inp_by_f.get(fr, {})
        hands = i.get("hands", [])
        objs = i.get("objects", [])
        out.append({
            "frame": fr,
            "ts_ms": d.get("ts_ms", i.get("ts_ms", 0)),
            "step_id": d.get("step_id", -1),
            "step_name": d.get("step_name", ""),
            "state": d.get("state", ""),
            "is_pass": d.get("is_pass", False),
            "conditions": d.get("conditions", {}),
            "violations": d.get("violations", []),
            "n_hands": len(hands),
            "valid_points": [h.get("valid_points", 0) for h in hands],
            "hands": hands,
            "objects": objs,
            "labels": [o.get("label", "") for o in objs],
        })
    return out


# ============================================================ 体检


def cmd_inspect(args):
    case = load_case(args.case_dir)
    tl = merge(case)
    name = os.path.basename(case["dir"])

    print("=" * 74)
    print("案例：%s" % name)
    print("=" * 74)

    # ---- 完整性 ----
    # ---- zones 快照（schema_version>=2 才有）----
    zones = case["meta"].get("zones") or []
    print("\n【区域快照】")
    if zones:
        for z in zones:
            print("  %-18s [%.0f,%.0f]-[%.0f,%.0f]"
                  % (z.get("id", "?"), z.get("x", 0), z.get("y", 0),
                     z.get("x", 0) + z.get("width", 0),
                     z.get("y", 0) + z.get("height", 0)))
    else:
        print("  无（schema_version < 2）")
        print("  ★ 该基线缺少区域快照 —— 一旦现场重新标定区域，")
        print("    历史 decisions.jsonl 的判定结果就永久无法复现，建议重录")
    warns_zone = not zones

    print("\n【完整性】")
    nd, ni, nf = len(case["decisions"]), len(case["inputs"]), len(case["frame_files"])
    print("  decisions.jsonl : %d 帧" % nd)
    print("  inputs.jsonl    : %d 帧" % ni)
    print("  frames/         : %d 张" % nf)
    ok = "OK" if (nd == ni) else "!! 不一致"
    print("  行数一致性      : %s" % ok)
    s = case["summary"]
    if s:
        print("  录制统计        : 写入%s丢弃%s 错误%s 耗时%ss"
              % (s.get("frames_written", "-"), s.get("frames_dropped", "-"),
                 s.get("errors", "-"), s.get("duration_sec", "-")))

    # ---- 步骤时间轴 ----
    print("\n【步骤时间轴】（状态/判定翻转点）")
    print("  %6s %9s %14s %7s %-30s %-12s" % ("帧", "t(s)", "步骤", "通过", "条件", "违规"))
    prev = None
    for r in tl:
        key = (r["step_id"], r["step_name"], r["is_pass"], tuple(sorted(r["violations"])))
        if key == prev:
            continue
        prev = key
        conds = ",".join("%s=%s" % (k, v) for k, v in sorted(r["conditions"].items()))
        print("  %6d %9.1f %14s %7s %-30s %-12s"
              % (r["frame"], r["ts_ms"] / 1000.0, r["step_name"],
                 r["is_pass"], conds[:30], ",".join(r["violations"])[:12]))

    # ---- 步骤分布 ----
    print("\n【步骤分布】")
    dist = collections.Counter(r["step_name"] for r in tl)
    for k, v in dist.most_common():
        print("  %-16s %4d 帧" % (k, v))

    # ---- 违规 ----
    print("\n【违规明细】（RK3588 对拍需 100% 对齐触发帧序号）")
    viol = collections.Counter()
    vframes = collections.defaultdict(list)
    for r in tl:
        for v in r["violations"]:
            viol[v] += 1
            vframes[v].append(r["frame"])
    if not viol:
        print("  无违规 —— 若本 case 属异常场景，说明没录到违规路径")
    for k, v in viol.most_common():
        fr = vframes[k]
        print("  %-16s %3d 帧   首帧=%d 末帧=%d" % (k, v, fr[0], fr[-1]))

    # ---- 手部 ----
    print("\n【手部】")
    nhand = collections.Counter(r["n_hands"] for r in tl)
    print("  每帧手数分布 : %s" % dict(sorted(nhand.items())))
    vp = collections.Counter()
    for r in tl:
        for v in r["valid_points"]:
            vp[v] += 1
    print("  valid_points : %s" % dict(sorted(vp.items())))
    if vp:
        common = vp.most_common(1)[0][0]
        print("  ★ 主值 %d —— RK3588 侧关键点数量必须与此一致" % common)
        if len(vp) > 1:
            print("  ! 存在多种点数，说明不同帧检出点数不同，"
                  "GetCenter() 分母会变，需在台账记录")

    # ---- 物体 ----
    print("\n【检出物体】（对象实例总数，非帧数）")
    objs = collections.Counter()
    for r in tl:
        for l in r["labels"]:
            objs[l] += 1
    for k, v in objs.most_common(12):
        print("  %-20s %5d" % (k, v))

    # ---- 质量体检 ----
    print("\n【质量体检】")
    warns = []
    if warns_zone:
        warns.append("meta.json 缺少 zones 快照 —— 区域一旦重新标定该基线即永久失效")
    if nd != ni:
        warns.append("decisions/inputs 行数不一致")
    if s.get("frames_dropped", 0) > 0:
        warns.append("录制期间丢弃 %s 帧（队列满）" % s.get("frames_dropped"))
    if s.get("errors", 0) > 0:
        warns.append("录制期间错误 %s 次" % s.get("errors"))
    if not viol:
        warns.append("无违规样本")
    if len(vp) > 1:
        warns.append("valid_points 不唯一")
    if os.path.basename(case["dir"]).strip() != case["meta"].get("case_name", "").strip():
        warns.append("目录名与 meta.case_name 不一致")
    if warns:
        for w in warns:
            print("  ! " + w)
    else:
        print("  OK —— 可作为 RK3588 对拍基线")

    print()
    return 0


# ============================================================ 比对（G4）


def cmd_compare(args):
    a = merge(load_case(args.base))
    b = merge(load_case(args.test))
    na, nb = len(a), len(b)

    print("=" * 74)
    print("G4 对拍：%s  vs  %s"
          % (os.path.basename(load_case(args.base)["dir"]),
             os.path.basename(load_case(args.test)["dir"])))
    print("=" * 74)
    print("\n【帧数】基线=%d 待测=%d %s"
          % (na, nb, "OK" if na == nb else "!! 不一致"))

    n = min(na, nb)

    # ---- 12 类条件逐帧布尔一致率 ----
    all_keys = set()
    for r in a[:n] + b[:n]:
        all_keys |= set(r["conditions"].keys())
    print("\n【条件一致率】（G4 主闸门）")
    print("  %-22s %10s %10s" % ("条件", "一致/总数", "一致率"))
    print("  " + "-" * 46)

    cond_stat = collections.defaultdict(lambda: [0, 0])
    step_diff = []
    viol_base = []
    viol_test = []

    for i in range(n):
        ra, rb = a[i], b[i]
        for k in all_keys:
            va = ra["conditions"].get(k)
            vb = rb["conditions"].get(k)
            if va is None or vb is None:
                continue          # 该帧未参与此条件评估（AND 短路），跳过
            st = cond_stat[k]
            st[1] += 1
            if va == vb:
                st[0] += 1
        # 步骤一致性
        if ra["step_id"] != rb["step_id"] or ra["step_name"] != rb["step_name"]:
            step_diff.append((i, ra["step_name"], rb["step_name"]))
        if ra["violations"]:
            viol_base.append((i, ra["violations"]))
        if rb["violations"]:
            viol_test.append((i, rb["violations"]))

    total_ok = total = 0
    for k in sorted(cond_stat):
        ok, tot = cond_stat[k]
        total_ok += ok
        total += tot
        rate = ok / tot * 100.0 if tot else 0.0
        flag = "" if rate >= 98.0 else "  <-- 未达标(≥98%)"
        print("  %-22s %10s %9.2f%%%s" % (k, "%d/%d" % (ok, tot), rate, flag))

    print("  " + "-" * 46)
    rate = total_ok / total * 100.0 if total else 0.0
    print("  %-22s %10s %9.2f%%" % ("【合计】", "%d/%d" % (total_ok, total), rate))

    # ---- 违规触发帧对齐 ----
    print("\n【违规触发帧序号】（要求 100% 对齐）")
    print("  基线违规帧 %d 个，待测违规帧 %d 个"
          % (len(viol_base), len(viol_test)))
    bset = set(i for i, _ in viol_base)
    tset = set(i for i, _ in viol_test)
    if not bset and not tset:
        print("  两边均无违规样本 —— 无法验证违规对齐，需补录异常场景")
    else:
        only_b = sorted(bset - tset)
        only_t = sorted(tset - bset)
        both = sorted(bset & tset)
        print("  对齐 %d，基线独有 %d，待测独有 %d"
              % (len(both), len(only_b), len(only_t)))
        if only_b and args.verbose:
            print("    基线独有帧: %s" % only_b[:20])
            for i in only_b[:10]:
                print("      frame%d 基线违规=%s 待测=%s"
                      % (i, a[i]["violations"], b[i]["violations"]))
        if only_t and args.verbose:
            print("    待测独有帧: %s" % only_t[:20])
        print("  %s" % ("OK" if not only_b and not only_t else "!! 存在错位"))

    # ---- 步骤推进一致性 ----
    print("\n【步骤推进一致性】")
    if not step_diff:
        print("  OK —— 全部 %d 帧步骤完全一致" % n)
    else:
        print("  !! %d / %d 帧步骤不一致" % (len(step_diff), n))
        for i, sa, sb in step_diff[:15]:
            print("    frame%d: 基线='%s' 待测='%s'" % (i, sa, sb))

    # ---- 结论 ----
    print("\n" + "=" * 74)
    print("【G4 结论】")
    hand_keys = [k for k in cond_stat
                 if k.startswith("Hand")]
    hand_bad = [k for k in hand_keys
                if cond_stat[k][1] and cond_stat[k][0] / cond_stat[k][1] * 100 < 100.0]
    ok_all = rate >= 98.0 and not step_diff and not only_b and not only_t
    print("  条件总一致率      : %.2f%%  (阈值 ≥98%%)  %s"
          % (rate, "PASS" if rate >= 98.0 else "FAIL"))
    if hand_keys:
        hr = sum(cond_stat[k][0] for k in hand_keys) / max(
            1, sum(cond_stat[k][1] for k in hand_keys)) * 100
        print("  手部条件一致率    : %.2f%%  (阈值 100%%)  %s"
              % (hr, "PASS" if hr >= 100.0 else "FAIL -> " + ",".join(hand_bad)))
    else:
        print("  手部条件          : 本批数据未含手部条件")
    print("  违规帧对齐        : %s"
          % ("PASS" if not only_b and not only_t else "FAIL"))
    print("  步骤推进一致      : %s"
          % ("PASS" if not step_diff else "FAIL"))
    print("\n  总体：%s" % ("PASS —— 可进入下一阶段" if ok_all else "FAIL —— 按映射文档第六节排查"))
    print("=" * 74)
    print()
    return 0 if ok_all else 1


# ============================================================ CLI


def main():
    ap = argparse.ArgumentParser(
        description="黄金数据集校验 / RK3588 对拍比对工具")
    ap.add_argument("cmd", choices=["inspect", "compare"])
    ap.add_argument("args", nargs="+")
    ap.add_argument("--verbose", action="store_true")
    a = ap.parse_args()

    if a.cmd == "inspect":
        return cmd_inspect(argparse.Namespace(case_dir=a.args[0]))
    return cmd_compare(argparse.Namespace(
        base=a.args[0], test=a.args[1], verbose=a.verbose))


if __name__ == "__main__":
    try:
        sys.exit(main())
    except FileNotFoundError as e:
        print("找不到文件: %s" % e)
        sys.exit(2)
    except Exception as e:
        print("错误: %s" % e)
        sys.exit(3)
#!/usr/bin/env python3
"""一条命令出全表：所有路由的「可用性 + 实测速度」。

为什么这一个脚本就够了——它读的是插件自己写下的两份事实，而不是我去 ping：
  1) ~/.dsh/our-free-model/availability.json
     插件每 15 分钟（settings.probeIntervalMinutes）亲自探一遍每个免费/EAC 模型，
     写下 state / latencyMs / ttftMs —— 这是"现在通不通、多快"的权威答案。
  2) ~/.dsh/channel-pack/token-ledger.json
     每一次真实调用都在线路上记了 requests/errors/outputTokens/
     tpsSum+tpsCount（逐次解码速度）/ttftSumMs+ttftCount（首字延迟）。
     这是"真实用起来多快"的权威答案，不是我合成的 ping。

用法：
  python -u tools/probes/route-report.py            # 打印报表
  python -u tools/probes/route-report.py --json out.json   # 另存全表
只读，不写 DSH 目录。
"""
from __future__ import annotations

import json
import os
import sys

HOME = os.environ.get("USERPROFILE", "")
PLUGIN = os.path.join(HOME, ".dsh", "our-free-model")
LEDGER = os.path.join(HOME, ".dsh", "channel-pack", "token-ledger.json")


def jload(path):
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return json.load(fh)
    except Exception as error:                          # 缺文件/坏 JSON 都要看得见原因
        print(f"!! 读不到 {path}: {error}")
        return None


def flatten(node, prefix=""):
    """把任意嵌套的对象摊平成 (路径, 叶子字典)。"""
    out = []
    if isinstance(node, dict):
        numbers = {k: v for k, v in node.items() if isinstance(v, (int, float, str, bool)) or v is None}
        if numbers and not any(isinstance(v, (dict, list)) for v in node.values()):
            out.append((prefix, numbers))
            return out
        for key, value in node.items():
            out.extend(flatten(value, f"{prefix}.{key}" if prefix else str(key)))
    elif isinstance(node, list):
        for i, value in enumerate(node):
            out.extend(flatten(value, f"{prefix}[{i}]"))
    return out


def ledger_rows():
    raw = jload(LEDGER) or {}
    rows = {}

    def walk(node, prefix):
        if not isinstance(node, dict):
            return
        numbers = {k: v for k, v in node.items() if isinstance(v, (int, float))}
        if "requests" in numbers:
            rows[prefix] = numbers
            return
        for key, value in node.items():
            if isinstance(value, dict):
                walk(value, f"{prefix}|{key}" if prefix else key)

    walk(raw, "")
    return rows


def main() -> int:
    availability = jload(os.path.join(PLUGIN, "availability.json"))
    catalog = jload(os.path.join(PLUGIN, "catalog.json")) or {}
    rows = ledger_rows()

    print("=" * 104)
    print("A. 插件自测的可用性与延迟（availability.json，插件每 15 分钟自己探的）")
    print("=" * 104)
    for path, leaf in flatten(availability):
        state = leaf.get("state") or leaf.get("status") or "?"
        latency = leaf.get("latencyMs")
        ttft = leaf.get("ttftMs")
        detail = str(leaf.get("detail") or "")[:44]
        print(f"  {path:<44} {str(state):<16} latency={latency if latency is not None else '-':>7} "
              f"ttft={ttft if ttft is not None else '-':>7} {detail}")

    print()
    print("=" * 104)
    print("B. 真实调用的实测速度（token-ledger.json；tps=逐次解码速度均值，ttft=首字延迟均值）")
    print("=" * 104)
    measured = []
    for key, row in rows.items():
        n = row.get("requests", 0)
        if n < 5:
            continue
        tps_n = row.get("tpsCount", 0)
        ttft_n = row.get("ttftCount", 0)
        measured.append({
            "route": "/".join([p for p in key.split("|") if p][-2:]),
            "key": key,
            "req": int(n),
            "errPct": round(100.0 * row.get("errors", 0) / n, 1),
            "out": int(row.get("outputTokens", 0)),
            "tps": round(row["tpsSum"] / tps_n, 0) if tps_n else None,
            "ttftMs": round(row["ttftSumMs"] / ttft_n, 0) if ttft_n else None,
        })
    measured.sort(key=lambda r: -(r["tps"] or 0))
    print(f"{'route':<46}{'req':>6}{'err%':>7}{'tps':>8}{'ttft_ms':>9}")
    for r in measured[:30]:
        print(f"{r['route']:<46}{r['req']:>6}{r['errPct']:>7}{(r['tps'] or 0):>8.0f}{(r['ttftMs'] or 0):>9.0f}")
    if len(measured) > 30:
        print(f"  … 另有 {len(measured) - 30} 条（--json 里是全表）")

    print()
    print("=" * 104)
    print("C. 又快又稳的候选（n>=50 且 错误率<5%，按 tps 排）")
    print("=" * 104)
    good = [r for r in measured if r["req"] >= 50 and r["errPct"] < 5.0 and r["tps"]]
    for r in good[:12]:
        print(f"  {r['route']:<46} n={r['req']:<6} err={r['errPct']:>4}% tps={r['tps']:>6.0f} ttft={r['ttftMs']:>6.0f}ms")

    print()
    print(f"模型宇宙：匿名免费 {len(catalog.get('entries') or [])} 条 / EAC 封签 {len(catalog.get('sealIds') or [])} 条 / "
          f"kilo-free {len(catalog.get('kiloRows') or [])} 条；账本里有调用记录的 (渠道,模型) 组合 {len(rows)} 条，"
          f"其中 requests>=5 的 {len(measured)} 条")

    if "--json" in sys.argv:
        out = sys.argv[sys.argv.index("--json") + 1]
        with open(out, "w", encoding="utf-8") as fh:
            json.dump({"measured": measured, "availability": availability}, fh, ensure_ascii=False, indent=1)
        print(f"WROTE {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

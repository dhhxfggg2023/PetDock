#!/usr/bin/env python3
"""从本机拉 GitHub 文件：GitHub 直连被网络屏蔽，走中继。

为什么需要它——2026-10-15 在本机实测：
  * 直连：baidu/npm registry 都 200，但 github.com / api.github.com / raw.githubusercontent.com
    一律连不上（curl 报 000，python 报 TimeoutError）；DNS 正常（dns.scu.edu.cn 解析出真实 IP），
    也没有任何代理配置（env/注册表/WinHTTP 都是空的）→ 是网络层封禁，不是配置错误。
  * 中继：https://ghproxy.net/ 与 https://gh-proxy.com/ 前缀 raw.githubusercontent.com 后
    都能 200 取到二进制（实测 1.6MB PNG 完整下载）。
  * 中继**只转发 raw**，不转发 api.github.com（403）与 github.com 的 HTML（403）。
    所以列目录要在工具通道（web_fetch 能到 api.github.com）做，把结果存成清单再用本脚本拉。

用法：
  python -u tools/fetch-github.py EDMOK/blue-fish-archive assets/deepseek_whale.png
  python -u tools/fetch-github.py EDMOK/blue-fish-archive --from-list list.txt --out-dir dl
  可选：--branch main --out FILE --tries 3 --timeout 90
清单格式：每行一个仓库内相对路径（# 开头为注释）。
"""
from __future__ import annotations

import argparse
import os
import sys
import urllib.parse
import urllib.request

RELAYS = ("https://ghproxy.net/", "https://gh-proxy.com/")
RAW = "https://raw.githubusercontent.com/"


def fetch(relay: str, raw_url: str, dest: str, timeout: int) -> int:
    """下到 dest（已存在则断点续传），返回字节数；失败抛异常。"""
    started = os.path.getsize(dest) if os.path.exists(dest) else 0
    request = urllib.request.Request(relay + raw_url, headers={"User-Agent": "fetch-github/1.0"})
    if started:
        request.add_header("Range", f"bytes={started}-")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        data = response.read()
        code = response.status
    if started and code == 206:
        with open(dest, "ab") as fh:
            fh.write(data)
        return started + len(data)
    if started and code == 200:                       # 中继不支持 Range：整份重写
        started = 0
    with open(dest, "wb") as fh:
        fh.write(data)
    return len(data)


def one(repo: str, path: str, out: str, branch: str, tries: int, timeout: int) -> bool:
    raw_url = f"{RAW}{repo}/{branch}/{urllib.parse.quote(path)}"
    os.makedirs(os.path.dirname(os.path.abspath(out)) or ".", exist_ok=True)
    for attempt in range(1, tries + 1):
        relay = RELAYS[(attempt - 1) % len(RELAYS)]
        try:
            size = fetch(relay, raw_url, out, timeout)
            print(f"  OK   {size:>9} B  {path}  <- {relay}")
            return True
        except Exception as error:                     # 换中继重试；最后一次报出原因
            print(f"  try{attempt} {relay} {type(error).__name__}: {error}")
    print(f"  FAIL {path}")
    return False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("repo", help="owner/name")
    parser.add_argument("path", nargs="?", help="仓库内相对路径")
    parser.add_argument("--branch", default="main")
    parser.add_argument("--out")
    parser.add_argument("--out-dir", default=".")
    parser.add_argument("--from-list")
    parser.add_argument("--tries", type=int, default=3)
    parser.add_argument("--timeout", type=int, default=90)
    args = parser.parse_args()

    jobs: list[tuple[str, str]] = []
    if args.from_list:
        with open(args.from_list, "r", encoding="utf-8") as fh:
            for line in fh:
                item = line.strip()
                if item and not item.startswith("#"):
                    jobs.append((item, os.path.join(args.out_dir, os.path.basename(item))))
    elif args.path:
        jobs.append((args.path, args.out or os.path.basename(args.path)))
    else:
        parser.error("给一个 path，或用 --from-list")

    ok = 0
    for path, dest in jobs:
        if one(args.repo, path, dest, args.branch, args.tries, args.timeout):
            ok += 1
    print(f"SUMMARY ok={ok} failed={len(jobs) - ok} total={len(jobs)}")
    return 0 if ok == len(jobs) else 1


if __name__ == "__main__":
    raise SystemExit(main())

# market-plugins/ — 独立插件（插件市场的货架）

这个目录里**每一个子目录**就是一个会出现在面板「插件市场」里的插件。

## 什么放这里

**只放不随主程序发行的插件。** 判断标准很简单：

| 情况 | 放哪 |
| --- | --- |
| 用户装好 PetDock 就应该有的（示例宠物、界面壳、行为脑…） | `packages/plugins/`（随包发，**不进市场**） |
| 用户想额外加的、可选的功能 | **这里** |

内置插件如果也上架，市场列表里会全是「已装 0.1.0」，点「更新」只是把同一个文件再解一遍。
市场是「额外装的东西」的地方。

## 一个插件长这样

```
market-plugins/
└── my-extra-plugin/
    ├── petdock-plugin.json     ← 必需（清单，格式同 packages/plugins/*/）
    ├── MyExtraPlugin.dll       ← 或 JS/脚本入口，由清单的 entry 决定
    └── …
```

## 怎么发布

1. 把插件目录放进 `market-plugins/<目录名>/`；
2. 跑 `node tools/gen-market-index.mjs` —— 它会算出每个插件的 sha256 与体积，写进 `plugins/index.json`；
3. 把插件目录打成 zip 上传到 **本仓库的 Release**（同一个 release，文件名 = 清单里的 `id` + `.zip`）；
4. 提交 `plugins/index.json`。

`npm run dist` 里已经接了索引生成与一致性校验（`ix-market-1/2/3`），漏上传或 sha 对不上会红。

## 用户那边发生什么

面板 → 插件页 → 「浏览插件市场…」→ 列表出现 → 点「安装」：

程序从 `raw.githubusercontent.com` 拉索引 → 从本仓库 Release 下载 zip →
**校验 sha256**（对不上就丢弃）→ 交给和本地安装完全相同的管线（清单校验、体积上限、进度、取消）。

下载失败、离线、索引坏了，三种情况界面分别报，不混成「没有插件」。

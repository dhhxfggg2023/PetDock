# 应用图标（**自绘**占位图，可发行）

本目录的 12 个文件是 `tools/make-icons.py` **自己画出来**的，不是任何第三方图片的派生物。

```
python tools/make-icons.py                      # 默认：画自绘占位图（= 本目录现在这一套）
python tools/make-icons.py <source> [outdir]    # 用你自己的图（本机构建；不要提交产物）
```

产出：`icon-{16,24,32,48,64,128,256,512}.png`、多分辨率 `icon.ico`（16/24/32/48/64/128/256）、
squircle 蒙版变体 `icon-rounded-{64,256,512}.png`。

## 为什么默认是「自绘」而不是「读源图」

这里原先有 `assets/icons/source/icon.jpg`（163,338 B，512×512），**来源不明、无授权**（第三方转发，
无作者、无来源、无许可）。按 `docs/52-legal-licensing.md` §5「只发布能力，永不发布别人的内容」，
不只是那张源图不能进仓库——**由它派生的图标同样不能**。所以它已出库（`git rm --cached`），文件仍在
所有者的本机：`assets/user/icons-source/icon.jpg`（`assets/user/` 已被 `.gitignore` 覆盖，永不发行）。

仓库里提交的这套是白脸猫耳占位图，配色取自 `docs/31` 示例宠物金图基线中心的 RGB 86,132,214，
由本项目自绘、随内核以 Apache-2.0 授权，**可随发行物分发**。

## 换成正式图标时

把有授权的图交给脚本，并同时更新 `assets/ASSETS.md` 里那两行的「来源 / 可否发行」：

```
python tools/make-icons.py <有授权的源图>
```

只想给本机构建换图、不让产物进仓库的做法：按上面生成后**不要提交**（脚本会打印一行 WARNING）。

# 应用图标（**自绘像素猫**，可发行）

本目录的 12 个文件是 `tools/make-icons.py --pixel` **自己画出来**的，不是任何第三方图片的派生物。

```
python tools/make-icons.py --pixel              # 像素猫（= 本目录现在这一套；16×16 网格原生，整数倍放大）
python tools/make-icons.py                      # 旧的自绘占位图（白脸猫耳平涂版）
python tools/make-icons.py <source> [outdir]    # 用你自己的图（本机构建；不要提交产物）
```

产出：`icon-{16,24,32,48,64,128,256,512}.png`、多分辨率 `icon.ico`（16/24/32/48/64/128/256）、
squircle 蒙版变体 `icon-rounded-{64,256,512}.png`。

## 形象

2026-10-26 所有者从若干候选里选定：深蓝底、奶油白毛、方眼带一块高光、粉鼻、两只尖耳。
形状只画在 16×16 网格上（`tools/make-icons.py` 的 `draw_pixel_cat()`），**每一档尺寸都是整数倍
NEAREST 放大**，所以 16/32/48/64/128/256/512 全是清晰方块；只有 24（1.5 倍）用 LANCZOS。
`icon.ico` 由脚本自己拼容器（每档一张 PNG 负载），不经过 Pillow 的 thumbnail 插值。

图标同时被嵌进 exe：`packages/core/PetDock.Core/PetDock.Core.csproj` 的 `<ApplicationIcon>`
指向本目录的 `icon.ico`，所以资源管理器/任务栏里显示的就是这一套。

## 为什么默认是「自绘」而不是「读源图」

这里原先有 `assets/icons/source/icon.jpg`（163,338 B，512×512），**来源不明、无授权**（第三方转发，
无作者、无来源、无许可）。按 `原规格 52` §5「只发布能力，永不发布别人的内容」，
不只是那张源图不能进仓库——**由它派生的图标同样不能**。所以它已出库（`git rm --cached`），文件仍在
所有者的本机：`assets/user/icons-source/icon.jpg`（`assets/user/` 已被 `.gitignore` 覆盖，永不发行）。

仓库里提交的这套是自绘像素猫，由本项目自绘、随内核以 Apache-2.0 授权，**可随发行物分发**。

## 换成正式图标时

把有授权的图交给脚本，并同时更新 `assets/ASSETS.md` 里那两行的「来源 / 可否发行」：

```
python tools/make-icons.py <有授权的源图>
```

只想给本机构建换图、不让产物进仓库的做法：按上面生成后**不要提交**（脚本会打印一行 WARNING）。

改像素猫的形状：改 `draw_pixel_cat()`，重新跑 `--pixel`，别在 512 上补细节——放大用的是 NEAREST，
补进去的细节在 16px 下会消失。

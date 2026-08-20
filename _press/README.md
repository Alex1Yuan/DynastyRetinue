# 发布物料 · 索引

Dynasty Retinue & Refit（家族卫队 · 座舰改装）1.0 发布相关的全部材料。

代码仓库：https://github.com/Alex1Yuan/DynastyRetinue　（tag `v1.0.0`）
发布包：`D:\RT_RetinueMod\dist\DynastyRetinue-1.0.0.zip`

---

## 文件

| 文件 | 用途 | 状态 |
|---|---|---|
| `nexus_zh.md` | Nexus 页面 · 中文 | ✅ 可直接贴 |
| `nexus_en.md` | Nexus 页面 · 英文 | ✅ 可直接贴 |
| `bilibili.md` | B站标题 / 简介 / 分镜脚本 | ✅ |
| `配音稿_TTS.md` | 剪映「文本朗读」用的分段稿 | ✅ |
| `测试与拍摄清单.md` | 测试流程 + 拍摄清单（已全部跑完）| 存档参考 |
| `video/` | Remotion 工程，出三个插入片段 | ✅ 可渲染 |

---

## 素材

三段实机录屏在 `C:\Users\kyua805\Videos\Desktop\`：

| 文件 | 时长 | 内容 |
|---|---|---|
| `...09.02.41.01.mp4` | ~2.5 min | 招募界面 / 换船 / 巡洋+大巡 / 改装页面 / 设置 |
| `...09.38.22.03.mp4` | ~6 min | 地面战斗 / 镜头自由观察 / 卫兵永久阵亡 |
| `...09.48.38.05.mp4` | ~5 min | 海战 / 五项加成实战 / 撞角 |

还有两张 08:57 的 png 截图（HDR 已关，格式正确）。

**还缺的**：UMM 面板全景（中文），版本号要是 `1.0.0`。
之前建议排到 1.0 之后拍，现在 1.0 已经出来了，可以补。

---

## Remotion 片段

```bash
cd D:\RT_RetinueMod\_press\video
npx remotion studio                    # 实时预览，可在界面里直接改数值

npx remotion render TitleCard   out/TitleCard.mp4
npx remotion render ShipStats   out/ShipStats.mp4

# ★FeatureBars 背景是透明的，必须带 alpha 渲染★
npx remotion render FeatureBars out/FeatureBars.mov --codec=prores --prores-profile=4444
```

| 片段 | 时长 | 怎么用 |
|---|---|---|
| `TitleCard` | 3 秒 | 接在冷开场之后，整屏 |
| `FeatureBars` | 8 秒 | 四条下三分之一横条，**叠在实机画面上** |
| `ShipStats` | 8 秒 | 座舰加成对照，整屏或半透明叠在改装画面上 |

`ShipStats` 里的数字取自代码默认值（`Main.cs` 的 Settings 字段），
不是从某次日志抄的。用百分比而非绝对值——护盾上限的基数随船而变。

---

## 剪辑顺序建议

1. 剪映里排主干：三段录屏 → 按 `bilibili.md` 的分镜切
2. 贴配音：`配音稿_TTS.md` 分段生成，对轴
3. 叠 Remotion 片段：`TitleCard` 在 00:15，`FeatureBars` 压在成长那段，
   `ShipStats` 压在座舰那段
4. 配字幕：卸载那一段**必须**有字幕，信息量太大
5. 导出 1080p60

---

## 待办

- [ ] 补拍 UMM 面板（中文，版本号 1.0.0）
- [ ] 从第一段录屏里截「座舰改装前后同机位」两帧，做对比图
- [ ] 洋红那张截图（不按流程卸载的样子）—— 已有，在对话记录里，需要从游戏重现或找回
- [ ] Nexus 页面配图：封面 + 6–8 张
- [ ] 发布：Nexus 建页 + B站投稿

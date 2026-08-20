# 发布物料 · 索引

Dynasty Retinue & Refit（家族卫队 · 座舰改装）1.0 发布相关的全部材料。

代码仓库：https://github.com/Alex1Yuan/DynastyRetinue　（tag `v1.0.5`）
发布包：`D:\RT_RetinueMod\dist\DynastyRetinue-1.0.5.zip`

---

## 文件

| 文件 | 用途 | 状态 |
|---|---|---|
| `nexus_zh.md` | Nexus 页面 · 中文 | ✅ 可直接贴 |
| `nexus_en.md` | Nexus 页面 · 英文 | ✅ 可直接贴 |
| `bilibili.md` | B站标题 / 简介 | ✅ |
| `配音稿_TTS.md` | 配音文稿（已用 edge-tts 生成）| ✅ |
| `vo/` | 六段中文女声 mp3 + srt | ✅ |
| `screenshots/` | 11 张，含封面 | ✅ |
| `video/out/家族卫队_宣传片.mp4` | **成片，3 分 32 秒，可直接投稿** | ✅ |
| `测试与拍摄清单.md` | 测试流程（已全部跑完）| 存档参考 |

---

## 成片

```bash
cd D:\RT_RetinueMod\_press\video
set TEMP=D:\_tmp\remotion & set TMP=D:\_tmp\remotion

# 4K（投稿用这个）
npx remotion render Film out/家族卫队_宣传片_4K.mp4 --codec=h264 --crf=19 --scale=2 --concurrency=5

# 1080p
npx remotion render Film out/家族卫队_宣传片.mp4 --codec=h264 --crf=18
```

★`TEMP` 必须指到 D 盘★ —— Remotion 打包时会把整个 `public/`（4K 素材 469 MB）
复制进临时目录，C 盘塞不下会直接 `ENOSPC` 报错中止。

`--scale=2` 把 1920×1080 的合成渲成 3840×2160：素材本身就是 4K，等于原生；
卡片和字幕也按 2 倍像素渲染，不是把 1080p 拉大。

结构（212 秒 / 60fps）：

| 起 | 段 | 画面 |
|---|---|---|
| 0:00 | 冷开场 | 战斗，压屏「这五个人不是我在操作」，无配音 |
| 0:13 | 标题 | `TitleCard` |
| 0:17 | 这是什么 | 招募面板 → 战斗 |
| 0:41 | 镜头不跟随 | 战斗 |
| 1:07 | 招募与成长 | 招募面板 → 战斗 → 战斗 + `FeatureBars`（只放前两条）|
| 1:43 | 精英与代价 | 灵能精英 → 战斗 |
| 2:20 | 座舰改装 | 船坞对话框 → 装备界面 → 虚空战 + `ShipStats`（overlay 模式）|
| 2:50 | 卸载安全 | `UninstallCard` 三条路径 |
| 3:26 | 尾板 | `EndCard` |

### 改内容怎么改

- **改词** → 改 `配音稿_TTS.md`，重跑 edge-tts（命令在那个文件末尾），
  再跑 `py tools/srt2ts.py _press/vo _press/video/src/captions.ts`，最后重渲
- **换镜头** → 改 `src/Film.tsx` 里的 `<Clip src=... from=... />`
- **换素材片段** → 用下面「切片」一节的 ffmpeg 命令重切进 `public/footage/`

### 切片

原录屏在 `_press/footage_raw/`（6.3 GB，**不进 git**）。
`public/footage/` 里是切好的 4K 片段，469 MB，也不进 git。

★UI 段（招募面板、船坞对话框、装备界面）要裁，战斗段不裁★

```bash
# 战斗 / 虚空：原生 4K，不加任何缩放滤镜
-c:v libx264 -crf 18 -pix_fmt yuv420p

# UI：裁到面板本身再用 lanczos 拉到 4K
-vf "crop=2560:1440:640:320,scale=3840:2160:flags=lanczos"
```

UI 为什么还要裁 —— 不裁的话面板文字在 4K 帧里只占那么一点，
B站转码到 1080p 又回到"看不清"的原点。裁完文字大 1.5 倍，
顺带把右上角的 FPS 叠层和空画面边缘切掉了。
那个裁切框招募面板和船坞对话框、装备界面三个都罩得住。

### 原录屏里可用的区间（已核对）

| 录像 | 区间 | 内容 |
|---|---|---|
| A `09.02.41` | 8–24s | 船坞 · 座舰改装对话框 |
| A | **25–35s** | 招募面板（只有这十秒，之后就关了）|
| A | 46–100s | 舰船装备界面；**87–96s 的 tooltip 上有「Mod 已计算」** |
| A | ~165s | UMM 设置面板 |
| B `09.38.22` | 全程 | 地面战斗。同一个房间同一机位 —— 镜头不跟随本来就是卖点，<br>所以隔两分钟切出来的两块看着一样，成片里靠缓慢推进拉开差别 |
| C `09.48.38` | 175–215s | 虚空战，大巡洋舰 + 射界。其余是读盘和星图赶路 |

---

## 待办

- [ ] Nexus 建页（贴 `nexus_zh.md` + `nexus_en.md`，配 `screenshots/`）
- [ ] B站投稿（贴 `bilibili.md`，传 4K 成片）

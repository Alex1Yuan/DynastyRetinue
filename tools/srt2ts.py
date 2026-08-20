# -*- coding: utf-8 -*-
"""把 edge-tts 产出的 .srt 转成 Remotion 直接 import 的 TS 模块。

为什么不在 Remotion 里运行时解析 srt：
  Remotion 的组件是同步渲染的，运行时 fetch + 解析会让第一帧拿不到字幕，
  渲染出来是空的。编译期转成静态数组最省事，也让字幕内容进 git diff。
"""
import re, sys, pathlib, json

VO = pathlib.Path(sys.argv[1])
OUT = pathlib.Path(sys.argv[2])
TS = re.compile(r"(\d+):(\d+):(\d+),(\d+)")

def secs(m):
    h, mi, s, ms = (int(x) for x in m.groups())
    return h * 3600 + mi * 60 + s + ms / 1000.0

blocks = {}
for f in sorted(VO.glob("0*.srt")):
    key = f.stem.split("_", 1)[0]          # "01"
    cues = []
    for chunk in f.read_text(encoding="utf-8").strip().split("\n\n"):
        lines = [l for l in chunk.splitlines() if l.strip()]
        if len(lines) < 3:
            continue
        a, b = lines[1].split(" --> ")
        text = " ".join(lines[2:]).strip()
        # edge-tts 偶尔给出 end < 上一条 start 的重叠区间，直接用会让两条字幕同屏。
        # 交给渲染端按 start 排序取最后一条命中的，这里只保证 end >= start。
        st, en = secs(TS.match(a)), secs(TS.match(b))
        cues.append({"from": round(st, 3), "to": round(max(en, st + 0.4), 3), "text": text})
    blocks[key] = cues

body = ",\n".join(f'  "{k}": {json.dumps(v, ensure_ascii=False)}' for k, v in blocks.items())
OUT.write_text(
    "// 由 tools/srt2ts.py 从 _press/vo/*.srt 生成，不要手改。\n"
    "// 改词请改 _press/配音稿_TTS.md，重跑 edge-tts，再跑这个脚本。\n\n"
    "export type Cue = { from: number; to: number; text: string };\n\n"
    "export const CAPTIONS: Record<string, Cue[]> = {\n" + body + ",\n};\n",
    encoding="utf-8",
)
print(f"{OUT}  {sum(len(v) for v in blocks.values())} 条")

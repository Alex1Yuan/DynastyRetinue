import io, json, re

raw = io.open(r"D:\RT_RetinueMod\ref\ship_plan_raw.txt", encoding="utf-8-sig").read()
# 文件是 '"plan": "....."\n  },\n  "workflowProgress": [...' 的片段
m = re.match(r'\s*"plan":\s*', raw)
s = raw[m.end():]
dec = json.JSONDecoder()
plan, _ = dec.raw_decode(s)

io.open(r"D:\RT_RetinueMod\ref\SHIP_PLAN.md", "w", encoding="utf-8").write(plan)
print("已写出 SHIP_PLAN.md，%d 字符" % len(plan))

"""列出所有存档的进度信息，用来挑一个接近通关的档。"""
import io, os, json, zipfile

SG = r"C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Saved Games"

rows = []
for fn in sorted(os.listdir(SG)):
    if not fn.lower().endswith(".zks"):
        continue
    p = os.path.join(SG, fn)
    try:
        z = zipfile.ZipFile(p)
        h = json.loads(z.read("header.json").decode("utf-8", "replace"))
    except Exception as e:
        rows.append((fn, "读取失败: %s" % e, "", "", ""))
        continue

    def g(*keys):
        for k in keys:
            if k in h and h[k] not in (None, ""):
                return h[k]
        return ""

    rows.append((
        fn,
        str(g("Name", "SaveName")),
        str(g("GameStarted", "SaveTime", "SystemSaveTime"))[:19],
        str(g("PlayerCharacterName", "CharacterName")),
        str(g("QuestName", "AreaName", "LocationName")),
    ))

w = max(len(r[0]) for r in rows) if rows else 10
print("%-*s | %-8s | %-19s | %s" % (w, "文件", "名称", "时间", "位置/任务"))
print("-" * (w + 70))
for r in rows:
    print("%-*s | %-8s | %-19s | %s  %s" % (w, r[0], r[1][:8], r[2], r[3], r[4]))

import zipfile, json, sys

PATH = r'D:\RT_RetinueMod\savebackup_20260815\ForImport_1.zks'

z = zipfile.ZipFile(PATH)
d = z.read('party.json').decode('utf-8', errors='replace')

i = d.find('StarshipEntity, Code')
s = d.rfind('{', 0, i)

# brace-match respecting strings
depth = 0
j = s
n = len(d)
while j < n:
    c = d[j]
    if c == '"':
        j += 1
        while j < n and d[j] != '"':
            if d[j] == '\\':
                j += 2
            else:
                j += 1
    elif c == '{':
        depth += 1
    elif c == '}':
        depth -= 1
        if depth == 0:
            break
    j += 1

seg = d[s:j + 1]
print('ship object length:', len(seg))
o = json.loads(seg)
print('top-level keys:', list(o.keys()))
print('Blueprint:', o.get('Blueprint'))
print()

parts = o.get('Parts')
print('Parts container keys:', list(parts.keys()) if isinstance(parts, dict) else type(parts))

lst = None
if isinstance(parts, dict):
    inner = parts.get('Container', parts)
    if isinstance(inner, dict):
        print('inner keys:', list(inner.keys()))
        for k in inner:
            if isinstance(inner[k], list):
                lst = inner[k]
                print('using list key:', k)
                break
    elif isinstance(inner, list):
        lst = inner

if lst is None:
    print('could not locate parts list')
    sys.exit()

print('part count:', len(lst))
print()
for p in lst:
    if not isinstance(p, dict):
        continue
    t = p.get('$type', '?').split(',')[0]
    short = t.split('.')[-1]
    interesting = ('ViewSettings' in short or 'UnitState' in short
                   or 'HoldPrefab' in short or 'Starship' in short)
    if interesting:
        body = {k: v for k, v in p.items() if k not in ('$id', '$type')}
        txt = json.dumps(body)[:300]
        print('  >>', short, txt)
    else:
        print('    ', short)

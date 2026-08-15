"""Build inheritance (prototype) map + component detection from blueprints-pack.bbp."""
import re, json, io, collections, struct
import bbp

PARENT_RE = re.compile(rb'^.{16}\x20([0-9a-f]{32})', re.S)
COMP_RE = re.compile(rb'\$([A-Za-z0-9_]+)\$[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}')
# length-prefixed short strings (field/name table entries)
NAME_RE = re.compile(rb'[\x01-\x40]([A-Za-z][A-Za-z0-9_.]{2,63})')

byguid, bytype = bbp.load_cheat()
f, spans = bbp.load_pack()


def blob_of(g):
    s, e = spans[g]
    f.seek(s)
    return f.read(e - s)


def parent_of(g):
    b = blob_of(g)
    m = PARENT_RE.match(b)
    if not m:
        return None
    p = m.group(1).decode()
    return p if p in byguid and p != g else None


def chain(g, maxd=12):
    out, cur, seen = [], g, set()
    while cur and cur not in seen and len(out) < maxd:
        seen.add(cur)
        out.append(cur)
        cur = parent_of(cur)
    return out


def components(g, inherit=True):
    """set of component class names on this blueprint (optionally incl. prototypes)"""
    s = set()
    for h in (chain(g) if inherit else [g]):
        s |= set(m.group(1).decode() for m in COMP_RE.finditer(blob_of(h)))
    return s


def names(g):
    return set(m.group(1).decode() for m in NAME_RE.finditer(blob_of(g)))


def nm(g):
    x = byguid.get(g)
    return x['Name'] if x else '?' + g


if __name__ == '__main__':
    import sys
    W = bytype['Kingmaker.Blueprints.Items.Weapons.BlueprintItemWeapon']
    roots = collections.Counter()
    for x in W:
        c = chain(x['Guid'])
        roots[nm(c[-1])] += 1
    print('# weapon prototype roots')
    for k, v in roots.most_common(40):
        print(v, k)

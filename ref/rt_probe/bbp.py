"""Read Owlcat blueprints-pack.bbp: GUID -> record bytes, and pull ASCII guid refs.

Layout:
  uint32 count
  count * (16-byte guid, uint32 offset)   # offset = absolute file offset of record
  ... records (contiguous, in offset order)

Inside a record, blueprint references are stored as ASCII 32-char lowercase hex
preceded by a 0x20 length byte. That is enough to classify by referenced type.
"""
import struct, io, json, re, sys, collections

BUNDLES = r"H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles"
BBP = BUNDLES + r"\blueprints-pack.bbp"
CHEAT = BUNDLES + r"\cheatdata.json"

GUID_RE = re.compile(rb'[0-9a-f]{32}')


def load_cheat():
    d = json.load(io.open(CHEAT, encoding='utf-8'))
    byguid, bytype = {}, collections.defaultdict(list)
    for x in d['Entries']:
        t = x['TypeFullName'].split(',')[0]
        x['Type'] = t
        byguid[x['Guid']] = x
        bytype[t].append(x)
    return byguid, bytype


def guid_net(b):
    """16 raw bytes -> .NET Guid 'N' string (first 4/2/2 little-endian)"""
    return (b[3::-1] + b[5:3:-1] + b[7:5:-1] + b[8:]).hex()


def load_pack():
    """returns {guid_hex: (start, end)}"""
    f = open(BBP, 'rb')
    n = struct.unpack('<I', f.read(4))[0]
    idx = f.read(n * 20)
    ents = []
    for i in range(n):
        b = idx[i * 20:i * 20 + 20]
        ents.append((guid_net(b[:16]), struct.unpack('<I', b[16:20])[0]))
    size = f.seek(0, 2)
    ents.sort(key=lambda t: t[1])
    spans = {}
    for i, (g, off) in enumerate(ents):
        end = ents[i + 1][1] if i + 1 < len(ents) else size
        spans[g] = (off, end)
    return f, spans


def refs(f, spans, guid):
    """set of guids referenced inside the record for `guid`"""
    if guid not in spans:
        return None
    s, e = spans[guid]
    f.seek(s)
    blob = f.read(e - s)
    return blob, set(m.group(0).decode() for m in GUID_RE.finditer(blob))


if __name__ == '__main__':
    byguid, bytype = load_cheat()
    f, spans = load_pack()
    print('pack records:', len(spans), 'cheat entries:', len(byguid))
    print('overlap:', len(set(spans) & set(byguid)))

import struct,sys,os,glob
from lz4mod import lz4_block_decompress
class R:
    def __init__(s,b): s.b=b; s.o=0
    def u32(s): v=struct.unpack_from('>I',s.b,s.o)[0]; s.o+=4; return v
    def i64(s): v=struct.unpack_from('>q',s.b,s.o)[0]; s.o+=8; return v
    def u16(s): v=struct.unpack_from('>H',s.b,s.o)[0]; s.o+=2; return v
    def cstr(s):
        e=s.b.index(b'\x00',s.o); r=s.b[s.o:e].decode('utf8','replace'); s.o=e+1; return r
def unpack(p, limit=None):
    f=open(p,'rb'); hdr=f.read(220); r=R(hdr)
    if r.cstr()!='UnityFS': return None
    ver=r.u32(); r.cstr(); r.cstr(); r.i64(); ci=r.u32(); ui=r.u32(); flags=r.u32()
    pos=(r.o+15)&~15; f.seek(pos)
    info=lz4_block_decompress(f.read(ci),ui) if (flags&0x3F) in (2,3) else f.read(ci)
    ir=R(info); ir.o+=16; nb=ir.u32()
    bl=[(ir.u32(),ir.u32(),ir.u16()) for _ in range(nb)]
    start=(pos+ci+15)&~15; f.seek(start)
    out=bytearray()
    for u,c,fl in bl:
        raw=f.read(c)
        out += lz4_block_decompress(raw,u) if (fl&0x3F) in (2,3) else raw
        if limit and len(out)>limit: break
    return bytes(out)
target=(4).to_bytes(4,'little')+b'TEXT'
for p in sorted(glob.glob(sys.argv[1])):
    try:
        d=unpack(p)
    except Exception as e:
        print('ERR',os.path.basename(p),e); continue
    if d is None: continue
    k=d.find(target)
    if k>=0:
        cnt=d.count(target)
        print('HIT',os.path.basename(p),'count',cnt,'first@',k, flush=True)

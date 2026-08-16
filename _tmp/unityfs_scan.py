import struct, sys, io

def lz4_block_decompress(src, uncomp_size):
    dst = bytearray(uncomp_size); s = 0; d = 0; n = len(src)
    while s < n:
        tok = src[s]; s += 1
        lit = tok >> 4
        if lit == 15:
            while True:
                b = src[s]; s += 1; lit += b
                if b != 255: break
        if lit:
            dst[d:d+lit] = src[s:s+lit]; s += lit; d += lit
        if s >= n: break
        off = src[s] | (src[s+1] << 8); s += 2
        ml = tok & 0x0F
        if ml == 15:
            while True:
                b = src[s]; s += 1; ml += b
                if b != 255: break
        ml += 4
        p = d - off
        for i in range(ml):
            dst[d] = dst[p]; d += 1; p += 1
    return bytes(dst[:d])

class R:
    def __init__(self, b): self.b=b; self.o=0
    def u32(self): v=struct.unpack_from('>I',self.b,self.o)[0]; self.o+=4; return v
    def i64(self): v=struct.unpack_from('>q',self.b,self.o)[0]; self.o+=8; return v
    def u16(self): v=struct.unpack_from('>H',self.b,self.o)[0]; self.o+=2; return v
    def cstr(self):
        e=self.b.index(b'\x00',self.o); s=self.b[self.o:e].decode('utf8','replace'); self.o=e+1; return s

def parse(path, needles, ctx=400, maxhits=40):
    f=open(path,'rb')
    hdr=f.read(200)
    r=R(hdr)
    sig=r.cstr()
    ver=r.u32(); unity=r.cstr(); rev=r.cstr()
    size=r.i64(); ci=r.u32(); ui=r.u32(); flags=r.u32()
    print(f"sig={sig} ver={ver} unity={unity} rev={rev} size={size} ci={ci} ui={ui} flags=0x{flags:x}", flush=True)
    pos=r.o
    if ver>=7:
        pos=(pos+15)&~15
    f.seek(pos)
    comp=flags & 0x3F
    blk=f.read(ci)
    if comp in (2,3):
        info=lz4_block_decompress(blk,ui)
    elif comp==0:
        info=blk
    else:
        print("unsupported comp",comp); return
    ir=R(info)
    ir.o+=16  # hash
    nblocks=ir.u32()
    blocks=[]
    for _ in range(nblocks):
        u=ir.u32(); c=ir.u32(); fl=ir.u16(); blocks.append((u,c,fl))
    ndir=ir.u32()
    print(f"blocks={nblocks} dirs={ndir}", flush=True)
    for _ in range(ndir):
        off=ir.i64(); sz=ir.i64(); fl=ir.u32(); nm=ir.cstr()
        print(f"  DIR {nm} size={sz}", flush=True)
    hits=0
    carry=b''
    for i,(u,c,fl) in enumerate(blocks):
        raw=f.read(c)
        if (fl & 0x3F) in (2,3):
            try: data=lz4_block_decompress(raw,u)
            except Exception as e: continue
        else:
            data=raw
        buf=carry+data
        for nd in needles:
            st=0
            while True:
                k=buf.find(nd,st)
                if k<0: break
                hits+=1
                lo=max(0,k-ctx); hi=min(len(buf),k+ctx)
                seg=buf[lo:hi]
                printable=bytes(ch if 32<=ch<127 else 46 for ch in seg).decode('ascii')
                print(f"\n### block{i} needle={nd!r} @{k}\n{printable}", flush=True)
                st=k+1
                if hits>=maxhits: return
        carry=buf[-256:]
    print("done hits=",hits)

if __name__=='__main__':
    p=sys.argv[1]
    needles=[x.encode() for x in sys.argv[2:]]
    parse(p,needles)

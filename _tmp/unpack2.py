import struct,sys
from lz4mod import lz4_block_decompress
class R:
    def __init__(self,b): self.b=b; self.o=0
    def u32(self): v=struct.unpack_from('>I',self.b,self.o)[0]; self.o+=4; return v
    def i64(self): v=struct.unpack_from('>q',self.b,self.o)[0]; self.o+=8; return v
    def u16(self): v=struct.unpack_from('>H',self.b,self.o)[0]; self.o+=2; return v
    def cstr(self):
        e=self.b.index(b'\x00',self.o); s=self.b[self.o:e].decode('utf8','replace'); self.o=e+1; return s
path,out=sys.argv[1],sys.argv[2]
f=open(path,'rb'); hdr=f.read(200); r=R(hdr)
r.cstr(); ver=r.u32(); r.cstr(); r.cstr(); r.i64(); ci=r.u32(); ui=r.u32(); flags=r.u32()
pos=(r.o+15)&~15 if ver>=7 else r.o
f.seek(pos); blk=f.read(ci)
info=lz4_block_decompress(blk,ui) if (flags&0x3F) in (2,3) else blk
ir=R(info); ir.o+=16; nb=ir.u32()
blocks=[(ir.u32(),ir.u32(),ir.u16()) for _ in range(nb)]
o=open(out,'wb')
for u,c,fl in blocks:
    raw=f.read(c)
    o.write(lz4_block_decompress(raw,u) if (fl&0x3F) in (2,3) else raw)
o.close(); print("wrote",out)

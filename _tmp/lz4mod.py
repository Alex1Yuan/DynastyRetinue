def lz4_block_decompress(src, uncomp_size):
    dst = bytearray(uncomp_size); s = 0; d = 0; n = len(src)
    while s < n and d < uncomp_size:
        tok = src[s]; s += 1
        lit = tok >> 4
        if lit == 15:
            while True:
                b = src[s]; s += 1; lit += b
                if b != 255: break
        if lit:
            if d + lit > uncomp_size: lit = uncomp_size - d
            dst[d:d+lit] = src[s:s+lit]; s += lit; d += lit
        if s >= n or d >= uncomp_size: break
        off = src[s] | (src[s+1] << 8); s += 2
        ml = tok & 0x0F
        if ml == 15:
            while True:
                b = src[s]; s += 1; ml += b
                if b != 255: break
        ml += 4
        if off == 0 or off > d: break
        p = d - off
        for _ in range(ml):
            if d >= uncomp_size: break
            dst[d] = dst[p]; d += 1; p += 1
    return bytes(dst[:d])

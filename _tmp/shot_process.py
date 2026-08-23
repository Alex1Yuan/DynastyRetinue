from PIL import Image, ImageFilter, ImageDraw
import os

SRC = r"C:\Users\kyua805\Videos\Desktop"
OUT = r"D:\RT_RetinueMod\_press\update"
TOPCROP = 72          # 性能浮层文字在 y 32~56，右上小齿轮到 ~58
TARGET_W = 1920

def inpaint(im, box, iters=140, radius=12, feather=24):
    """
    扩散式修补：把 box 挖空，反复「整图模糊 → 还原洞外像素」，
    让周围星云自己长进洞里。星云是平滑渐变，这么填看不出痕迹；
    左边没有等宽干净区域可复制（橙色路径线在那儿），所以不用复制粘贴。
    """
    x0, y0, x1, y1 = box
    hole = Image.new("L", im.size, 0)
    ImageDraw.Draw(hole).rectangle([x0, y0, x1, y1], fill=255)
    hole = hole.filter(ImageFilter.GaussianBlur(feather))   # 羽化边界，避免硬缝

    known = im.copy()
    cur = im.copy()
    # 先把洞内涂成边界均值，给扩散一个中性起点。
    # ★均值只能取洞外★ 直接对含洞区域求平均会把绿条的颜色带进种子，
    #   扩散完顶部会残留一片偏绿 —— 第一版就是这么留下痕迹的。
    ring = im.crop((max(0, x0 - 60), max(0, y0 - 60), min(im.width, x1 + 60), min(im.height, y1 + 60)))
    rw, rh = ring.size
    band = ring.crop((0, 0, min(60, rw), rh))              # 只用左侧那条洞外竖带
    avg = band.resize((1, 1), Image.LANCZOS).getpixel((0, 0))
    ImageDraw.Draw(cur).rectangle([x0, y0, x1, y1], fill=avg)

    for _ in range(iters):
        cur = cur.filter(ImageFilter.GaussianBlur(radius))
        cur = Image.composite(cur, known, hole)             # 洞外一律还原
    return cur

def finish(im, name):
    im = im.crop((0, TOPCROP, im.width, im.height))
    h = round(im.height * TARGET_W / im.width)
    im = im.resize((TARGET_W, h), Image.LANCZOS)
    dst = os.path.join(OUT, name)
    im.save(dst, "PNG", optimize=True)
    print(name, im.size, f"{os.path.getsize(dst)/1024:.0f} KB")

a = Image.open(os.path.join(SRC, "Desktop Screenshot 2026.08.23 - 12.56.07.27.png")).convert("RGB")
finish(a, "1.5.0_01_斜向射界.png")

b = Image.open(os.path.join(SRC, "Desktop Screenshot 2026.08.23 - 12.56.15.65.png")).convert("RGB")
# 残留 UI：实心绿条 x3038~3312 y210~519 + 黑底 x3313~3838 y210~389。
# ★还要往上顶到裁切线★ 绿条的辉光比实心部分更高，探测阈值抓不到，
#   只按实心范围补会在顶部留一片偏绿。y 起点取 60（<TOPCROP，裁掉后无缝）。
b = inpaint(b, (2950, 60, 3839, 560))
finish(b, "1.5.0_02_船体占位与移动区.png")

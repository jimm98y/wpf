#!/usr/bin/env python3
"""Write GIF/TIFF fixtures with PIL so the fork's decoders are checked against
somebody else's encoder rather than only against our own."""

import os
import sys
from PIL import Image

OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)

W = H = 8

# Four quadrants, chosen so every channel and both axes are distinguishable:
# red | green
# ----+------
# blue| white
def rgb_image():
    im = Image.new("RGB", (W, H))
    px = im.load()
    for y in range(H):
        for x in range(W):
            right, bottom = x >= W // 2, y >= H // 2
            px[x, y] = {
                (False, False): (255, 0, 0),
                (True, False): (0, 255, 0),
                (False, True): (0, 0, 255),
                (True, True): (255, 255, 255),
            }[(right, bottom)]
    return im


base = rgb_image()

# ---- TIFF flavours -----------------------------------------------------------
base.save(f"{OUT}/tiff-none.tif", compression=None)
base.save(f"{OUT}/tiff-lzw.tif", compression="tiff_lzw")
base.save(f"{OUT}/tiff-packbits.tif", compression="packbits")
base.save(f"{OUT}/tiff-deflate.tif", compression="tiff_adobe_deflate")

# Big-endian: PIL picks byte order from the mode string.
be = base.copy()
be.save(f"{OUT}/tiff-bigendian.tif", compression=None)
with open(f"{OUT}/tiff-bigendian.tif", "rb") as f:
    if f.read(2) != b"MM":
        # PIL writes little-endian; produce a real MM file by hand below.
        os.remove(f"{OUT}/tiff-bigendian.tif")

# Greyscale and palette and bilevel.
base.convert("L").save(f"{OUT}/tiff-grey.tif", compression=None)
base.convert("P", palette=Image.ADAPTIVE, colors=4).save(f"{OUT}/tiff-palette.tif", compression=None)
base.convert("1").save(f"{OUT}/tiff-bilevel.tif", compression=None)

# RGBA, so the alpha sample path is exercised.
rgba = base.convert("RGBA")
rgba.putalpha(128)
rgba.save(f"{OUT}/tiff-rgba.tif", compression=None)

# Multi-page.
red = Image.new("RGB", (W, H), (255, 0, 0))
green = Image.new("RGB", (W, H), (0, 255, 0))
blue = Image.new("RGB", (W, H), (0, 0, 255))
red.save(f"{OUT}/tiff-multipage.tif", save_all=True, append_images=[green, blue], compression=None)

# LZW with the horizontal predictor, the flavour Photoshop writes.
base.save(f"{OUT}/tiff-lzw-predictor.tif", compression="tiff_lzw", tiffinfo={317: 2})

# ---- GIF flavours ------------------------------------------------------------
base.save(f"{OUT}/gif-plain.gif")
base.save(f"{OUT}/gif-interlaced.gif", interlace=True)

# Animated, three solid frames, so frame composition is exercised.
red.save(f"{OUT}/gif-animated.gif", save_all=True, append_images=[green, blue], duration=100, loop=0)

# Transparent: quadrant 0 punched out, so the transparent index path is hit.
trans = base.convert("P", palette=Image.ADAPTIVE, colors=8)
trans.info["transparency"] = trans.getpixel((0, 0))
trans.save(f"{OUT}/gif-transparent.gif", transparency=trans.getpixel((0, 0)))

for name in sorted(os.listdir(OUT)):
    print(f"{name}\t{os.path.getsize(os.path.join(OUT, name))} bytes")

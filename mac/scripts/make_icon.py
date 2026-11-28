"""Generates Resources/AppIcon.png (1024x1024) — a macOS-style squircle with a pen nib."""
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops

S = 1024
SS = 2  # supersample
W = S * SS

def squircle_mask(size, inset, n=5.0):
    m = Image.new("L", (size, size), 0)
    px = m.load()
    c = size / 2
    r = size / 2 - inset
    for y in range(size):
        dy = abs((y + 0.5 - c) / r)
        if dy > 1:
            continue
        dx = (1 - dy ** n) ** (1 / n)
        x0 = int(c - dx * r)
        x1 = int(c + dx * r)
        for x in range(max(0, x0), min(size, x1 + 1)):
            px[x, y] = 255
    return m

def vgradient(size, top, bottom):
    g = Image.new("RGB", (1, size))
    for y in range(size):
        t = y / (size - 1)
        g.putpixel((0, y), tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    return g.resize((size, size))

def diag_gradient(size, a, b):
    import numpy as np
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    t = ((xx + yy) / (2 * (size - 1)))[..., None]
    arr = np.array(a, np.float32) * (1 - t) + np.array(b, np.float32) * t
    return Image.fromarray(arr.clip(0, 255).astype("uint8"), "RGB")

inset = int(100 * SS)  # Apple grid: ~824px body on a 1024 canvas
mask = squircle_mask(W, inset)

# Background: indigo -> violet -> magenta
bg = diag_gradient(W, (79, 70, 229), (192, 38, 211))
glow = Image.new("RGB", (W, W), (0, 0, 0))
gd = ImageDraw.Draw(glow)
gd.ellipse([W * 0.15, W * 0.05, W * 0.95, W * 0.75], fill=(120, 140, 255))
glow = glow.filter(ImageFilter.GaussianBlur(W * 0.12))
bg = ImageChops.screen(bg, Image.blend(Image.new("RGB", (W, W)), glow, 0.55))

icon = Image.new("RGBA", (W, W), (0, 0, 0, 0))
icon.paste(bg, (0, 0), mask)

# Glass sheen on the top half
sheen = Image.new("L", (W, W), 0)
sd = ImageDraw.Draw(sheen)
sd.ellipse([-W * 0.25, -W * 0.75, W * 1.25, W * 0.50], fill=55)
sheen = sheen.filter(ImageFilter.GaussianBlur(W * 0.045))
sheen = ImageChops.multiply(sheen, mask)
icon = Image.alpha_composite(icon, Image.merge("RGBA", (*[Image.new("L", (W, W), 255)] * 3, sheen)))

# Nib shape
cx, cy = W / 2, W / 2 + 10 * SS
u = W / 1024
nib = [
    (cx, cy + 300 * u),            # tip
    (cx - 175 * u, cy + 20 * u),
    (cx - 150 * u, cy - 150 * u),
    (cx - 95 * u, cy - 235 * u),
    (cx + 95 * u, cy - 235 * u),
    (cx + 150 * u, cy - 150 * u),
    (cx + 175 * u, cy + 20 * u),
]
# soft shadow
shadow = Image.new("L", (W, W), 0)
ImageDraw.Draw(shadow).polygon([(x, y + 28 * u) for x, y in nib], fill=110)
shadow = shadow.filter(ImageFilter.GaussianBlur(30 * u))
icon = Image.alpha_composite(icon, Image.merge("RGBA", (*[Image.new("L", (W, W), 20)] * 3, ImageChops.multiply(shadow, mask))))

nib_layer = Image.new("RGBA", (W, W), (0, 0, 0, 0))
nd = ImageDraw.Draw(nib_layer)
nd.polygon(nib, fill=(255, 255, 255, 245))
# collar
nd.rounded_rectangle([cx - 110 * u, cy - 300 * u, cx + 110 * u, cy - 220 * u], radius=24 * u, fill=(255, 255, 255, 235))
# slit + breather hole (cut out)
cut = Image.new("L", (W, W), 0)
cd = ImageDraw.Draw(cut)
cd.ellipse([cx - 34 * u, cy - 74 * u, cx + 34 * u, cy - 6 * u], fill=255)
cd.line([(cx, cy - 20 * u), (cx, cy + 300 * u)], fill=255, width=int(16 * u))
a = nib_layer.split()[3]
a = ImageChops.subtract(a, cut)
nib_layer.putalpha(a)
icon = Image.alpha_composite(icon, nib_layer)

# Sparkle
def sparkle(d, x, y, r, fill):
    k = r * 0.22
    d.polygon([(x, y - r), (x + k, y - k), (x + r, y), (x + k, y + k), (x, y + r), (x - k, y + k), (x - r, y), (x - k, y - k)], fill=fill)

sp = ImageDraw.Draw(icon)
sparkle(sp, cx + 235 * u, cy - 230 * u, 70 * u, (255, 255, 255, 240))
sparkle(sp, cx + 300 * u, cy - 110 * u, 32 * u, (255, 255, 255, 200))

# Hairline rim
rim = ImageChops.subtract(mask, squircle_mask(W, inset + 3 * SS))
icon = Image.alpha_composite(icon, Image.merge("RGBA", (*[Image.new("L", (W, W), 255)] * 3, rim.point(lambda v: v * 0.35))))

icon = icon.resize((S, S), Image.LANCZOS)
icon.save(sys.argv[1])
print("saved", sys.argv[1])

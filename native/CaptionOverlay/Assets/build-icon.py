"""Rebuild the embedded multi-resolution Rayme icon (developer tool; Pillow only)."""
from pathlib import Path
from PIL import Image, ImageDraw
root = Path(__file__).parent
image = Image.new('RGBA', (1024, 1024))
draw = ImageDraw.Draw(image)
def box(values): return tuple(v * 4 for v in values)
draw.rounded_rectangle(box((8, 8, 248, 248)), radius=208, fill='#14243A', outline='#446580', width=8)
draw.rounded_rectangle(box((49, 49, 207, 176)), radius=128, fill='#91D9FF')
draw.polygon([box((78, 160)), box((78, 211)), box((132, 171))], fill='#91D9FF')
draw.rounded_rectangle(box((88, 80, 163, 126)), radius=68, outline='#14243A', width=48)
draw.line([box((94, 86)), box((94, 151))], fill='#14243A', width=48)
draw.line([box((130, 125)), box((162, 153))], fill='#14243A', width=48)
image.save(root / 'Rayme.ico', sizes=[(n,n) for n in [16,20,24,32,40,48,64,128,256]])

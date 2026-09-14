"""Render the simple monochrome LOGO.svg into platform icons. Requires Pillow."""
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw
repo = Path(__file__).resolve().parents[1]
assets = repo / 'src/CodexManager/Assets'
svg = ET.parse(repo / 'LOGO.svg').getroot()
scale = 32
image = Image.new('RGBA', (128 * scale, 128 * scale))
draw = ImageDraw.Draw(image)
def render(node, inherited_fill='#000000'):
    fill = node.get('fill', inherited_fill)
    tag = node.tag.rsplit('}', 1)[-1]
    if tag == 'rect':
        x, y = float(node.get('x', 0)), float(node.get('y', 0))
        width, height = float(node.get('width')), float(node.get('height'))
        draw.rectangle((x * scale, y * scale, (x + width) * scale - 1, (y + height) * scale - 1), fill=fill)
    elif tag == 'polygon':
        points = [tuple(float(value) * scale for value in point.split(',')) for point in node.get('points').split()]
        draw.polygon(points, fill=fill)
    elif tag not in ('svg', 'g'):
        raise ValueError(f'Unsupported logo element: {tag}')
    for child in node:
        render(child, fill)
render(svg)
image = image.resize((1024, 1024), Image.Resampling.LANCZOS)
shutil.copyfile(repo / 'LOGO.svg', assets / 'app.svg')
image.save(repo / 'LOGO.png')
image.save(assets / 'app.png')
image.save(assets / 'app.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
image.resize((1024,1024), Image.Resampling.LANCZOS).save(assets / 'app.icns')

"""Convert the repository LOGO.png into platform app icons. Requires Pillow."""
from pathlib import Path
from PIL import Image
repo = Path(__file__).resolve().parents[1]
assets = repo / 'src/CodexManager/Assets'
image = Image.open(repo / 'LOGO.png').convert('RGBA')
image.save(assets / 'app.png')
image.save(assets / 'app.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
image.resize((1024,1024), Image.Resampling.LANCZOS).save(assets / 'app.icns')

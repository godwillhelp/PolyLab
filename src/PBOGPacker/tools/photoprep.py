import io
import json
import os
import re
import time
import urllib.parse
import urllib.request

import numpy as np
from PIL import Image, ImageFilter

OUT = os.path.join(os.path.dirname(__file__), '..', 'Assets', 'Photos')
CACHE = os.path.join(os.path.dirname(__file__), '_cache')
os.makedirs(OUT, exist_ok=True)
os.makedirs(CACHE, exist_ok=True)
UA = {'User-Agent': 'PolyLabPBOGPacker/0.1 (desktop app asset prep; https://github.com/KRdayzmodding/KR_GRAFTED) python-urllib'}
rng = np.random.default_rng(163451)

# role -> (Commons file, output size, focus x, focus y, zoom, grade)
JOBS = {
    'bg_build':    ('DUGA Radar Array near Chernobyl, Ukraine 2014.jpg', (1920, 1080), 0.5, 0.45, 1.0, 'bg'),
    'bg_servers':  ('Pripyat panorama 2009-001.jpg', (1920, 1080), 0.55, 0.5, 1.0, 'bg'),
    'bg_keys':     ('Pripyat ghost town.jpg', (1920, 1080), 0.5, 0.5, 1.0, 'bg'),
    'bg_env':      ('Control Room of Chernobyl Nuclear Power Plant Unit 3.jpg', (1920, 1080), 0.5, 0.5, 1.0, 'bg'),
    'bg_settings': ('Aerial view of Pripyat.jpg', (1920, 1080), 0.5, 0.5, 1.0, 'bg'),
    'bg_about':    ('Pripyat amusement park (37746771195).jpg', (1920, 1080), 0.5, 0.45, 1.0, 'bg'),
    'st_env':      ('Dosimeter in Chernobyl.jpg', (480, 300), 0.5, 0.5, 1.0, 'tile'),
    'st_build':    ('Duga radar system- wreckage of electronic devices (37885983114).jpg', (480, 300), 0.5, 0.5, 1.0, 'tile'),
    'st_protogen': ('Печатная машинка.jpg', (480, 300), 0.5, 0.5, 1.0, 'tile'),
    'st_pack':     ('Crates of Soviet 7.62mm small arms ammunition found in storage on Grenada.jpg', (480, 300), 0.5, 0.5, 1.0, 'tile'),
    'st_sign':     ('Czech office time stamp with inkpillow.jpg', (480, 300), 0.5, 0.5, 1.0, 'tile'),
    'st_assemble': ('Military backpack 01.jpg', (480, 300), 0.5, 0.45, 1.0, 'tile'),
    'st_deploy':   ('Chernobyl - vehicles 01.jpg', (480, 300), 0.5, 0.55, 1.0, 'tile'),
    'st_host':     ('Funkstation R-123 IMG 1390.JPG', (480, 300), 0.5, 0.5, 1.0, 'tile'),
    'dlg_ok':      ('Duga-1 radar 20190504-2.jpg', (300, 540), 0.5, 0.5, 1.0, 'side'),
    'dlg_err':     ('Gas Masks Littered on Floor of Abandoned Schoolhouse - Pripyat Ghost Town - Chernobyl Exclusion Zone - Northern Ukraine (27031462131).jpg', (300, 540), 0.5, 0.5, 1.0, 'side'),
    'logo_radio':  ('Duga-1 radar 20190504-1.jpg', (200, 200), 0.5, 0.5, 1.6, 'logo'),
    'logo_stats':  ('ChNPP Unit1control.JPG', (200, 200), 0.5, 0.5, 1.4, 'logo'),
    'tc_tile':     ('Geiger counter (38365895582).jpg', (200, 200), 0.5, 0.5, 1.3, 'logo'),
}


def api(params):
    for a in range(6):
        try:
            url = 'https://commons.wikimedia.org/w/api.php?' + urllib.parse.urlencode(params)
            return json.load(urllib.request.urlopen(urllib.request.Request(url, headers=UA), timeout=40))
        except urllib.error.HTTPError as e:
            if e.code != 429:
                raise
            time.sleep(20 * (a + 1))
    raise RuntimeError('rate limited')


def fetch(title, width):
    key = re.sub(r'[^A-Za-z0-9]+', '_', title)[:80]
    meta_p = os.path.join(CACHE, key + '.json')
    img_p = os.path.join(CACHE, key + '.img')
    if os.path.exists(meta_p) and os.path.exists(img_p):
        return json.load(open(meta_p, encoding='utf-8')), Image.open(img_p).convert('RGB')
    d = api({'action': 'query', 'format': 'json', 'titles': 'File:' + title, 'prop': 'imageinfo',
             'iiprop': 'url|extmetadata', 'iiurlwidth': str(width)})
    page = next(iter(d['query']['pages'].values()))
    ii = page['imageinfo'][0]
    m = ii['extmetadata']
    meta = {
        'title': title,
        'author': re.sub(r'\s+', ' ', re.sub('<[^>]+>', '', m.get('Artist', {}).get('value', '?'))).strip(),
        'license': m.get('LicenseShortName', {}).get('value', '?'),
        'licenseUrl': m.get('LicenseUrl', {}).get('value', ''),
        'page': ii.get('descriptionurl', ''),
    }
    time.sleep(3)
    raw = None
    for a in range(5):
        try:
            raw = urllib.request.urlopen(urllib.request.Request(ii.get('thumburl') or ii['url'], headers=UA), timeout=90).read()
            break
        except (TimeoutError, urllib.error.URLError, ConnectionError) as e:
            if isinstance(e, urllib.error.HTTPError) and e.code != 429:
                raise
            time.sleep(15 * (a + 1))
    if raw is None:
        raise RuntimeError('download failed: ' + title)
    open(img_p, 'wb').write(raw)
    json.dump(meta, open(meta_p, 'w', encoding='utf-8'), ensure_ascii=False)
    time.sleep(3)
    return meta, Image.open(io.BytesIO(raw)).convert('RGB')


def cover(im, size, fx, fy, zoom):
    W, H = size
    sw, sh = im.size
    scale = max(W / sw, H / sh) * zoom
    nw, nh = int(sw * scale + 0.5), int(sh * scale + 0.5)
    im = im.resize((nw, nh), Image.LANCZOS)
    x = int(min(max(fx * nw - W / 2, 0), nw - W))
    y = int(min(max(fy * nh - H / 2, 0), nh - H))
    return im.crop((x, y, x + W, y + H))


def grade(im, kind):
    a = np.asarray(im, np.float32) / 255
    lum = a @ np.array([0.299, 0.587, 0.114], np.float32)
    sat = {'bg': 0.28, 'tile': 0.42, 'side': 0.3, 'logo': 0.5}[kind]
    a = lum[..., None] + (a - lum[..., None]) * sat
    a = np.clip((a - 0.5) * 1.12 + 0.5, 0, 1)
    a = a * np.array([1.0, 0.965, 0.89], np.float32) + np.array([0.012, 0.008, 0.0], np.float32)
    H, W = a.shape[:2]
    y = np.linspace(0, 1, H, dtype=np.float32)[:, None]
    x = np.linspace(0, 1, W, dtype=np.float32)[None, :]
    if kind == 'bg':
        v = np.clip(1 - 0.6 * (((x - 0.6) / 0.8) ** 2 + ((y - 0.4) / 0.85) ** 2), 0, 1)
        v *= np.clip(0.3 + x / 0.24 * 0.7, 0.3, 1)
        v *= 0.62
    elif kind == 'side':
        v = np.clip(1 - 0.7 * (((x - 0.5) / 0.7) ** 2 + ((y - 0.4) / 0.8) ** 2), 0, 1) * 0.75
    elif kind == 'tile':
        v = np.clip(1 - 0.8 * (((x - 0.5) / 0.75) ** 2 + ((y - 0.5) / 0.75) ** 2), 0, 1) * 0.72
    else:
        v = np.clip(1 - 0.5 * (((x - 0.5) / 0.7) ** 2 + ((y - 0.5) / 0.7) ** 2), 0, 1) * 0.9
    a = a * v[..., None]
    a += rng.normal(0, 0.018, (H, W))[..., None]
    return Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8))


credits = {}
for role, (title, size, fx, fy, zoom, kind) in JOBS.items():
    meta, im = fetch(title, 2400 if kind == 'bg' else 1400)
    out = grade(cover(im, size, fx, fy, zoom), kind)
    if kind == 'bg':
        out = out.filter(ImageFilter.GaussianBlur(0.6))
    out.save(os.path.join(OUT, role + '.jpg'), quality=86)
    credits[meta['title']] = meta
    print(role, '|', meta['license'], '|', meta['author'][:50])

json.dump(sorted(credits.values(), key=lambda m: m['title']), open(os.path.join(OUT, 'credits.json'), 'w', encoding='utf-8'),
          ensure_ascii=False, indent=1)
print('ok', len(credits))

"""Lists text keys used by code and XAML that src/ParticleStudio/Pfx.App/Lang/ru.xaml lacks.

Literal keys: Loc.T("k"), Loc.F("k", ...) and {DynamicResource k}. Composed keys (prefix + value) are listed by the
caller as extra arguments, e.g. "Xr.Skip.:empty,binary".

    py -3 tools/lang/missing_keys.py <file>... [--extra prefix:a,b,c ...]
"""
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[3]
have = set(re.findall(r'x:Key="([^"]+)"', (root / "src/ParticleStudio/Pfx.App/Lang/ru.xaml").read_text(encoding="utf-8")))
args = sys.argv[1:]
extra = args[args.index("--extra") + 1:] if "--extra" in args else []
files = args[:args.index("--extra")] if "--extra" in args else args
used = set()
for f in files:
    text = Path(f).read_text(encoding="utf-8")
    used |= set(re.findall(r'Loc\.[TF]\("([A-Za-z0-9.]+)"', text))
    used |= set(re.findall(r'DynamicResource ([A-Za-z0-9.]+)\}', text))
for e in extra:
    prefix, names = e.split(":", 1)
    used |= {prefix + n for n in names.split(",")}
for k in sorted(used - have):
    print(k)

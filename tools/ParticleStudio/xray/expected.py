import importlib.util
import json
import os
import sys

xray_path, out = os.path.abspath(sys.argv[1]), sys.argv[2]
sys.path.insert(0, os.path.dirname(xray_path))
spec = importlib.util.spec_from_file_location("xray", xray_path)
xray = importlib.util.module_from_spec(spec)
spec.loader.exec_module(xray)

src = xray.Sources()
roots, counts = [], {}
for tag, variants in xray.XRAY_SOURCES.items():
    pe, pg = set(), set()
    for variant, rel in variants.items():
        path = os.path.normpath(os.path.join(xray.XRAY_ROOT, rel.replace("/", "\\")))
        roots.append({"tag": tag, "variant": variant, "path": path})
        for key in src.files(tag, variant):
            if key.startswith("particles/") and key.endswith(".pe"):
                pe.add(key)
            elif key.startswith("particles/") and key.endswith(".pg"):
                pg.add(key)
        for key in src.library(tag, variant)[1]:
            (pe if key.endswith(".pe") else pg).add(key)
    counts[tag] = {"effects": len(pe), "groups": len(pg)}
with open(out, "w", encoding="utf-8") as f:
    json.dump({"order": list(xray.XRAY_SOURCES), "roots": roots, "counts": counts, "stage": xray.XRAY_STAGE}, f, indent=1)
print(json.dumps(counts))

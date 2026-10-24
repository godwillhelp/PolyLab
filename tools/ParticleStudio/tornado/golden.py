import json
import os
import re
import sys

script, out = sys.argv[1], sys.argv[2]
VARIANT = {"FUNNEL_HEIGHT": "80.0", "FUNNEL_TOP": "26.0", "FUNNEL_SPIN": "-120.0", "STRANDS": "7",
           "STRAND_SPEED": "7.5", "CAP_SPIN": "-30.0", "AROUND_LIFE": "6.0", "STRIKE_BURST": "0.25",
           "RINGS": "[(25.0, 10.0, 8, 2.5), (60.0, 16.0, 12, 5.0)]", "DARK": "(0.3, 0.2, 0.1)"}

with open(script, encoding="utf-8") as f:
    source = f.read()


def run(name, overrides):
    text = source
    for key, value in overrides.items():
        text, n = re.subn(r"^%s = .*$" % key, "%s = %s" % (key, value), text, flags=re.M)
        if n != 1:
            raise SystemExit("constant %s not found" % key)
    folder = os.path.join(out, name)
    os.makedirs(folder, exist_ok=True)
    module = {"__file__": script, "__name__": "own_tornado"}
    exec(compile(text, script, "exec"), module)
    module["OUT"] = folder
    module["main"]()
    return {"spins": module["SPINS"], "swirl": module["SWIRL"], "near": module["NEAR"], "strike": module["STRIKE"],
            "overrides": overrides}


result = {"donor": os.path.join(os.path.dirname(os.path.abspath(script)), "_xray", "ar", "particles", "generator_ar_idle.ptc"),
          "default": run("default", {}), "variant": run("variant", VARIANT)}
with open(os.path.join(out, "golden.json"), "w", encoding="utf-8") as f:
    json.dump(result, f, indent=1)

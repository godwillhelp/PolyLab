import json
import os
import subprocess
import sys

full, xray_path, out, limit = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]) if len(sys.argv) > 4 else 16
RANDOM = {"randomaccel", "randomvelocity", "randomdisplace"}
with open(full, encoding="utf-8") as f:
    model = json.load(f)

chosen, covered = [], set()
for p in model["particles"]:
    for e in p["emitters"]:
        r, eff = e["replay"], e["effect"]
        if r is None or eff is None:
            continue
        types = [a["type"] for a in eff["actions"]]
        src = next((a for a in eff["actions"] if a["type"] == "source"), None)
        if src is None or src["Age Sigma"] != 0 or RANDOM & set(types) or types.count("source") > 1:
            continue
        if any(a["type"] in ("sink", "sinkvelocity") and a["Domain"]["domain"] == 8 for a in eff["actions"]):
            continue
        if r["life"][2] > 4.0:
            continue
        new = set(types) - covered
        if new or len(chosen) < limit // 2:
            chosen.append((p, e["name"]))
            covered |= set(types)
        if len(chosen) >= limit:
            break
    if len(chosen) >= limit:
        break

particles = []
for p, _ in chosen:
    row = [p["tag"], p["variant"], p["ref"], p["ptype"], p["state"]]
    if row not in particles:
        particles.append(row)
req = {"sources": model["sources"], "particles": particles, "sounds": [], "subst": model["subst"],
       "replay": sorted({n for _, n in chosen})}
req_path = out + ".request.json"
with open(req_path, "w", encoding="utf-8") as f:
    json.dump(req, f)
subprocess.run([sys.executable, "-I", xray_path, "--json", out, "--request", req_path], check=True)
print("emitters %d, actions covered: %s" % (len(chosen), " ".join(sorted(covered))))

import importlib.util
import os
import sys
import time

xray_path, stage = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
tools = os.environ.get("PFX_XRAY_TOOLS") or os.path.dirname(xray_path)
sys.path.insert(0, tools)

import manifest

manifest.XRAY_STAGE = stage
spec = importlib.util.spec_from_file_location("xray", xray_path)
xray = importlib.util.module_from_spec(spec)
sys.modules["xray"] = xray
spec.loader.exec_module(xray)
sys.argv = [xray_path] + sys.argv[3:]
t = time.perf_counter()
xray.main()
print("seconds %.1f" % (time.perf_counter() - t))

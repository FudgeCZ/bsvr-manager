"""Captures tab screenshots + the VR cover render from the built exe.

Usage: python tools/capture_shots.py [exePath] [outDir]
- launches exe --desktop-launched --debug-api, drives nav.<tab> via /invoke,
  saves each center screen via /shot (each capture waits for a drawn frame)
- then launches exe --cover-shot to render the 3-panel VR scene to cover-vr.png
"""
import os, sys, time, json, subprocess, urllib.request

exe = sys.argv[1] if len(sys.argv) > 1 else r"build\BSVRManager-v1.0.0.exe"
out = sys.argv[2] if len(sys.argv) > 2 else r"itch\screenshots"
os.makedirs(out, exist_ok=True)
BASE = "http://127.0.0.1:8790"
exe_dir = os.path.dirname(os.path.abspath(exe))

def http(path, body=None):
    req = urllib.request.Request(BASE + path, method="POST" if body is not None else "GET",
                                 data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json"})
    return urllib.request.urlopen(req, timeout=10).read().decode()

def wait_api(proc, seconds=30):
    end = time.time() + seconds
    while time.time() < end:
        if proc.poll() is not None:
            raise SystemExit(f"app exited early, code {proc.returncode}")
        try:
            return http("/state")
        except Exception:
            time.sleep(0.4)
    raise SystemExit("debug api never came up")

def wait_settled(proc, seconds=20):
    """wait until the startup work (scans etc.) reports an empty status"""
    end = time.time() + seconds
    while time.time() < end:
        if proc.poll() is not None:
            raise SystemExit(f"app exited early, code {proc.returncode}")
        try:
            st = json.loads(http("/state"))
            if not st.get("status"):
                time.sleep(2.0)  # let the last UI refresh paint
                return
        except Exception:
            pass
        time.sleep(0.5)
    print("warning: status never emptied, shooting anyway")

# ---- 1. desktop tab screenshots ----
proc = subprocess.Popen([exe, "--desktop-launched", "--debug-api"], cwd=os.getcwd())
state = wait_api(proc)
print("state:", state)
wait_settled(proc)
tabs = ["dashboard", "profiles", "mods", "versions", "settings"]
try:
    for t in tabs:
        http("/invoke", {"action": f"nav.{t}", "param": ""})
        time.sleep(2.5)
        path = os.path.abspath(os.path.join(out, f"tab-{t}.png"))
        http("/shot?path=" + urllib.request.quote(path))
        for _ in range(60):
            if os.path.exists(path) and os.path.getsize(path) > 3000:
                break
            time.sleep(0.25)
        print("shot:", path, os.path.exists(path))
finally:
    proc.terminate()
    time.sleep(1.0)
    if proc.poll() is None:
        proc.kill()

# ---- 2. VR cover render ----
for f in ("cover-vr.png", "cover-logo.png"):
    for d in (exe_dir, os.getcwd()):
        p = os.path.join(d, f)
        if os.path.exists(p):
            os.remove(p)
proc = subprocess.Popen([exe, "--cover-shot"], cwd=os.getcwd())
try:
    proc.wait(timeout=60)
    print("cover exit:", proc.returncode)
finally:
    if proc.poll() is None:
        proc.kill()
for f in ("cover-vr.png", "cover-logo.png"):
    for d in (exe_dir, os.getcwd()):
        p = os.path.join(d, f)
        if os.path.exists(p):
            dest = os.path.join(out, f)
            os.replace(p, dest)
            print("moved", dest)

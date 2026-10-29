"""
Builds the signed release of the condo's Android app: an AAB (Google Play) and an APK (direct install, Firebase App
Distribution). Output: artifacts/android/.

Usage (from the repo root of a condo deployment repo):
    python deploy/build_android.py

Needs the maui-android workload, JDK 21 and the keystore from deploy/android_keystore.py (secrets.json
AndroidKeystorePath / AndroidKeystorePassword). The version code is the number of commits, so every build from a
newer commit can update the previous one; the version name comes from the csproj (ApplicationDisplayVersion).
"""
import glob
import json
import os
import re
import shutil
import unicodedata
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
store = json.load(open(os.path.join(ROOT, "secrets.json"), encoding="utf-8"))
if not store.get("AndroidKeystorePath"):
    sys.exit("No keystore: run python deploy/android_keystore.py first.")

version_code = subprocess.run(["git", "rev-list", "--count", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
out = os.path.join(ROOT, "artifacts", "android")
shutil.rmtree(out, ignore_errors=True)

# The password goes through an environment variable (MSBuild "env:" prefix), never the command line.
env = dict(os.environ, MANTENIO_KEYSTORE_PASSWORD=store["AndroidKeystorePassword"])
result = subprocess.run([
    "dotnet", "publish", "src/Mantenio.Mobile", "-f", "net10.0-android", "-c", "Release", "-o", out,
    "-p:AndroidKeyStore=true",
    f"-p:AndroidSigningKeyStore={store['AndroidKeystorePath']}",
    "-p:AndroidSigningKeyAlias=mantenio",
    "-p:AndroidSigningStorePass=env:MANTENIO_KEYSTORE_PASSWORD",
    "-p:AndroidSigningKeyPass=env:MANTENIO_KEYSTORE_PASSWORD",
    "-p:AndroidPackageFormats=aab%3Bapk",
    f"-p:ApplicationVersion={version_code}",
], cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", shell=os.name == "nt")
if result.returncode != 0:
    sys.exit("Build failed:\n" + (result.stdout + result.stderr)[-2500:])

# publish copies only the AAB; the signed APK stays in the build folder.
for apk in glob.glob(os.path.join(ROOT, "src", "Mantenio.Mobile", "bin", "Release", "net10.0-android", "*-Signed.apk")):
    shutil.copy2(apk, out)
for leftover in glob.glob(os.path.join(out, "*")):
    if not leftover.endswith(("-Signed.apk", "-Signed.aab")):
        os.remove(leftover)
# A friendly copy to share (Drive, WhatsApp) named like the app, without accents: "El Prado de San Jose 1.0.3.apk".
csproj = open(os.path.join(ROOT, "src", "Mantenio.Mobile", "Mantenio.Mobile.csproj"), encoding="utf-8").read()
version_name = re.search(r"<ApplicationDisplayVersion>([^<]+)</ApplicationDisplayVersion>", csproj).group(1)
props = os.path.join(ROOT, "Condo.props")
slug = "demo"
if os.path.exists(props):
    match = re.search(r"<Condo\b[^>]*>\s*([^<\s]+)\s*</Condo>", open(props, encoding="utf-8").read())
    slug = match.group(1) if match else slug
app_title = json.load(open(os.path.join(ROOT, "condos", slug, "condo.json"), encoding="utf-8"))["android"]["appTitle"]
plain_title = re.sub(r"[^A-Za-z0-9 ]", "", unicodedata.normalize("NFKD", app_title).encode("ascii", "ignore").decode()).strip()
for apk in glob.glob(os.path.join(out, "*-Signed.apk")):
    shutil.copy2(apk, os.path.join(out, f"{plain_title} {version_name}.apk"))
files = sorted(f for f in glob.glob(os.path.join(out, "*")) if f.endswith((".apk", ".aab")))
for f in files:
    print(f"{os.path.relpath(f, ROOT)}  ({os.path.getsize(f) // 1024} KB)")
print(f"Version {version_name} (code {version_code}).")

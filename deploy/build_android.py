"""
Builds the signed release of the condo's Android app: an AAB (Google Play) and an APK (direct install, Firebase App
Distribution). Output: artifacts/android/.

Usage (from the repo root of a condo deployment repo):
    python deploy/build_android.py                      # build only
    python deploy/build_android.py --publish            # build and publish: the apps offer the update
    python deploy/build_android.py --publish --required # older apps must update (they no longer work with the API)

--publish uploads the APK and app/android/version.json to the files bucket (deploy/deployment.json "storageBucket",
secrets.json R2 keys; Docker runs the AWS CLI). The API serves them at /api/app/android (stable download link, also
<web>/app) and announces the new version once to the users with the app on a phone.

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
from datetime import datetime, timezone

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

if "--publish" not in sys.argv:
    sys.exit(0)

# ---- publish: APK + version.json in the files bucket ----------------------------------------------------------
config = json.load(open(os.path.join(ROOT, "deploy", "deployment.json"), encoding="utf-8"))
bucket = config["storageBucket"]
endpoint = f"https://{store['R2AccountId']}.r2.cloudflarestorage.com"
aws_env = dict(os.environ, AWS_ACCESS_KEY_ID=store["R2AccessKeyId"], AWS_SECRET_ACCESS_KEY=store["R2SecretAccessKey"],
               AWS_DEFAULT_REGION="auto", MSYS_NO_PATHCONV="1")
aws = ["docker", "run", "--rm", "-i", "-e", "AWS_ACCESS_KEY_ID", "-e", "AWS_SECRET_ACCESS_KEY", "-e", "AWS_DEFAULT_REGION",
       "-v", f"{out}:/work", "amazon/aws-cli:2.27.0"]


def s3(*args, input_text=None, check=True):
    result = subprocess.run(aws + ["s3", *args, "--endpoint-url", endpoint], env=aws_env, capture_output=True, text=True,
                            input=input_text)
    if check and result.returncode != 0:
        sys.exit("Upload failed: " + result.stderr.strip()[-500:])
    return result


# The minimum only rises with --required; otherwise it stays as the previous release left it.
previous = s3("cp", f"s3://{bucket}/app/android/version.json", "-", check=False)
previous_min = json.loads(previous.stdout).get("minVersionCode", 0) if previous.returncode == 0 and previous.stdout.strip() else 0
code = int(version_code)
apk_name = f"{plain_title.replace(' ', '-')}-{version_name}.apk"
apk_key = f"app/android/{apk_name}"
shutil.copy2(glob.glob(os.path.join(out, "*-Signed.apk"))[0], os.path.join(out, apk_name))
s3("cp", f"/work/{apk_name}", f"s3://{bucket}/{apk_key}", "--content-type", "application/vnd.android.package-archive",
   "--only-show-errors")
release = {
    "versionCode": code,
    "versionName": version_name,
    "minVersionCode": code if "--required" in sys.argv else previous_min,
    "apkKey": apk_key,
    "publishedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
}
s3("cp", "-", f"s3://{bucket}/app/android/version.json", "--content-type", "application/json", input_text=json.dumps(release))
os.remove(os.path.join(out, apk_name))
print(f"Published {version_name} (code {code}, minimum {release['minVersionCode']}): {apk_key}")
print("The apps offer it when they open; users with the app on a phone get one notice within ~10 minutes.")

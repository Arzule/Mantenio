"""
Creates the release signing keystore of a condo's Android app, once, OUTSIDE the repo:
    %USERPROFILE%/.mantenio/<slug>-release.keystore
The random password goes to secrets.json (AndroidKeystorePassword) and the path to AndroidKeystorePath.

BACK UP the keystore file and its password (e.g. a password manager): without them the app can never be updated in
Google Play, and installed copies cannot be upgraded with an APK signed by another key.

Usage (from the repo root of a condo deployment repo):
    python deploy/android_keystore.py
"""
import json
import os
import re
import secrets
import string
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
secrets_path = os.path.join(ROOT, "secrets.json")
store = json.load(open(secrets_path, encoding="utf-8"))

props = os.path.join(ROOT, "Condo.props")
slug = "demo"
if os.path.exists(props):
    match = re.search(r"<Condo\b[^>]*>\s*([^<\s]+)\s*</Condo>", open(props, encoding="utf-8").read())
    slug = match.group(1) if match else slug
profile = json.load(open(os.path.join(ROOT, "condos", slug, "condo.json"), encoding="utf-8"))

path = os.path.join(os.path.expanduser("~"), ".mantenio", f"{slug}-release.keystore")
if os.path.exists(path) or store.get("AndroidKeystorePath"):
    sys.exit(f"A keystore already exists ({store.get('AndroidKeystorePath') or path}); not creating another one.")

os.makedirs(os.path.dirname(path), exist_ok=True)
password = "".join(secrets.choice(string.ascii_letters + string.digits) for _ in range(32))
java_home = os.environ.get("JAVA_HOME") or r"C:\Program Files\Java\jdk-21"
keytool = os.path.join(java_home, "bin", "keytool")
result = subprocess.run([keytool, "-genkeypair", "-keystore", path, "-alias", "mantenio", "-keyalg", "RSA", "-keysize", "2048",
                         "-validity", "10000", "-storepass", password, "-keypass", password,
                         "-dname", f"CN={profile['displayName']}, O={profile['displayName']}, C=PE"],
                        capture_output=True, text=True)
if result.returncode != 0:
    sys.exit("keytool failed: " + result.stderr[-500:].replace(password, "***"))

store["AndroidKeystorePath"] = path
store["AndroidKeystorePassword"] = password
json.dump(store, open(secrets_path, "w", encoding="utf-8"), indent=2)
print(f"Created {path} (alias 'mantenio'); password saved in secrets.json. Back up both.")

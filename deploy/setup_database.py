"""
One-time setup of a condo database: migrations, apartments from the profile, founder admin.

Usage (from the repo root of a condo deployment repo):
    python deploy/setup_database.py --email <founder email> --first-name <name> --last-name <name>

Uses secrets.json AppDbConnectionString. The founder's temporary password is stored in secrets.json
(FounderTemporaryPassword) instead of being printed; it must be changed at the first login.
"""
import argparse
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # wrangler prints emoji; the Windows console does not
parser = argparse.ArgumentParser()
parser.add_argument("--email", required=True)
parser.add_argument("--first-name", required=True)
parser.add_argument("--last-name", required=True)
args = parser.parse_args()

secrets_path = os.path.join(ROOT, "secrets.json")
store = json.load(open(secrets_path, encoding="utf-8"))
env = dict(os.environ,
           ASPNETCORE_ENVIRONMENT="Production",
           ConnectionStrings__mantenio=store["AppDbConnectionString"],
           Jwt__SigningKey="setup-commands-do-not-sign-tokens-0123456789",
           Email__Host="localhost")


def api(*command):
    result = subprocess.run(["dotnet", "run", "--project", "src/Mantenio.Api", "-c", "Release", "--no-launch-profile",
                             "--", *command], cwd=ROOT, env=env, capture_output=True, text=True,
                            encoding="utf-8", errors="replace")
    output = (result.stdout + result.stderr).strip()
    return result.returncode, output


for step in (["migrate"], ["seed-apartments"]):
    code, output = api(*step)
    print(f"{step[0]}: {output.splitlines()[-1] if output else ''}")
    if code != 0:
        sys.exit(1)

code, output = api("seed-founder", "--email", args.email, "--first-name", args.first_name, "--last-name", args.last_name)
match = re.search(r"Temporary password[^:]*: (\S+)", output)
if match:
    store["FounderTemporaryPassword"] = match.group(1)
    json.dump(store, open(secrets_path, "w", encoding="utf-8"), indent=2)
    print(f"seed-founder: created {args.email}; temporary password saved in secrets.json (FounderTemporaryPassword)")
else:
    print("seed-founder: " + (output.splitlines()[-1] if output else "no output"))
    sys.exit(0 if "already exists" in output else 1)

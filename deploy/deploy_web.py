"""
Publishes the Blazor WebAssembly app and deploys it to Cloudflare Pages (free, global CDN).

Usage (from the repo root of a condo deployment repo):
    python deploy/deploy_web.py

Needs: Node.js (npx wrangler), deploy/deployment.json (pagesProject) and secrets.json
(CloudflareApiToken with "Cloudflare Pages: Edit", R2AccountId = the Cloudflare account id).
The API address comes from the condo profile (condo.json "apiBaseUrl").
"""
import json
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # wrangler prints emoji; the Windows console does not
config = json.load(open(os.path.join(ROOT, "deploy", "deployment.json"), encoding="utf-8"))
store = json.load(open(os.path.join(ROOT, "secrets.json"), encoding="utf-8"))
env = dict(os.environ, CLOUDFLARE_API_TOKEN=store["CloudflareApiToken"], CLOUDFLARE_ACCOUNT_ID=store["R2AccountId"],
           MSYS_NO_PATHCONV="1")
shell = os.name == "nt"


def run(args, check=True):
    result = subprocess.run(args, cwd=ROOT, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", shell=shell)
    if check and result.returncode != 0:
        sys.exit(f"Command failed: {' '.join(args[:3])}\n{(result.stderr or result.stdout)[-1500:]}")
    return result


out = os.path.join(ROOT, "artifacts", "web")
shutil.rmtree(out, ignore_errors=True)
run(["dotnet", "publish", "src/Mantenio.Web", "-c", "Release", "-o", out])
site = os.path.join(out, "wwwroot")

project = config["pagesProject"]
listing = run(["npx", "--yes", "wrangler@4", "pages", "project", "list"], check=False)
if project not in listing.stdout:
    # --force: create a classic Pages project (newer wrangler would otherwise turn it into a Worker).
    run(["npx", "--yes", "wrangler@4", "pages", "project", "create", project, "--production-branch", "main", "--force"])
    print(f"Created Pages project {project}")

result = run(["npx", "--yes", "wrangler@4", "pages", "deploy", site, "--project-name", project, "--branch", "main",
              "--commit-dirty=true"])
print(result.stdout.strip().splitlines()[-1] if result.stdout.strip() else "Deployed")
print(f"Web: https://{project}.pages.dev")

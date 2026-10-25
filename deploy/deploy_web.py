"""
Publishes the Blazor WebAssembly app and deploys it to Cloudflare Pages (free, global CDN).

Usage (from the repo root of a condo deployment repo):
    python deploy/deploy_web.py

Needs: Node.js (npx wrangler), deploy/deployment.json (pagesProject) and secrets.json
(CloudflareApiToken with "Cloudflare Pages: Edit", R2AccountId = the Cloudflare account id).
The API address comes from the condo profile (condo.json "apiBaseUrl").
"""
import base64
import hashlib
import json
import os
import re
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


def api_origin():
    """apiBaseUrl of the condo profile this repo builds (Condo.props, default demo)."""
    props = os.path.join(ROOT, "Condo.props")
    slug = "demo"
    if os.path.exists(props):
        match = re.search(r"<Condo\b[^>]*>\s*([^<\s]+)\s*</Condo>", open(props, encoding="utf-8").read())
        slug = match.group(1) if match else slug
    profile = json.load(open(os.path.join(ROOT, "condos", slug, "condo.json"), encoding="utf-8"))
    return (profile.get("apiBaseUrl") or "").rstrip("/")


def write_security_headers(site_dir):
    """Cloudflare Pages `_headers`: CSP (the inline import map is allowed by its hash), no framing, no sniffing."""
    index = open(os.path.join(site_dir, "index.html"), encoding="utf-8").read()
    hashes = " ".join(f"'sha256-{base64.b64encode(hashlib.sha256(m.encode()).digest()).decode()}'"
                      for m in re.findall(r'<script type="importmap">(.*?)</script>', index, re.S))
    csp = "; ".join([
        "default-src 'self'",
        f"script-src 'self' 'wasm-unsafe-eval' {hashes}".strip(),
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com",
        "font-src 'self' https://fonts.gstatic.com",
        "img-src 'self' data: blob: https://*.r2.cloudflarestorage.com",
        f"connect-src 'self' {api_origin()} https://*.r2.cloudflarestorage.com",
        "object-src 'none'",
        "base-uri 'self'",
        "frame-ancestors 'none'",
    ])
    with open(os.path.join(site_dir, "_headers"), "w", encoding="utf-8", newline="\n") as f:
        f.write("/*\n")
        f.write(f"  Content-Security-Policy: {csp}\n")
        f.write("  X-Content-Type-Options: nosniff\n")
        f.write("  X-Frame-Options: DENY\n")
        f.write("  Referrer-Policy: strict-origin-when-cross-origin\n")
        f.write("  Permissions-Policy: geolocation=(), microphone=()\n")


write_security_headers(site)

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

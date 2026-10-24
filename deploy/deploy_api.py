"""
Builds the API container image, pushes it to GHCR and creates/updates the Azure Container App.

Usage (from the repo root of a condo deployment repo):
    python deploy/deploy_api.py [--skip-build]

Needs: `az login` (Azure), `gh auth login` (pushes the image with your GitHub token), Docker running,
deploy/deployment.json (resource names, storageBucket) and secrets.json (AppDbConnectionString, GhcrReadToken,
R2AccountId, R2AccessKeyId, R2SecretAccessKey; JwtSigningKey is generated on the first run). Secrets go to Container Apps secrets and are never printed.
"""
import json
import os
import secrets
import string
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # wrangler prints emoji; the Windows console does not
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")


def run(args, *, capture=False, secret_values=()):
    result = subprocess.run(args, cwd=ROOT, env=ENV, capture_output=True, text=True, encoding="utf-8", errors="replace",
                            shell=os.name == "nt")
    if result.returncode != 0:
        message = (result.stderr or result.stdout)
        for value in secret_values:
            message = message.replace(value, "***")
        sys.exit(f"Command failed: {args[0]} {args[1] if len(args) > 1 else ''}\n{message[-1500:]}")
    return result.stdout.strip() if capture else None


config = json.load(open(os.path.join(ROOT, "deploy", "deployment.json"), encoding="utf-8"))
secrets_path = os.path.join(ROOT, "secrets.json")
store = json.load(open(secrets_path, encoding="utf-8"))
if not store.get("JwtSigningKey"):
    store["JwtSigningKey"] = "".join(secrets.choice(string.ascii_letters + string.digits) for _ in range(64))
    json.dump(store, open(secrets_path, "w", encoding="utf-8"), indent=2)
    print("Generated JwtSigningKey (saved in secrets.json).")

rg, app, env_name = config["resourceGroup"], config["apiApp"], config["containerEnvironment"]
image_repo = config["image"]  # e.g. arzule/mantenio-elprado-api
tag = run(["git", "rev-parse", "--short", "HEAD"], capture=True)
image = f"ghcr.io/{image_repo}:{tag}"

if "--skip-build" not in sys.argv:
    github_token = run(["gh", "auth", "token"], capture=True)
    login = subprocess.run(["docker", "login", "ghcr.io", "-u", config["githubUser"], "--password-stdin"],
                           input=github_token, env=ENV, capture_output=True, text=True)
    if login.returncode != 0:
        sys.exit("docker login ghcr.io failed: " + login.stderr[-300:])
    run(["dotnet", "publish", "src/Mantenio.Api", "-c", "Release", "-t:PublishContainer",
         "-p:ContainerRegistry=ghcr.io", f"-p:ContainerRepository={image_repo}", f"-p:ContainerImageTags={tag}"])
    print(f"Pushed {image}")

acs_connection = run(["az", "communication", "list-key", "-n", config["communicationService"], "-g", rg,
                      "--query", "primaryConnectionString", "-o", "tsv"], capture=True)
sender_domain = run(["az", "communication", "email", "domain", "show", "--domain-name", "AzureManagedDomain",
                     "--email-service-name", config["emailService"], "-g", rg,
                     "--query", "mailFromSenderDomain", "-o", "tsv"], capture=True)

app_secrets = {
    "db-connection": store["AppDbConnectionString"],
    "jwt-signing-key": store["JwtSigningKey"],
    "acs-connection": acs_connection,
    "r2-access-key-id": store["R2AccessKeyId"],
    "r2-secret-access-key": store["R2SecretAccessKey"],
}
env_vars = {
    "ASPNETCORE_FORWARDEDHEADERS_ENABLED": "true",
    "Database__MigrateOnStartup": "true",
    "ConnectionStrings__mantenio": "secretref:db-connection",
    "Jwt__SigningKey": "secretref:jwt-signing-key",
    "Email__AcsConnectionString": "secretref:acs-connection",
    "Email__FromAddress": f"DoNotReply@{sender_domain}",
    # Payment-proof screenshots and receipt PDFs (private bucket, read through presigned URLs).
    "Storage__Provider": "R2",
    "Storage__Bucket": config["storageBucket"],
    "Storage__R2AccountId": store["R2AccountId"],
    "Storage__R2AccessKeyId": "secretref:r2-access-key-id",
    "Storage__R2SecretAccessKey": "secretref:r2-secret-access-key",
}
for i, origin in enumerate(config["corsOrigins"]):
    env_vars[f"Cors__AllowedOrigins__{i}"] = origin

secret_args = [f"{k}={v}" for k, v in app_secrets.items()]
env_args = [f"{k}={v}" for k, v in env_vars.items()]
hidden = list(app_secrets.values()) + [store["GhcrReadToken"]]

exists = subprocess.run(["az", "containerapp", "show", "-n", app, "-g", rg, "-o", "none"],
                        env=ENV, capture_output=True, shell=os.name == "nt").returncode == 0
if not exists:
    run(["az", "containerapp", "create", "-n", app, "-g", rg, "--environment", env_name, "--image", image,
         "--registry-server", "ghcr.io", "--registry-username", config["githubUser"],
         "--registry-password", store["GhcrReadToken"],
         "--target-port", "8080", "--ingress", "external",
         "--min-replicas", "0", "--max-replicas", "1", "--cpu", "0.25", "--memory", "0.5Gi",
         "--secrets", *secret_args, "--env-vars", *env_args, "-o", "none"], secret_values=hidden)
    print(f"Created container app {app}")
else:
    run(["az", "containerapp", "secret", "set", "-n", app, "-g", rg, "--secrets", *secret_args, "-o", "none"],
        secret_values=hidden)
    run(["az", "containerapp", "update", "-n", app, "-g", rg, "--image", image,
         "--set-env-vars", *env_args, "-o", "none"], secret_values=hidden)
    print(f"Updated container app {app}")

fqdn = run(["az", "containerapp", "show", "-n", app, "-g", rg,
            "--query", "properties.configuration.ingress.fqdn", "-o", "tsv"], capture=True)
print(f"API: https://{fqdn}")

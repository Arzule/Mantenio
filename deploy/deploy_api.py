"""
Builds the API container image, pushes it to GHCR and creates/updates the Azure Container App.

Usage (from the repo root of a condo deployment repo):
    python deploy/deploy_api.py [--skip-build]

Needs: `az login` (Azure), `gh auth login` (pushes the image with your GitHub token), Docker running,
deploy/deployment.json (resource names, storageBucket) and secrets.json (AppDbConnectionString, GhcrReadToken,
R2AccountId, R2AccessKeyId, R2SecretAccessKey; optional FirebaseServiceAccount for push; JwtSigningKey is generated
on the first run). Secrets go to Container Apps secrets and are never printed.
The scheduled jobs run the same image: deploy them afterwards with deploy/deploy_jobs.py.
"""
import subprocess
import sys

from common import ENV, app_settings, git_tag, load, run

config, store = load()
rg, app, env_name = config["resourceGroup"], config["apiApp"], config["containerEnvironment"]
image_repo = config["image"]  # e.g. arzule/mantenio-elprado-api
tag = git_tag()
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

app_secrets, env_vars = app_settings(config, store)
env_vars["Database__MigrateOnStartup"] = "true"
for i, origin in enumerate(config["corsOrigins"]):
    env_vars[f"Cors__AllowedOrigins__{i}"] = origin

secret_args = [f"{k}={v}" for k, v in app_secrets.items()]
env_args = [f"{k}={v}" for k, v in env_vars.items()]
hidden = list(app_secrets.values()) + [store["GhcrReadToken"]]

exists = subprocess.run(["az", "containerapp", "show", "-n", app, "-g", rg, "-o", "none"],
                        env=ENV, capture_output=True, shell=True).returncode == 0
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

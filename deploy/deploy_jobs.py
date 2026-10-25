"""
Creates/updates the scheduled jobs (Azure Container Apps Jobs, cron in UTC; Lima = UTC-5, no DST):

    generate-charges  day 1, 00:05 Lima   5 5 1 * *
    late-fees         daily, 00:30 Lima   30 5 * * *
    reminders         day 15, 09:00 Lima  0 14 15 * *
    cleanup           daily, 03:30 Lima   30 8 * * *
    backup            daily, 03:00 Lima   0 8 * * *   (own image: deploy/backup, pg_dump -> R2)

Usage (after deploy/deploy_api.py, which pushed the API image of this commit):
    python deploy/deploy_jobs.py [--skip-backup-build]

Needs what deploy_api.py needs, plus deploy/deployment.json "backupBucket" and "jobsEnvironment": a Container Apps
environment in WorkloadProfiles mode (Express environments, like the one the API may run in, do not support jobs):
    az containerapp env create -n <name> -g <rg> --location <region> --logs-destination none --environment-mode WorkloadProfiles
"""
import re
import subprocess
import sys

from common import ENV, app_settings, git_tag, load, run

JOBS = {
    "generate-charges": "5 5 1 * *",
    "late-fees": "30 5 * * *",
    "reminders": "0 14 15 * *",
    "cleanup": "30 8 * * *",
}
BACKUP_CRON = "0 8 * * *"

config, store = load()
rg, slug = config["resourceGroup"], config["apiApp"].removeprefix("api-")
env_name = config.get("jobsEnvironment", config["containerEnvironment"])
tag = git_tag()
api_image = f"ghcr.io/{config['image']}:{tag}"
backup_repo = config["image"].removesuffix("-api") + "-backup"
backup_image = f"ghcr.io/{backup_repo}:{tag}"


def libpq(npgsql):
    """Npgsql connection string -> libpq keyword/value string for pg_dump."""
    parts = dict(p.split("=", 1) for p in npgsql.split(";") if "=" in p)
    keys = {k.strip().lower().replace(" ", ""): v.strip() for k, v in parts.items()}
    def quote(value):
        return "'" + value.replace("\\", "\\\\").replace("'", "\\'") + "'"
    return " ".join([
        f"host={quote(keys['host'])}", f"port={quote(keys.get('port', '5432'))}", f"dbname={quote(keys['database'])}",
        f"user={quote(keys.get('username') or keys['userid'])}", f"password={quote(keys['password'])}", "sslmode=require",
    ])


def exists(name):
    return subprocess.run(["az", "containerapp", "job", "show", "-n", name, "-g", rg, "-o", "none"],
                          env=ENV, capture_output=True, shell=True).returncode == 0


def deploy(name, image, cron, secrets_map, env_map, args, hidden):
    secret_args = [f"{k}={v}" for k, v in secrets_map.items()]
    env_args = [f"{k}={v}" for k, v in env_map.items()]
    if not exists(name):
        run(["az", "containerapp", "job", "create", "-n", name, "-g", rg, "--environment", env_name,
             "--trigger-type", "Schedule", "--cron-expression", cron,
             "--replica-timeout", "1800", "--replica-retry-limit", "1", "--parallelism", "1", "--replica-completion-count", "1",
             "--image", image, "--registry-server", "ghcr.io", "--registry-username", config["githubUser"],
             "--registry-password", store["GhcrReadToken"], "--cpu", "0.25", "--memory", "0.5Gi",
             "--secrets", *secret_args, "--env-vars", *env_args, *(["--args", *args] if args else []), "-o", "none"],
            secret_values=hidden)
        print(f"Created job {name} ({cron} UTC)")
    else:
        run(["az", "containerapp", "job", "secret", "set", "-n", name, "-g", rg, "--secrets", *secret_args, "-o", "none"],
            secret_values=hidden)
        run(["az", "containerapp", "job", "update", "-n", name, "-g", rg, "--image", image, "--cron-expression", cron,
             "--set-env-vars", *env_args, *(["--args", *args] if args else []), "-o", "none"], secret_values=hidden)
        print(f"Updated job {name} ({cron} UTC)")


app_secrets, env_vars = app_settings(config, store)
env_vars["Database__MigrateOnStartup"] = "false"
hidden = list(app_secrets.values()) + [store["GhcrReadToken"]]
for job, cron in JOBS.items():
    deploy(f"job-{slug}-{job}", api_image, cron, app_secrets, env_vars, ["job", job], hidden)

# Nightly backup: its own small image with pg_dump and the AWS CLI (R2 speaks S3).
if "--skip-backup-build" not in sys.argv:
    github_token = run(["gh", "auth", "token"], capture=True)
    login = subprocess.run(["docker", "login", "ghcr.io", "-u", config["githubUser"], "--password-stdin"],
                           input=github_token, env=ENV, capture_output=True, text=True)
    if login.returncode != 0:
        sys.exit("docker login ghcr.io failed: " + login.stderr[-300:])
    run(["docker", "build", "-t", backup_image, "deploy/backup"])
    run(["docker", "push", backup_image])
    print(f"Pushed {backup_image}")

backup_secrets = {
    "pg-connection": libpq(store["AppDbConnectionString"]),
    "r2-access-key-id": store["R2AccessKeyId"],
    "r2-secret-access-key": store["R2SecretAccessKey"],
}
backup_env = {
    "PG_CONNECTION": "secretref:pg-connection",
    "AWS_ACCESS_KEY_ID": "secretref:r2-access-key-id",
    "AWS_SECRET_ACCESS_KEY": "secretref:r2-secret-access-key",
    "R2_ACCOUNT_ID": store["R2AccountId"],
    "BACKUP_BUCKET": config["backupBucket"],
    "BACKUP_PREFIX": re.sub(r"[^a-z0-9-]", "", slug),
}
deploy(f"job-{slug}-backup", backup_image, BACKUP_CRON, backup_secrets, backup_env, None,
       list(backup_secrets.values()) + [store["GhcrReadToken"]])
print("Jobs ready. Run one now with: az containerapp job start -n <name> -g " + rg)

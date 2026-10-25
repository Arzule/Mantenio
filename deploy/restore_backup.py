"""
Restore drill (or real restore): downloads the newest nightly backup from the R2 backups bucket and restores it into
a throwaway local PostgreSQL 18 container, then prints row counts. The container is left running when --keep is
given (connect with: docker exec -it mantenio-restore psql -U postgres -d restored).

Usage (from the repo root of a condo deployment repo, Docker running):
    python deploy/restore_backup.py [--keep]

Needs deploy/deployment.json "backupBucket" and secrets.json R2AccountId, R2AccessKeyId, R2SecretAccessKey.
To restore into a real server instead: pg_restore --no-owner --no-privileges -d <connection> <file>.dump
"""
import json
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
config = json.load(open(os.path.join(ROOT, "deploy", "deployment.json"), encoding="utf-8"))
store = json.load(open(os.path.join(ROOT, "secrets.json"), encoding="utf-8"))
work = os.path.join(ROOT, "artifacts", "restore")
os.makedirs(work, exist_ok=True)
env = dict(os.environ, AWS_ACCESS_KEY_ID=store["R2AccessKeyId"], AWS_SECRET_ACCESS_KEY=store["R2SecretAccessKey"],
           AWS_DEFAULT_REGION="auto", MSYS_NO_PATHCONV="1")
endpoint = f"https://{store['R2AccountId']}.r2.cloudflarestorage.com"
bucket = config["backupBucket"]
aws = ["docker", "run", "--rm", "-e", "AWS_ACCESS_KEY_ID", "-e", "AWS_SECRET_ACCESS_KEY", "-e", "AWS_DEFAULT_REGION",
       "-v", f"{work}:/work", "amazon/aws-cli:2.27.0"]

listing = subprocess.run(aws + ["s3", "ls", f"s3://{bucket}/", "--endpoint-url", endpoint], env=env, capture_output=True, text=True)
dumps = sorted(line.split()[-1] for line in listing.stdout.splitlines() if line.strip().endswith(".dump"))
if not dumps:
    sys.exit(f"No backups in {bucket}: {listing.stderr.strip()[-300:]}")
newest = dumps[-1]
subprocess.run(aws + ["s3", "cp", f"s3://{bucket}/{newest}", f"/work/{newest}", "--endpoint-url", endpoint, "--only-show-errors"],
               env=env, check=True)
print(f"Downloaded {newest} ({os.path.getsize(os.path.join(work, newest))} bytes)")

name = "mantenio-restore"
subprocess.run(["docker", "rm", "-f", name], capture_output=True)
subprocess.run(["docker", "run", "-d", "--name", name, "-e", "POSTGRES_PASSWORD=restore", "-v", f"{work}:/work", "postgres:18"],
               env=env, check=True, capture_output=True)
for _ in range(60):
    if subprocess.run(["docker", "exec", name, "pg_isready", "-U", "postgres"], capture_output=True).returncode == 0:
        break
    time.sleep(1)
time.sleep(2)
psql = ["docker", "exec", "-e", "PGPASSWORD=restore", name, "psql", "-U", "postgres", "-t", "-A"]
subprocess.run(psql + ["-c", "CREATE DATABASE restored"], check=True, capture_output=True)
restore = subprocess.run(["docker", "exec", "-e", "PGPASSWORD=restore", name, "pg_restore", "-U", "postgres", "-d", "restored",
                          "--no-owner", "--no-privileges", f"/work/{newest}"], capture_output=True, text=True)
if restore.returncode != 0:
    sys.exit("pg_restore failed: " + restore.stderr.strip()[-800:])
query = ('select (select count(*) from "Apartments") apartments, (select count(*) from "AspNetUsers") users, '
         '(select count(*) from "Charges") charges, (select count(*) from "Receipts") receipts, '
         '(select count(*) from "AuditEvents") audit_events, (select max("MigrationId") from "__EFMigrationsHistory") migration')
print("Restored (apartments|users|charges|receipts|audit events|migration):",
      subprocess.run(psql + ["-d", "restored", "-c", query], capture_output=True, text=True).stdout.strip())
os.remove(os.path.join(work, newest))
if "--keep" not in sys.argv:
    subprocess.run(["docker", "rm", "-f", name], capture_output=True)
    print("Restore drill OK (container removed).")

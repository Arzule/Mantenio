"""Shared by the deploy scripts: configuration, secrets and the settings the API and its jobs run with."""
import base64
import json
import os
import secrets
import string
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # wrangler prints emoji; the Windows console does not
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")


def run(args, *, capture=False, secret_values=(), input_text=None):
    result = subprocess.run(args, cwd=ROOT, env=ENV, capture_output=True, text=True, encoding="utf-8", errors="replace",
                            shell=os.name == "nt" and input_text is None, input=input_text)
    if result.returncode != 0:
        message = (result.stderr or result.stdout)
        for value in secret_values:
            message = message.replace(value, "***")
        sys.exit(f"Command failed: {args[0]} {args[1] if len(args) > 1 else ''}\n{message[-1500:]}")
    return result.stdout.strip() if capture else None


def load():
    """deploy/deployment.json and secrets.json (JwtSigningKey is generated on the first run)."""
    config = json.load(open(os.path.join(ROOT, "deploy", "deployment.json"), encoding="utf-8"))
    secrets_path = os.path.join(ROOT, "secrets.json")
    store = json.load(open(secrets_path, encoding="utf-8"))
    if not store.get("JwtSigningKey"):
        store["JwtSigningKey"] = "".join(secrets.choice(string.ascii_letters + string.digits) for _ in range(64))
        json.dump(store, open(secrets_path, "w", encoding="utf-8"), indent=2)
        print("Generated JwtSigningKey (saved in secrets.json).")
    return config, store


def firebase_service_account(store):
    """The Firebase service account key: secrets.json FirebaseServiceAccount, or firebase-service-account.json."""
    if store.get("FirebaseServiceAccount"):
        return store["FirebaseServiceAccount"]
    path = os.path.join(ROOT, "firebase-service-account.json")
    return open(path, encoding="utf-8").read() if os.path.exists(path) else None


def git_tag():
    return run(["git", "rev-parse", "--short", "HEAD"], capture=True)


def app_settings(config, store):
    """(secrets, env vars) of the API container and of the jobs that run the same image."""
    rg = config["resourceGroup"]
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

    service_account = firebase_service_account(store)
    if service_account:
        # Base64: the key is multi-line JSON and goes through the command line.
        app_secrets["firebase-service-account"] = base64.b64encode(service_account.encode()).decode()
        env_vars["Push__ServiceAccountJson"] = "secretref:firebase-service-account"
    else:
        print("No Firebase service account (secrets.json FirebaseServiceAccount or firebase-service-account.json): push stays off.")

    return app_secrets, env_vars

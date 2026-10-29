"""
Creates (or rotates the password of) the login the API and its jobs run with: it can read and write the app data, but
it does not own the tables, so it cannot drop or alter them, disable triggers, delete ledger rows or edit the audit
log. Migrations keep using the owner login (AppDbConnectionString). Runs psql from the postgres:18 image.

Usage: python deploy/aiven_runtime_user.py <secrets.json> <database> <owner role> <runtime role>
Reads AivenServiceUri (admin) from secrets.json and writes AppRuntimeConnectionString (Npgsql format) back to it.
Run it after the tables exist (after the first deploy) and again whenever you want to rotate its password.
Nothing secret is printed.
"""
import json
import os
import secrets
import string
import subprocess
import sys
from urllib.parse import urlparse

secrets_path, database, owner, runtime = sys.argv[1:5]
data = json.load(open(secrets_path, encoding="utf-8"))
admin = urlparse(data["AivenServiceUri"])
password = "".join(secrets.choice(string.ascii_letters + string.digits) for _ in range(32))
ledger = '"Charges", "PaymentProofs", "PaymentAllocations", "Receipts"'

sql = f"""
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{runtime}') THEN
    CREATE ROLE {runtime} LOGIN PASSWORD '{password}' CONNECTION LIMIT 15;
  ELSE
    ALTER ROLE {runtime} WITH LOGIN PASSWORD '{password}' CONNECTION LIMIT 15;
  END IF;
END $$;
GRANT {owner} TO CURRENT_USER;
GRANT CONNECT ON DATABASE {database} TO {runtime};
GRANT USAGE ON SCHEMA public TO {runtime};
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {runtime};
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {runtime};
ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {runtime};
ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {runtime};
REVOKE DELETE, TRUNCATE ON {ledger} FROM {runtime};
REVOKE UPDATE, DELETE, TRUNCATE ON "AuditEvents" FROM {runtime};
"""
admin_db = admin._replace(path=f"/{database}").geturl()
env = dict(os.environ, PGURI=admin_db, PGSQL=sql, MSYS_NO_PATHCONV="1")
result = subprocess.run(["docker", "run", "--rm", "-e", "PGURI", "-e", "PGSQL", "postgres:18", "sh", "-c",
                         'psql "$PGURI" -v ON_ERROR_STOP=1 -qc "$PGSQL"'], env=env, capture_output=True, text=True)
if result.returncode != 0:
    sys.exit("psql failed: " + result.stderr.replace(password, "***")[-400:])

data["AppRuntimeConnectionString"] = (
    f"Host={admin.hostname};Port={admin.port};Database={database};Username={runtime};Password={password};SSL Mode=Require")
json.dump(data, open(secrets_path, "w", encoding="utf-8"), indent=2)
print(f"Role '{runtime}' ready (no DROP/ALTER, no ledger deletes, no audit edits). AppRuntimeConnectionString saved to {secrets_path}.")

"""
Creates (or rotates the password of) the dedicated PostgreSQL login the API uses on Aiven and makes it the owner
of the condo database, so EF Core migrations can create tables. Runs psql from the postgres:18 image.

Usage: python deploy/aiven_app_user.py <secrets.json> <database> <role>
Reads AivenServiceUri (admin) from secrets.json and writes AppDbConnectionString (Npgsql format) back to it.
Nothing secret is printed.
"""
import json
import os
import secrets
import string
import subprocess
import sys
from urllib.parse import urlparse

secrets_path, database, role = sys.argv[1], sys.argv[2], sys.argv[3]
data = json.load(open(secrets_path, encoding="utf-8"))
admin = urlparse(data["AivenServiceUri"])
password = "".join(secrets.choice(string.ascii_letters + string.digits) for _ in range(32))

sql = f"""
DO $$
BEGIN
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{role}') THEN
    CREATE ROLE {role} LOGIN PASSWORD '{password}' CONNECTION LIMIT 15;
  ELSE
    ALTER ROLE {role} WITH LOGIN PASSWORD '{password}' CONNECTION LIMIT 15;
  END IF;
END $$;
GRANT {role} TO CURRENT_USER;
ALTER DATABASE {database} OWNER TO {role};
"""
admin_db = admin._replace(path=f"/{database}").geturl()
env = dict(os.environ, PGURI=admin_db, PGSQL=sql, MSYS_NO_PATHCONV="1")
result = subprocess.run(
    ["docker", "run", "--rm", "-e", "PGURI", "-e", "PGSQL", "postgres:18", "sh", "-c",
     'psql "$PGURI" -v ON_ERROR_STOP=1 -qc "$PGSQL" && psql "$PGURI" -qc "ALTER SCHEMA public OWNER TO ' + role + '"'],
    env=env, capture_output=True, text=True)
if result.returncode != 0:
    sys.exit("psql failed: " + result.stderr.replace(password, "***")[-400:])

data["AppDbConnectionString"] = (
    f"Host={admin.hostname};Port={admin.port};Database={database};Username={role};Password={password};"
    "SSL Mode=Require")
json.dump(data, open(secrets_path, "w", encoding="utf-8"), indent=2)
print(f"Role '{role}' ready and owner of '{database}'. AppDbConnectionString saved to {secrets_path}.")

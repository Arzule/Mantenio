# Nightly database backup (Container Apps Job): pg_dump in custom format, uploaded to the private R2 backups bucket.
# Retention (30 days) is an R2 lifecycle rule on the bucket. Restore with pg_restore into any PostgreSQL 18.
FROM postgres:18-alpine
RUN apk add --no-cache aws-cli
COPY backup.sh /usr/local/bin/backup.sh
ENTRYPOINT ["/bin/sh", "/usr/local/bin/backup.sh"]

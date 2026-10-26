#!/bin/sh
# Env: PG_CONNECTION (libpq "host=... dbname=... user=... password=... sslmode=require"), BACKUP_BUCKET,
#      BACKUP_PREFIX, R2_ACCOUNT_ID, AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY.
set -eu
name="${BACKUP_PREFIX}-$(date -u +%Y-%m-%dT%H%M%SZ).dump"
pg_dump --format=custom --no-owner --no-privileges --dbname="$PG_CONNECTION" --file="/tmp/$name"
export AWS_DEFAULT_REGION=auto AWS_REQUEST_CHECKSUM_CALCULATION=when_required AWS_RESPONSE_CHECKSUM_VALIDATION=when_required
aws s3 cp "/tmp/$name" "s3://$BACKUP_BUCKET/$name" --endpoint-url "https://$R2_ACCOUNT_ID.r2.cloudflarestorage.com" --only-show-errors
echo "Backup uploaded: $name ($(wc -c < "/tmp/$name") bytes)"

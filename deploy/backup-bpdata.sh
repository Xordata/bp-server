#!/bin/bash
# /usr/local/bin/backup-bpdata.sh — cron: 30 3 * * * (после backup_maddy.sh в 03:00)
set -euo pipefail
ts=$(date +%Y%m%d-%H%M)
tar -czf /var/backups/bpdata-$ts.tar.gz -C /opt bpdata
find /var/backups -name 'bpdata-*.tar.gz' -mtime +14 -delete

#!/bin/bash
# /usr/local/bin/backup-bpdata.sh — cron: 30 3 * * * (после backup_maddy.sh в 03:00)
# Один архив всех пользователей /opt/bpdata → /opt/bpdata/backup/bpdata-<ts>.tar.gz.
# Ротация: последние KEEP архивов. rsync/borg забирает /opt/bpdata/backup/ на внешнее хранилище.
set -euo pipefail

SRC=/opt/bpdata
DEST=$SRC/backup
KEEP=14
ts=$(date +%Y%m%d-%H%M%S)

mkdir -p "$DEST"

# --exclude=bpdata/backup — КРИТИЧНО: без него tar читает архив, который сам же пишет в
# обходимый каталог (рекурсия). Паттерн матчится по пути члена архива (-C /opt → bpdata/...).
# data.bin может дописываться во время упаковки: exit 1 у tar = «file changed as we read it»,
# это допустимо (append-only журнал — хвост просто попадёт в следующий прогон); fatal — rc>=2.
rc=0
tar --warning=no-file-changed \
    --exclude=bpdata/backup \
    -czf "$DEST/bpdata-$ts.tar.gz.part" -C /opt bpdata || rc=$?
if [ "$rc" -ge 2 ]; then
    rm -f "$DEST/bpdata-$ts.tar.gz.part"
    echo "backup-bpdata: tar failed (rc=$rc)" >&2
    exit "$rc"
fi

# Целостность: архив должен читаться целиком (сжатый поток не оборван).
if ! tar -tzf "$DEST/bpdata-$ts.tar.gz.part" >/dev/null; then
    rm -f "$DEST/bpdata-$ts.tar.gz.part"
    echo "backup-bpdata: archive failed integrity check" >&2
    exit 1
fi
mv -f "$DEST/bpdata-$ts.tar.gz.part" "$DEST/bpdata-$ts.tar.gz"
chown -R bpxor:bpxor "$DEST"

# Ротация: держим последние KEEP файлов.
ls -1t "$DEST"/bpdata-*.tar.gz 2>/dev/null | tail -n +$((KEEP + 1)) | xargs -r rm -f

echo "backup-bpdata: ok $DEST/bpdata-$ts.tar.gz ($(du -h "$DEST/bpdata-$ts.tar.gz" | cut -f1))"

#!/usr/bin/env bash
# Бэкап базы SQLite и фото салонов из тома rideway_gohome-data в ./backups. Хранится 14 последних дней.
# Копия базы делается командой SQLite .backup — она корректна, даже пока приложение пишет в базу.
# Бэкап лежит на том же сервере: время от времени скачивайте его к себе (кнопка «Download file» в SSH-окне Google Cloud).
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [ "$(id -u)" -eq 0 ]; then SUDO=""; else SUDO="sudo"; fi
mkdir -p "$DIR/backups"
STAMP="$(date +%F)"

$SUDO docker run --rm -v rideway_gohome-data:/data:ro -v "$DIR/backups":/b alpine:3.20 sh -c \
  "apk add --no-cache sqlite >/dev/null && sqlite3 /data/gohome.db \".backup /b/gohome-${STAMP}.db\" \
   && if [ -d /data/uploads ]; then tar -czf /b/uploads-${STAMP}.tar.gz -C /data uploads; fi"

find "$DIR/backups" -type f -mtime +14 -delete
echo "Бэкап: $DIR/backups/gohome-${STAMP}.db"

#!/usr/bin/env bash
# Быстрый туннель Cloudflare выдаёт новый адрес при каждом перезапуске (например, после перезагрузки сервера).
# Скрипт берёт текущий адрес из логов туннеля, и если он изменился — пишет его в .env (PUBLIC_URL)
# и перезапускает приложение: бот при старте ставит кнопку Mini App с новым адресом. Запускается cron'ом каждые 2 минуты.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"
if [ "$(id -u)" -eq 0 ]; then SUDO=""; else SUDO="sudo"; fi
# Постоянный туннель (TUNNEL_TOKEN) — адрес свой и не меняется, следить не за чем.
grep -qE '^TUNNEL_TOKEN=.+' .env 2>/dev/null && exit 0

URL="$($SUDO docker compose --profile tunnel logs tunnel 2>/dev/null | grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' | tail -n1 || true)"
[ -z "$URL" ] && exit 0

CURRENT="$(grep -E '^PUBLIC_URL=' .env 2>/dev/null | cut -d= -f2- || true)"
[ "$URL" = "$CURRENT" ] && exit 0

# Перезаписываем содержимое, а не файл (не sed -i): cron работает от root, и новый файл стал бы принадлежать root,
# после чего install.sh от обычного пользователя не смог бы прочитать .env.
if grep -qE '^PUBLIC_URL=' .env; then
  UPDATED="$(sed "s|^PUBLIC_URL=.*|PUBLIC_URL=${URL}|" .env)"
  printf '%s\n' "$UPDATED" > .env
else
  echo "PUBLIC_URL=${URL}" >> .env
fi
$SUDO docker compose up -d gohome >/dev/null 2>&1
logger -t rideway "PUBLIC_URL -> ${URL}" 2>/dev/null || true
echo "PUBLIC_URL -> ${URL}"

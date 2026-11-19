#!/usr/bin/env bash
# Установка и обновление RideWay на сервере Google Cloud (e2-micro, Debian 12 или Ubuntu).
# Запуск из папки, куда распакован пакет:  bash install.sh
# Повторный запуск безопасен: .env и база сохраняются, загружается новая версия образа.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$DIR"
say() { printf '\n\033[1;32m▶ %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33m! %s\033[0m\n' "$*"; }

if [ "$(id -u)" -eq 0 ]; then SUDO=""; else SUDO="sudo"; fi
# Профиль туннеля: быстрый (адрес *.trycloudflare.com) или постоянный, если в .env задан TUNNEL_TOKEN.
PROFILE="tunnel"
dc() { $SUDO docker compose --profile "$PROFILE" "$@"; }

# ── 1. Подкачка: у e2-micro 1 ГБ памяти, 2 ГБ swap страхуют от нехватки ──
if ! swapon --show 2>/dev/null | grep -q .; then
  say "Создаю файл подкачки 2 ГБ"
  if $SUDO fallocate -l 2G /swapfile && $SUDO chmod 600 /swapfile && $SUDO mkswap /swapfile >/dev/null && $SUDO swapon /swapfile; then
    grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' | $SUDO tee -a /etc/fstab >/dev/null
  else
    warn "Не удалось создать подкачку — продолжаю без неё"
  fi
fi

# ── 2. Docker из официального репозитория Docker ──
if ! command -v docker >/dev/null 2>&1 || ! $SUDO docker compose version >/dev/null 2>&1; then
  say "Устанавливаю Docker"
  # shellcheck disable=SC1091
  . /etc/os-release
  $SUDO apt-get update -qq
  $SUDO apt-get install -y -qq ca-certificates curl >/dev/null
  $SUDO install -m 0755 -d /etc/apt/keyrings
  $SUDO curl -fsSL "https://download.docker.com/linux/${ID}/gpg" -o /etc/apt/keyrings/docker.asc
  $SUDO chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${ID} ${VERSION_CODENAME} stable" \
    | $SUDO tee /etc/apt/sources.list.d/docker.list >/dev/null
  $SUDO apt-get update -qq
  $SUDO apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-compose-plugin >/dev/null
  $SUDO systemctl enable --now docker >/dev/null
  # Логи контейнеров не должны съесть диск.
  if [ ! -f /etc/docker/daemon.json ]; then
    echo '{ "log-driver": "json-file", "log-opts": { "max-size": "10m", "max-file": "3" } }' | $SUDO tee /etc/docker/daemon.json >/dev/null
    $SUDO systemctl restart docker
  fi
fi

# ── 3. Образ приложения ──
if [ -f rideway-image.tar ]; then
  say "Загружаю образ приложения"
  $SUDO docker load -i rideway-image.tar
  rm -f rideway-image.tar            # место на диске; при обновлении придёт новый
elif ! $SUDO docker image inspect rideway-gohome:latest >/dev/null 2>&1; then
  echo "Нет файла rideway-image.tar рядом со скриптом. Соберите пакет на компьютере (deploy/gcp/pack.ps1) и загрузите его." >&2
  exit 1
fi

# ── 4. Настройки ──
NEW_PASSWORD=""
if [ ! -f .env ]; then
  say "Первичная настройка"
  printf 'Токен бота от @BotFather (ввод скрыт; Enter — пропустить, бот будет выключен): '
  read -rs BOT_TOKEN; echo
  NEW_PASSWORD="$(head -c 18 /dev/urandom | base64 | tr '+/' 'xy' | tr -d '=')"
  umask 077
  cat > .env <<EOF
ADMIN_PASSWORD=${NEW_PASSWORD}
BOT_TOKEN=${BOT_TOKEN}
PUBLIC_URL=
TUNNEL_TOKEN=
EOF
  umask 022
  [ -z "$BOT_TOKEN" ] && warn "Токен не задан: допишите BOT_TOKEN в $DIR/.env и выполните: bash $DIR/install.sh"
fi

# ── 5. Запуск ──
if grep -qE '^TUNNEL_TOKEN=.+' .env; then
  PROFILE="tunnel-named"
  $SUDO docker compose --profile tunnel stop tunnel >/dev/null 2>&1 || true
  say "Запускаю приложение и постоянный туннель Cloudflare (TUNNEL_TOKEN)"
else
  say "Запускаю приложение и быстрый туннель Cloudflare"
fi
dc up -d

# ── 6. Адрес быстрого туннеля → PUBLIC_URL (по нему бот ставит кнопку Mini App) ──
if [ "$PROFILE" = "tunnel" ]; then
  say "Жду адрес туннеля"
  URL=""
  for _ in $(seq 1 30); do
    URL="$(dc logs tunnel 2>/dev/null | grep -oE 'https://[a-z0-9-]+\.trycloudflare\.com' | tail -n1 || true)"
    [ -n "$URL" ] && break
    sleep 2
  done
  if [ -n "$URL" ]; then
    bash "$DIR/sync-url.sh"
  else
    warn "Туннель пока не выдал адрес. Посмотрите: cd $DIR && sudo docker compose --profile tunnel logs tunnel"
  fi
fi

# ── 7. Расписание: проверка адреса туннеля и ежедневный бэкап ──
$SUDO mkdir -p /etc/cron.d
$SUDO tee /etc/cron.d/rideway >/dev/null <<EOF
# RideWay: адрес быстрого туннеля меняется при перезапуске — обновляем PUBLIC_URL; бэкап базы в 03:30.
SHELL=/bin/bash
*/2 * * * * root bash $DIR/sync-url.sh >/dev/null 2>&1
@reboot     root sleep 60 && bash $DIR/sync-url.sh >/dev/null 2>&1
30 3 * * *  root bash $DIR/backup.sh >/dev/null 2>&1
EOF
$SUDO chmod 644 /etc/cron.d/rideway

# ── 8. Проверка ──
HEALTH="не отвечает — смотрите логи"
for _ in $(seq 1 20); do
  if curl -fsS -m 5 http://127.0.0.1:8080/health >/dev/null 2>&1; then HEALTH="работает"; break; fi
  sleep 3
done
STATUS="$(dc ps --format '{{.Service}}: {{.Status}}' 2>/dev/null || true)"
URL="$(grep -E '^PUBLIC_URL=' .env | cut -d= -f2-)"

say "Готово"
echo "Приложение: $HEALTH"
echo "$STATUS"
echo
if [ -n "$URL" ]; then
  echo "  Mini App:  $URL/app/   (открывается из Telegram — кнопка в меню бота)"
  echo "  Админка:   $URL/"
else
  echo "  Адрес появится через минуту-две: cat $DIR/.env | grep PUBLIC_URL"
fi
if [ -n "$NEW_PASSWORD" ]; then
  echo "  Пароль админки: $NEW_PASSWORD"
  echo "  (сохраните его; он также лежит в $DIR/.env)"
fi
echo
echo "  Логи:        cd $DIR && sudo docker compose logs -f gohome"
echo "  Бэкапы базы: $DIR/backups (каждую ночь, хранятся 14 дней)"

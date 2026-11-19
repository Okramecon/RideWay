#!/usr/bin/env bash
# Один раз на сервере: включает автообновление из GitHub Actions.
#   bash ~/rideway/ci-setup.sh
# Создаёт отдельный SSH-ключ, разрешает ему только запуск ci-deploy.sh и печатает значения
# для секретов GitHub (Settings → Environments → release-test). Закрытый ключ после этого с сервера удаляется.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KEY="$(mktemp -d)/rideway_ci"
AUTH="$HOME/.ssh/authorized_keys"
TAG="rideway-ci-deploy"

[ -f "$DIR/ci-deploy.sh" ] || { echo "Нет $DIR/ci-deploy.sh — обновите пакет (pack.ps1) и повторите." >&2; exit 1; }
[ -f "$DIR/.env" ] || { echo "Сначала установите приложение: bash $DIR/install.sh" >&2; exit 1; }

ssh-keygen -q -t ed25519 -N '' -C "$TAG" -f "$KEY"

install -m 700 -d "$HOME/.ssh"
touch "$AUTH" && chmod 600 "$AUTH"
# Старый ключ CI (если запускали раньше) заменяем новым.
if grep -q "$TAG" "$AUTH"; then
  grep -v "$TAG" "$AUTH" > "$AUTH.tmp" || true
  cat "$AUTH.tmp" > "$AUTH" && rm -f "$AUTH.tmp"
fi
echo "command=\"bash $DIR/ci-deploy.sh\",no-port-forwarding,no-X11-forwarding,no-agent-forwarding,no-pty $(cat "$KEY.pub")" >> "$AUTH"

meta() { curl -fsS -m 3 -H 'Metadata-Flavor: Google' "http://metadata.google.internal/computeMetadata/v1/$1" 2>/dev/null || true; }
IP="$(meta instance/network-interfaces/0/access-configs/0/external-ip)"
[ -n "$IP" ] || IP="$(curl -fsS -m 5 https://api.ipify.org 2>/dev/null || echo '<внешний IP сервера>')"
HOSTKEY="$(cut -d' ' -f1,2 /etc/ssh/ssh_host_ed25519_key.pub)"
PRIVATE="$(base64 -w0 "$KEY")"
rm -rf "$(dirname "$KEY")"

cat <<EOF

Готово. В GitHub: репозиторий → Settings → Environments → release-test → Environment secrets → Add secret.
Четыре секрета (имя — значение):

DEPLOY_HOST
$IP

DEPLOY_USER
$(id -un)

SSH_KNOWN_HOSTS
$IP $HOSTKEY

SSH_PRIVATE_KEY
$PRIVATE

Закрытый ключ на сервере не сохранён: если потеряете значение — просто запустите ci-setup.sh ещё раз.
EOF

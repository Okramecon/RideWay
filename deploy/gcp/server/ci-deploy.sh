#!/usr/bin/env bash
# Принимает пакет обновления от GitHub Actions и устанавливает его. Не запускается вручную:
# SSH-ключ CI в ~/.ssh/authorized_keys привязан к этому скрипту (command="…", см. ci-setup.sh),
# поэтому с этим ключом можно только передать пакет — ни shell, ни других команд.
#   CI:  ssh user@host deploy < rideway-deploy.tar.gz

# Всё тело — в функции: bash прочитает её целиком до запуска, и распаковка новой версии этого же файла
# посреди работы ничего не сломает.
main() {
  set -euo pipefail
  local DIR
  DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
  cd "$DIR"

  if [ "${SSH_ORIGINAL_COMMAND:-deploy}" != "deploy" ]; then
    echo "Этот ключ только для обновления: ssh … deploy < rideway-deploy.tar.gz" >&2
    exit 2
  fi
  if [ ! -f .env ]; then
    echo "В $DIR нет .env — сначала установите приложение вручную (bash install.sh), затем включайте автообновление." >&2
    exit 1
  fi

  # TMP глобальная: trap EXIT срабатывает уже после выхода из main.
  TMP="$(mktemp -d)"
  trap 'rm -rf "${TMP:-}"' EXIT
  echo "▶ Принимаю пакет"
  cat > "$TMP/bundle.tar.gz"
  # Проверяем архив целиком до распаковки: оборванная передача не должна оставить сервер с половиной файлов.
  if ! tar -tzf "$TMP/bundle.tar.gz" >/dev/null 2>&1; then
    echo "Пакет повреждён или передан не полностью — ничего не меняю." >&2
    exit 1
  fi
  echo "  $(du -m "$TMP/bundle.tar.gz" | cut -f1) МБ, распаковываю"
  tar -xzf "$TMP/bundle.tar.gz" -C "$DIR" --no-same-owner
  rm -f "$TMP/bundle.tar.gz"

  bash "$DIR/install.sh" </dev/null

  # Старый образ после загрузки нового остаётся без тега — убираем, диск маленький.
  sudo docker image prune -f >/dev/null 2>&1 || true

  if curl -fsS -m 5 http://127.0.0.1:8080/health >/dev/null 2>&1; then
    echo "✔ Обновлено до версии $(cat "$DIR/VERSION" 2>/dev/null || echo '?')"
  else
    echo "✘ Приложение не отвечает после обновления. Логи: cd $DIR && sudo docker compose logs --tail 100 gohome" >&2
    exit 1
  fi
}
main "$@"
exit

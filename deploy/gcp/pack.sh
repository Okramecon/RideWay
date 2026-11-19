#!/bin/sh
# Linux/CI-версия pack.ps1: собирает deploy/gcp/out/rideway-deploy.tar.gz
# (готовый образ приложения + docker-compose.yml + скрипты сервера). Нужен только Docker.
# Запуск из любой папки:  sh deploy/gcp/pack.sh
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="$HERE/out"
STAGE="$OUT/stage"
BUNDLE="$OUT/rideway-deploy.tar.gz"
REV="${CI_COMMIT_SHORT_SHA:-local}"

echo "▶ Собираю образ приложения (linux/amd64, версия $REV)"
docker build --platform linux/amd64 \
  --label "org.opencontainers.image.revision=${CI_COMMIT_SHA:-local}" \
  -t rideway-gohome:latest "$ROOT"

rm -rf "$STAGE"
mkdir -p "$STAGE"

echo "▶ Сохраняю образ в файл"
docker save rideway-gohome:latest -o "$STAGE/rideway-image.tar"

# Скрипты и compose — с переводами строк LF (на случай, если репозиторий выкачан с CRLF).
for f in "$ROOT/docker-compose.yml" "$HERE"/server/*.sh; do
  # shellcheck disable=SC2094  # пишем в $STAGE, читаем из репозитория — это разные файлы
  tr -d '\r' < "$f" > "$STAGE/$(basename "$f")"
done
echo "$REV" > "$STAGE/VERSION"

echo "▶ Упаковываю"
rm -f "$BUNDLE"
tar -czf "$BUNDLE" -C "$STAGE" .
rm -rf "$STAGE"
echo "Готово: $BUNDLE ($(du -m "$BUNDLE" | cut -f1) МБ)"

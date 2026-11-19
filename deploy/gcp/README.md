# RideWay на Google Cloud (бесплатный e2-micro)

Приложение не открывает наружу ни одного порта: оно доступно через туннель Cloudflare, бот сам ходит в Telegram.
Домен и сертификат не нужны. Наружу открыт только SSH (порт 22) — для консоли Google Cloud и автообновления из GitHub.

## 1. Создать сервер (один раз)

Google Cloud Console → **Compute Engine → VM instances → Create instance**:

| Поле | Значение | Почему |
|---|---|---|
| Name | `rideway` | |
| Region | `us-central1` (Iowa), `us-west1` или `us-east1` | бесплатный e2-micro только в этих регионах США |
| Machine type | **E2 → e2-micro** | |
| Boot disk | **Debian 12**, тип **Standard persistent disk**, **30 ГБ** | по умолчанию стоит *Balanced* — он **платный**, поменяйте |
| Firewall | галочки HTTP/HTTPS **не ставить** | порты не нужны |

Или одной командой в Cloud Shell:

```bash
gcloud compute instances create rideway --zone=us-central1-a --machine-type=e2-micro \
  --image-family=debian-12 --image-project=debian-cloud --boot-disk-size=30GB --boot-disk-type=pd-standard
```

Сразу заведите бюджет с уведомлением (**Billing → Budgets & alerts**, например на $1), чтобы узнать о любых списаниях.
Отдельно проверьте в первые дни счёт за внешний IPv4-адрес сервера: Google берёт плату за используемые адреса,
и покрывает ли её бесплатный уровень — уточните на странице цен. Если адрес платный, его можно убрать, но тогда серверу нужен
Cloud NAT (тоже платный) — проще оставить и платить копейки или перейти на дешёвый VPS.

## 2. Собрать пакет (на своём компьютере)

Нужен Docker Desktop. В папке проекта:

```bash
powershell -ExecutionPolicy Bypass -File deploy\gcp\pack.ps1
```

Получится `deploy\gcp\out\rideway-deploy.tar.gz` (~100 МБ): готовый образ приложения, `docker-compose.yml` и скрипты.
Сервер ничего не собирает — для 1 ГБ памяти это слишком тяжело.

## 3. Установить на сервер

1. В списке VM нажмите **SSH** у сервера — откроется терминал в браузере.
2. В окне терминала: ⚙ (или «Upload file») → загрузите `rideway-deploy.tar.gz`.
3. Выполните:

```bash
mkdir -p ~/rideway && tar -xzf ~/rideway-deploy.tar.gz -C ~/rideway && bash ~/rideway/install.sh
```

Скрипт:
- добавит 2 ГБ подкачки и поставит Docker;
- спросит токен бота от @BotFather (ввод скрыт) и сгенерирует пароль админки — **запишите его**, он показывается один раз
  (и хранится в `~/rideway/.env`);
- запустит приложение и быстрый туннель, сам пропишет его адрес в `PUBLIC_URL` — бот поставит кнопку Mini App в меню;
- включит по расписанию проверку адреса туннеля (каждые 2 минуты) и бэкап базы (каждую ночь в 03:30, 14 дней).

В конце покажет адрес Mini App и админки. Проверка: откройте бота в Telegram → кнопка меню **RideWay**.

## Обновление

На компьютере снова `pack.ps1`, загрузите новый `rideway-deploy.tar.gz` и выполните ту же команду из шага 3.
База, пароль и токен сохраняются; адрес туннеля не меняется (перезапускается только приложение).

## Автообновление из GitHub (push в `release-test`)

`.github/workflows/deploy.yml`: на каждый push и pull request идут тесты; при push в ветку `release-test` GitHub Actions
собирает образ (`pack.sh`), передаёт пакет на сервер по SSH и запускает там `install.sh`.
База, `.env` и адрес туннеля сохраняются. Ключ CI на сервере умеет только одно — передать пакет (`ci-deploy.sh`):
ни shell, ни других команд им не выполнить.

Один раз:

1. **Обновить сервер вручную** новым пакетом (в нём скрипты для CI): `pack.ps1` → загрузить → команда из шага 3.
2. **На сервере:** `bash ~/rideway/ci-setup.sh` — напечатает четыре значения.
3. **GitHub → репозиторий → Settings → Environments → New environment** `release-test`:
   - **Deployment branches and tags** → *Selected branches and tags* → добавить `release-test` — секреты получит только эта ветка;
   - **Environment secrets → Add secret** — `DEPLOY_HOST`, `DEPLOY_USER`, `SSH_KNOWN_HOSTS`, `SSH_PRIVATE_KEY` со значениями из шага 2.

   Закрытый ключ на сервере не остаётся: потеряли — запустите `ci-setup.sh` ещё раз и обновите секреты.
4. **Google Cloud → VPC network → IP addresses** — у внешнего адреса сервера нажать **Promote to static**.
   Обычный адрес меняется, если сервер остановить и запустить (не при перезагрузке), — тогда CI перестанет его находить.
   Статический адрес на работающем сервере стоит столько же, сколько обычный.

Проверка: push в `release-test` → вкладка **Actions** → запуск *Test & deploy*; в шаге «Обновить сервер» в конце
`✔ Обновлено до версии <коммит>`. Деплой идёт только если тесты прошли.

GitHub Actions бесплатны для публичных репозиториев, для приватных — 2000 минут в месяц (деплой ≈ 5 минут).
Сервер должен быть доступен по SSH (порт 22) из интернета: в Google Cloud он открыт правилом `default-allow-ssh` сети `default`.

Если деплой упал: `Permission denied (publickey)` — неверный `SSH_PRIVATE_KEY`/`DEPLOY_USER` или ключ стёрт с сервера
(запустите `ci-setup.sh` заново); `Host key verification failed` — сменился адрес, обновите `DEPLOY_HOST` и `SSH_KNOWN_HOSTS`;
`Приложение не отвечает` — смотрите `sudo docker compose logs --tail 100 gohome` на сервере.

## Полезное

```bash
cd ~/rideway
sudo docker compose logs -f gohome          # логи приложения
sudo docker compose --profile tunnel ps     # что запущено
grep PUBLIC_URL .env                        # текущий адрес
bash backup.sh                              # бэкап прямо сейчас (папка ~/rideway/backups)
nano .env && bash install.sh                # поменять токен бота или пароль
```

Скачать бэкап к себе: в SSH-окне ⚙ → **Download file** → `/home/<ваше имя>/rideway/backups/gohome-ГГГГ-ММ-ДД.db`.
Бэкап лежит на том же сервере — если сервер удалить, пропадёт и он.

## Ограничения быстрого туннеля

- **Адрес меняется** при перезапуске туннеля (например, после перезагрузки сервера). Скрипт `sync-url.sh` в течение 2 минут
  замечает новый адрес и перезапускает приложение, бот обновляет кнопку меню. QR-наклейки в машинах ведут на бота, а не на
  адрес туннеля, поэтому их менять не нужно. Но закладки на админку придётся обновлять.
- Cloudflare не даёт гарантий для быстрых туннелей — для пилота хватит. Когда появится свой домен, переключитесь на
  постоянный туннель: в Cloudflare Zero Trust → **Networks → Tunnels** создайте туннель с Public Hostname → `http://gohome:8080`,
  впишите токен в `.env` (`TUNNEL_TOKEN`), свой адрес в `PUBLIC_URL` (`https://…`) и запустите `bash ~/rideway/install.sh`.
  Скрипт увидит токен, остановит быстрый туннель и запустит постоянный; проверка адреса отключится сама, бэкапы останутся.

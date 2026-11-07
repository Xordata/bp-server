# DEPLOY — установка bp.xor.world (bpxor) на любой сервер

Пошаговая инструкция развёртки «с нуля» на чистом Linux-сервере. Проверено на Ubuntu 22.04/Debian 12;
для других дистрибутивов отличаются только имена пакетов и пути конфигов nginx.

Что разворачивается:

```
Интернет ─ 443 ─ nginx (TLS certbot, http2, brotli) ─ 127.0.0.1:8090 ─ bpxor (ASP.NET Core 8, self-contained)
                     │                                                   ├─ данные: /opt/bpdata/users/<uid>/
                     └─ 80 → 301 на https                                ├─ бинарь+статика: /usr/local/bin/bpxor/
                                                                         ├─ конфиг: /usr/local/bin/bpxor/bpxor.config.json (0600)
Логин пользователей ─ IMAP LOGIN на ваш почтовый сервер (maddy и т.п.)  └─ systemd unit `bpxor.service`
Почта будильников  ─ SMTP submission (STARTTLS, бот-аккаунт)
```

**Про аутентификацию (важно понять до начала):** отдельного пароля «дневника» нет — вход проверяется
IMAP-логином существующей почты (`AuthHost:AuthPort`). Первый вход создаёт аккаунт дневника.
Идеально — maddy на этом же сервере (тогда почтовый домен ваш). Можно использовать и любой внешний
IMAP-сервер с паролем-паролем (укажите его в `AuthHost/AuthPort`) — но тогда вы зависите от него.
Если почты нет вообще — bpxor запустить нельзя (см. TODO: альтернативные авторизаторы).

---

## 0. Требования

- 1–2 vCPU, 1–2 GB RAM (+swap обязателен при 2 GB), 10 GB диск.
- Открытые порты 22, 80, 443 (и 25/465/587/993, если поднимаете свою почту).
- Домен (или поддомен), например `bp.example.com`.
- На dev-машине (Windows): .NET 8 SDK, `git`, `tar`, `scp`.

## 1. DNS

A-запись `bp.example.com → IP сервера` (и AAAA, если есть IPv6). Проверить:
```bash
dig +short bp.example.com @1.1.1.1   # должен вернуть IP сервера
```

## 2. Подготовка сервера (root)

```bash
apt update && apt -y upgrade
# swap, если RAM < 4 GB:
fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
timedatectl set-timezone Europe/Moscow   # по желанию
```

## 3. nginx + certbot (+ brotli опционально)

```bash
apt -y install nginx certbot python3-certbot-nginx
# brotli (опционально, ускорение статики):
apt -y install libnginx-mod-brotli || echo "модуля нет — пропустите brotli-строки в vhost"
```

## 4. Пользователь и каталоги

```bash
useradd -r -s /usr/sbin/nologin bpxor
mkdir -p /opt/bpdata /usr/local/bin/bpxor
chown bpxor:bpxor /opt/bpdata
```

## 5. Почтовый бэкенд

### Вариант A — maddy на этом же сервере (боевой вариант bp.xor.world)
Установите maddy по его инструкции (maddy.email), включите IMAP на 993 (STARTTLS/TLS) и submission 587.
Создайте пользовательские почтовые ящики для будущих владельцев дневника и бота:
```bash
maddy creds create bpxor@example.com        # бот для писем-будильников
maddy imap-acct create bpxor@example.com
```
bpxor будет проверять вход `imap://127.0.0.1:993` (это значения по умолчанию).

### Вариант B — внешний IMAP
Ничего не ставим; в `bpxor.config.json` укажете `AuthHost`/`AuthPort` вашего IMAP.
Поддерживается **только implicit TLS** (порт 993): код сразу поднимает TLS-соединение (сертификат может быть
самоподписанным — проверка сертификата отключена), STARTTLS на 143 не используется.

## 6. Сборка и загрузка приложения (с dev-машины)

```powershell
:: Windows, из папки репозитория bp.xor.world
dotnet publish src\bpxor -c Release -r linux-x64 --self-contained -o U:\AISess\bp.xor.world\publish\linux-x64
cd \u\AISess\bp.xor.world\publish\linux-x64   # git-bash
tar czf /z/Temp/bpxor-full.tar.gz bpxor index.html login.html help.html q.html sw.js manifest.webmanifest icons alarm.mp3
scp /z/Temp/bpxor-full.tar.gz root@SERVER:/root/
```
(в репозитории-экспорте бинарь не лежит — его нужно собрать; `publish/` в `.gitignore` намеренно)

На сервере:
```bash
tar xzf /root/bpxor-full.tar.gz -C /usr/local/bin/bpxor
chmod +x /usr/local/bin/bpxor/bpxor
chown -R bpxor:bpxor /usr/local/bin/bpxor
```

## 7. Конфигурация `/usr/local/bin/bpxor/bpxor.config.json`

Права 0600, владелец bpxor. Все поля опциональны (значения по умолчанию в скобках):

| Поле | Значение по умолчанию | Смысл |
|---|---|---|
| `AuthHost` | `127.0.0.1` | IMAP-сервер для проверки паролей |
| `AuthPort` | `993` | порт IMAP (993 = TLS) |
| `Domain` | `xor.world` | **поменяйте на свой почтовый домен** — проверка «логин = user@Domain» |
| `MaxUsers` | `50` | лимит аккаунтов (счёт каталогов users/) |
| `FailDelaySec` | `5` | база бэкоффа rate-limit'а неудачных входов |
| `SessionDays` | `30` | срок жизни сессии |
| `Port` | `8090` | порт Kestrel (слушает только 127.0.0.1) |
| `SecureCookies` | `true` | `false` — только локальный http-дев, на проде не трогайте |
| `MaxConcurrentJobs` | `1` | параллельность фоновых задач (обучение/бэкфилл) |
| `SmtpHost`/`SmtpPort` | `127.0.0.1`/`587` | submission для писем-будильников |
| `SmtpUser`/`SmtpPass` | пусто (= фолбэк выключен) | бот-аккаунт (maddy creds) |
| `SmtpFrom` | `bpxor@example.org` | отправитель |
| `WxSource` | `openmeteo` | **рекомендуем `metno`** — open-meteo режет часть хостинговых ASN |

```bash
cat > /usr/local/bin/bpxor/bpxor.config.json <<'EOF'
{
  "Domain": "example.com",
  "WxSource": "metno",
  "SmtpUser": "bpxor@example.com",
  "SmtpPass": "ПАРОЛЬ_БОТА",
  "SmtpFrom": "bpxor@example.com"
}
EOF
chmod 600 /usr/local/bin/bpxor/bpxor.config.json
chown bpxor:bpxor /usr/local/bin/bpxor/bpxor.config.json
```

## 8. systemd

Файл `deploy/bpxor.service` из репозитория:
```bash
cp bpxor.service /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now bpxor
systemctl status bpxor --no-pager        # active (running)
journalctl -u bpxor -n 20 --no-pager     # без ошибок; VAPID-ключи создадутся сами в /opt/bpdata/push/
```

## 9. nginx vhost + TLS

Файл `deploy/bp.xor.world` из репозитория — положить в `/etc/nginx/sites-available/bp.example.com`,
заменить имя домена, включить:
```bash
ln -s /etc/nginx/sites-available/bp.example.com /etc/nginx/sites-enabled/
nginx -t && systemctl reload nginx
certbot --nginx -d bp.example.com --redirect
```
Что должно быть в vhost (в файле репо уже есть): `listen 443 ssl http2`; `proxy_pass http://127.0.0.1:8090`;
для SSE-эндпоинта `/api/events` — `proxy_buffering off; proxy_read_timeout 3600s;` (иначе уведомления
и дельты «зависают» в буфере); access-лог `/var/log/nginx/bp.access.log` (для fail2ban).

## 10. fail2ban (защита входа)

По `deploy/README-deploy.md` §4: фильтр ловит **только** `POST /api/login → 401` (не любые 401):
```bash
apt -y install fail2ban
# filter.d/bpxor-login.conf + jail.d/bpxor.conf — содержимое в deploy/README-deploy.md
fail2ban-client reload && fail2ban-client status bp-login
```

## 11. Резервные копии

```bash
install -m 755 backup-bpdata.sh /usr/local/bin/
( crontab -l 2>/dev/null; echo '30 3 * * * /usr/local/bin/backup-bpdata.sh' ) | crontab -
# проверка: прогон вручную + отсутствие рекурсии + ровно одна строка в cron
/usr/local/bin/backup-bpdata.sh && ls -lh /opt/bpdata/backup/
tar -tzf /opt/bpdata/backup/bpdata-*.tar.gz | grep -c backup/   # должно быть 0
crontab -l | grep -c backup-bpdata                               # должно быть 1
```

Скрипт раз в сутки (03:30, после `backup_maddy.sh` в 03:00) пакует **всё содержимое `/opt/bpdata`
(все пользователи)** одним архивом `/opt/bpdata/backup/bpdata-<ts>.tar.gz`, ротация — последние 14.
Особенности реализации:
- каталог `/opt/bpdata/backup` исключён из tar (`--exclude=bpdata/backup`) — иначе архив читал бы сам себя;
- запись через `.part` с проверкой целостности (`tar -tzf`) перед переименованием — rsync/borg никогда
  не заберут оборванный архив;
- «file changed as we read it» (append-only `data.bin` дописывается во время упаковки) не считается
  ошибкой — fatal только rc>=2 у tar;
- владелец файлов — `bpxor:bpxor`.

Внешняя синхронизация: rsync/borg забирают `/opt/bpdata/backup/` на внешнее хранилище.
Пер-profile суточные zip внутри `users/<uid>/backups/` остаются как локальная страховка.

## 11a. Языковые пакеты (i18n)

Интерфейс веба многоязычен (ru базовый + en, de, fr, pt, es, zh, ja). Ключ перевода — сам русский
текст (gettext-стиль), пакеты — плоские JSON `{"<русский текст>": "<перевод>"}`:

```bash
mkdir -p /opt/bpdata/lang && chown bpxor:bpxor /opt/bpdata/lang
# положить en.json de.json fr.json pt.json es.json zh.json ja.json (в репо: deploy/lang/)
chown bpxor:bpxor /opt/bpdata/lang/*.json
curl -s -o /dev/null -w "%{http_code}\n" https://bp.example.com/lang/en.json   # 200 (публичный роут)
```

Пакеты правятся **без пересборки и рестарта** (кэш по mtime файла). Язык профиля — поле `Lang`
в config.json профиля; не выбран — SPA показывает окно выбора после логина; смена — 🌐 в шапке
или «Настройки → Пользователь → Язык». Серверные тексты (push/email будильников, письмо экспорта)
переводятся тем же пакетом по языку профиля. Десктопный BPMonitor мультиязычен (пакеты `<каталог exe>/lang/*.json`, язык в Config.Lang).

## 12. Проверка установки (снаружи сервера)

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://bp.example.com/login          # 200
curl -s -o /dev/null -w "%{http_code}\n" https://bp.example.com/api/state      # 401 (гейт)
curl -s -o /dev/null -w "%{http_code}\n" https://bp.example.com/sw.js          # 200
curl -s -o /dev/null -w "%{http_code}\n" https://bp.example.com/help           # 401 (за логином)
curl -sI https://bp.example.com/login | grep -i "^HTTP"                        # HTTP/2
```
Живой тест: открыть /login, войти существующим почтовым ящиком домена → должен создаться профиль
(в `/opt/bpdata/users/` появится каталог). Дальше — «Настройки → Устройства → Создать токен» и
подключить десктопный BPMonitor (раздел 13).

## 13. Подключение десктопного приложения

1. Скачайте установщик: со страницы «Устройства» на сайте («⬇ Десктоп-версия», файл `BpMonitorSetup.exe`
   лежит рядом с бинарем bpxor и раздаётся GET `/download`) — или соберите сами (BPMonitor/deploy/BpMonitor.iss).
2. В приложении: Настройки → Синхронизация → включить, вставить токен из «Устройства».
3. Проверка: статус синхрона «push +N / pull +N», ошибок нет.

## 14. Перенос на другой сервер (существующей установки)

```bash
# со старого сервера:
tar czf bpdata.tgz -C /opt bpdata
# на новый: распаковать в /opt, поставить bpxor (шаги 4–9), тот же bpxor.config.json, systemctl start bpxor
```
Данные — только `/opt/bpdata` (пользователи, токены, VAPID, подписки push). Сессии переживут перенос,
подписки Web Push — тоже (endpoint не привязан к серверу).

## 15. Типичные проблемы

| Симптом | Причина / решение |
|---|---|
| 502 сразу после `systemctl restart` | гонка: Kestrel ещё не слушает — `sleep 2` и повторить; если не прошло — `journalctl -u bpxor -n 40`, `ss -tlnp \| grep 8090` |
| Пуш-уведомления не доходят | «Тест доставки» в настройках; `journalctl -u bpxor \| grep 'push fail'`; TTL 3600 — телефон в doze получит при пробуждении |
| Будильник звонит дважды | легально: десктоп-приложение + веб-вкладка звонят независимо (между вкладками дедуп через SSE) |
| Погода пустая | `WxSource`: на хостингах open-meteo часто недоступен → `metno`; UA для met.no уже корректный в коде |
| Логин не проходит | IMAP-бэкенд: `openssl s_client -connect 127.0.0.1:993` и ручной `A1 LOGIN user pass`; `Domain` в конфиге должен совпадать с доменом почты |
| SSE (живые обновления) не работает | в vhost на `/api/events` нет `proxy_buffering off` |
| Рестарт systemd 30 с | в коде стоит `HostOptions.ShutdownTimeout=3` — SSE-запросы больше не держат graceful shutdown |

## 16. Обновление (редиплой)

См. `deploy/README-deploy.md` §0.5: tar только изменённых файлов → scp → `tar xzf -C /usr/local/bin/bpxor`
→ `chown -R bpxor:bpxor` → `systemctl restart bpxor` → `sleep 2` → `curl /login`.

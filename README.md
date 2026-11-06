# bp.xor.world — веб-порт дневника давления (bpxor)

Многопользовательский веб-интерфейс дневника АД (десктоп-оригинал: `U:\AISess\BPMonitor`).
Прод: **https://bp.xor.world** — ASP.NET Core 8 (self-contained), один процесс `bpxor` за nginx,
данные на сервере в `/opt/bpdata`. Логин — проверкой пароля почты через локальный maddy (IMAP),
аккаунт создаётся автоматически при первом входе.

Документация: [PLAN.md](PLAN.md) — план/история по фазам · [TODO.md](TODO.md) — backlog ·
[DEPLOY.md](DEPLOY.md) — **полная установка на любой чистый сервер** ·
[deploy/README-deploy.md](deploy/README-deploy.md) — установка/редиплой конкретного прода bp.xor.world ·
инструкция для пользователя — `help.html` (общая с десктопом, на проде `/help`).

## Архитектура

```
nginx (443 http2, brotli, TLS certbot; 80→301)
  └─ proxy → bpxor (Kestrel, 127.0.0.1:8090, systemd unit bpxor.service, user bpxor)
       ├─ AuthGate (middleware): публичны только GET /login, POST /api/login, /logout, /favicon.ico, /icons/*.png, /sw.js;
       │    сессия bpxsid (HttpOnly), CSRF bpxcsrf double-submit (заголовок X-CSRF), rate-limit ip|login с бэкоффом
       ├─ UserStore: ленивый LRU-кэш ~20 профилей /opt/bpdata/users/<uid>/, лимит MaxUsers под lock
       ├─ Profile (на пользователя): config.json + data.bin (append-only журнал, дельта-курсор (gen,seq)) + nn.bin + medpk.json
       │    — та же модель данных и та же НН, что в десктопном BpMonitor (порт)
       ├─ ApiTokens (/api/sync/push|pull, Bearer): дельта-синхрон с десктопами (LWW по seq, тумстоуны, echo-подавление)
       ├─ Scheduler: будильники — тик 30 с по всем каталогам users/, tz юзера, догон 30 мин;
       │    дедуп звонка: SSE-событие «alarm» открытым вкладкам (звонит вкладка), push/email — только если вкладок нет
       ├─ EventHub (SSE /api/events): дельты состояния/будильников/dismiss на вкладки (heartbeat 25 с, nginx proxy_buffering off)
       ├─ Push (Web Push VAPID, TTL 3600, tag+renotify) + SmtpSender (STARTTLS к maddy, бот bpxor@example.org) — фолбэк будильников
       ├─ WxCache: общий кэш погоды (met.no live/hourly по округлённым координатам, GFZ Kp, LRU)
       └─ JobGate: FIFO-очередь фоновых задач (train/tune/validate/backfill), max 1 одновременно (2 vCPU)
```

Ключевые модули (`src/bpxor/`): `Program.cs` (AuthGate+запуск), `HttpApi.cs` (эндпоинты),
`Storage.cs` (Profile/UserStore/data.bin), `Sync.cs` (токены+дельта), `Scheduler.cs` (будильники),
`Push.cs` (VAPID/webpush/SMTP), `Weather.cs` (met.no/open-meteo/Kp), `Neural.cs` (НН), `State.cs`, `Export.cs`, `SelfTest.cs`.

Особенности протокола: дельта-курсор (gen, seq) — seq байтовое смещение в data.bin, компакция поднимает gen
и клиенты получают `{reset:true}`; ETag/304 на state; rev'ы cfg/nn/medpk = mtime, wxRev = Ticks — сравнение «!=».

## Разработка (dev-машина Windows)

```
cd src\bpxor
dotnet build -c Release
dotnet run -c Release -- --selftest        # 35 проверок в песочнице (реальные данные не трогает)
```
- Локальный запуск: порт из `bpxor.config.json` РЯДОМ С EXE (не из data-дир); для http-дева `SecureCookies=false`.
- Смоуки (в scratch): smoke.py (e2e будильники/push/smtp через fake_imap/fake_smtp/fake_push), smoke7 (фаза 5),
  smoke8 (токены/синхрон), smoke10 (дедуп звонка SSE). Наружные проверки к проду — через curl (python urllib TLS висит).
- Критические питфалы сборки/деплоя — в skill `bpmonitor-project` и в конце PLAN.md
  (publish только с `-o U:/...`, убивать висящий dotnet/bpxor перед сборкой, CRLF+BOM в .cs).

## Публикация и редиплой

```
dotnet publish src\bpxor -c Release -r linux-x64 --self-contained -o U:\AISess\bp.xor.world\publish\linux-x64
# проверить размер bpxor.dll и маркеры в index.html; BpMonitorSetup.exe — копировать сюда же при релизе десктопа
```
Штучный редиплой (не 100 МБ, а изменённые файлы) и начальная установка — в [deploy/README-deploy.md](deploy/README-deploy.md).

## Конфигурация сервера (`/usr/local/bin/bpxor/bpxor.config.json`, 0600)

| Поле | Смысл |
|---|---|
| `Port` | порт Kestrel (127.0.0.1) |
| `MaxUsers` | лимит аккаунтов (счёт каталогов users/) |
| `WxSource` | `metno` (на проде; open-meteo недоступен с этого VPS) |
| `SmtpUser`/`SmtpPass` | бот bpxor@example.org для писем-будильников |
| `SecureCookies` | false только для локального http-дева |

VAPID-ключи — `/opt/bpdata/push/vapid.json` (создаются сами), подписки — `push/subs.json`,
токены синхронизации — `tokens.json` (только SHA256-хэши).


## Переводы

Интерфейс переведён на 8 языков (ru/en/de/fr/pt/es/zh/ja): пакеты `deploy/lang/*.json`, страницы справки `lang/help-*.html`, тексты EULA. Перевод выполнен ИИ-ассистентом Hermes Agent (модель `halogen-qwen3.8-flash-next`), вычитка и приёмка — владельцем проекта. Пользовательские данные (названия лекарств, типы шумов, заметки) не переводятся принципиально.

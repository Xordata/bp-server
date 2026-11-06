# Порядок развёртывания (root на <VPS-HOST>)

## 0. Статус
- [x] swap 2G + fstab
- [x] brotli-модули nginx, HTTP/2 (`listen 443 ssl http2`)
- [x] DNS bp.xor.world → <VPS-IP>
- [x] бот-аккаунт bpxor@example.org в maddy (пароль в U:\AISess\secrets\bpxor.config.json — вне репо)
- [x] всё ниже выполнено, прод живой с 2026-11-05 (systemd `bpxor.service`, данные /opt/bpdata)

## 0.5. Штучный редиплой (обычный путь обновления)

С dev-машины (Windows, git-bash). SSH на нестандартном порту <SSH-PORT>; наружные проверки — только curl
(python urllib TLS-handshake к серверу висит). Пакет = tar **только изменённых файлов** из `publish/linux-x64`
(весь publish — 100 МБ, не гонять):

```bash
cd /u/AISess/bp.xor.world/publish/linux-x64
tar czf /z/Temp/bpxor-update.tar.gz bpxor.dll index.html login.html help.html q.html sw.js manifest.webmanifest icons BpMonitorSetup.exe   # состав по факту изменённого
scp -P <SSH-PORT> /z/Temp/bpxor-update.tar.gz root@<VPS-IP>:/root/
```

На сервере:
```bash
tar xzf /root/bpxor-update.tar.gz -C /usr/local/bin/bpxor
chown -R bpxor:bpxor /usr/local/bin/bpxor
systemctl restart bpxor        # 3–4 с (HostOptions.ShutdownTimeout=3)
sleep 2                        # гонка рестарта: 502 сразу после restart — норма
curl -s -o /dev/null -w "%{http_code}\n" https://bp.xor.world/login   # ожидаем 200
# если 502 не прошёл сам: journalctl -u bpxor -n 40 --no-pager; ss -tlnp | grep 8090
```
Редкие, но нужные вещи: `BpMonitorSetup.exe` — раздать при каждом релизе десктопа (отдаётся GET /download, рестарт не нужен);
`bpxor.config.json` меняется отдельно (chmod 600, рестарт обязателен).

## 1. Пользователь и каталог данных
```bash
useradd -r -s /usr/sbin/nologin bpxor
mkdir -p /opt/bpdata /usr/local/bin/bpxor
chown bpxor:bpxor /opt/bpdata
```

## 2. nginx vhost (файл bp.xor.world)
```bash
# положить bp.xor.world в /etc/nginx/sites-available/
ln -s /etc/nginx/sites-available/bp.xor.world /etc/nginx/sites-enabled/
nginx -t && systemctl reload nginx
```

## 3. Сертификат (DNS уже живой)
```bash
certbot --nginx -d bp.xor.world --redirect
```

## 4. fail2ban (бывший fail2ban-bpxor.conf — два файла, содержимое ниже целиком)
```bash
# лог-файл должен существовать до старта jail, иначе fail2ban его не подхватит
touch /var/log/nginx/bp.access.log && chown www-data:adm /var/log/nginx/bp.access.log

cat > /etc/fail2ban/filter.d/bpxor-login.conf <<'EOF'
# Ловим ТОЛЬКО неверные логины (POST /api/login -> 401); 401 от /api/* при
# истёкшей сессии легального пользователя банить НЕ должны.
# fail2ban 1.x вырезает дату из строки до матчинга — внутри [] звёздочка.
[Definition]
failregex = ^<HOST> - \S+ \[[^\]]*\] "POST /api/login[^"]*" 401
ignoreregex =
EOF

cat > /etc/fail2ban/jail.d/bpxor.conf <<'EOF'
[bp-login]
enabled  = true
filter   = bpxor-login
logpath  = /var/log/nginx/bp.access.log
maxretry = 5
findtime = 60
bantime  = 3600
banaction = iptables-multiport
action = %(banaction)s[name=%(__name__)s, bantime="%(bantime)s", port="80,443", protocol="tcp", chain="%(chain)s"]
EOF

fail2ban-client reload
fail2ban-client status bp-login    # должен показать Filter/Log file и 0 bans
```

## 5. Бэкап (файл backup-bpdata.sh)
```bash
install -m 755 backup-bpdata.sh /usr/local/bin/
( crontab -l 2>/dev/null; echo '30 3 * * * /usr/local/bin/backup-bpdata.sh' ) | crontab -
# проверка: /usr/local/bin/backup-bpdata.sh && tar -tzf /opt/bpdata/backup/bpdata-*.tar.gz | grep -c backup/  # 0
```
Архив всех пользователей → `/opt/bpdata/backup/bpdata-<ts>.tar.gz`, ротация 14, каталог backup
исключён из tar (рекурсия), `.part` + проверка целостности перед переименованием. Подробности — DEPLOY.md §11.

## 6. Бот-аккаунт для писем-будильников — СДЕЛАНО 2026-11-05
```bash
# bpxor@example.org уже создан; пароль лежит в U:\AISess\secrets\bpxor.config.json (dev-машина).
# Проверить AUTH с самого сервера (снаружи порты maddy не доступны):
#   openssl s_client -connect 127.0.0.1:587 -starttls smtp  → AUTH LOGIN (base64 логина/пароля)
```

## 7. Приложение (бинарь готов: publish/linux-x64, Фаза 4)
```bash
# bpxor (self-contained linux-x64) + index.html + login.html + sw.js + alarm.mp3 → /usr/local/bin/bpxor/, chmod +x, chown -R bpxor:bpxor
# bpxor.config.json (из U:\AISess\secrets\) → /usr/local/bin/bpxor/bpxor.config.json, chmod 600, chown bpxor:bpxor
# bpxor.service → /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now bpxor
systemctl status bpxor --no-pager
```

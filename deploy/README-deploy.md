# Порядок развёртывания (root на <VPS-HOST>)

## 0. Статус на 2026-11-05
- [x] swap 2G + fstab
- [x] brotli-модули nginx
- [x] DNS bp.xor.world → <VPS-IP> (проверено через 1.1.1.1/8.8.8.8)
- [x] бот-аккаунт bpxor@example.org создан в maddy (пароль в U:\AISess\secrets\bpxor.config.json — вне репо)
- [ ] шаги ниже

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
( crontab -l; echo '30 3 * * * /usr/local/bin/backup-bpdata.sh' ) | crontab -
```

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

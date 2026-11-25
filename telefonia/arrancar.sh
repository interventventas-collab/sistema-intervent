#!/bin/sh
# Copia la configuracion y pone las claves de los internos (vienen del .env)
set -e
: "${TEL_101_PASS:?falta TEL_101_PASS en .env}"
: "${TEL_102_PASS:?falta TEL_102_PASS en .env}"
mkdir -p /etc/asterisk
for f in /etc/asterisk-plantilla/*.conf; do
  sed -e "s|__TEL_101_PASS__|${TEL_101_PASS}|g" \
      -e "s|__TEL_102_PASS__|${TEL_102_PASS}|g" \
      -e "s|__IP_PUBLICA__|${IP_PUBLICA}|g" \
      "$f" > "/etc/asterisk/$(basename "$f")"
done
mkdir -p /var/log/asterisk /var/spool/asterisk /var/lib/asterisk /var/run/asterisk
exec asterisk -f -U asterisk -G asterisk -vvv

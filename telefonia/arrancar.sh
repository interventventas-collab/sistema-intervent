#!/bin/sh
# Copia la configuracion y pone las claves de los internos (vienen del .env)
set -e
: "${TEL_101_PASS:?falta TEL_101_PASS en .env}"
: "${TEL_102_PASS:?falta TEL_102_PASS en .env}"
: "${TEL_SECRET:?falta TEL_SECRET en .env}"
# claves de los internos del navegador: sha256("TEL_SECRET:interno"), 24 letras (igual en la API)
clave() { printf "%s:%s" "$TEL_SECRET" "$1" | sha256sum | cut -c1-24; }
mkdir -p /etc/asterisk
for f in /etc/asterisk-plantilla/*.conf; do
  sed -e "s|__TEL_101_PASS__|${TEL_101_PASS}|g" \
      -e "s|__TEL_102_PASS__|${TEL_102_PASS}|g" \
      -e "s|__TEL_103_PASS__|${TEL_103_PASS}|g" \
      -e "s|__PASS_201__|$(clave 201)|g" -e "s|__PASS_202__|$(clave 202)|g" -e "s|__PASS_203__|$(clave 203)|g" \
      -e "s|__PASS_210__|$(clave 210)|g" -e "s|__PASS_ARI__|$(clave ari)|g" \
      -e "s|__IP_PUBLICA__|${IP_PUBLICA}|g" \
      "$f" > "/etc/asterisk/$(basename "$f")"
done
mkdir -p /var/lib/asterisk-db && chown asterisk:asterisk /var/lib/asterisk-db
mkdir -p /var/log/asterisk /var/spool/asterisk /var/lib/asterisk /var/run/asterisk
exec asterisk -f -U asterisk -G asterisk -vvv

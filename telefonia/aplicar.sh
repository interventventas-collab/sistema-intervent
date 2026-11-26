#!/bin/sh
# Arma /etc/asterisk desde las plantillas poniendo las claves (vienen del .env).
# Lo usa arrancar.sh al prender, y sirve para aplicar cambios SIN reiniciar la central
# (reiniciar desconecta un rato al telefono de la oficina):
#   docker cp telefonia/conf/X.conf aiml-central-telefonica:/etc/asterisk-plantilla/
#   docker exec aiml-central-telefonica /aplicar.sh
#   docker exec aiml-central-telefonica asterisk -rx "core reload"
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
      -e "s|__PASS_220__|$(clave 220)|g" -e "s|__PASS_CENTRAL__|$(clave central)|g" \
      -e "s|__DOMINIO__|${TEL_DOMINIO:-localhost}|g" \
      -e "s|__IP_PUBLICA__|${IP_PUBLICA}|g" \
      "$f" > "/etc/asterisk/$(basename "$f")"
done

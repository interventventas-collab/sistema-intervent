#!/bin/sh
# Arma el tunel WireGuard y arranca go2rtc.
# - Las claves y la config de cada local se generan UNA vez y quedan en /data (volume).
# - Cada local tiene su IP fija en el tunel (lista LOCALES: nombre:numero).
# - Al local solo se le manda el trafico del tunel (10.13.13.0/24), NO todo su internet.
set -e
LOCALES="${LOCALES:-tostadero:2}"
IP_PUBLICA="${IP_PUBLICA:-179.43.123.134}"
PUERTO="${WG_PUERTO:-51820}"
mkdir -p /data/locales
umask 077

[ -f /data/server.key ] || wg genkey > /data/server.key
wg pubkey < /data/server.key > /data/server.pub

CONF=/etc/wireguard/wg0.conf
mkdir -p /etc/wireguard
cat > $CONF <<C
[Interface]
Address = 10.13.13.1/24
ListenPort = $PUERTO
PrivateKey = $(cat /data/server.key)
C

for L in $LOCALES; do
  NOMBRE=${L%%:*}; N=${L##*:}
  [ -f /data/locales/$NOMBRE.key ] || wg genkey > /data/locales/$NOMBRE.key
  wg pubkey < /data/locales/$NOMBRE.key > /data/locales/$NOMBRE.pub
  cat >> $CONF <<C

[Peer]
# $NOMBRE
PublicKey = $(cat /data/locales/$NOMBRE.pub)
AllowedIPs = 10.13.13.$N/32
C
  # Config para pegar en WireGuard de la PC del local.
  cat > /data/locales/$NOMBRE.conf <<C
[Interface]
PrivateKey = $(cat /data/locales/$NOMBRE.key)
Address = 10.13.13.$N/32

[Peer]
PublicKey = $(cat /data/server.pub)
Endpoint = $IP_PUBLICA:$PUERTO
AllowedIPs = 10.13.13.1/32
PersistentKeepalive = 25
C
done

wg-quick down wg0 2>/dev/null || true
wg-quick up wg0
echo "[camaras] tunel arriba; locales: $LOCALES"
exec go2rtc -config /conf/go2rtc.yaml

#!/bin/sh
# Arma la configuracion (aplicar.sh) y prende la central.
set -e
/aplicar.sh
mkdir -p /var/lib/asterisk-db && chown asterisk:asterisk /var/lib/asterisk-db
mkdir -p /var/log/asterisk /var/spool/asterisk /var/lib/asterisk /var/run/asterisk
exec asterisk -f -U asterisk -G asterisk -vvv

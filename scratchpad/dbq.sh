#!/usr/bin/env bash
# Query helper para la DB de PRODUCCION (aiml-sqlserver-prod, base AIml)
set -euo pipefail
PW=$(grep -E "^SQL_SA_PASSWORD=" /home/dev/ai-ml/.env | cut -d= -f2-)
sudo docker exec -i aiml-sqlserver-prod /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$PW" -C -d AIml -W -s "|" -Q "$1"

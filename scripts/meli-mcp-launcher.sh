#!/usr/bin/env bash
# ============================================================
# Launcher del MCP server de MercadoLibre.
#
# Este script se invoca desde .mcp.json cada vez que Claude Code
# (u otro cliente MCP) abre una sesion en este proyecto.
#
# Que hace:
#   1. Lee SQL_SA_PASSWORD desde el .env del proyecto.
#   2. Consulta SQL Server (dev) por el AccessToken vigente de la
#      cuenta de MercadoLibre conectada mas recientemente en el
#      dashboard (Integraciones).
#   3. Lanza "mcp-remote" como bridge stdio<->HTTP contra el server
#      remoto https://mcp.mercadolibre.com/mcp con ese token en el
#      header Authorization.
#
# Como los tokens de MeLi expiran cada 6 horas pero la app los renueva
# automaticamente por dentro, este launcher siempre agarra el ultimo
# token vigente.
#
# Requisitos:
#   - docker compose corriendo (al menos el servicio sqlserver)
#   - .env con SQL_SA_PASSWORD definido
#   - npx disponible (Node.js, instalado por setup.sh)
#   - al menos una cuenta MeLi conectada en el dashboard
# ============================================================

set -euo pipefail

# Mover a la raiz del proyecto (este script vive en /scripts).
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$PROJECT_ROOT"

# 1) SQL_SA_PASSWORD desde .env
if [ ! -f .env ]; then
    echo "[meli-mcp] Error: no existe .env en $(pwd)." >&2
    exit 1
fi

SQL_SA_PASSWORD=$(grep -E '^SQL_SA_PASSWORD=' .env | head -1 | cut -d'=' -f2-)
if [ -z "${SQL_SA_PASSWORD:-}" ]; then
    echo "[meli-mcp] Error: SQL_SA_PASSWORD vacio en .env." >&2
    exit 1
fi

# 2) Token de la cuenta MeLi mas reciente.
#    MSYS_NO_PATHCONV=1 evita que Git Bash (Windows) convierta rutas Linux
#    a rutas Windows antes de pasarlas a docker.
#    -h-1: sin header / -W: trim de espacios / SET NOCOUNT ON: sin "(N rows affected)"
RAW_OUTPUT=$(MSYS_NO_PATHCONV=1 docker compose exec -T sqlserver \
    /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SQL_SA_PASSWORD" -C \
    -d AIml -h-1 -W \
    -Q "SET NOCOUNT ON; SELECT TOP 1 AccessToken FROM MeliAccounts ORDER BY ISNULL(UpdatedAt, CreatedAt) DESC" \
    2>&1) || {
    echo "[meli-mcp] Error consultando SQL Server:" >&2
    echo "$RAW_OUTPUT" >&2
    echo "" >&2
    echo "[meli-mcp] Verifica que el container sqlserver este corriendo:" >&2
    echo "    docker compose up -d sqlserver" >&2
    exit 1
}

# Extraer la primera linea con contenido y limpiar espacios.
TOKEN=$(printf '%s' "$RAW_OUTPUT" | tr -d '\r' | awk 'NF{print; exit}' | xargs || true)

# Validacion: un Access Token de MeLi es del estilo APP_USR-XXXX-YYYY-ZZZZ-AAAA,
# alfanumerico con guiones, sin espacios, generalmente >50 chars. Si no parece
# un token valido, abortar con mensaje claro.
if [ -z "${TOKEN:-}" ] \
   || [ "${#TOKEN}" -lt 20 ] \
   || [[ "$TOKEN" == *" "* ]] \
   || [ "$TOKEN" = "NULL" ]; then
    echo "[meli-mcp] No se obtuvo un Access Token valido." >&2
    echo "[meli-mcp] Causas posibles:" >&2
    echo "  - No hay cuentas conectadas en el dashboard (Integraciones -> MercadoLibre)" >&2
    echo "  - La tabla MeliAccounts esta vacia" >&2
    if [ -n "${RAW_OUTPUT:-}" ]; then
        echo "" >&2
        echo "[meli-mcp] Salida cruda de sqlcmd:" >&2
        echo "$RAW_OUTPUT" >&2
    fi
    exit 1
fi

# 3) Bridge stdio<->HTTP. mcp-remote se instala on-demand con npx -y.
exec npx -y mcp-remote https://mcp.mercadolibre.com/mcp \
    --header "Authorization:Bearer $TOKEN"

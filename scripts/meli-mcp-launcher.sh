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
#
# 2026-08-25: si el usuario no esta en el grupo "docker" (caso de este server),
# "docker compose exec" fallaba con "permission denied ... /var/run/docker.sock"
# y el conector NUNCA levantaba — sin aviso claro. Ahora se detecta solo y usa
# sudo si hace falta. Ademas, si en DESARROLLO no hay token util, se cae a
# PRODUCCION (donde las cuentas estan vivas y el token se renueva solo).
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

# 1.b) ¿Hace falta sudo para hablar con Docker?
DOCKER="docker"
if ! docker info >/dev/null 2>&1; then
    if sudo -n docker info >/dev/null 2>&1; then
        DOCKER="sudo docker"
    else
        echo "[meli-mcp] Error: no se puede hablar con Docker (ni con sudo)." >&2
        echo "[meli-mcp] Agregá tu usuario al grupo docker:  sudo usermod -aG docker \$USER" >&2
        exit 1
    fi
fi

# Consulta el AccessToken en el container que se le pase. Devuelve vacio si no hay.
leer_token() {
    local contenedor="$1"
    MSYS_NO_PATHCONV=1 $DOCKER exec -i "$contenedor" \
        /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$SQL_SA_PASSWORD" -C \
        -d AIml -h-1 -W \
        -Q "SET NOCOUNT ON; SELECT TOP 1 AccessToken FROM MeliAccounts ORDER BY ISNULL(UpdatedAt, CreatedAt) DESC" \
        2>/dev/null | tr -d '\r' | awk 'NF{print; exit}' | xargs || true
}

# Hace cuantas horas se renovo ese token. Los de MeLi duran 6 h: si pasaron mas,
# esta vencido aunque tenga forma de token valido (caso tipico de DESARROLLO,
# donde nadie los renueva: el 25/08 el de dev tenia 2.213 horas = 3 meses).
horas_token() {
    local contenedor="$1"
    MSYS_NO_PATHCONV=1 $DOCKER exec -i "$contenedor" \
        /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "$SQL_SA_PASSWORD" -C \
        -d AIml -h-1 -W \
        -Q "SET NOCOUNT ON; SELECT TOP 1 DATEDIFF(hour, ISNULL(UpdatedAt, CreatedAt), GETUTCDATE()) FROM MeliAccounts ORDER BY ISNULL(UpdatedAt, CreatedAt) DESC" \
        2>/dev/null | tr -d '\r' | awk 'NF{print; exit}' | xargs || echo 9999
}

es_token_valido() {
    local t="${1:-}"
    [ -n "$t" ] && [ "${#t}" -ge 20 ] && [[ "$t" != *" "* ]] && [ "$t" != "NULL" ]
}

# 2) Token de la cuenta MeLi mas reciente.
#    Primero DESARROLLO (aiml-sqlserver); si ahi no hay token util, PRODUCCION
#    (aiml-sqlserver-prod), que es donde las cuentas estan vivas y el token se
#    renueva solo cada 6 horas.
HORAS_DEV="$(horas_token aiml-sqlserver)"
HORAS_PROD="$(horas_token aiml-sqlserver-prod)"
[[ "$HORAS_DEV" =~ ^[0-9]+$ ]] || HORAS_DEV=9999
[[ "$HORAS_PROD" =~ ^[0-9]+$ ]] || HORAS_PROD=9999

# Gana el mas fresco. Un token de MeLi vive 6 horas.
if [ "$HORAS_DEV" -le "$HORAS_PROD" ]; then
    TOKEN="$(leer_token aiml-sqlserver)"; ORIGEN="desarrollo"; HORAS="$HORAS_DEV"
else
    TOKEN="$(leer_token aiml-sqlserver-prod)"; ORIGEN="produccion"; HORAS="$HORAS_PROD"
fi

# Si el elegido tampoco sirve, probar el otro antes de rendirse.
if ! es_token_valido "$TOKEN"; then
    if [ "$ORIGEN" = "desarrollo" ]; then
        TOKEN="$(leer_token aiml-sqlserver-prod)"; ORIGEN="produccion"; HORAS="$HORAS_PROD"
    else
        TOKEN="$(leer_token aiml-sqlserver)"; ORIGEN="desarrollo"; HORAS="$HORAS_DEV"
    fi
fi

if ! es_token_valido "$TOKEN"; then
    echo "[meli-mcp] No se obtuvo un Access Token valido (ni de desarrollo ni de produccion)." >&2
    echo "[meli-mcp] Causas posibles:" >&2
    echo "  - No hay cuentas conectadas en el dashboard (Integraciones -> MercadoLibre)" >&2
    echo "  - Los containers de base de datos no estan corriendo" >&2
    exit 1
fi

if [ "${HORAS:-9999}" -gt 6 ]; then
    echo "[meli-mcp] OJO: el token de $ORIGEN se renovo hace ${HORAS}h y los de MeLi duran 6h — puede estar vencido." >&2
else
    echo "[meli-mcp] Token de $ORIGEN (renovado hace ${HORAS}h)." >&2
fi

# 3) Bridge stdio<->HTTP. mcp-remote se instala on-demand con npx -y.
exec npx -y mcp-remote https://mcp.mercadolibre.com/mcp \
    --header "Authorization:Bearer $TOKEN"

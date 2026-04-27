#!/bin/bash
# ===========================================================
# AI Coding Environment - Instalador
# ===========================================================
# Instala todo lo necesario y levanta el proyecto.
# Uso: chmod +x setup.sh && ./setup.sh
# ===========================================================

set -e

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

ok()   { echo -e "  ${GREEN}[OK]${NC} $1"; }
skip() { echo -e "  ${YELLOW}[YA INSTALADO]${NC} $1"; }
fail() { echo -e "  ${RED}[ERROR]${NC} $1"; }
info() { echo -e "\n${YELLOW}>>>${NC} $1\n"; }

# --- Detectar OS ---
detect_os() {
    if [ -f /etc/os-release ]; then
        . /etc/os-release
        OS=$ID
    elif command -v sw_vers &>/dev/null; then
        OS="macos"
    else
        OS="unknown"
    fi
    echo "$OS"
}

OS=$(detect_os)

echo ""
echo "  ==========================================="
echo "  AI Coding Environment - Instalador"
echo "  ==========================================="
echo "  Sistema detectado: $OS"
echo "  ==========================================="

# --- Funciones de instalacion por OS ---
install_apt() {
    sudo apt-get update -qq
    sudo apt-get install -y -qq "$@" > /dev/null 2>&1
}

# ===========================================================
# 1. Herramientas base (git, curl, build tools)
# ===========================================================
info "1/8 - Herramientas base"

if [ "$OS" = "ubuntu" ] || [ "$OS" = "debian" ]; then
    NEEDED=""
    for pkg in git curl wget build-essential ca-certificates gnupg; do
        if ! dpkg -s "$pkg" &>/dev/null; then
            NEEDED="$NEEDED $pkg"
        fi
    done
    if [ -n "$NEEDED" ]; then
        install_apt $NEEDED
        ok "Herramientas base instaladas:$NEEDED"
    else
        skip "Herramientas base"
    fi
elif [ "$OS" = "macos" ]; then
    if ! command -v brew &>/dev/null; then
        /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
        ok "Homebrew instalado"
    fi
    skip "Herramientas base (macOS incluye lo necesario)"
else
    echo "  Sistema no soportado automaticamente. Instala manualmente: git, curl, Node.js, Docker."
    exit 1
fi

# ===========================================================
# 2. Node.js 20
# ===========================================================
info "2/8 - Node.js"

if command -v node &>/dev/null; then
    NODE_VERSION=$(node --version)
    skip "Node.js $NODE_VERSION"
else
    if [ "$OS" = "ubuntu" ] || [ "$OS" = "debian" ]; then
        curl -fsSL https://deb.nodesource.com/setup_20.x | sudo -E bash - > /dev/null 2>&1
        install_apt nodejs
        ok "Node.js $(node --version) instalado"
    elif [ "$OS" = "macos" ]; then
        brew install node@20
        ok "Node.js $(node --version) instalado"
    fi
fi

# npm (viene con Node.js, pero verificamos por las dudas)
if command -v npm &>/dev/null; then
    skip "npm $(npm --version)"
else
    if [ "$OS" = "ubuntu" ] || [ "$OS" = "debian" ]; then
        install_apt npm
        ok "npm $(npm --version) instalado"
    elif [ "$OS" = "macos" ]; then
        brew install npm
        ok "npm $(npm --version) instalado"
    fi
fi

# ===========================================================
# 3. Python 3
# ===========================================================
info "3/8 - Python"

if command -v python3 &>/dev/null; then
    PYTHON_VERSION=$(python3 --version)
    skip "$PYTHON_VERSION"
else
    if [ "$OS" = "ubuntu" ] || [ "$OS" = "debian" ]; then
        install_apt python3 python3-pip python3-venv
        ok "$(python3 --version) instalado"
    elif [ "$OS" = "macos" ]; then
        brew install python@3
        ok "$(python3 --version) instalado"
    fi
fi

# ===========================================================
# 4. Docker + Docker Compose
# ===========================================================
info "4/8 - Docker"

if command -v docker &>/dev/null; then
    DOCKER_VERSION=$(docker --version 2>/dev/null | cut -d' ' -f3 | tr -d ',')
    skip "Docker $DOCKER_VERSION"
else
    if [ "$OS" = "ubuntu" ] || [ "$OS" = "debian" ]; then
        # Instalar Docker oficial
        sudo install -m 0755 -d /etc/apt/keyrings
        curl -fsSL https://download.docker.com/linux/$OS/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
        sudo chmod a+r /etc/apt/keyrings/docker.gpg
        echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/$OS $(lsb_release -cs) stable" | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
        install_apt docker-ce docker-ce-cli containerd.io docker-compose-plugin
        sudo usermod -aG docker "$USER"
        ok "Docker $(docker --version | cut -d' ' -f3 | tr -d ',') instalado"
        echo -e "  ${YELLOW}NOTA: Cerra sesion y volve a entrar para usar Docker sin sudo${NC}"
    elif [ "$OS" = "macos" ]; then
        echo -e "  ${YELLOW}Instala Docker Desktop desde: https://www.docker.com/products/docker-desktop/${NC}"
        echo "  Despues de instalarlo, volve a correr este script."
        exit 1
    fi
fi

# Verificar que Docker este corriendo
if ! docker info &>/dev/null; then
    echo -e "  ${YELLOW}Docker no esta corriendo. Intentando iniciar...${NC}"
    sudo systemctl start docker 2>/dev/null || true
    sleep 2
    if ! docker info &>/dev/null; then
        fail "Docker no esta corriendo. Inicialo manualmente y volve a correr este script."
        exit 1
    fi
fi

# Verificar Docker Compose plugin
if docker compose version &>/dev/null; then
    skip "Docker Compose $(docker compose version --short 2>/dev/null || echo 'plugin')"
else
    if [ "$OS" = "ubuntu" ] || [ "$OS" = "debian" ]; then
        install_apt docker-compose-plugin
        if docker compose version &>/dev/null; then
            ok "Docker Compose instalado"
        else
            fail "No se pudo instalar Docker Compose"
            exit 1
        fi
    elif [ "$OS" = "macos" ]; then
        fail "Docker Compose no disponible. Verifica Docker Desktop actualizado."
        exit 1
    fi
fi

# ===========================================================
# 5. GitHub CLI (gh)
# ===========================================================
info "5/8 - GitHub CLI"

if command -v gh &>/dev/null; then
    skip "gh $(gh --version | head -1 | awk '{print $3}')"
else
    if [ "$OS" = "ubuntu" ] || [ "$OS" = "debian" ]; then
        (type -p wget >/dev/null || sudo apt-get install -y wget) >/dev/null 2>&1
        sudo mkdir -p -m 755 /etc/apt/keyrings
        wget -qO- https://cli.github.com/packages/githubcli-archive-keyring.gpg | sudo tee /etc/apt/keyrings/githubcli-archive-keyring.gpg >/dev/null
        sudo chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg
        echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" | sudo tee /etc/apt/sources.list.d/github-cli.list >/dev/null
        sudo apt-get update -qq
        sudo apt-get install -y -qq gh >/dev/null 2>&1
        ok "GitHub CLI instalado"
    elif [ "$OS" = "macos" ]; then
        brew install gh
        ok "GitHub CLI instalado"
    fi
fi

# ===========================================================
# 6. Herramientas AI
# ===========================================================
info "6/8 - Herramientas AI"

install_npm_tool() {
    local name="$1"
    local package="$2"
    local cmd="$3"
    if command -v "$cmd" &>/dev/null; then
        local ver=$($cmd --version 2>/dev/null | head -1)
        skip "$name ($ver)"
    else
        echo -n "  Instalando $name... "
        if sudo npm install -g "$package" > /dev/null 2>&1; then
            ok "$name instalado"
        else
            fail "$name (podes intentar despues con: npm install -g $package)"
        fi
    fi
}

install_npm_tool "Claude Code"  "@anthropic-ai/claude-code"  "claude"

# ===========================================================
# 7. Inicializar repositorio local limpio
# ===========================================================
info "7/8 - Repositorio Git"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

if [ -d .git ]; then
    REMOTE_URL=$(git remote get-url origin 2>/dev/null || echo "")
    if [ -n "$REMOTE_URL" ]; then
        echo "  Se detecto repositorio clonado desde: $REMOTE_URL"
        echo "  Eliminando historial de GitHub y creando repo local nuevo..."
        rm -rf .git
        git init
        git checkout -b develop
        git add -A
        git commit -m "Proyecto base inicial"
        ok "Repositorio local inicializado (rama develop, sin conexion a GitHub)"
    else
        skip "Repositorio local (sin remote)"
    fi
else
    git init
    git checkout -b develop
    git add -A
    git commit -m "Proyecto base inicial"
    ok "Repositorio local inicializado"
fi

# ===========================================================
# 8. Levantar el proyecto
# ===========================================================
info "8/8 - Proyecto"

# Crear .env si no existe y generar secretos aleatorios
if [ ! -f .env ]; then
    cp .env.example .env

    # Generar secretos aleatorios si hay openssl disponible.
    if command -v openssl &>/dev/null; then
        JWT=$(openssl rand -base64 48 | tr -d '\n=/+' | cut -c1-48)
        SQL=$(openssl rand -base64 24 | tr -d '\n=/+' | cut -c1-20)"Aa1!"
        ADMIN=$(openssl rand -base64 16 | tr -d '\n=/+' | cut -c1-16)

        # Compatibilidad con sed de macOS y Linux.
        if [[ "$OSTYPE" == "darwin"* ]]; then
            sed -i '' "s|^JWT_SECRET=.*|JWT_SECRET=$JWT|"                .env
            sed -i '' "s|^SQL_SA_PASSWORD=.*|SQL_SA_PASSWORD=$SQL|"      .env
            sed -i '' "s|^DEFAULT_ADMIN_PASSWORD=.*|DEFAULT_ADMIN_PASSWORD=$ADMIN|" .env
        else
            sed -i "s|^JWT_SECRET=.*|JWT_SECRET=$JWT|"                   .env
            sed -i "s|^SQL_SA_PASSWORD=.*|SQL_SA_PASSWORD=$SQL|"         .env
            sed -i "s|^DEFAULT_ADMIN_PASSWORD=.*|DEFAULT_ADMIN_PASSWORD=$ADMIN|" .env
        fi

        ok "Archivo .env creado con secretos aleatorios"
        echo "  -> Password inicial del admin: $ADMIN"
        echo "  -> Guardalo en un lugar seguro. Despues de entrar la primera vez,"
        echo "     cambiala desde el dashboard y vacia DEFAULT_ADMIN_PASSWORD en .env."
    else
        ok "Archivo .env creado (instala openssl para generar secretos automaticamente)"
    fi
else
    skip "Archivo .env"
fi

# Levantar DESARROLLO (puerto 3000)
echo "  Levantando entorno de DESARROLLO (puerto 3000)..."
if docker compose up --build -d 2>&1 | tail -5; then
    ok "Desarrollo levantado"
else
    fail "Error levantando desarrollo. Revisa los logs con: docker compose logs"
    exit 1
fi

# Restart de web: nginx cachea la IP del upstream "api" al arrancar y si la
# API se reconstruyo, la IP cambia y nginx queda apuntando a la vieja -> 502.
# Hacemos un restart "por si acaso" para que el primer acceso post-instalacion
# no muera con un 502.
echo "  Reiniciando web (dev) para refrescar upstream de nginx..."
docker compose restart web > /dev/null 2>&1
ok "web (dev) reiniciado"

# Levantar PRODUCCION (puertos 80 + 443 via Caddy)
echo "  Levantando entorno de PRODUCCION (puertos 80 + 443)..."
if docker compose -f docker-compose.prod.yml up --build -d 2>&1 | tail -5; then
    ok "Produccion levantada"
else
    fail "Error levantando produccion. Revisa los logs con: docker compose -f docker-compose.prod.yml logs"
    exit 1
fi

# Mismo restart de "por si acaso" para web-prod.
echo "  Reiniciando web-prod para refrescar upstream de nginx..."
docker compose -f docker-compose.prod.yml restart web-prod > /dev/null 2>&1
ok "web-prod reiniciado"

# ===========================================================
# Resumen final
# ===========================================================
echo ""
echo "  ==========================================="
echo "  Instalacion completa!"
echo "  ==========================================="
echo ""
echo "  Dashboard (desarrollo): http://localhost:3000"
echo "  Dashboard (produccion): https://localhost  (cert autofirmado: aceptar advertencia)"
echo "  Login:                  admin / (ver DEFAULT_ADMIN_PASSWORD en .env)"
echo ""
echo "  Herramientas disponibles:"
command -v gh       &>/dev/null && echo "    - gh        (GitHub CLI)"
command -v claude   &>/dev/null && echo "    - claude    (Claude Code)"
echo ""
echo "  Para configurar las API keys, edita .env"
echo "  Para parar desarrollo:  docker compose down"
echo "  Para parar produccion:  docker compose -f docker-compose.prod.yml down"
echo "  Para volver a levantar: docker compose up -d  (dev)"
echo "                          docker compose -f docker-compose.prod.yml up -d  (prod)"
echo "  ==========================================="
echo ""

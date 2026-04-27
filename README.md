# AI Coding Environment

Un template completo para crear ambientes de desarrollo con Docker + herramientas AI. Un solo comando instala todo y levanta el proyecto.

## Que incluye

- **Dashboard web** con login y gestion de usuarios (Blazor WebAssembly)
- **API REST** con autenticacion JWT en cookie httpOnly (.NET 8)
- **Base de datos** SQL Server Express
- **Backups** de la base de datos: crear, subir, descargar, restaurar y programacion automatica con retencion configurable
- **HTTPS automatico** en produccion via Caddy (Let's Encrypt si hay dominio, autofirmado si se usa IP) + redireccion forzada de HTTP a HTTPS
- **Hardening de seguridad**: rate-limit en login, registro publico cerrado (solo admin invita), CORS por whitelist, JWT en cookie httpOnly + SameSite=Strict
- **MCP server de MercadoLibre** integrado: cuando trabajas con Claude Code en este proyecto, podes consultar la documentacion oficial de MeLi y generar codigo de integracion. Usa el Access Token de la cuenta conectada en Integraciones (sin configuracion extra)
- **Herramientas AI**: Claude Code, OpenCode, Codex CLI, Gemini CLI
- **Nginx** como reverse proxy
- **100% Docker** - un solo comando para levantar todo

## Stack

| Componente | Tecnologia |
|------------|------------|
| Backend | .NET 8 + C# + Entity Framework Core |
| Base de datos | SQL Server 2022 Express |
| Frontend | Blazor WebAssembly (.NET 8) + CSS |
| Servidor web | Nginx |
| Reverse proxy + HTTPS (prod) | Caddy (Let's Encrypt / autofirmado) |
| Agentes AI | Claude Code, OpenCode, Codex CLI, Gemini CLI |
| Contenedores | Docker + Docker Compose |

## Instalacion rapida

### Requisito previo

- **Linux (Ubuntu/Debian)**: El script instala todo automaticamente
- **macOS**: Necesitas [Docker Desktop](https://www.docker.com/products/docker-desktop/) instalado
- **Windows**: Usa WSL2 y seguí las instrucciones de Linux

### Instalar todo

```bash
git clone https://github.com/ajmalfe/ai-ml.git
cd ai-ml
chmod +x setup.sh
./setup.sh
```

El script `setup.sh` instala automaticamente:
1. Herramientas base (git, curl, etc.)
2. Node.js 20
3. Python 3
4. Docker + Docker Compose
5. Herramientas AI (Claude Code, OpenCode, Codex CLI, Gemini CLI)
6. Levanta **desarrollo** (puerto 3000) y **produccion** (puertos 80 + 443 via Caddy)

### Acceder

| Que | Direccion |
|-----|-----------|
| Dashboard (desarrollo) | http://localhost:3000 |
| Dashboard (produccion) | https://localhost (cert autofirmado, aceptar advertencia) |

**Login:** `admin` / la clave inicial se genera en `.env` (variable `DEFAULT_ADMIN_PASSWORD`). El `setup.sh` la imprime al final.

## Instalacion manual

Si preferis instalar paso a paso:

```bash
# 1. Configurar variables de entorno
cp .env.example .env

# 2. (Opcional) Editar .env con tus API keys

# 3. Levantar la app
docker compose up --build -d

# 4. Abrir http://localhost:3000
```

## Herramientas AI

Las herramientas se instalan en tu maquina con el script `setup.sh`. Despues las podes usar directo desde la terminal:

| Herramienta | Comando | API Key necesaria |
|-------------|---------|-------------------|
| Claude Code | `claude` | ANTHROPIC_API_KEY |
| OpenCode | `opencode` | OPENAI_API_KEY |
| Codex CLI | `codex` | OPENAI_API_KEY |
| Gemini CLI | `gemini` | GEMINI_API_KEY |

Configura las API keys en el archivo `.env`.

## MCP server de MercadoLibre

Cuando trabajas con Claude Code (u otro cliente MCP) en este proyecto, se levanta automaticamente un MCP server contra `https://mcp.mercadolibre.com/mcp` que le permite al agente:

- Buscar en la documentacion oficial de MercadoLibre Developers.
- Sugerir endpoints, parametros y ejemplos de codigo correctos al integrar nuevas funciones de MeLi.

Para activarlo no hay que copiar tokens a ningun lado: alcanza con conectar al menos una cuenta de MercadoLibre desde el dashboard (`Administracion -> Integraciones -> MercadoLibre`). Cada vez que arranque Claude Code, `scripts/meli-mcp-launcher.sh` lee el `AccessToken` vigente desde la base de datos y se lo pasa al server remoto. Como la app refresca los tokens cada 6 horas por su cuenta, el MCP siempre usa el token mas fresco.

Si todavia no hay ninguna cuenta conectada, el launcher avisa con un mensaje claro y Claude Code sigue funcionando normal (sin las tools de MeLi).

## Estructura del proyecto

```
ai-ml/
├── docker-compose.yml          # Levanta la app (DB + API + Frontend)
├── setup.sh                    # Instalador automatico
├── .env.example                # Variables de entorno (API keys)
├── .mcp.json                   # Config del MCP server de MercadoLibre
├── scripts/
│   └── meli-mcp-launcher.sh    # Lee el token de la DB y lanza el bridge MCP
├── src/Api/                    # Backend .NET 8
│   ├── Controllers/            # Endpoints de la API
│   ├── Models/                 # Modelos de datos
│   ├── Services/               # Logica de negocio
│   ├── Data/                   # Entity Framework (base de datos)
│   └── Dockerfile              # Build del backend
├── src/Web/                    # Frontend Blazor WebAssembly
│   ├── Pages/                  # Paginas (Login, Dashboard, Config)
│   ├── Layout/                 # Layouts (MainLayout, LoginLayout)
│   ├── Shared/                 # Componentes reutilizables
│   ├── Services/               # Servicios (Auth, API, Toast)
│   ├── wwwroot/                # Archivos estaticos (HTML, CSS)
│   └── Dockerfile              # Build del frontend
├── db/init.sql                 # Creacion de tablas
└── nginx/nginx.conf            # Reverse proxy + SPA routing
```

## Servicios Docker

**Desarrollo** (`docker compose up -d`):

| Servicio | Que hace | Puerto |
|----------|----------|--------|
| sqlserver | Base de datos SQL Server Express | 1433 (interno) |
| backups-init | Ajusta permisos del volume de backups (corre una vez) | - |
| api | Backend .NET 8 con autenticacion JWT | 80 (interno) |
| playwright | Automatizacion de WhatsApp Web | 3001 (interno) |
| web | Blazor WASM + Nginx | **3000** |

**Produccion** (`docker compose -f docker-compose.prod.yml up -d`):

| Servicio | Que hace | Puerto |
|----------|----------|--------|
| sqlserver-prod | Base de datos (DB separada de dev) | 1433 (interno) |
| backups-init-prod | Ajusta permisos del volume de backups | - |
| api-prod | Backend .NET 8 | 80 (interno) |
| playwright-prod | Automatizacion de WhatsApp Web | 3001 (interno) |
| web-prod | Blazor WASM + Nginx | 80 (interno) |
| caddy | Reverse proxy + HTTPS automatico | **80 + 443** |

### HTTPS en produccion

Caddy maneja los certificados SSL automaticamente. Basado en el `.env`:

- `DOMAIN=` vacio -> certificado autofirmado (funciona por IP, navegador mostrara advertencia la primera vez pero el trafico va encriptado).
- `DOMAIN=midominio.com` + `ACME_EMAIL=tu@email.com` -> certificado real gratis de Let's Encrypt, renovado solo. Requiere que el DNS apunte al VPS y los puertos 80/443 esten abiertos al publico.

En cualquier modo, todo el trafico HTTP (`:80`) se redirige automaticamente a HTTPS (`:443`).

### Seguridad

El backend incluye varias protecciones por defecto:

- **JWT en cookie httpOnly** (`HttpOnly + SameSite=Strict + Secure` en HTTPS): el token nunca toca `localStorage`, asi que un eventual XSS no puede robarlo. El frontend no maneja el token directamente.
- **Rate-limit en `/api/auth/login`**: maximo 5 intentos por minuto por IP. Bloquea brute-force devolviendo HTTP 429.
- **Registro publico deshabilitado**: el endpoint `/api/auth/register` requiere rol `admin`. Los usuarios se crean desde Administracion -> Usuarios.
- **CORS por whitelist**: solo se aceptan los origenes definidos en `CORS_ALLOWED_ORIGINS` (env var). Si esta vacio, CORS queda cerrado y solo funcionan requests del mismo origen (que es el caso normal cuando el frontend va detras del mismo Caddy).
- **Forwarded headers**: el rate-limit y los logs ven la IP real del cliente aun detras del proxy.
- **HSTS y headers de seguridad** (`X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`): los aplica Caddy en produccion.

Lo que aun **depende del cliente del VPS** y no puede automatizarse desde el repo:

- Mantener actualizado el sistema operativo del servidor (`apt update && apt upgrade` periodico o `unattended-upgrades`).
- Configurar un firewall (`ufw`) que solo abra los puertos 22 (SSH), 80 y 443.
- Cambiar el puerto SSH default y/o forzar autenticacion con keys (deshabilitar passwords).
- Opcional: instalar `fail2ban` para bloquear IPs con fallos repetidos a nivel SO.

### Backups

Desde `Administracion > Backups` en el dashboard (solo rol admin):

- **Crear backup manual** de la DB en cualquier momento.
- **Subir** un `.bak` externo (hasta 2 GB).
- **Descargar** cualquier backup para guardarlo offline.
- **Restaurar** desde un `.bak` (pide confirmacion doble: hay que escribir el nombre exacto del archivo).
- **Programacion automatica**: intervalo configurable (6h a 7 dias) y retencion en dias. Los backups mas viejos que la retencion se borran solos.

Los `.bak` viven en un volume Docker (`backups_data` / `backups_prod_data`) que sobrevive a los rebuilds.

## Credenciales

Los secretos (password de SQL, JWT, password inicial del admin, dominio para HTTPS, lista de origenes CORS) se manejan desde el archivo `.env` (fuera del repositorio). Ver `.env.example` para la lista completa.

Al correr `setup.sh` por primera vez se generan valores aleatorios y se imprime el password inicial del admin en pantalla.

**Importante para produccion:**
- Generar `.env` con valores nuevos (no reutilizar los de desarrollo).
- Despues del primer login, cambiar la clave del admin desde el dashboard y vaciar `DEFAULT_ADMIN_PASSWORD` en `.env`.
- Rotar `JWT_SECRET` y `SQL_SA_PASSWORD` periodicamente.

## Comandos utiles

```bash
# Levantar todo
docker compose up --build -d

# Ver logs
docker compose logs -f

# Parar todo
docker compose down

# Reinstalar herramientas AI
./setup.sh
```

## Para agentes de IA

Este proyecto incluye `CLAUDE.md` con instrucciones detalladas para que cualquier agente de IA pueda trabajar en el automaticamente.

## Expansion

- **Nueva pagina:** crear `.razor` en `src/Web/Pages/` y agregar navegacion en `MainLayout.razor`
- **Nueva tabla:** crear modelo en `src/Api/Models/` + agregar en `db/init.sql`
- **Nuevo endpoint:** crear controller en `src/Api/Controllers/`
- **Nuevo servicio Docker:** agregar en `docker-compose.yml`

Ver `CLAUDE.md` para instrucciones detalladas.

## Licencia

MIT

# AGENTS.md - Reglas para Agentes de IA

Este archivo contiene las instrucciones que SIEMPRE debe seguir cualquier agente de IA (Claude Code, OpenCode, o cualquier otro) cuando trabaje en este proyecto.

Lee este archivo completo antes de hacer cualquier cosa.

---

## Quien es el usuario

El usuario NO es programador ni tiene conocimientos de IT. Habla en lenguaje cotidiano, no tecnico. Tu trabajo es:

1. Escuchar lo que dice, aunque sea vago o impreciso
2. Interpretar que es lo que realmente necesita
3. Transformar su pedido en tareas concretas y ejecutarlas
4. Explicarle lo que hiciste en palabras simples, sin jerga tecnica

Cuando el usuario diga algo como "quiero que se vea mas lindo" o "hace que funcione eso", no le pidas que sea mas especifico con terminos tecnicos. Vos tenes que deducir que quiere y proponer opciones claras.

### Ejemplos

- Usuario dice: "quiero guardar cosas" -> Vos entendes: necesita una tabla en la base de datos + formulario + listado
- Usuario dice: "que se pueda entrar con clave" -> Vos entendes: necesita autenticacion/login
- Usuario dice: "no me anda" -> Vos entendes: hay que revisar los logs, el estado de los containers, y debuggear

---

## Reglas obligatorias

### 1. Commit + push constantes

Commiteá **en cada paso logico que funcione**, no al final de una tarea grande. Esto da control de versionado fino: si algo se rompe dos pasos despues, volvemos atras sin perder todo.

Guia:
- Despues de cada cambio que compile y funcione (aunque sea chico), hacer commit + push.
- Un commit = una intencion clara. Si el mensaje tiene que usar "y" para unir varias cosas, probablemente debieron ser commits separados.
- **Siempre hacer push en el mismo paso que el commit**, salvo que el usuario haya dicho lo contrario. El repo remoto es el backup real.

```bash
git add -A
git commit -m "Agregar formulario de contacto en el dashboard"
git push
```

Si el push falla, avisar al usuario pero continuar trabajando (el commit local ya quedo).

### 1.1 Autor de commits: solo propietario del repo

- El autor de commits debe ser el propietario del repositorio.
- No incluir en commits texto tipo: "hecho por Claude", "hecho por agente", "AI generated", ni trailers `Co-authored-by` de agentes.
- Antes de commitear, verificar identidad git:

```bash
git config user.name
git config user.email
```

Si no coincide con el propietario, corregir:

```bash
git config user.name "<OWNER_NAME>"
git config user.email "<OWNER_EMAIL>"
```

### 2. Probar antes de decir que esta listo

No digas "listo, funciona" sin haber verificado. Siempre:
- Si tocaste el backend: verifica que compila (`dotnet build`)
- Si tocaste el frontend: recarga el browser y verifica visualmente
- Si tocaste Docker: hace `docker compose up --build -d` y verifica que los containers esten corriendo

### 2.1 Validacion obligatoria de lo que ve el navegador

Siempre hacer estos dos pasos antes de confirmar que un cambio web quedo aplicado:

1. **Rebuild real del servicio web** (no confiar en archivos locales viejos)
   - `docker compose up --build -d web`
2. **Chequeo adentro del contenedor** (confirmar lo que realmente sirve Nginx)
   - ejemplo: `docker compose exec web sh -lc "ls -la /usr/share/nginx/html && grep -n \"texto-clave\" /usr/share/nginx/html/index.html || true"`

Si el archivo local dice una cosa pero el contenedor sirve otra, prevalece lo del contenedor.

### 3. No romper lo que ya funciona

Antes de modificar un archivo, leelo primero. Entende que hace antes de cambiarlo. Si tu cambio puede afectar otras partes, revisalas tambien.

### 4. Explicar lo que hiciste

Despues de cada tarea, explica brevemente al usuario:
- Que cambiaste (en palabras simples)
- Por que lo hiciste asi
- Como lo puede ver o probar

Ejemplo: "Agregue una seccion de contacto en la pagina principal. Ahora cuando entres al dashboard vas a ver un formulario donde podes escribir un mensaje. Los mensajes se guardan en la base de datos."

### 4.1 Flujo de ramas recomendado (produccion vs desarrollo)

Usar este modelo simple:

- `main`: produccion estable (solo merges probados)
- `develop`: integracion de cambios en desarrollo
- `feature/<nombre-corto>`: una funcionalidad especifica
- `hotfix/<nombre-corto>`: arreglo urgente de produccion

Flujo sugerido:

1. Crear rama de feature desde `develop`
2. Implementar cambios + commits chicos
3. Abrir PR hacia `develop`
4. Cuando `develop` este estable, merge a `main`

Ejemplo:

```bash
git checkout develop
git pull
git checkout -b feature/integracion-mercadolibre
# cambios...
git add -A
git commit -m "Agregar endpoint base de publicaciones"
git push -u origin feature/integracion-mercadolibre
```

Con GitHub CLI:

```bash
gh pr create --base develop --head feature/integracion-mercadolibre --title "Integracion MercadoLibre base" --body "Cambios iniciales"
```

### 5. Usar subagentes para tareas grandes

Si el usuario pide algo complejo (mas de 3 archivos o mas de una funcionalidad), dividilo en partes y usa subagentes en paralelo:
- Un agente para el backend (API, base de datos)
- Un agente para el frontend (paginas, estilos)
- Un agente para infraestructura (Docker, nginx) si hace falta

Esto es mas rapido y reduce errores.

### 6. No inventar funcionalidades extra

Hace SOLO lo que el usuario pidio. No agregues cosas "por las dudas" o "porque seria buena idea". Si crees que algo seria util, proponelo al usuario primero.

### 7. Password del admin: se setea solo la primera vez

`DEFAULT_ADMIN_PASSWORD` del `.env` se aplica solo cuando el hash del admin es el placeholder del `init.sql` (primer arranque en una DB nueva). Una vez que el usuario cambio la clave desde el dashboard, esa variable se ignora en los arranques siguientes. **Nunca resetear la clave sin que el usuario lo pida.**

Si el usuario olvido la clave y pide un reseteo, agregar `FORCE_RESET_ADMIN_PASSWORD=true` al `.env`, rebuildear una vez, y despues vaciar esa variable.

### 8. Volume de backups: no mover el sidecar `backups-init`

Los `.bak` viven en un volume Docker (`backups_data` en dev, `backups_prod_data` en prod) montado tanto en `sqlserver` (`/var/opt/mssql/backup`) como en `api` (`/data/backups`). SQL Server corre como usuario `mssql` (UID 10001), no root, asi que el volume arranca sin permisos para escribir.

El servicio `backups-init` (alpine) corre UNA vez antes que sqlserver y hace `chown 10001:10001` del volume. **No lo saques del compose** — sin el, `BACKUP DATABASE` falla con "Access is denied".

### 9. Si la web tira 502 despues de un rebuild de la API

Nginx cachea la IP del upstream `api` durante su lifetime. Cuando rebuildeás solo la API (`docker compose up -d --build api`), el container de api cambia de IP y nginx sigue apuntando a la vieja → 502 Bad Gateway.

Fix: `docker compose restart web` despues de cada rebuild de la API (o rebuildear ambos juntos).

### 10. No debilitar las protecciones de seguridad

Estas decisiones de seguridad ya estan tomadas. **No las revertir** salvo que el usuario lo pida explicitamente:

- **JWT en cookie httpOnly** (`Api/Controllers/AuthController.cs::SetAccessTokenCookie` y `Api/Program.cs::OnMessageReceived`): el token va en una cookie, no en `localStorage`. El frontend (`Web/Services/AuthService.cs`) NO debe guardar el token en JS, ni `ApiClient` debe agregar header `Authorization`. Si ves codigo en `tm_token` o `localStorage.getItem('tm_token')`, es legacy a borrar.
- **Endpoint de registro cerrado**: `/api/auth/register` tiene `[Authorize(Roles="admin")]`. No sacarlo: los usuarios se crean desde `Administracion -> Usuarios`.
- **Rate-limit en `/login`**: 5 intentos por minuto por IP (`Api/Program.cs::AddRateLimiter`). Funciona detras del proxy gracias a `UseForwardedHeaders`.
- **CORS por whitelist**: `Api/Program.cs` lee `CORS_ALLOWED_ORIGINS`. Nunca volver a `AllowAnyOrigin()`.
- **Caddy fuerza HTTPS**: el bloque `:80` redirige siempre a `:443`. No quitar el redirect.
- **Sin volumes sensibles en api-prod**: el container `api` (dev y prod) no monta `/proc` ni `/var/run/docker.sock`. Esto degrada la tarjeta "Host info" del dashboard a proposito (muestra ceros) — no es bug, es por seguridad.

Si una tarea parece requerir tocar algo de esto, pausar y preguntar al usuario antes.

### 11. MCP server de MercadoLibre

El proyecto trae configurado un MCP server contra `https://mcp.mercadolibre.com/mcp` (declarado en `.mcp.json` en la raiz). Sirve **al agente, mientras programa**, para consultar documentacion oficial de MeLi y generar codigo de integracion correcto. NO procesa ventas ni toca el runtime de la app.

Como funciona:

- `.mcp.json` declara el server `mercadolibre` con `command: bash, args: [scripts/meli-mcp-launcher.sh]`.
- `scripts/meli-mcp-launcher.sh`:
  1. Lee `SQL_SA_PASSWORD` de `.env`.
  2. Hace `docker compose exec sqlserver sqlcmd ...` para obtener el `AccessToken` de la cuenta MeLi mas reciente en la tabla `MeliAccounts`.
  3. Lanza `npx -y mcp-remote https://mcp.mercadolibre.com/mcp --header "Authorization:Bearer $TOKEN"` como bridge stdio<->HTTP.

Requisitos para que funcione:

- `docker compose up -d` (al menos sqlserver corriendo en dev).
- Al menos una cuenta conectada en `Integraciones -> MercadoLibre`.
- Node.js disponible (lo instala `setup.sh`).

Si no hay cuentas, el launcher sale con codigo 1 y mensaje claro. Claude Code sigue funcionando — solo no tiene las tools de MeLi.

**No hardcodear tokens** en `.mcp.json` ni en `.env`: los tokens de MeLi expiran cada 6 horas y la app los refresca sola. El launcher siempre agarra el mas fresco al iniciar la sesion.

---

## Arquitectura del proyecto

```
ai-ml/
|
|-- docker-compose.yml          <- Levanta la app (DB + API + Frontend)
|-- setup.sh                    <- Instalador: prepara la maquina y levanta todo
|-- .env.example                <- Variables de entorno (API keys)
|-- .mcp.json                   <- Config del MCP server de MercadoLibre
|-- scripts/
|   '-- meli-mcp-launcher.sh    <- Lee el AccessToken de MeLi de la DB y lanza el bridge MCP
|
|-- src/Api/                  <- Backend (API REST)
|   |-- Program.cs            <- Punto de entrada de la API
|   |-- Controllers/          <- Endpoints (AuthController, DashboardController)
|   |-- Models/               <- Modelos de datos (User.cs)
|   |-- DTOs/                 <- Objetos de transferencia (AuthDtos.cs)
|   |-- Services/             <- Logica de negocio (AuthService.cs)
|   |-- Data/                 <- Base de datos (AppDbContext.cs)
|   |-- Dockerfile            <- Como se construye el container de la API
|   |-- appsettings.json      <- Configuracion (JWT, connection string)
|   '-- Api.csproj            <- Dependencias del proyecto
|
|-- src/Web/                  <- Frontend (Blazor WebAssembly)
|   |-- Program.cs            <- Punto de entrada, registro de servicios
|   |-- App.razor             <- Router y autenticacion
|   |-- _Imports.razor        <- Usings globales
|   |-- Web.csproj            <- Dependencias del proyecto Blazor
|   |-- Dockerfile            <- Build multi-stage (SDK + nginx)
|   |-- Pages/                <- Paginas de la app
|   |   |-- Login.razor       <- Pagina de login
|   |   |-- Dashboard.razor   <- Pagina principal del dashboard
|   |   '-- Config.razor      <- Pagina de configuracion
|   |-- Layout/               <- Layouts de la app
|   |   |-- MainLayout.razor  <- Layout principal (sidebar + topbar)
|   |   '-- LoginLayout.razor <- Layout de login
|   |-- Shared/               <- Componentes reutilizables
|   |   |-- NavItem.razor     <- Item de navegacion del sidebar
|   |   |-- StatCard.razor    <- Tarjeta de estadistica
|   |   |-- ToastContainer.razor <- Notificaciones toast
|   |   |-- SvgIcons.razor    <- Iconos SVG centralizados
|   |   '-- RedirectToLogin.razor <- Redireccion a login
|   |-- Models/               <- Modelos de datos del frontend
|   |   |-- LoginRequest.cs   <- Modelo de login
|   |   |-- AuthResponse.cs   <- Respuesta de autenticacion
|   |   |-- UserDto.cs        <- Datos del usuario
|   |   '-- DashboardStats.cs <- Estadisticas del dashboard
|   |-- Services/             <- Servicios del frontend
|   |   |-- AuthService.cs    <- Manejo de sesion y login (JWT en cookie httpOnly)
|   |   |-- JwtAuthStateProvider.cs <- Proveedor de estado de autenticacion (lee user de localStorage, sin token)
|   |   |-- ApiClient.cs      <- Cliente HTTP (la cookie httpOnly viaja sola, no hay header Authorization)
|   |   '-- ToastService.cs   <- Servicio de notificaciones
|   '-- wwwroot/              <- Archivos estaticos
|       |-- index.html        <- Pagina host de Blazor
|       '-- css/app.css       <- Estilos visuales
|
|-- db/
|   '-- init.sql              <- Script que crea las tablas iniciales
|
|-- nginx/
|   '-- nginx.conf            <- Configuracion del servidor web
|
'-- AGENTS.md                 <- Este archivo
```

### Servicios Docker

Todo se levanta con `docker compose up --build -d` (o con `./setup.sh` que hace todo automaticamente):

| Servicio | Que hace | Puerto |
|----------|----------|--------|
| sqlserver | Base de datos SQL Server Express | 1433 (interno) |
| sqlserver-init | Ejecuta init.sql para crear tablas (corre una vez y termina) | - |
| api | Backend .NET 8 con autenticacion JWT | 80 (interno) |
| web | Blazor WASM + Nginx - sirve el frontend y proxea la API | 3000 |

### Como se conectan

```
Browser -> localhost:3000 -> Nginx
                              |-- /            -> Blazor WASM (frontend)
                              |-- /_framework/ -> Runtime de Blazor
                              |-- /api/        -> Backend .NET (api:80)
                              '-- /swagger     -> Documentacion de la API
```

### Herramientas AI (se instalan en la maquina con setup.sh)

- **Claude Code** - `claude` (necesita ANTHROPIC_API_KEY)
- **OpenCode** - `opencode` (necesita OPENAI_API_KEY)
- **Codex CLI** - `codex` (necesita OPENAI_API_KEY)
- **Gemini CLI** - `gemini` (necesita GEMINI_API_KEY)

### Tecnologias

- **Backend**: .NET 8 + C# + Entity Framework Core
- **Base de datos**: SQL Server 2022 Express
- **Frontend**: Blazor WebAssembly (.NET 8) + CSS
- **Servidor web**: Nginx
- **Agentes AI**: Claude Code, OpenCode, Codex CLI, Gemini CLI
- **Autenticacion**: JWT en cookie httpOnly + rate-limit en login
- **Contenedores**: Docker + Docker Compose

---

## Como expandir el proyecto

### Agregar una nueva pagina al dashboard

1. Crear el archivo `.razor` en `src/Web/Pages/NuevaPagina.razor`:
   - Agregar `@page "/ruta"` y `@attribute [Authorize]`
   - Inyectar servicios necesarios (`@inject ApiClient Api`)
   - Implementar la UI con HTML y logica en el bloque `@code {}`

2. Agregar la navegacion en `src/Web/Layout/MainLayout.razor`:
   - Agregar un `<NavItem>` en la seccion `sidebar-nav`
   - Agregar el icono en `src/Web/Shared/SvgIcons.razor` si hace falta

3. Si necesita datos del backend, crear el endpoint en la API:
   - Crear el modelo en `src/Api/Models/`
   - Agregar la tabla en `db/init.sql`
   - Agregar el DbSet en `src/Api/Data/AppDbContext.cs`
   - Crear el controller en `src/Api/Controllers/`
   - Agregar la funcion en `src/Web/Services/ApiClient.cs`

4. Si necesita estilos nuevos, agregarlos en `src/Web/wwwroot/css/app.css`

### Agregar una nueva tabla a la base de datos

1. Crear el modelo C# en `src/Api/Models/NuevoModelo.cs`
2. Agregar `DbSet<NuevoModelo>` en `AppDbContext.cs`
3. Agregar `CREATE TABLE` en `db/init.sql`
4. Crear el controller con los endpoints CRUD

### Cambiar el nombre de la marca

Buscar "Tu Marca" en estos archivos y reemplazar:
- `src/Web/wwwroot/index.html` (titulo)
- `src/Web/Pages/Login.razor` (encabezado del login)
- `src/Web/Layout/MainLayout.razor` (sidebar y topbar)

### Agregar un nuevo servicio Docker

1. Crear una carpeta con su `Dockerfile`
2. Agregarlo en `docker-compose.yml` dentro de `services:`
3. Si necesita ser accesible desde el browser, agregar la ruta en `nginx/nginx.conf`

---

## Credenciales

Los secretos se leen desde `.env` (no commiteado). Ver `.env.example` para la lista completa:

- `SQL_SA_PASSWORD`: password del usuario `sa` de SQL Server.
- `JWT_SECRET`: clave de firma de los tokens (minimo 32 caracteres).
- `DEFAULT_ADMIN_PASSWORD`: password inicial del admin del dashboard.
- `DOMAIN` / `ACME_EMAIL`: opcional, para HTTPS con Let's Encrypt via Caddy.
- `CORS_ALLOWED_ORIGINS`: opcional, lista separada por coma de origenes permitidos para CORS. Si esta vacio en produccion, CORS queda cerrado y solo funcionan requests del mismo origen (caso tipico detras del mismo Caddy). En desarrollo, si esta vacio, la API permite `http://localhost:3000` por default.

El `setup.sh` genera valores aleatorios automaticamente en desarrollo e imprime la clave del admin en pantalla. En produccion hay que generar un `.env` nuevo y propio.

---

## Como levantar el proyecto

### Opcion 1: Instalador automatico (recomendado)

```bash
chmod +x setup.sh
./setup.sh
```

Instala todo lo necesario (Node.js, Python, Docker, herramientas AI) y levanta el proyecto.

### Opcion 2: Manual

```bash
# 1. Copiar variables de entorno
cp .env.example .env

# 2. (Opcional) Poner tus API keys en .env

# 3. Levantar la app
docker compose up --build -d

# 4. Abrir en el browser: http://localhost:3000
```

---

## Resumen para el agente

Cuando el usuario te pida algo:

1. Lee este archivo si no lo leiste
2. Escucha lo que pide y traducilo a tareas tecnicas
3. Dividi en subagentes si es complejo
4. Ejecuta los cambios
5. Probalo
6. Hace commit
7. Explicale al usuario que hiciste, en simple

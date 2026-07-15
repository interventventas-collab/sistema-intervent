# App C.S.E (electricista Agustín Caldeira) — Nota de traspaso

Este documento es para el **próximo agente/desarrollador** que continúe el sistema.
Lenguaje simple porque el dueño (Osmar / Agustín) NO es técnico.

## Qué es esto

Un sistema de gestión para un **electricista** (marca **C.S.E — Caldeira Servicio Eléctrico**).
Gestiona: clientes, servicios (mano de obra), productos/stock, y **presupuestos** que se
aprueban/rechazan, con un **tablero con gráficos** y **PDF imprimible** con el diseño de la marca.

## Estado actual (3 versiones, de menor a mayor)

1. **`/presupuestos-electricista/armador-presupuestos-cse.html`** — un solo archivo, genera 1 presupuesto en PDF. Datos en el navegador (localStorage). Publicado en `https://app.palanica.com.ar/electricista/armador.html`.

2. **`/presupuestos-electricista/sistema-cse.html`** — SISTEMA COMPLETO en un archivo: login, tablero con gráficos (donut + barras, SVG hecho a mano, sin librerías), clientes, servicios, productos/stock (aviso stock bajo), presupuestos con estado pendiente/aprobado/rechazado, numeración automática, PDF, backup export/import JSON. **Datos en localStorage (por dispositivo).** Publicado en `https://app.palanica.com.ar/electricista/`. **Esta es la referencia visual/funcional completa.**

3. **`/electricista-nube/Api/`** — VERSIÓN NUBE (datos compartidos entre dispositivos). **Solo la base está hecha.** Ver abajo.

## La versión nube (lo que hay que terminar)

- **Backend:** .NET 8 minimal API + **SQLite**. Un solo archivo `Api/Program.cs`.
  - Base de datos en volume Docker `electri_prod_data` → `/data/electricista.db`.
  - Tablas: Usuarios, Clientes, Servicios, Productos, Presupuestos (items guardados como JSON en la columna `ItemsJson`), Configs (marca, logo, proximoNumero, secret).
  - **Auth:** clave hasheada con PBKDF2 + cookie httpOnly firmada con HMAC. Usuario default `admin` / `1234`. Cookie con `Path=/electricista-nube`.
  - **Endpoints ya hechos (todos requieren login salvo /api/login):**
    - `POST /api/login`, `POST /api/logout`, `GET /api/me`
    - `GET/POST/PUT/DELETE /api/clientes`
    - `GET/POST/PUT/DELETE /api/servicios`
    - `GET/POST/PUT/DELETE /api/productos`
    - `GET/POST/PUT /api/presupuestos`, `PUT /api/presupuestos/{id}/estado`, `DELETE /api/presupuestos/{id}`
    - `GET/PUT /api/config`, `PUT /api/usuario`, `GET /api/stats`
  - **El backend ya está completo.** Se probó en vivo (login, CRUD, stats).

- **Frontend actual (`Api/wwwroot/index.html`):** SOLO login + una pantalla de prueba (stats + alta de un cliente). **Falta portar toda la UI.**

### ⭐ Lo que falta hacer (tarea principal)

**Portar la interfaz completa de `sistema-cse.html` (versión 2) a `electricista-nube/Api/wwwroot/index.html`, cambiando el guardado en localStorage por llamadas a la API** (`fetch('api/...')` con `credentials:'same-origin'`).

Prácticamente: copiar toda la UI/CSS/pantallas de la versión 2 y reemplazar:
- `guardar()` / `localStorage` → `POST/PUT/DELETE` a los endpoints.
- `cargar()` inicial → `GET` de clientes/servicios/productos/presupuestos/config al entrar.
- El login local → el `/api/login` que ya existe.
- El formato de datos es casi idéntico (los nombres de campos coinciden: nombre, precio, stock, items[{cant,desc,unit,detalle}], etc.).

Después, rebuild: `sudo docker compose -f docker-compose.prod.yml up -d --build electri-api`
(el `wwwroot` va dentro de la imagen, así que cambios en el front requieren rebuild;
alternativa: montar `./electricista-nube/Api/wwwroot` como volume para iterar sin rebuild).

## Cómo está desplegado (infra)

- **Servicio Docker** `electri-api` (container `aiml-electri-prod`) en `docker-compose.prod.yml`. Red `aiml-prod-net`.
- **nginx** (`nginx/nginx.prod.conf`): `location ^~ /electricista-nube/` hace `proxy_pass http://electri-api:8080/` (la **barra final saca el prefijo**). Por eso el front usa rutas **relativas** (`api/...`).
- **Caddy** reenvía todo el dominio a la web nginx — no hay que tocarlo.
- Tras cambiar nginx.prod.conf: `docker compose -f docker-compose.prod.yml restart web-prod`.

## Comandos útiles

```bash
# rebuild + levantar el backend del electricista
sudo docker compose -f docker-compose.prod.yml up -d --build electri-api

# ver logs
sudo docker compose -f docker-compose.prod.yml logs -f electri-api

# backup de la base (SQLite es un solo archivo)
sudo docker compose -f docker-compose.prod.yml cp electri-api:/data/electricista.db ./backup-electricista.db
```

## Notas importantes

- Todo vive en la rama `develop`. El repo **no tiene remoto** (no hay push). Prod levanta desde el working tree.
- Separado 100% de INTER VENT: su propia DB, su propio container. No tocar la DB SQL Server de Osmar.
- Los presupuestos reales de Agustín (PDFs) están en `/presupuestos-electricista/*.pdf` (gitignoreados, privados) — sirven de referencia del formato que él usa.
- Diseño de marca C.S.E: logo rayo amarillo + negro, IG Caldeira.servi.electrico, WhatsApp 11 2505 2932, mail caldeira.servicioelectrico@gmail.com, zona Esteban Echeverría PBA.

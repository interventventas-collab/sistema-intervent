# 📞 Llamadas de WhatsApp — Puesta en marcha

Guía del día que encendemos las llamadas de voz. Todo el código ya está hecho y probado
en desarrollo; acá quedan los pasos que faltan, que solo se pueden hacer "en la calle"
(servidor real, HTTPS y configuración en Meta).

**Regla de oro:** el interruptor "Permitir llamadas de voz" en Meta se prende **AL FINAL**,
recién en el paso 5. Si se prende antes, un cliente puede llamar y no atiende nadie.

---

## Qué ya está listo (hecho en desarrollo)

- Pantalla **📞 WhatsApp · Llamadas** (`/llamadas`): timbre, Atender / Rechazar / Colgar, historial.
- El sistema **recibe y registra** cada llamada (tabla `WhatsApp_Llamadas`).
- El "teléfono" del navegador (WebRTC) que arma el audio y le contesta a Meta.
- El servidor de audio **TURN** ya configurado en `docker-compose.prod.yml` (servicio `coturn`),
  apagado hasta que lo encendamos a propósito.

---

## Orden de encendido (los 5 pasos)

### Paso 1 — Publicar el sistema en producción
El usuario dice **"PUBLICAR EN PRODUCCIÓN"** y se sigue el flujo normal (merge `develop`→`master`
+ rebuild prod). Esto sube la pantalla y toda la cañería. Todavía no entra ninguna llamada
(el botón de Meta sigue apagado).

### Paso 2 — Avisarle a Meta que nos mande las llamadas (webhook)
Esto se hace en **Meta para Desarrolladores** (developers.facebook.com), no en el Administrador
de WhatsApp. En la app → WhatsApp → Configuración → Webhooks → **suscribir el campo `calls`**
(al lado de `messages`, que ya está suscripto). Es invisible para el cliente: solo hace que,
cuando llamen, el aviso llegue al sistema. Lo hace quien administra la app de Meta (Gabriel).

### Paso 3 — Completar los datos del servidor de audio en el `.env` de producción
En el `.env` del servidor, poner:

```
PUBLIC_IP=<la IP pública del servidor de DonWeb>
TURN_USERNAME=frikaf
TURN_CREDENTIAL=<una clave que elijas, larga>
TURN_URL=turn:<la misma IP pública>:3478
```

### Paso 4 — Abrir los puertos y encender el servidor de audio
En el panel de DonWeb / firewall, abrir: **3478/udp**, **3478/tcp** y el rango **49160-49200/udp**.
Después, encender solo el servidor de audio (no reinicia el resto):

```bash
docker compose -f docker-compose.prod.yml --profile turn up -d coturn
```

Y volver a levantar la API para que tome las variables TURN del `.env`:

```bash
docker compose -f docker-compose.prod.yml up -d api-prod
docker compose -f docker-compose.prod.yml restart web-prod
```

Comprobar que el servidor de audio quedó corriendo:

```bash
docker compose -f docker-compose.prod.yml ps coturn
```

### Paso 5 — Prender el botón en Meta y probar en el momento
1. Poner en el `.env` `WA_LLAMADAS_ENABLED=true` y `docker compose -f docker-compose.prod.yml up -d api-prod`.
2. En el **Administrador de WhatsApp** → número → **Configuración de llamadas** →
   prender **"Permitir llamadas de voz"**.
3. Abrir la pantalla **📞 WhatsApp · Llamadas** en el sitio real (https) con un operador mirando.
4. Desde un celular, **llamar al número por WhatsApp**. Debe sonar en la pantalla → Atender → hablar.

---

## Si algo no anda en la prueba

- **No suena en la pantalla:** falta el paso 2 (webhook `calls`) o el botón del paso 5.
  Revisar en el sistema la tabla `WhatsApp_Llamadas` (¿llegó el evento `connect`?).
- **Suena pero no hay audio:** casi siempre es el servidor de audio (paso 4): puertos cerrados,
  `PUBLIC_IP` mal, o `TURN_URL` no coincide con user/clave. Ver `docker compose ... logs coturn`.
- **El micrófono no arranca:** tiene que ser por **HTTPS** (en producción con Caddy ya lo es).
  En http común el navegador bloquea el micrófono.
- **Meta rechaza el "accept":** puede necesitar ajustar el par `pre_accept`/`accept` en
  `MetaWhatsAppService.SendCallActionAsync`. Es el único punto que no se pudo probar sin una
  llamada real; se afina en la primera prueba.

---

## Para apagar todo (si hace falta dar marcha atrás)

1. En Meta: apagar "Permitir llamadas de voz".
2. `WA_LLAMADAS_ENABLED=` (vacío) + `docker compose -f docker-compose.prod.yml up -d api-prod`.
3. `docker compose -f docker-compose.prod.yml stop coturn`.

Nada de esto toca los mensajes ni el resto del sistema.

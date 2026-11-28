# Cámaras en vivo sin EZVIZ: cómo se conecta un local

La PC de cada local (siempre prendida, en la misma red que las cámaras) se conecta al
servidor por un túnel privado (WireGuard). Cuando alguien abre una cámara en el sistema, el
servidor le pide el video a esa PC. Si nadie mira, no se gasta internet del local.

```
Cámara --(red del local)--> PC del local (go2rtc) ==túnel==> servidor (aiml-camaras-tunel) --> sistema
```

## Una sola vez, en el servidor

1. Firewall de DonWeb (panel → Cloud & IaaS → Gestionar → Firewall): dejar entrar **UDP 51820**.
   Sin esto el túnel no conecta (el `ufw` del servidor no importa).
2. Levantar: `docker compose -f camaras/docker-compose.yml --env-file .env up --build -d`
   (necesita `CAM_RTSP_PASS` en `.env`).

## Por cada local

1. Sumar el local en `LOCALES` de `camaras/docker-compose.yml` (`nombre:numero`; tostadero = 2)
   y sus cámaras en `camaras/conf/go2rtc.yaml`. Rebuild.
2. Sacar los archivos para la PC (tienen claves: NO commitear ni mandar a nadie):
   - `docker exec aiml-camaras-tunel cat /data/locales/<local>.conf` → `tunel_<local>.conf`
   - `go2rtc.yaml` del local, con la clave `CAM_RTSP_PASS` y una línea por cámara.
   - `instalar.bat`.

## En la PC del local (unos 10 minutos)

1. **Túnel**: bajar WireGuard de https://www.wireguard.com/install/ (Windows) e instalarlo.
   Abrirlo → *Agregar túnel* → *Importar túnel desde archivo* → elegir `tunel_<local>.conf`
   → **Activar**. Queda prendido solo aunque se reinicie la PC.
2. **Lector de cámaras**: bajar `go2rtc_win64.zip` de
   https://github.com/AlexxIT/go2rtc/releases (v1.9.15), descomprimir y dejar
   `C:\camaras\go2rtc.exe`. Copiar al lado `go2rtc.yaml`.
3. En `go2rtc.yaml` reemplazar en cada cámara `CODIGO` (el de 6 letras de la etiqueta) y
   `IP_DE_LA_CAMARA` (app EZVIZ → cámara → ⚙ → Información de red / Wi-Fi).
4. Clic derecho en `instalar.bat` → *Ejecutar como administrador*. Abre el permiso del
   firewall de Windows y deja go2rtc arrancando solo con la PC.
5. Probar en esa PC: http://localhost:1984 → tiene que aparecer la cámara y verse.

## En cada cámara (app EZVIZ)

Activar la salida directa **RTSP**: cámara → ⚙ Configuración → *Configuración de servicio
local* / *LAN* → RTSP encendido. En algunos modelos se hace desde EZVIZ Studio en la PC.
La clave de RTSP es el código de verificación de la cámara.

## Cómo saber si anda (en el servidor)

- Túnel: `docker exec aiml-camaras-tunel wg show` → el local tiene que tener *latest handshake*.
- Video: `curl -s http://127.0.0.1:1984/api/streams` y
  `curl -s -o /tmp/f.jpg "http://127.0.0.1:1984/api/frame.jpeg?src=frikaf"`.

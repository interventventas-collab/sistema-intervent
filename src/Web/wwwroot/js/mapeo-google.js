// 2026-07-24: Mapa de Mapeo Flex con GOOGLE MAPS real (reemplaza al de Leaflet/CARTO).
// Redefine window.mapeoFlex manteniendo el MISMO contrato que usaba Blazor:
//   init(elementId, dotnetHelper) -> arma el mapa (async: carga Google Maps JS con la clave del server)
//   renderMarkers(items)          -> dibuja los globitos (círculo=Flex, cuadrado=ME1, triángulo=otros)
//   focusOn(lat,lng,zoom) / destroy() / refit()
// Callbacks a Blazor: OnMarkerClicked(id), OnClusterClicked(ids), ToggleMarkerInRoute(id).
//
// También expone window.mapeoMini: un mini-mapa de PREVIEW (no interactivo) para la card del Dashboard.

// ══════════ Helpers compartidos (mapa grande + mini-mapa) ══════════
(function () {
    let googleReady = null;

    const ZONE_COLORS = [
        '#1d4ed8', '#16a34a', '#dc2626', '#9333ea', '#ea580c', '#0891b2',
        '#ca8a04', '#db2777', '#65a30d', '#7c3aed', '#0d9488', '#b91c1c'
    ];

    // La clave del navegador que trae ensureGoogle. La guardamos acá para poder pedir
    // también la fotito de Street View del domicilio cuando se abre un globito.
    let browserKey = null;

    // Carga la librería de Google Maps UNA sola vez (trae la clave del navegador desde el server).
    function ensureGoogle() {
        if (window.google && window.google.maps) return Promise.resolve();
        if (googleReady) return googleReady;
        googleReady = fetch('/api/mapeo/stops/map-key')
            .then(r => r.json())
            .then(cfg => new Promise((resolve, reject) => {
                const key = cfg && cfg.key;
                if (!key) { reject(new Error('Falta la clave del mapa (GOOGLE_MAPS_BROWSER_KEY).')); return; }
                browserKey = key; // la reutilizamos para la fotito de Street View
                window.__mapeoGmapsReady = function () { resolve(); };
                const s = document.createElement('script');
                s.src = 'https://maps.googleapis.com/maps/api/js?key=' + encodeURIComponent(key) + '&libraries=places,geometry&callback=__mapeoGmapsReady';
                s.async = true;
                s.defer = true;
                s.onerror = function () { reject(new Error('No se pudo cargar Google Maps.')); };
                document.head.appendChild(s);
            }))
            .catch(err => { googleReady = null; throw err; });
        return googleReady;
    }

    function escapeXml(s) {
        return ('' + s).replace(/[<>&'"]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;', "'": '&apos;', '"': '&quot;' }[c]));
    }

    // Geometría del pin "gota" estilo Google Maps (en unidades del viewBox del SVG).
    // La punta de la gota es la que apunta a la ubicación exacta (ahí va el anchor).
    const PIN_VB_W = 48, PIN_VB_H = 50;   // lienzo del SVG
    const PIN_TIP_X = 24, PIN_TIP_Y = 44; // punta de la gota
    const PIN_HEAD_CX = 24, PIN_HEAD_CY = 18; // centro de la cabeza redonda
    // Path de la gota (icono material "location_on" escalado x2): cabeza en (24,18), punta en (24,44).
    const PIN_PATH = 'M24 4C16.26 4 10 10.26 10 18c0 10.5 14 26 14 26s14-15.5 14-26c0-7.74-6.26-14-14-14z';

    // Construye el marcador como un pin gota estilo Google (data URI SVG).
    // Conserva el color del repartidor y el número/etiqueta adentro de la cabeza.
    function markerSvg(group) {
        const first = group[0];
        const extras = group.length - 1;
        const color = first.color || '#1d4ed8';
        const dimmed = first.dimmed === true;
        const inRoute = group.some(x => x.inRoute === true);
        const rawLabel = first.label || '';
        const label = escapeXml(rawLabel);

        // Cuerpo de la gota: lleno del color del repartidor (o blanco si está "dimmed").
        const body = dimmed ? '#ffffff' : color;
        const txt = dimmed ? '#111827' : '#ffffff';

        // Tamaño de texto según cuántos caracteres entran en la cabeza (ej: "12", "V1").
        const fs = rawLabel.length >= 2 ? 13 : 17;

        // FORMA de la cabeza del pin según el tipo de envío (la punta siempre cae en 24,44):
        //   circle=Flex/manual · square=ME1 · diamond=Alquiler · triangle=Venta por fuera.
        // El color sigue indicando repartidor/asignación; la forma indica el TIPO.
        const shape = first.shape || 'circle';
        let headPath, labelY;
        switch (shape) {
            case 'square':   // ME1 — cuadrado con puntita
                headPath = 'M13 4 H35 Q38 4 38 7 V27 Q38 30 35 30 H29 L24 44 L19 30 H13 Q10 30 10 27 V7 Q10 4 13 4 Z';
                labelY = 17;
                break;
            case 'diamond':  // Alquiler — rombo (cometa)
                headPath = 'M24 3 L39 19 L24 44 L9 19 Z';
                labelY = 15;
                break;
            case 'triangle': // Venta por fuera — triángulo apuntando a la ubicación
                headPath = 'M10 7 Q10 5 12 5 H36 Q38 5 38 7 L24 44 Z';
                labelY = 13;
                break;
            default:         // circle — Flex / manual / favorito (gota redonda de siempre)
                headPath = PIN_PATH;
                labelY = 18;
        }

        // Aro verde alrededor de la cabeza cuando la parada está seleccionada para la ruta.
        const ring = inRoute
            ? `<circle cx="${PIN_HEAD_CX}" cy="${PIN_HEAD_CY}" r="19" fill="none" stroke="#16a34a" stroke-width="3"/>`
            : '';

        // Badge rojo +N cuando hay varias entregas en el mismo domicilio.
        const badge = extras > 0
            ? `<circle cx="38" cy="7" r="8.5" fill="#dc2626" stroke="#ffffff" stroke-width="2"/>` +
              `<text x="38" y="10.5" text-anchor="middle" font-size="9" font-weight="800" fill="#ffffff" font-family="Inter,Arial,sans-serif">+${extras}</text>`
            : '';

        // Tilde verde ✓ arriba a la izquierda cuando MeLi confirmó la entrega.
        const delivered = group.some(x => x.delivered === true);
        const check = delivered
            ? `<circle cx="10" cy="8" r="8.5" fill="#16a34a" stroke="#ffffff" stroke-width="2"/>` +
              `<path d="M5.8 8.2 L8.6 11 L14 5.4" fill="none" stroke="#ffffff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>`
            : '';

        // Cartelito "OJO, calle no asfaltada" abajo a la izquierda del pin: para estar atentos
        // en ese envío sin abrir el globito. Marrón = tierra · gris piedra = empedrado.
        // Lo prende el pase de fondo que consulta el tipo de calle (first.surface).
        const surf = first.surface;
        const surfaceBadge = (surf === 'tierra' || surf === 'empedrado')
            ? `<circle cx="10" cy="30" r="8.5" fill="${surf === 'tierra' ? '#b45309' : '#57534e'}" stroke="#ffffff" stroke-width="2"/>` +
              `<text x="10" y="34" text-anchor="middle" font-size="12" font-weight="900" fill="#ffffff" font-family="Inter,Arial,sans-serif">!</text>`
            : '';

        return `<svg xmlns="http://www.w3.org/2000/svg" width="${PIN_VB_W}" height="${PIN_VB_H}" viewBox="0 0 ${PIN_VB_W} ${PIN_VB_H}">` +
            `<defs><filter id="sh" x="-40%" y="-40%" width="180%" height="180%"><feDropShadow dx="0" dy="1.5" stdDeviation="1.5" flood-opacity="0.4"/></filter></defs>` +
            `${ring}` +
            `<path d="${headPath}" fill="${body}" stroke="#ffffff" stroke-width="2" filter="url(#sh)"/>` +
            `<text x="${PIN_HEAD_CX}" y="${labelY + fs * 0.35}" text-anchor="middle" font-size="${fs}" font-weight="800" fill="${txt}" font-family="Inter,Arial,sans-serif">${label}</text>` +
            `${badge}${check}${surfaceBadge}</svg>`;
    }

    // Arma el icono para Google Maps a partir del SVG. dispH = alto deseado en px;
    // el ancho se ajusta solo para mantener la proporción, y el anchor cae en la punta de la gota.
    function markerIcon(svg, dispH) {
        const dispW = dispH * PIN_VB_W / PIN_VB_H;
        return {
            url: 'data:image/svg+xml;charset=UTF-8,' + encodeURIComponent(svg),
            scaledSize: new google.maps.Size(dispW, dispH),
            anchor: new google.maps.Point(dispW * PIN_TIP_X / PIN_VB_W, dispH * PIN_TIP_Y / PIN_VB_H)
        };
    }

    // ══════════ Fotito de Street View para el globito ══════════
    // Cuando se abre un globito de un domicilio, le agregamos arriba una miniatura de
    // "cómo se ve la calle" en esa dirección (Street View estático de Google). Primero
    // consultamos el metadata (gratis) para saber si hay foto: si no hay, no mostramos
    // nada (así nunca aparece el cartel gris de "sin imágenes").
    // Cada apertura de globito incrementa svSeq: si el usuario abre otro antes de que
    // llegue la foto, la respuesta vieja se descarta y no pisa el globito nuevo.
    let svSeq = 0;

    // Bumpea el contador para cancelar cualquier foto de Street View en vuelo.
    function cancelStreetView() { svSeq++; }

    // Cartelito de TIPO DE CALLE según lo que dedujo la IA (asfalto/tierra/empedrado).
    // Solo se muestra cuando hay una respuesta clara; en 'no_seguro'/'sin_foto' no ensucia.
    function surfaceChipHtml(tipo) {
        let emoji, label, bg, fg;
        switch (tipo) {
            case 'asfalto':   emoji = '🛣️'; label = 'Asfalto';   bg = '#e5e7eb'; fg = '#374151'; break;
            case 'tierra':    emoji = '🟤'; label = 'Tierra';    bg = '#fdead0'; fg = '#92400e'; break;
            case 'empedrado': emoji = '🧱'; label = 'Empedrado'; bg = '#e7e0d8'; fg = '#78350f'; break;
            default: return ''; // no_seguro / sin_foto → no mostramos nada
        }
        return '<div style="display:inline-flex;align-items:center;gap:4px;margin:0 0 7px;'
            + 'padding:2px 9px;border-radius:999px;font-size:0.72rem;font-weight:800;'
            + 'background:' + bg + ';color:' + fg + ';font-family:Inter,Arial,sans-serif;" '
            + 'title="Tipo de calle deducido de la foto de Street View">'
            + emoji + ' ' + label + '</div>';
    }

    // iw = InfoWindow ya abierto; baseHtml = el contenido actual (sin foto ni cartelito).
    // enabled=false solo cancela pedidos viejos (para clusters o el punto de partida).
    // Trae DOS cosas en paralelo y las va mostrando arriba del contenido a medida que llegan:
    //   1) la fotito de Street View de la fachada
    //   2) el cartelito de tipo de calle (lo deduce el servidor con IA, cacheado por domicilio)
    // El guard svSeq descarta respuestas viejas si se abrió otro globito mientras tanto.
    function streetView(iw, lat, lng, baseHtml, enabled) {
        const myId = ++svSeq;
        if (!enabled || lat == null || lng == null) return;
        const loc = (+lat).toFixed(6) + ',' + (+lng).toFixed(6);
        const slot = { chip: '', thumb: '' };
        const render = function () { if (myId === svSeq) iw.setContent(slot.chip + slot.thumb + baseHtml); };

        // 1) Fotito de Street View (necesita la clave del navegador + metadata).
        if (browserKey) {
            const metaUrl = 'https://maps.googleapis.com/maps/api/streetview/metadata?location='
                + loc + '&source=outdoor&key=' + encodeURIComponent(browserKey);
            fetch(metaUrl)
                .then(r => r.json())
                .then(meta => {
                    if (myId !== svSeq) return;
                    if (!meta || meta.status !== 'OK') return;
                    // Pedimos la foto al DOBLE de tamaño (600x340) y la mostramos a ~150px de alto:
                    // al bajarla queda mucho más nítida que antes (que se veía borrosa al agrandarla).
                    const imgUrl = 'https://maps.googleapis.com/maps/api/streetview?size=600x340&location='
                        + loc + '&fov=80&source=outdoor&key=' + encodeURIComponent(browserKey);
                    const panoUrl = 'https://www.google.com/maps/@?api=1&map_action=pano&viewpoint=' + loc;
                    slot.thumb = '<a href="' + panoUrl + '" target="_blank" rel="noopener" '
                        + 'title="Ver la calle en Google Street View" '
                        + 'style="display:block;margin:0 0 8px;border-radius:8px;overflow:hidden;'
                        + 'border:1px solid #e5e7eb;line-height:0;">'
                        + '<img src="' + imgUrl + '" alt="Vista de la calle en el domicilio" '
                        + 'style="width:100%;height:150px;object-fit:cover;display:block;"/></a>';
                    render();
                })
                .catch(function () { });
        }

        // 2) Cartelito de tipo de calle (lo calcula el servidor con IA; queda cacheado).
        fetch('/api/mapeo/stops/surface?lat=' + encodeURIComponent(loc.split(',')[0])
                + '&lng=' + encodeURIComponent(loc.split(',')[1]), { credentials: 'same-origin' })
            .then(r => r.ok ? r.json() : null)
            .then(s => {
                if (myId !== svSeq || !s) return;
                const chip = surfaceChipHtml(s.tipo);
                if (chip) { slot.chip = chip; render(); }
            })
            .catch(function () { });
    }

    // Exponer helpers al scope de este archivo (los dos módulos de abajo los usan).
    window.__mapeoHelpers = { ensureGoogle, ZONE_COLORS, escapeXml, markerSvg, markerIcon, streetView, cancelStreetView };
})();

// ══════════ Mapa grande (pantalla Mapeo) ══════════
window.mapeoFlex = (function () {
    const H = window.__mapeoHelpers;
    let map = null;
    let markers = [];
    let snapMarkers = [];         // pines del histórico (foto de un día anterior) — modo solo mirar
    let infoWindow = null;
    let infoOpen = false;         // ¿hay un globito (popup) abierto? el refresco automático no lo pisa
    let dotNetRef = null;
    let zonePolygon = null;       // polígono que el usuario dibuja a mano (esquina por esquina) — el relleno del área
    let zoneLine = null;          // línea que UNE los puntos mientras dibujás (la que se ve trazándose)
    let zonePath = null;          // lista de puntos (MVCArray) del polígono — la manejamos nosotros
    let zoneClickListener = null; // listener de clicks del mapa mientras dibuja
    let zoneVertexMarkers = [];   // puntitos que se ven en cada esquina tocada (feedback visual)
    let routeLines = [];          // líneas de ruta dibujadas (una por repartidor)
    let trafficLayer = null;      // capa de tráfico de Google (rojo/amarillo/verde en las calles)
    let lastFitStops = -1; // cuántas paradas (sin contar el punto de partida) había en el último auto-encuadre

    // ── Cartelito de "calle no asfaltada" en los pines (tierra/empedrado) ──
    // Al dibujar los pines lanzamos un pase de fondo que le pregunta al servidor el tipo de
    // calle de cada domicilio; cuando es tierra o empedrado, le agregamos el cartelito ! al pin.
    // Así el usuario ve DE UN VISTAZO cuáles envíos van por calle no asfaltada, sin abrir el globito.
    const surfaceMem = {};        // "lat,lng" -> tipo (memoria de la sesión, para no repreguntar)
    let surfaceQueue = [];        // pines pendientes de consultar { marker, group, loc, ver }
    let surfaceActive = 0;        // consultas en vuelo (limitamos cuántas a la vez)
    let markersVersion = 0;       // sube en cada renderMarkers: descarta pines de un dibujo viejo
    const SURFACE_MAX_PARALELO = 4;

    // Le pone (o saca) el cartelito al pin según el tipo de calle que llegó.
    function applySurfaceBadge(job, tipo) {
        if (job.ver !== markersVersion) return; // se redibujó el mapa: este pin ya no existe
        if (tipo !== 'tierra' && tipo !== 'empedrado') return;
        job.group.forEach(g => { g.surface = tipo; });
        try { job.marker.setIcon(H.markerIcon(H.markerSvg(job.group), 50)); } catch (e) {}
    }

    // Procesa la cola de consultas de tipo de calle respetando el límite de paralelo.
    function pumpSurface() {
        while (surfaceActive < SURFACE_MAX_PARALELO && surfaceQueue.length) {
            const job = surfaceQueue.shift();
            if (job.ver !== markersVersion) continue; // ya viejo
            // Si ya lo sabemos de esta sesión, aplicamos sin volver a pedir.
            if (Object.prototype.hasOwnProperty.call(surfaceMem, job.loc)) {
                applySurfaceBadge(job, surfaceMem[job.loc]);
                continue;
            }
            surfaceActive++;
            const parts = job.loc.split(',');
            fetch('/api/mapeo/stops/surface?lat=' + encodeURIComponent(parts[0]) + '&lng=' + encodeURIComponent(parts[1]),
                { credentials: 'same-origin' })
                .then(r => r.ok ? r.json() : null)
                .then(s => {
                    const t = (s && s.tipo) ? s.tipo : 'no_seguro';
                    surfaceMem[job.loc] = t;
                    applySurfaceBadge(job, t);
                })
                .catch(function () { })
                .finally(function () { surfaceActive--; pumpSurface(); });
        }
    }
    // "Ver dirección al tocar" (geocodificación inversa): cuando está encendido, un click en el
    // mapa muestra la calle+número más cercanos. Es un modo que se prende/apaga desde el buscador.
    let reverseGeoMode = false;
    let reverseGeoListener = null;
    let lastReverseAddr = '';

    // Limpia el estado del dibujo de zona (saca el polígono, los puntitos, el listener y el cursor).
    function cleanupZone() {
        if (zoneClickListener) { google.maps.event.removeListener(zoneClickListener); zoneClickListener = null; }
        if (zonePolygon) { zonePolygon.setMap(null); zonePolygon = null; }
        if (zoneLine) { zoneLine.setMap(null); zoneLine = null; }
        zonePath = null;
        for (const m of zoneVertexMarkers) m.setMap(null);
        zoneVertexMarkers = [];
        if (map) map.setOptions({ draggableCursor: null });
    }

    // Vista por defecto: todo el AMBA (CABA + conurbano + La Plata), como pidió el usuario.
    const AMBA_CENTER = { lat: -34.72, lng: -58.52 };
    const AMBA_ZOOM = 10;

    // ── "Qué compró" + "Mensajes de la venta" dentro del globito (solo paradas Flex/ME1) ──
    // El globito trae un cajón vacío <div class="mapeo-vinfo" data-ship="{nºenvío}">. Al abrirse el
    // globito (evento domready) pedimos /api/mapeo/stops/venta-info y lo llenamos. Guardamos el
    // resultado por envío (vinfoStore) para no volver a pedirlo si el globito se re-dibuja (p. ej.
    // cuando entra la fotito de Street View, que re-setea el contenido y vuelve a disparar domready).
    const vinfoStore = {};                 // ship -> { status:'loading'|'done', html }
    const VINFO_LOADING = "<div style='color:#94a3b8;font-size:0.72rem;margin-top:0.4rem;'>Buscando la venta…</div>";

    function buildVentaHtml(data) {
        if (!data || !data.ok) return '';
        let html = '';
        const prods = data.productos || [];
        if (prods.length) {
            html += "<div style='margin-top:0.45rem;border-top:1px solid #e5e7eb;padding-top:0.4rem;'>";
            html += "<div style='font-weight:700;font-size:0.72rem;color:#334155;margin-bottom:0.3rem;'>🛒 Qué compró</div>";
            for (const p of prods) {
                const img = p.thumbnail
                    ? "<img src='" + H.escapeXml(p.thumbnail) + "' style='width:34px;height:34px;object-fit:cover;border-radius:5px;flex:0 0 auto;border:1px solid #e5e7eb;'/>"
                    : "";
                const cant = p.cantidad ? "<span style='font-weight:700;'>×" + p.cantidad + "</span> " : "";
                html += "<div style='display:flex;gap:0.4rem;align-items:center;margin-bottom:0.3rem;'>" + img
                     + "<span style='font-size:0.72rem;line-height:1.25;'>" + cant + H.escapeXml(p.titulo || '') + "</span></div>";
            }
            html += "</div>";
        }
        const msgs = data.mensajes || [];
        if (msgs.length) {
            html += "<div style='margin-top:0.45rem;border-top:1px solid #e5e7eb;padding-top:0.4rem;'>";
            html += "<div style='font-weight:700;font-size:0.72rem;color:#334155;margin-bottom:0.3rem;'>📩 Mensajes de la venta</div>";
            for (const m of msgs) {
                const esComp = m.de === 'comprador';
                const quien = esComp ? '🛒 Comprador' : '🏪 Vos';
                const bg = esComp ? '#eff6ff' : '#f0fdf4';
                html += "<div style='background:" + bg + ";border-radius:6px;padding:0.3rem 0.45rem;margin-bottom:0.25rem;font-size:0.72rem;line-height:1.3;'>"
                     + "<span style='font-weight:700;'>" + quien + ":</span> " + H.escapeXml(m.texto || '') + "</div>";
            }
            html += "</div>";
        }
        return html;
    }

    // Busca todos los cajones .mapeo-vinfo del globito abierto y los llena (con caché por envío).
    function fillVentaInfo() {
        const nodes = document.querySelectorAll('.mapeo-vinfo[data-ship]');
        nodes.forEach(node => {
            const ship = node.getAttribute('data-ship');
            if (!ship || ship === '0') return;
            const cached = vinfoStore[ship];
            if (cached && cached.status === 'done') { node.innerHTML = cached.html; return; }
            if (cached && cached.status === 'loading') { node.innerHTML = VINFO_LOADING; return; }
            vinfoStore[ship] = { status: 'loading', html: '' };
            node.innerHTML = VINFO_LOADING;
            fetch('/api/mapeo/stops/venta-info?shipmentId=' + encodeURIComponent(ship), { credentials: 'same-origin' })
                .then(r => r.ok ? r.json() : null)
                .then(data => {
                    const html = buildVentaHtml(data);
                    vinfoStore[ship] = { status: 'done', html };
                    // El globito puede haberse re-dibujado mientras tanto: buscamos el cajón actual.
                    const n = document.querySelector(".mapeo-vinfo[data-ship='" + ship + "']");
                    if (n) n.innerHTML = html;
                })
                .catch(() => {
                    vinfoStore[ship] = { status: 'done', html: '' };
                    const n = document.querySelector(".mapeo-vinfo[data-ship='" + ship + "']");
                    if (n) n.innerHTML = '';
                });
        });
    }

    function loadZones() {
        if (!map) return;
        let zi = 0;
        map.data.addListener('addfeature', e => {
            e.feature.setProperty('_c', H.ZONE_COLORS[(zi++) % H.ZONE_COLORS.length]);
        });
        map.data.loadGeoJson('/data/amba-zonas.geojson');
        map.data.setStyle(f => ({
            fillColor: f.getProperty('_c') || '#93c5fd',
            fillOpacity: 0.18,
            strokeColor: '#9ca3af',
            strokeWeight: 1,
            strokeOpacity: 0.6,
            clickable: false
        }));
    }

    return {
        async init(elementId, dotnetHelper) {
            dotNetRef = dotnetHelper;
            window.mapeoActions = {
                toggleRoute: function (markerId) {
                    if (dotNetRef) dotNetRef.invokeMethodAsync('ToggleMarkerInRoute', markerId);
                },
                // 2026-07-29: cambiar/asignar el repartidor desde el globito del pin.
                assignDriver: function (markerId, driverId) {
                    if (dotNetRef) dotNetRef.invokeMethodAsync('AsignarRepartidorDesdePopup', markerId, parseInt(driverId, 10) || 0);
                },
                // 2026-08-06: mandar el envío a una zona desde el globito. 0 = sin zona, -1 = nueva zona.
                assignZone: function (markerId, slot) {
                    var n = parseInt(slot, 10);
                    if (isNaN(n)) return;
                    if (infoWindow) { infoWindow.close(); infoOpen = false; }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('AsignarZonaDesdePopup', markerId, n);
                },
                // 2026-07-30: corregir/mover la ubicación de este pin desde el globito.
                // Cerramos el globito primero: el modo edición redibuja SOLO ese pin con
                // keepView, y renderMarkers ignora el redibujo si hay un globito abierto.
                correctLocation: function (markerId) {
                    if (infoWindow) { infoWindow.close(); infoOpen = false; }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('CorregirUbicacionDesdePopup', markerId);
                },
                // 2026-07-30: quitar esta parada del mapa desde el globito.
                removeStop: function (markerId) {
                    if (infoWindow) { infoWindow.close(); infoOpen = false; }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('EliminarParadaDesdePopup', markerId);
                },
                // 2026-08-04: poner esta parada en un puesto puntual de la ruta y correr las demás.
                // Cerramos el globito antes de recalcular así renderMarkers puede redibujar los números.
                setOrder: function (markerId, pos) {
                    var n = parseInt(pos, 10);
                    if (!n || n < 1) { alert('Escribí un número de puesto (1, 2, 3…).'); return; }
                    if (infoWindow) { infoWindow.close(); infoOpen = false; }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('PonerEnPuestoDesdePopup', markerId, n);
                },
                // "Ver dirección al tocar": copia al portapapeles la dirección del globito.
                copyReverseAddress: function (btn) {
                    if (!lastReverseAddr) return;
                    try { navigator.clipboard.writeText(lastReverseAddr); } catch (e) {}
                    if (btn) { btn.textContent = '✓ Copiado'; btn.disabled = true; btn.style.background = '#16a34a'; }
                }
            };
            try {
                await H.ensureGoogle();
            } catch (e) {
                const el = document.getElementById(elementId);
                if (el) el.innerHTML = '<div style="padding:1rem;color:#b91c1c;font-family:Inter,sans-serif;font-size:0.9rem;">No se pudo cargar el mapa de Google: ' + (e && e.message ? e.message : e) + '</div>';
                return;
            }
            const el = document.getElementById(elementId);
            if (!el) return;

            map = new google.maps.Map(el, {
                center: AMBA_CENTER,   // arranca mostrando todo el AMBA
                zoom: AMBA_ZOOM,
                gestureHandling: 'greedy',   // arrastrar con un dedo en el celu
                clickableIcons: false,       // no abrir fichas de comercios de Google
                mapTypeControl: true,        // permite cambiar a satélite
                streetViewControl: true,
                fullscreenControl: true,
                zoomControl: true
            });
            infoWindow = new google.maps.InfoWindow();
            infoWindow.addListener('closeclick', () => { infoOpen = false; });
            // Cada vez que el globito muestra su contenido (incluye el re-dibujo por la fotito de
            // Street View), llenamos el cajón "qué compró + mensajes" de las paradas Flex/ME1.
            infoWindow.addListener('domready', () => fillVentaInfo());
            markers = [];
            lastFitStops = -1;
            // Capa de tráfico: arranca APAGADA (como el Google Maps original). Se prende/apaga
            // con el botón del dock (setTraffic). Rojo=congestionado, amarillo=lento, verde=fluido.
            // NOTA: no cargamos el overlay de zonas AMBA (loadZones) — el usuario quiere el mapa
            // lo más parecido posible al Google Maps original, sin los tonos de colores encima.
        },

        // keepView = true: redibuja los globitos SIN mover el mapa (ni zoom ni centro).
        // Se usa en el refresco automático, para que los envíos escaneados aparezcan solos
        // sin saltarte la vista mientras estás mirando una zona.
        renderMarkers(items, keepView) {
            if (!map || !window.google) return;
            // Refresco automático con un globito abierto: no lo pisamos, lo dejamos como está.
            if (keepView && infoOpen) return;
            for (const m of markers) m.setMap(null);
            markers = [];
            this.clearRoutes(); // las líneas viejas se borran; se redibujan al optimizar

            // Nuevo dibujo: descartamos las consultas de tipo de calle del dibujo anterior.
            markersVersion++;
            surfaceQueue = [];

            const groups = new Map();
            for (const it of items) {
                if (it.lat == null || it.lng == null) continue;
                const key = `${(+it.lat).toFixed(5)},${(+it.lng).toFixed(5)}`;
                if (!groups.has(key)) groups.set(key, []);
                groups.get(key).push(it);
            }

            const bounds = new google.maps.LatLngBounds();
            let any = false;
            let realStops = 0; // paradas de verdad (sin contar el punto de partida)

            for (const group of groups.values()) {
                const first = group[0];
                const pos = { lat: +first.lat, lng: +first.lng };
                const esArrastrable = first.draggable === true;
                const marker = new google.maps.Marker({
                    position: pos,
                    map: map,
                    icon: H.markerIcon(H.markerSvg(group), 50),
                    draggable: esArrastrable,
                    title: esArrastrable ? 'Arrastrame para ajustar el punto de partida' : undefined,
                    zIndex: group.some(g => g.inRoute) ? 1000 : (esArrastrable ? 900 : 1)
                });

                // Arrastrable: el punto de partida (casita) o una parada con el candado abierto.
                // Al soltar, avisamos a Blazor: si tiene stopId es una parada, si no es el punto de partida.
                if (esArrastrable) {
                    const draggedStopId = (first.stopId != null) ? first.stopId : null;
                    marker.addListener('dragend', function (e) {
                        const la = e.latLng.lat(), ln = e.latLng.lng();
                        if (!dotNetRef) return;
                        if (draggedStopId != null) dotNetRef.invokeMethodAsync('OnStopDragged', draggedStopId, la, ln);
                        else dotNetRef.invokeMethodAsync('OnStartPointDragged', la, ln);
                    });
                }

                let popupHtml;
                if (group.length === 1) {
                    popupHtml = first.popupHtml || '';
                } else {
                    const header = `<div style="font-size:0.78rem;font-weight:800;color:#dc2626;margin-bottom:0.4rem;padding-bottom:0.3rem;border-bottom:1px solid #fecaca;">⚠ ${group.length} entregas en este domicilio</div>`;
                    const list = group.map((g, idx) =>
                        `<div style="${idx > 0 ? 'border-top:1px dashed #e5e7eb;margin-top:0.4rem;padding-top:0.4rem;' : ''}">${g.popupHtml || ''}</div>`
                    ).join('');
                    popupHtml = `<div style="max-width:280px;max-height:280px;overflow-y:auto;">${header}${list}</div>`;
                }

                const ids = group.map(g => g.id);
                const isCluster = group.length > 1;
                // Mostramos la fotito de Street View solo en un domicilio único (no en clusters
                // ni en la casita del punto de partida, que se puede arrastrar).
                const conStreetView = !isCluster && !esArrastrable;
                marker.addListener('click', () => {
                    if (infoWindow) {
                        infoWindow.setContent(popupHtml);
                        infoWindow.open(map, marker);
                        infoOpen = true;
                        H.streetView(infoWindow, first.lat, first.lng, popupHtml, conStreetView);
                    }
                    if (!dotNetRef) return;
                    if (isCluster) dotNetRef.invokeMethodAsync('OnClusterClicked', ids);
                    else dotNetRef.invokeMethodAsync('OnMarkerClicked', first.id);
                });

                markers.push(marker);
                bounds.extend(pos);
                any = true;
                if (!esArrastrable) realStops++;

                // Encolamos este domicilio único para consultar su tipo de calle en el pase de fondo.
                if (conStreetView && first.lat != null && first.lng != null) {
                    surfaceQueue.push({
                        marker: marker, group: group,
                        loc: (+first.lat).toFixed(6) + ',' + (+first.lng).toFixed(6),
                        ver: markersVersion
                    });
                }
            }

            // Arranca el pase de fondo que le pone el cartelito a los pines de tierra/empedrado.
            pumpSurface();

            // Encuadre inteligente:
            //  - Sin paradas: mostramos TODO el AMBA (aunque haya punto de partida).
            //  - Con paradas: encuadramos para que entren todas + el punto de partida.
            //    Solo reajustamos cuando ENTRÓ una parada nueva (no al asignar/tocar), para no
            //    pisarle el zoom al usuario. Al soltar la casita tampoco reencuadra.
            // Refresco automático: NO tocamos la vista, solo registramos el conteo.
            if (keepView) { lastFitStops = realStops; return; }
            if (realStops === 0) {
                map.setCenter(AMBA_CENTER);
                map.setZoom(AMBA_ZOOM);
            } else if (realStops > lastFitStops) {
                if (markers.length === 1) {
                    // un solo punto en total: fitBounds haría un zoom exagerado
                    map.setCenter(bounds.getCenter());
                    map.setZoom(15);
                } else {
                    map.fitBounds(bounds, 60);
                }
            }
            lastFitStops = realStops;
        },

        // Prende/apaga la capa de tráfico (embotellamientos). La creamos recién la primera vez.
        setTraffic(on) {
            if (!map || !window.google) return;
            if (on) {
                try {
                    if (!trafficLayer) trafficLayer = new google.maps.TrafficLayer();
                    trafficLayer.setMap(map);
                } catch (e) {}
            } else if (trafficLayer) {
                trafficLayer.setMap(null);
            }
        },

        focusOn(lat, lng, zoom) {
            if (!map) return;
            map.setCenter({ lat: +lat, lng: +lng });
            map.setZoom(zoom || 16);
            if (infoWindow) infoWindow.close();
            infoOpen = false;
        },

        // Buscador estilo Google Maps: engancha un Autocomplete de Places al input dado.
        // Cuando el usuario elige una dirección, centra el mapa y avisa a Blazor (OnPlacePicked)
        // para que cree una parada ahí. Restringido a Argentina.
        attachSearch(inputId) {
            if (!map || !google.maps.places) return;
            const input = document.getElementById(inputId);
            if (!input || input.dataset.acAttached === '1') return;
            input.dataset.acAttached = '1';
            const ac = new google.maps.places.Autocomplete(input, {
                fields: ['geometry', 'formatted_address', 'name'],
                componentRestrictions: { country: 'ar' }
            });
            ac.bindTo('bounds', map); // sesga las sugerencias a lo que se ve en el mapa
            ac.addListener('place_changed', () => {
                const place = ac.getPlace();
                if (!place || !place.geometry || !place.geometry.location) return;
                const lat = place.geometry.location.lat();
                const lng = place.geometry.location.lng();
                const addr = place.formatted_address || place.name || input.value;
                map.setCenter({ lat: lat, lng: lng });
                map.setZoom(16);
                if (dotNetRef) dotNetRef.invokeMethodAsync('OnPlacePicked', lat, lng, addr);
                input.value = '';
            });
        },

        // "Ver dirección al tocar" (geocodificación inversa). Mientras está ENCENDIDO, tocar
        // cualquier punto del mapa abre un globito con la calle+número más cercanos y un botón
        // para copiar. No pisa el dibujo de zonas (si estás dibujando, gana el dibujo) ni los
        // clicks sobre pines (esos abren su propio globito). Se apaga tocando de nuevo el botón.
        setReverseGeoMode(on) {
            reverseGeoMode = !!on;
            if (reverseGeoListener) { google.maps.event.removeListener(reverseGeoListener); reverseGeoListener = null; }
            if (!map) return;
            if (!reverseGeoMode) {
                map.setOptions({ draggableCursor: null });
                if (infoWindow) { infoWindow.close(); infoOpen = false; }
                return;
            }
            map.setOptions({ draggableCursor: 'help' }); // cursor con "?" = modo "¿qué dirección es?"
            reverseGeoListener = map.addListener('click', function (e) {
                if (zonePath) return; // si estás dibujando una zona, no interferimos
                const ll = e.latLng;
                const lat = ll.lat(), lng = ll.lng();
                H.cancelStreetView(); // por si venía una foto en vuelo de otro globito
                // Globito "Buscando…" inmediato, para que se sienta que registró el toque.
                infoWindow.setContent('<div style="font-family:Inter,sans-serif;font-size:0.85rem;color:#6b7280;padding:2px 4px;">Buscando dirección…</div>');
                infoWindow.setPosition(ll);
                infoWindow.open(map);
                infoOpen = true;
                // La geocodificación inversa la hace el SERVIDOR (clave con Geocoding API habilitada).
                fetch('/api/mapeo/stops/reverse-geocode?lat=' + lat + '&lng=' + lng, { credentials: 'same-origin' })
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (data) {
                        const addr = data && data.address ? data.address : null;
                        lastReverseAddr = addr || '';
                        const shown = addr || 'No se encontró una dirección para este punto.';
                        let html = '<div style="font-family:Inter,sans-serif;min-width:190px;max-width:270px;">' +
                            '<div style="font-size:0.66rem;color:#6b7280;font-weight:700;margin-bottom:2px;letter-spacing:.03em;">📍 DIRECCIÓN APROXIMADA</div>' +
                            '<div style="font-size:0.92rem;color:#111827;font-weight:600;margin-bottom:7px;line-height:1.3;">' + H.escapeXml(shown) + '</div>';
                        if (addr) {
                            html += '<button onclick="window.mapeoActions.copyReverseAddress(this)" ' +
                                'style="font-size:0.78rem;padding:4px 11px;background:#2563eb;color:#fff;border:none;border-radius:6px;cursor:pointer;font-weight:600;">📋 Copiar</button>';
                        }
                        html += '</div>';
                        infoWindow.setContent(html);
                    })
                    .catch(function () {
                        infoWindow.setContent('<div style="font-family:Inter,sans-serif;font-size:0.85rem;color:#b91c1c;padding:2px 4px;">No se pudo consultar la dirección.</div>');
                    });
            });
        },

        // Dibujar una ZONA a mano: el usuario toca cada esquina en el mapa y se va armando un polígono.
        // (No usamos DrawingManager porque Google lo deprecó/quitó en v3.65+.)
        startDrawZone() {
            if (!map) return;
            cleanupZone();
            map.setOptions({ draggableCursor: 'crosshair' }); // cursor de cruz = estás dibujando
            zonePath = new google.maps.MVCArray();
            // Línea gruesa que UNE los puntos a medida que tocás. Es lo ÚNICO que dibujamos mientras
            // se marca (nada de polígono: compartir la lista de puntos con un polígono rompía el trazo).
            // El área encerrada la calcula el backend al Terminar (ray-casting sobre estos puntos).
            zoneLine = new google.maps.Polyline({
                map: map, path: zonePath,
                strokeColor: '#dc2626', strokeOpacity: 0.95, strokeWeight: 4,
                clickable: false, zIndex: 2
            });
            zoneClickListener = map.addListener('click', function (e) {
                if (!zonePath) return;
                zonePath.push(e.latLng);
                // Puntito visible en cada esquina, para que se vea que registró el toque.
                zoneVertexMarkers.push(new google.maps.Marker({
                    position: e.latLng, map: map, clickable: false, zIndex: 2,
                    icon: {
                        path: google.maps.SymbolPath.CIRCLE, scale: 6,
                        fillColor: '#dc2626', fillOpacity: 1, strokeColor: '#ffffff', strokeWeight: 2
                    }
                }));
            });
        },

        // Terminar la zona: junta las esquinas y se las manda a Blazor (OnZoneDrawn).
        finishDrawZone() {
            const path = (zonePath ? zonePath.getArray() : []).map(p => [p.lat(), p.lng()]);
            cleanupZone();
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnZoneDrawn', path);
        },

        // Deshacer el último punto marcado (saca la última esquina y su puntito).
        undoLastZonePoint() {
            if (!zonePath || zonePath.getLength() === 0) return;
            zonePath.pop();
            const m = zoneVertexMarkers.pop();
            if (m) m.setMap(null);
        },

        // Cancelar el dibujo sin asignar nada.
        cancelDrawZone() { cleanupZone(); },

        // Dibuja las líneas de ruta (una por repartidor). routes = [{color, segments:[encoded...]}].
        // Compatibilidad: también acepta {color, encoded} (una sola línea).
        drawRoutes(routes) {
            this.clearRoutes();
            if (!map || !routes || !google.maps.geometry) return;
            // Flechita de sentido que se repite a lo largo de la línea (para qué lado va el recorrido).
            const flecha = {
                path: google.maps.SymbolPath.FORWARD_CLOSED_ARROW,
                scale: 2.6, strokeColor: '#ffffff', strokeWeight: 1.2, fillOpacity: 1
            };
            for (const r of routes) {
                if (!r) continue;
                const color = r.color || '#1d4ed8';
                // Normalizamos: una ruta puede venir en varios tramos (rutas de >25 paradas).
                const encodeds = (r.segments && r.segments.length) ? r.segments : (r.encoded ? [r.encoded] : []);
                for (const enc of encodeds) {
                    if (!enc) continue;
                    const path = google.maps.geometry.encoding.decodePath(enc);
                    // Casing (borde blanco) por debajo, como la ruta azul de Google Maps:
                    // hace que la línea de color resalte sobre las calles y el tráfico.
                    routeLines.push(new google.maps.Polyline({
                        path: path, map: map,
                        strokeColor: '#ffffff', strokeOpacity: 0.9, strokeWeight: 9, zIndex: 4
                    }));
                    routeLines.push(new google.maps.Polyline({
                        path: path, map: map,
                        strokeColor: color, strokeOpacity: 0.95, strokeWeight: 5, zIndex: 5,
                        // Flechas blancas cada ~110px indicando el sentido de circulación.
                        icons: [{ icon: Object.assign({}, flecha, { fillColor: color }), offset: '0', repeat: '110px' }]
                    }));
                }
            }
        },

        clearRoutes() {
            for (const l of routeLines) l.setMap(null);
            routeLines = [];
        },

        // ── Ver una FOTO (snapshot) de un día anterior sobre el mapa (modo SOLO MIRAR) ──
        // items = [{lat,lng,label,color,shape,delivered,popupHtml}]. Oculta los pines de HOY,
        // dibuja los del histórico (no arrastrables, sin botones de ruta) y encuadra todo.
        showSnapshot(items) {
            if (!map || !window.google) return;
            // Escondemos lo de hoy (paradas + rutas) para no mezclar.
            for (const m of markers) m.setMap(null);
            this.clearRoutes();
            this.clearSnapshot();
            if (infoWindow) { infoWindow.close(); infoOpen = false; }

            const bounds = new google.maps.LatLngBounds();
            let any = false;
            for (const it of (items || [])) {
                if (it.lat == null || it.lng == null) continue;
                const pos = { lat: +it.lat, lng: +it.lng };
                const marker = new google.maps.Marker({
                    position: pos, map: map,
                    icon: H.markerIcon(H.markerSvg([it]), 50),
                    draggable: false, zIndex: 1
                });
                const html = it.popupHtml || '';
                const svLat = it.lat, svLng = it.lng;
                marker.addListener('click', () => {
                    if (infoWindow) {
                        infoWindow.setContent(html);
                        infoWindow.open(map, marker);
                        infoOpen = true;
                        H.streetView(infoWindow, svLat, svLng, html, true);
                    }
                });
                snapMarkers.push(marker);
                bounds.extend(pos);
                any = true;
            }
            if (any) {
                if (snapMarkers.length === 1) { map.setCenter(bounds.getCenter()); map.setZoom(15); }
                else map.fitBounds(bounds, 60);
            }
        },

        // Saca los pines del histórico (para volver al mapa de hoy). El re-dibujo de hoy lo hace Blazor.
        clearSnapshot() {
            for (const m of snapMarkers) m.setMap(null);
            snapMarkers = [];
            if (infoWindow) { infoWindow.close(); infoOpen = false; }
        },

        // Encuadra el mapa sobre un conjunto de puntos [[lat,lng],...] (para hacer foco en una zona).
        fitPoints(points) {
            if (!map || !points || !points.length) return;
            const b = new google.maps.LatLngBounds();
            for (const p of points) b.extend({ lat: +p[0], lng: +p[1] });
            if (points.length === 1) { map.setCenter(b.getCenter()); map.setZoom(15); }
            else map.fitBounds(b, 80);
        },

        destroy() {
            for (const m of markers) m.setMap(null);
            markers = [];
            for (const m of snapMarkers) m.setMap(null);
            snapMarkers = [];
            map = null;
            infoWindow = null;
            dotNetRef = null;
            lastFitStops = -1;
            cleanupZone();
            for (const l of routeLines) l.setMap(null);
            routeLines = [];
            if (trafficLayer) { trafficLayer.setMap(null); trafficLayer = null; }
        },

        refit() { lastFitStops = -1; },

        // Fuerza un redibujado cuando cambia el tamaño del contenedor (ej: pantalla completa).
        resize() {
            if (!map || !window.google) return;
            try { google.maps.event.trigger(map, 'resize'); } catch (e) {}
        }
    };
})();

// ══════════ Mini-mapa de PREVIEW (card del Dashboard) — no interactivo ══════════
window.mapeoMini = (function () {
    const H = window.__mapeoHelpers;
    let map = null;
    let markers = [];

    return {
        async init(elementId) {
            try {
                await H.ensureGoogle();
            } catch (e) {
                const el = document.getElementById(elementId);
                if (el) el.style.background = '#1e293b';
                return;
            }
            const el = document.getElementById(elementId);
            if (!el) return;
            map = new google.maps.Map(el, {
                center: { lat: -34.6037, lng: -58.3816 },
                zoom: 10,
                disableDefaultUI: true,     // sin controles: es un preview
                gestureHandling: 'none',    // no se puede arrastrar/zoom (la card entera es un link)
                keyboardShortcuts: false,
                clickableIcons: false
            });
        },

        renderMarkers(items) {
            if (!map || !window.google) return;
            for (const m of markers) m.setMap(null);
            markers = [];
            const bounds = new google.maps.LatLngBounds();
            let any = false;
            for (const it of items) {
                if (it.lat == null || it.lng == null) continue;
                const pos = { lat: +it.lat, lng: +it.lng };
                const marker = new google.maps.Marker({
                    position: pos,
                    map: map,
                    icon: H.markerIcon(H.markerSvg([it]), 42)
                });
                markers.push(marker);
                bounds.extend(pos);
                any = true;
            }
            if (any) {
                map.fitBounds(bounds, 34);
            } else {
                map.setCenter({ lat: -34.6037, lng: -58.3816 });
                map.setZoom(10);
            }
        }
    };
})();

// 2026-07-30: barra negra (dock) arrastrable. Aparece a la derecha por defecto;
// el usuario la arrastra de la manija a cualquier lado y la posición queda guardada
// en el navegador (localStorage). Reengancha solo al re-render de Blazor.
// Usa Pointer Events + setPointerCapture para que el arrastre no se pierda cuando el
// puntero pasa por encima del mapa de Google (que si no se "come" los movimientos).
window.mapeoDock = (function () {
    const KEY = 'mapeoDockPos';
    function clamp(v, min, max) { return Math.max(min, Math.min(max, v)); }

    // Marca de qué lado del mapa quedó el dock, para que los desplegables abran hacia adentro.
    function updateAnchor(dock, parent) {
        if (!parent) return;
        const pr = parent.getBoundingClientRect();
        const dr = dock.getBoundingClientRect();
        const enDerecha = (dr.left + dr.right) / 2 > (pr.left + pr.right) / 2;
        dock.classList.toggle('anchor-right', enDerecha);
        dock.classList.toggle('anchor-left', !enDerecha);
    }

    // Aplica la posición guardada (si existe), clampeada dentro del mapa.
    function applySaved(dock, parent) {
        let pos = null;
        try { pos = JSON.parse(localStorage.getItem(KEY) || 'null'); } catch (e) { pos = null; }
        if (!pos || !parent) return;
        const pr = parent.getBoundingClientRect();
        const left = clamp(pos.left, 0, Math.max(0, pr.width - dock.offsetWidth));
        const top = clamp(pos.top, 0, Math.max(0, pr.height - dock.offsetHeight));
        dock.style.left = left + 'px';
        dock.style.top = top + 'px';
        dock.style.right = 'auto';
        dock.style.transform = 'none';
    }

    return {
        enableDrag() {
            const dock = document.getElementById('mapeoDock');
            if (!dock) return;
            const parent = dock.offsetParent || dock.parentElement;
            const handle = dock.querySelector('.mapeo-dock-handle');
            if (!handle) return;

            applySaved(dock, parent);
            updateAnchor(dock, parent);

            if (dock.__dragBound) return; // no reenganchar dos veces
            dock.__dragBound = true;

            let startX = 0, startY = 0, startLeft = 0, startTop = 0, dragging = false;

            function onMove(e) {
                if (!dragging) return;
                const pr = parent.getBoundingClientRect();
                let nl = clamp(startLeft + (e.clientX - startX), 0, pr.width - dock.offsetWidth);
                let nt = clamp(startTop + (e.clientY - startY), 0, pr.height - dock.offsetHeight);
                dock.style.left = nl + 'px';
                dock.style.top = nt + 'px';
                updateAnchor(dock, parent);
                e.preventDefault();
            }
            function onUp(e) {
                if (!dragging) return;
                dragging = false;
                try { handle.releasePointerCapture(e.pointerId); } catch (x) { }
                handle.removeEventListener('pointermove', onMove);
                handle.removeEventListener('pointerup', onUp);
                handle.removeEventListener('pointercancel', onUp);
                try {
                    localStorage.setItem(KEY, JSON.stringify({
                        left: parseFloat(dock.style.left) || 0,
                        top: parseFloat(dock.style.top) || 0
                    }));
                } catch (x) { /* localStorage lleno o bloqueado: no pasa nada */ }
            }
            function onDown(e) {
                dragging = true;
                const pr = parent.getBoundingClientRect();
                const dr = dock.getBoundingClientRect();
                // Fijamos left/top numéricos (convertimos desde right/transform del arranque).
                startLeft = dr.left - pr.left;
                startTop = dr.top - pr.top;
                dock.style.left = startLeft + 'px';
                dock.style.top = startTop + 'px';
                dock.style.right = 'auto';
                dock.style.transform = 'none';
                startX = e.clientX; startY = e.clientY;
                // Capturamos el puntero: aunque pase sobre el mapa, los pointermove siguen llegando acá.
                try { handle.setPointerCapture(e.pointerId); } catch (x) { }
                handle.addEventListener('pointermove', onMove);
                handle.addEventListener('pointerup', onUp);
                handle.addEventListener('pointercancel', onUp);
                e.preventDefault();
            }

            handle.addEventListener('pointerdown', onDown);
        }
    };
})();

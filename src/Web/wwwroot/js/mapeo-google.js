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

    // Carga la librería de Google Maps UNA sola vez (trae la clave del navegador desde el server).
    function ensureGoogle() {
        if (window.google && window.google.maps) return Promise.resolve();
        if (googleReady) return googleReady;
        googleReady = fetch('/api/mapeo/stops/map-key')
            .then(r => r.json())
            .then(cfg => new Promise((resolve, reject) => {
                const key = cfg && cfg.key;
                if (!key) { reject(new Error('Falta la clave del mapa (GOOGLE_MAPS_BROWSER_KEY).')); return; }
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

        return `<svg xmlns="http://www.w3.org/2000/svg" width="${PIN_VB_W}" height="${PIN_VB_H}" viewBox="0 0 ${PIN_VB_W} ${PIN_VB_H}">` +
            `<defs><filter id="sh" x="-40%" y="-40%" width="180%" height="180%"><feDropShadow dx="0" dy="1.5" stdDeviation="1.5" flood-opacity="0.4"/></filter></defs>` +
            `${ring}` +
            `<path d="${headPath}" fill="${body}" stroke="#ffffff" stroke-width="2" filter="url(#sh)"/>` +
            `<text x="${PIN_HEAD_CX}" y="${labelY + fs * 0.35}" text-anchor="middle" font-size="${fs}" font-weight="800" fill="${txt}" font-family="Inter,Arial,sans-serif">${label}</text>` +
            `${badge}${check}</svg>`;
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

    // Exponer helpers al scope de este archivo (los dos módulos de abajo los usan).
    window.__mapeoHelpers = { ensureGoogle, ZONE_COLORS, escapeXml, markerSvg, markerIcon };
})();

// ══════════ Mapa grande (pantalla Mapeo) ══════════
window.mapeoFlex = (function () {
    const H = window.__mapeoHelpers;
    let map = null;
    let markers = [];
    let infoWindow = null;
    let dotNetRef = null;
    let zonePolygon = null;       // polígono que el usuario dibuja a mano (esquina por esquina)
    let zonePath = null;          // lista de puntos (MVCArray) del polígono — la manejamos nosotros
    let zoneClickListener = null; // listener de clicks del mapa mientras dibuja
    let zoneVertexMarkers = [];   // puntitos que se ven en cada esquina tocada (feedback visual)
    let routeLines = [];          // líneas de ruta dibujadas (una por repartidor)
    let trafficLayer = null;      // capa de tráfico de Google (rojo/amarillo/verde en las calles)
    let lastFitStops = -1; // cuántas paradas (sin contar el punto de partida) había en el último auto-encuadre

    // Limpia el estado del dibujo de zona (saca el polígono, los puntitos, el listener y el cursor).
    function cleanupZone() {
        if (zoneClickListener) { google.maps.event.removeListener(zoneClickListener); zoneClickListener = null; }
        if (zonePolygon) { zonePolygon.setMap(null); zonePolygon = null; }
        zonePath = null;
        for (const m of zoneVertexMarkers) m.setMap(null);
        zoneVertexMarkers = [];
        if (map) map.setOptions({ draggableCursor: null });
    }

    // Vista por defecto: todo el AMBA (CABA + conurbano + La Plata), como pidió el usuario.
    const AMBA_CENTER = { lat: -34.72, lng: -58.52 };
    const AMBA_ZOOM = 10;

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
            markers = [];
            lastFitStops = -1;
            // Capa de tráfico: arranca APAGADA (como el Google Maps original). Se prende/apaga
            // con el botón del dock (setTraffic). Rojo=congestionado, amarillo=lento, verde=fluido.
            // NOTA: no cargamos el overlay de zonas AMBA (loadZones) — el usuario quiere el mapa
            // lo más parecido posible al Google Maps original, sin los tonos de colores encima.
        },

        renderMarkers(items) {
            if (!map || !window.google) return;
            for (const m of markers) m.setMap(null);
            markers = [];
            this.clearRoutes(); // las líneas viejas se borran; se redibujan al optimizar

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
                marker.addListener('click', () => {
                    if (infoWindow) { infoWindow.setContent(popupHtml); infoWindow.open(map, marker); }
                    if (!dotNetRef) return;
                    if (isCluster) dotNetRef.invokeMethodAsync('OnClusterClicked', ids);
                    else dotNetRef.invokeMethodAsync('OnMarkerClicked', first.id);
                });

                markers.push(marker);
                bounds.extend(pos);
                any = true;
                if (!esArrastrable) realStops++;
            }

            // Encuadre inteligente:
            //  - Sin paradas: mostramos TODO el AMBA (aunque haya punto de partida).
            //  - Con paradas: encuadramos para que entren todas + el punto de partida.
            //    Solo reajustamos cuando ENTRÓ una parada nueva (no al asignar/tocar), para no
            //    pisarle el zoom al usuario. Al soltar la casita tampoco reencuadra.
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

        // Dibujar una ZONA a mano: el usuario toca cada esquina en el mapa y se va armando un polígono.
        // (No usamos DrawingManager porque Google lo deprecó/quitó en v3.65+.)
        startDrawZone() {
            if (!map) return;
            cleanupZone();
            map.setOptions({ draggableCursor: 'crosshair' }); // cursor de cruz = estás dibujando
            zonePath = new google.maps.MVCArray();
            // Contorno bien marcado mientras se dibuja (línea gruesa + relleno suave),
            // así se ve claramente cómo se va uniendo punto a punto y qué área queda adentro.
            zonePolygon = new google.maps.Polygon({
                map: map, paths: zonePath,
                fillColor: '#dc2626', fillOpacity: 0.14,
                strokeColor: '#dc2626', strokeWeight: 4, strokeOpacity: 0.95,
                clickable: false, zIndex: 1
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

        // Dibuja las líneas de ruta (una por repartidor). routes = [{color, encoded}].
        drawRoutes(routes) {
            this.clearRoutes();
            if (!map || !routes || !google.maps.geometry) return;
            for (const r of routes) {
                if (!r || !r.encoded) continue;
                const path = google.maps.geometry.encoding.decodePath(r.encoded);
                const color = r.color || '#1d4ed8';
                // Casing (borde blanco) por debajo, como la ruta azul de Google Maps:
                // hace que la línea de color resalte sobre las calles y el tráfico.
                routeLines.push(new google.maps.Polyline({
                    path: path, map: map,
                    strokeColor: '#ffffff', strokeOpacity: 0.9, strokeWeight: 9, zIndex: 4
                }));
                routeLines.push(new google.maps.Polyline({
                    path: path, map: map,
                    strokeColor: color, strokeOpacity: 0.95, strokeWeight: 5, zIndex: 5
                }));
            }
        },

        clearRoutes() {
            for (const l of routeLines) l.setMap(null);
            routeLines = [];
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

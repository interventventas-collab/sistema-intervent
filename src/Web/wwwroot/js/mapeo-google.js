// 2026-07-24: Mapa de Mapeo Flex con GOOGLE MAPS real (reemplaza al de Leaflet/CARTO).
// Redefine window.mapeoFlex manteniendo el MISMO contrato que usaba Blazor:
//   init(elementId, dotnetHelper) -> arma el mapa (async: carga Google Maps JS con la clave del server)
//   renderMarkers(items)          -> dibuja los globitos (círculo=Flex, cuadrado=ME1, triángulo=otros)
//   focusOn(lat,lng,zoom) / destroy() / refit()
// Callbacks a Blazor: OnMarkerClicked(id), OnClusterClicked(ids), ToggleMarkerInRoute(id).
window.mapeoFlex = (function () {
    let map = null;
    let markers = [];
    let infoWindow = null;
    let dotNetRef = null;
    let hasAutoFitted = false;
    let googleReady = null;

    const ZONE_COLORS = [
        '#1d4ed8', '#16a34a', '#dc2626', '#9333ea', '#ea580c', '#0891b2',
        '#ca8a04', '#db2777', '#65a30d', '#7c3aed', '#0d9488', '#b91c1c'
    ];

    // Carga la librería de Google Maps una sola vez (trae la clave del navegador desde el server).
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
                s.src = 'https://maps.googleapis.com/maps/api/js?key=' + encodeURIComponent(key) + '&callback=__mapeoGmapsReady';
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

    // Construye el globito como SVG (data URI) — replica las 3 formas del mapa anterior.
    function markerSvg(group) {
        const first = group[0];
        const extras = group.length - 1;
        const color = first.color || '#1d4ed8';
        const dimmed = first.dimmed === true;
        const bg = dimmed ? '#ffffff' : color;
        const txt = dimmed ? '#111827' : '#ffffff';
        const border = dimmed ? '#111827' : '#ffffff';
        const inRoute = group.some(x => x.inRoute === true);
        const label = escapeXml(first.label || '');
        const shape = first.shape;
        const cx = 23, cy = 23, half = 15;

        const ring = inRoute
            ? `<rect x="${cx - half - 3}" y="${cy - half - 3}" width="${(half * 2) + 6}" height="${(half * 2) + 6}" rx="${shape === 'square' ? 7 : (shape === 'triangle' ? 3 : 18)}" fill="none" stroke="#16a34a" stroke-width="3"/>`
            : '';

        let shapeSvg;
        if (shape === 'triangle') {
            shapeSvg =
                `<polygon points="${cx},${cy - half} ${cx - half},${cy + half} ${cx + half},${cy + half}" fill="${color}" stroke="#ffffff" stroke-width="1.5" filter="url(#sh)"/>` +
                `<text x="${cx}" y="${cy + half - 4}" text-anchor="middle" font-size="11" font-weight="700" fill="#ffffff" font-family="Inter,Arial,sans-serif">${label}</text>`;
        } else if (shape === 'square') {
            shapeSvg =
                `<rect x="${cx - half}" y="${cy - half}" width="${half * 2}" height="${half * 2}" rx="4" fill="${bg}" stroke="${border}" stroke-width="2" filter="url(#sh)"/>` +
                `<text x="${cx}" y="${cy + 4}" text-anchor="middle" font-size="12" font-weight="700" fill="${txt}" font-family="Inter,Arial,sans-serif">${label}</text>`;
        } else {
            shapeSvg =
                `<circle cx="${cx}" cy="${cy}" r="${half}" fill="${bg}" stroke="${border}" stroke-width="2" filter="url(#sh)"/>` +
                `<text x="${cx}" y="${cy + 4}" text-anchor="middle" font-size="12" font-weight="700" fill="${txt}" font-family="Inter,Arial,sans-serif">${label}</text>`;
        }

        const badge = extras > 0
            ? `<circle cx="39" cy="8" r="9" fill="#dc2626" stroke="#ffffff" stroke-width="2"/>` +
              `<text x="39" y="11.5" text-anchor="middle" font-size="9" font-weight="800" fill="#ffffff" font-family="Inter,Arial,sans-serif">+${extras}</text>`
            : '';

        return `<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48" viewBox="0 0 48 48">` +
            `<defs><filter id="sh" x="-30%" y="-30%" width="160%" height="160%"><feDropShadow dx="0" dy="1" stdDeviation="1.2" flood-opacity="0.45"/></filter></defs>` +
            `${ring}${shapeSvg}${badge}</svg>`;
    }

    function loadZones() {
        if (!map) return;
        let zi = 0;
        map.data.addListener('addfeature', e => {
            e.feature.setProperty('_c', ZONE_COLORS[(zi++) % ZONE_COLORS.length]);
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
                await ensureGoogle();
            } catch (e) {
                const el = document.getElementById(elementId);
                if (el) el.innerHTML = '<div style="padding:1rem;color:#b91c1c;font-family:Inter,sans-serif;font-size:0.9rem;">No se pudo cargar el mapa de Google: ' + (e && e.message ? e.message : e) + '</div>';
                return;
            }
            const el = document.getElementById(elementId);
            if (!el) return;

            map = new google.maps.Map(el, {
                center: { lat: -34.6037, lng: -58.3816 }, // CABA por default
                zoom: 11,
                gestureHandling: 'greedy',   // arrastrar con un dedo en el celu
                clickableIcons: false,       // no abrir fichas de comercios de Google
                mapTypeControl: true,        // permite cambiar a satélite
                streetViewControl: true,
                fullscreenControl: true,
                zoomControl: true
            });
            infoWindow = new google.maps.InfoWindow();
            markers = [];
            hasAutoFitted = false;
            loadZones();
        },

        renderMarkers(items) {
            if (!map || !window.google) return;
            // Limpiar marcadores previos
            for (const m of markers) m.setMap(null);
            markers = [];

            // Agrupar por coordenada (5 decimales ~1.1m) — mismas paradas en un domicilio = 1 globito.
            const groups = new Map();
            for (const it of items) {
                if (it.lat == null || it.lng == null) continue;
                const key = `${(+it.lat).toFixed(5)},${(+it.lng).toFixed(5)}`;
                if (!groups.has(key)) groups.set(key, []);
                groups.get(key).push(it);
            }

            const bounds = new google.maps.LatLngBounds();
            let any = false;

            for (const group of groups.values()) {
                const first = group[0];
                const svg = markerSvg(group);
                const url = 'data:image/svg+xml;charset=UTF-8,' + encodeURIComponent(svg);
                const pos = { lat: +first.lat, lng: +first.lng };

                const marker = new google.maps.Marker({
                    position: pos,
                    map: map,
                    icon: {
                        url: url,
                        scaledSize: new google.maps.Size(48, 48),
                        anchor: new google.maps.Point(23, 23)
                    },
                    zIndex: group.some(g => g.inRoute) ? 1000 : 1
                });

                // Popup (InfoWindow): 1 parada = su popup; varias = encabezado + lista.
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
            }

            // Solo auto-encuadra en el primer render; después respeta el zoom/pan del usuario.
            if (any && !hasAutoFitted) {
                map.fitBounds(bounds, 48);
                hasAutoFitted = true;
            }
        },

        focusOn(lat, lng, zoom) {
            if (!map) return;
            map.setCenter({ lat: +lat, lng: +lng });
            map.setZoom(zoom || 16);
            if (infoWindow) infoWindow.close();
        },

        destroy() {
            for (const m of markers) m.setMap(null);
            markers = [];
            map = null;
            infoWindow = null;
            dotNetRef = null;
            hasAutoFitted = false;
        },

        refit() { hasAutoFitted = false; }
    };
})();

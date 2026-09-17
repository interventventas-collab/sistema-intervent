// ─────────────────────────────────────────────────────────────────────────────
// "POR DÓNDE VAN" — 2026-09-17. Lo pidió Gabriel ("que su recorrido funcione
// como GPS real"). Muestra en el mapa dónde está cada repartidor y por dónde fue.
//
// Vive APARTE a propósito: tiene sus propios marcadores y su propia línea, y no
// toca nada de mapeoFlex. Por eso el refresco automático del mapa (que cada 7s
// borra y redibuja los globitos y las líneas de ruta) no la pisa ni la borra.
//
// Arranca apagada siempre: el mapa abre igual que antes hasta que alguien toca
// el botón. No recuerda el estado entre visitas, es a pedido del usuario.
// ─────────────────────────────────────────────────────────────────────────────
(function () {
    let marcadores = [];
    let lineas = [];
    let info = null;

    function mapa() { return window.__mapeoMapa || null; }

    function limpiar() {
        for (const m of marcadores) { try { m.setMap(null); } catch (e) { } }
        marcadores = [];
        for (const l of lineas) { try { l.setMap(null); } catch (e) { } }
        lineas = [];
        if (info) { try { info.close(); } catch (e) { } }
    }

    // Camioncito en un círculo del color del repartidor. Gris cuando el dato es viejo
    // (hace más de media hora que el celu no manda), para que nadie lo confunda con
    // dónde está AHORA.
    function icono(color, viejo) {
        const c = viejo ? '#9ca3af' : color;
        const svg =
            '<svg xmlns="http://www.w3.org/2000/svg" width="46" height="46" viewBox="0 0 46 46">' +
            '<circle cx="23" cy="23" r="21" fill="' + c + '" fill-opacity="0.18"/>' +
            '<circle cx="23" cy="23" r="13" fill="' + c + '" stroke="#ffffff" stroke-width="3"/>' +
            '<text x="23" y="28" font-size="14" text-anchor="middle">🚚</text>' +
            '</svg>';
        return {
            url: 'data:image/svg+xml;charset=UTF-8,' + encodeURIComponent(svg),
            scaledSize: new google.maps.Size(46, 46),
            anchor: new google.maps.Point(23, 23)
        };
    }

    function cartel(ch) {
        const cuando = ch.haceMinutos < 1 ? 'recién'
            : (ch.haceMinutos < 60 ? 'hace ' + ch.haceMinutos + ' min'
                : 'hace ' + Math.floor(ch.haceMinutos / 60) + ' h ' + (ch.haceMinutos % 60) + ' min');
        const aviso = ch.viejo
            ? '<div style="margin-top:6px; color:#b45309; font-size:12px; max-width:230px;">' +
              'Hace rato que el celular no manda nada. Puede estar en otro lado: esto es donde se lo vio por última vez.</div>'
            : '';
        return '<div style="font-family:Inter,system-ui,sans-serif; font-size:13px; line-height:1.5;">' +
            '<div style="font-weight:800; font-size:14px;">' + (ch.nombre || 'Repartidor') + '</div>' +
            '<div style="color:#374151;">' + cuando + ' · ' + (ch.hora || '') + '</div>' +
            '<div style="color:#6b7280; font-size:12px;">' + (ch.recorrido ? ch.recorrido.length : 0) + ' marcas en el día</div>' +
            aviso + '</div>';
    }

    window.mapeoChoferes = {
        // choferes = lo que devuelve GET /api/mapeo/ubicaciones
        // verRecorrido = dibujar además el hilito por dónde fue
        show: function (choferes, verRecorrido) {
            const m = mapa();
            if (!m || !choferes) return 0;
            limpiar();
            if (!info) info = new google.maps.InfoWindow();

            for (const ch of choferes) {
                if (ch.lat === 0 && ch.lng === 0) continue;

                if (verRecorrido && ch.recorrido && ch.recorrido.length > 1) {
                    const camino = ch.recorrido.map(p => ({ lat: Number(p.lat), lng: Number(p.lng) }));
                    lineas.push(new google.maps.Polyline({
                        path: camino,
                        map: m,
                        strokeColor: ch.viejo ? '#9ca3af' : (ch.color || '#1d4ed8'),
                        strokeOpacity: 0,      // punteado: la línea llena es la ruta ARMADA, ésta es la real
                        strokeWeight: 4,
                        zIndex: 3,
                        icons: [{
                            icon: { path: 'M 0,-1 0,1', strokeOpacity: 0.95, strokeWeight: 4, scale: 1 },
                            offset: '0', repeat: '12px'
                        }]
                    }));
                }

                const mk = new google.maps.Marker({
                    position: { lat: Number(ch.lat), lng: Number(ch.lng) },
                    map: m,
                    icon: icono(ch.color || '#1d4ed8', ch.viejo),
                    title: (ch.nombre || '') + ' · ' + (ch.viejo ? 'dato viejo' : 'hace ' + ch.haceMinutos + ' min'),
                    zIndex: 900   // por encima de los globitos, para que no quede tapado
                });
                mk.addListener('click', () => {
                    info.setContent(cartel(ch));
                    info.open(m, mk);
                });
                marcadores.push(mk);
            }
            return marcadores.length;
        },

        hide: function () { limpiar(); },

        // Centra el mapa en los repartidores que se están mostrando.
        fit: function () {
            const m = mapa();
            if (!m || marcadores.length === 0) return;
            if (marcadores.length === 1) { m.panTo(marcadores[0].getPosition()); return; }
            const b = new google.maps.LatLngBounds();
            for (const mk of marcadores) b.extend(mk.getPosition());
            m.fitBounds(b, 60);
        }
    };
})();

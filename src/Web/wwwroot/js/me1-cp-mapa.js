// 2026-10-05: mapa de /meli/me1/codigos-postales (Leaflet + OpenStreetMap).
// Un puntito por código postal (centro de la zona del código según Google), color de la zona,
// gris si no se ofrece, borde rojo si está tildado. Tocar cualquier lugar del mapa pregunta a Blazor
// (InfoPunto → Google reverse geocoding) qué código postal es ese punto.
// Tildar/destildar vuelve a Blazor con dotnet.invokeMethodAsync('TildarDesdeMapa' | 'DestildarDesdeMapa', cps).
window.me1CpMapa = (function () {
    let map = null, capa = null, dotnet = null, esAdmin = false;
    let puntos = [];          // [{ cp, localidad, partido, lat, lng, color, ofrecido, precio, zona, compras, incluye, tildado }]

    function init(elId, dotnetRef, admin) {
        dotnet = dotnetRef;
        esAdmin = !!admin;
        const el = document.getElementById(elId);
        if (!el || typeof L === 'undefined') return false;
        if (map) { map.remove(); map = null; }
        map = L.map(el, { zoomControl: true }).setView([-34.55, -58.6], 9);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 18, attribution: '© OpenStreetMap'
        }).addTo(map);
        capa = L.layerGroup().addTo(map);

        // Tocar un lugar vacío del mapa: qué código postal es
        map.on('click', async e => {
            if (!dotnet) return;
            const pop = L.popup({ closeButton: false }).setLatLng(e.latlng)
                .setContent('<div style="font-size:12px;color:#6b7280">Buscando el código postal…</div>').openOn(map);
            try {
                const info = await dotnet.invokeMethodAsync('InfoPunto', e.latlng.lat, e.latlng.lng);
                pop.setContent(htmlPunto(info));
            } catch {
                pop.setContent('<div style="font-size:12px;color:#dc2626">No se pudo averiguar el código postal.</div>');
            }
        });
        setTimeout(() => map.invalidateSize(), 50);
        return true;
    }

    function fmt(n) { return '$' + Number(n).toLocaleString('es-AR'); }
    function esc(s) { return String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])); }

    function botonTildar(cp, tildado) {
        if (!esAdmin) return '';
        return `<br><a href="#" style="color:#dc2626;font-weight:700" onclick="me1CpMapa._tildar(${cp}, ${!!tildado});return false;">`
            + (tildado ? '☑ Destildar' : '☐ Tildar') + '</a>';
    }

    // Cartelito de un código (puntito o lugar tocado)
    function htmlCp(c, titulo) {
        const estado = c.ofrecido
            ? '<span style="color:#16a34a;font-weight:700">✓ Se ofrece en me1</span>'
            : '<span style="color:#6b7280;font-weight:700">✗ No se ofrece</span>';
        const inc = c.incluye || [];
        const incluye = inc.length ? `<br><span style="color:#6b7280">Incluye: ${esc(inc.slice(0, 4).join(', '))}${inc.length > 4 ? ` y ${inc.length - 4} más` : ''}</span>` : '';
        const compras = c.compras ? `<br>${c.compras} ${c.compras === 1 ? 'compra' : 'compras'} tuyas en este código` : '';
        return `<div style="font-size:12px;line-height:1.55;min-width:190px">
            ${titulo ? `<span style="color:#6b7280">${titulo}</span><br>` : ''}
            <b style="font-size:13.5px">CP ${c.cp} · ${esc(c.localidad)}</b>
            ${c.partido ? `<br><span style="color:#6b7280">${esc(c.partido)}</span>` : ''}
            <br>${esc(c.zona)} · ${fmt(c.precio)}<br>${estado}${compras}${incluye}${botonTildar(c.cp, c.tildado)}</div>`;
    }

    function htmlPunto(info) {
        if (!info || !info.cp) {
            return `<div style="font-size:12px;line-height:1.5">${info && info.error ? esc(info.error) : 'Google no tiene código postal para este punto.'}</div>`;
        }
        if (!info.enTabla) {
            return `<div style="font-size:12px;line-height:1.55;min-width:190px"><span style="color:#6b7280">Tocaste acá:</span><br>
                <b style="font-size:13.5px">CP ${info.cp}</b>${info.direccion ? `<br><span style="color:#6b7280">${esc(info.direccion)}</span>` : ''}
                <br><span style="color:#6b7280;font-weight:700">No está en tu tabla de me1</span> (no se ofrece)</div>`;
        }
        return htmlCp(info, 'Tocaste acá:');
    }

    function setPuntos(lista, ajustar) {
        puntos = lista || [];
        if (!map) return;
        capa.clearLayers();
        puntos.forEach(p => {
            const m = L.circleMarker([p.lat, p.lng], {
                radius: p.tildado ? 8 : 6,
                color: p.tildado ? '#dc2626' : '#fff',
                weight: p.tildado ? 3 : 1.5,
                fillColor: p.ofrecido ? p.color : '#9ca3af',
                fillOpacity: p.ofrecido ? 0.95 : 0.75,
                bubblingMouseEvents: false   // tocar un puntito no dispara el "qué código es acá" del mapa
            });
            m.bindTooltip(`${p.cp} · ${esc(p.localidad)}`, { direction: 'top', offset: [0, -6] });
            m.bindPopup(() => htmlCp(p), { closeButton: false });
            m.addTo(capa);
        });
        if (ajustar) verTodos();
    }

    // Encuadra el mapa para que entren todos los puntitos que se están mostrando.
    function verTodos() {
        if (!map || !puntos.length) return;
        map.fitBounds(L.latLngBounds(puntos.map(p => [p.lat, p.lng])), { padding: [20, 20] });
    }

    function _tildar(cp, destildar) {
        if (!dotnet) return;
        map.closePopup();
        dotnet.invokeMethodAsync(destildar ? 'DestildarDesdeMapa' : 'TildarDesdeMapa', [cp]);
    }

    function destroy() {
        if (map) { map.remove(); map = null; }
        dotnet = null;
    }

    return { init, setPuntos, verTodos, destroy, _tildar };
})();

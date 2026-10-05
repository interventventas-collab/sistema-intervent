// 2026-10-05: mapa de /meli/me1/codigos-postales (Leaflet + OpenStreetMap).
// Un puntito por código postal (centro de la zona del código según Google), color de la zona,
// gris si no se ofrece, borde rojo si está tildado. Tocar cualquier lugar del mapa pregunta a Blazor
// (InfoPunto → Google reverse geocoding) qué código postal es ese punto.
// Tildar/destildar vuelve a Blazor con dotnet.invokeMethodAsync('TildarDesdeMapa' | 'DestildarDesdeMapa', cps).
window.me1CpMapa = (function () {
    let map = null, capa = null, dotnet = null, esAdmin = false, depositoMarker = null, saltoMarker = null;
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
        if (!document.getElementById('me1-cp-estilos')) {
            const st = document.createElement('style');
            st.id = 'me1-cp-estilos';
            // Salto del pin al ir a un código desde la lista (como el BOUNCE de Mapeo)
            st.textContent = '@keyframes me1cpSalto{0%,100%{transform:translateY(0)}50%{transform:translateY(-16px)}}'
                + '.me1-cp-salto{animation:me1cpSalto .45s ease-in-out 6;font-size:30px;line-height:1;filter:drop-shadow(0 2px 2px rgba(0,0,0,.35))}';
            document.head.appendChild(st);
        }

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
        const km = c.km != null ? `<br>🏠 ${String(c.km).replace('.', ',')} km del depósito${c.minutos ? ` (${c.minutos} min)` : ''}` : '';
        const precio = c.especial
            ? `<b>${fmt(c.precio)}</b> <span style="color:#6b7280">precio especial (la zona: ${fmt(c.precioZona)})</span>`
            : fmt(c.precio);
        return `<div style="font-size:12px;line-height:1.55;min-width:190px">
            ${titulo ? `<span style="color:#6b7280">${titulo}</span><br>` : ''}
            <b style="font-size:13.5px">CP ${c.cp} · ${esc(c.localidad)}</b>
            ${c.partido ? `<br><span style="color:#6b7280">${esc(c.partido)}</span>` : ''}
            <br>${esc(c.zona)} · ${precio}<br>${estado}${km}${compras}${incluye}${botonTildar(c.cp, c.tildado)}</div>`;
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

    // Casita del depósito (punto de partida de Mapeo)
    function setDeposito(lat, lng) {
        if (!map || lat == null || lng == null) return;
        if (depositoMarker) map.removeLayer(depositoMarker);
        depositoMarker = L.marker([lat, lng], {
            icon: L.divIcon({ className: '', html: '<div style="font-size:24px;line-height:1;filter:drop-shadow(0 1px 2px rgba(0,0,0,.4))">🏠</div>', iconSize: [26, 26], iconAnchor: [13, 13] }),
            zIndexOffset: 1000, interactive: true
        }).bindTooltip('Depósito', { direction: 'top' }).addTo(map);
    }

    // Tocaron un código en la lista: el mapa va hasta ahí, el pin salta y se abre el cartelito.
    function irA(cp) {
        if (!map) return;
        const p = puntos.find(x => x.cp === cp);
        if (!p) return;
        if (saltoMarker) { map.removeLayer(saltoMarker); saltoMarker = null; }
        map.flyTo([p.lat, p.lng], Math.max(map.getZoom(), 14), { duration: 0.8 });
        map.once('moveend', () => {
            saltoMarker = L.marker([p.lat, p.lng], {
                icon: L.divIcon({ className: '', html: '<div class="me1-cp-salto">📍</div>', iconSize: [30, 30], iconAnchor: [15, 30] }),
                zIndexOffset: 2000, interactive: false
            }).addTo(map);
            L.popup({ closeButton: false, offset: [0, -28] }).setLatLng([p.lat, p.lng]).setContent(htmlCp(p)).openOn(map);
            const m = saltoMarker;
            setTimeout(() => { if (saltoMarker === m) { map.removeLayer(m); saltoMarker = null; } }, 6000);
        });
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

    return { init, setPuntos, verTodos, setDeposito, irA, destroy, _tildar };
})();

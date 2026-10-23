// 21/10/2026: link del repartidor → Oficina → 📍 Por dónde van. Mapa chico (Leaflet + OpenStreetMap,
// sin la clave de Google, así anda también en desarrollo) con TODOS los repartidores:
//   🚚 en vivo (los que mandan GPS), con la línea punteada del recorrido si se pide;
//   📦 el resto, en el punto de su última entrega del día (no es en vivo: borde gris).
window.ofiUbicaciones = (function () {
    let map = null, capa = null;

    function esc(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    function hace(min) {
        if (min < 1) return 'recién';
        if (min < 60) return min + ' min';
        const h = Math.floor(min / 60);
        return h + ' h';
    }

    // marcas: [{ nombre, color, lat, lng, vivo, haceMinutos, viejo, recorrido: [{lat,lng}], hora, hoy, ultima }]
    function pintar(elId, marcas, conRecorrido) {
        const el = document.getElementById(elId);
        if (!el || typeof L === 'undefined') return 'error';
        if (map && map.getContainer() !== el) { map.remove(); map = null; }
        if (!map) {
            map = L.map(el, { zoomControl: true, attributionControl: true }).setView([-34.7, -58.45], 11);
            L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 18, attribution: '© OpenStreetMap'
            }).addTo(map);
        }
        if (capa) capa.remove();
        capa = L.layerGroup().addTo(map);
        const puntos = [];
        (marcas || []).forEach(c => {
            if (c.lat == null || c.lng == null) return;
            if (c.vivo && conRecorrido && c.recorrido && c.recorrido.length > 1) {
                const linea = c.recorrido.map(p => [Number(p.lat), Number(p.lng)]);
                L.polyline(linea, { color: c.color || '#0e7490', weight: 3, dashArray: '5 6', opacity: 0.85 }).addTo(capa);
                linea.forEach(p => puntos.push(p));
            }
            const pos = [Number(c.lat), Number(c.lng)];
            puntos.push(pos);
            const tres = esc((c.nombre || '').substring(0, 3).toUpperCase());
            const gris = !c.vivo || c.viejo;
            const borde = c.vivo && !c.viejo ? esc(c.color || '#0e7490') : '#d1d5db';
            const arriba = c.vivo
                ? '🚚 ' + tres + ' · ' + (c.hoy === false ? 'últ. ' + esc(c.hora) : hace(c.haceMinutos))
                : '📦 ' + tres + (c.hora ? ' · ' + esc(c.hora) : '');
            const abajo = c.vivo
                ? (c.ultima ? '📦 ' + esc(c.ultima) : '')
                : esc(c.ultima || '');
            const html = '<div style="background:' + (c.vivo ? '#fff' : '#fafafa') + ';border-radius:12px;padding:3px 9px;font:700 12px sans-serif;line-height:1.3;white-space:nowrap;'
                + 'box-shadow:0 2px 6px rgba(0,0,0,.3);border:2px solid ' + borde + ';'
                + 'color:' + (c.viejo ? '#9ca3af' : '#111') + ';transform:translate(-50%,-50%);display:inline-block">'
                + arriba
                + (abajo ? '<div style="font-weight:400;font-size:11px;color:#4b5563;max-width:170px;overflow:hidden;text-overflow:ellipsis">' + abajo + '</div>' : '')
                + '</div>';
            L.marker(pos, { icon: L.divIcon({ className: '', html: html, iconSize: [0, 0] }), zIndexOffset: gris ? 0 : 1000 }).addTo(capa);
        });
        setTimeout(() => {
            if (!map) return;
            map.invalidateSize();
            if (puntos.length === 1) map.setView(puntos[0], 15);
            else if (puntos.length > 1) map.fitBounds(puntos, { padding: [40, 40], maxZoom: 16 });
        }, 60);
        return 'ok';
    }

    function cerrar() {
        if (map) { map.remove(); map = null; capa = null; }
    }

    return { pintar, cerrar };
})();

// 21/10/2026: link del repartidor → Oficina → 📍 Por dónde van. Mapa chico (Leaflet + OpenStreetMap,
// sin la clave de Google, así anda también en desarrollo) con un camioncito por repartidor y, si se
// pide, la línea punteada del recorrido del día. Los datos son los mismos del mapa de la compu.
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

    // choferes: [{ nombre, color, lat, lng, haceMinutos, viejo, recorrido: [{lat,lng}], ultima: "14:32 CLIENTE" }]
    function pintar(elId, choferes, conRecorrido) {
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
        (choferes || []).forEach(c => {
            if (conRecorrido && c.recorrido && c.recorrido.length > 1) {
                const linea = c.recorrido.map(p => [Number(p.lat), Number(p.lng)]);
                L.polyline(linea, { color: c.color || '#0e7490', weight: 3, dashArray: '5 6', opacity: 0.85 }).addTo(capa);
                linea.forEach(p => puntos.push(p));
            }
            const pos = [Number(c.lat), Number(c.lng)];
            puntos.push(pos);
            const tres = esc((c.nombre || '').substring(0, 3).toUpperCase());
            const html = '<div style="background:#fff;border-radius:12px;padding:3px 9px;font:700 12px sans-serif;line-height:1.3;white-space:nowrap;'
                + 'box-shadow:0 2px 6px rgba(0,0,0,.3);border:2px solid ' + (c.viejo ? '#d1d5db' : esc(c.color || '#0e7490')) + ';'
                + 'color:' + (c.viejo ? '#9ca3af' : '#111') + ';transform:translate(-50%,-50%);display:inline-block">'
                + '🚚 ' + tres + ' · ' + hace(c.haceMinutos)
                + (c.ultima ? '<div style="font-weight:400;font-size:11px;color:#4b5563;max-width:170px;overflow:hidden;text-overflow:ellipsis">📦 ' + esc(c.ultima) + '</div>' : '')
                + '</div>';
            L.marker(pos, { icon: L.divIcon({ className: '', html: html, iconSize: [0, 0] }) }).addTo(capa);
        });
        if (puntos.length === 1) map.setView(puntos[0], 15);
        else if (puntos.length > 1) map.fitBounds(puntos, { padding: [30, 30], maxZoom: 16 });
        setTimeout(() => map && map.invalidateSize(), 50);
        return 'ok';
    }

    function cerrar() {
        if (map) { map.remove(); map = null; capa = null; }
    }

    return { pintar, cerrar };
})();

// 2026-10-05: mapa de /meli/me1/codigos-postales (Leaflet + OpenStreetMap).
// Un puntito por localidad (agrupa sus CPs), color de la zona, gris si ninguno se ofrece,
// borde rojo si está tildado. Herramientas: Mover / Encerrar (recuadro) / Círculo.
// Lo que se encierra o se tilda desde el globito vuelve a Blazor con dotnet.invokeMethodAsync('TildarDesdeMapa', cps).
window.me1CpMapa = (function () {
    let map = null, capa = null, dotnet = null, modo = 'mover';
    let puntos = [];          // [{ clave, localidad, provincia, lat, lng, color, cps:[{cp, ofrecido, precio, zona}], tildados }]
    let dibujo = null, inicio = null;

    function init(elId, dotnetRef) {
        dotnet = dotnetRef;
        const el = document.getElementById(elId);
        if (!el || typeof L === 'undefined') return false;
        if (map) { map.remove(); map = null; }
        map = L.map(el, { zoomControl: true, boxZoom: false }).setView([-34.55, -58.6], 9);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 18, attribution: '© OpenStreetMap'
        }).addTo(map);
        capa = L.layerGroup().addTo(map);

        map.on('mousedown', e => {
            if (modo === 'mover') return;
            inicio = e.latlng;
            dibujo = modo === 'encerrar'
                ? L.rectangle([inicio, inicio], { color: '#dc2626', weight: 2, dashArray: '6 4', fillOpacity: 0.07 })
                : L.circle(inicio, { radius: 1, color: '#dc2626', weight: 2, dashArray: '6 4', fillOpacity: 0.07 });
            dibujo.addTo(map);
        });
        map.on('mousemove', e => {
            if (!dibujo || !inicio) return;
            if (modo === 'encerrar') dibujo.setBounds(L.latLngBounds(inicio, e.latlng));
            else dibujo.setRadius(map.distance(inicio, e.latlng));
        });
        map.on('mouseup', () => {
            if (!dibujo) return;
            const adentro = [];
            puntos.forEach(p => {
                const ll = L.latLng(p.lat, p.lng);
                const esta = modo === 'encerrar'
                    ? dibujo.getBounds().contains(ll)
                    : map.distance(dibujo.getLatLng(), ll) <= dibujo.getRadius();
                if (esta) p.cps.forEach(c => adentro.push(c.cp));
            });
            map.removeLayer(dibujo);
            dibujo = null; inicio = null;
            if (adentro.length) dotnet.invokeMethodAsync('TildarDesdeMapa', adentro);
        });
        setTimeout(() => map.invalidateSize(), 50);
        return true;
    }

    function setModo(m) {
        modo = m;
        if (!map) return;
        if (m === 'mover') map.dragging.enable(); else map.dragging.disable();
        map.getContainer().style.cursor = m === 'mover' ? '' : 'crosshair';
    }

    function fmt(n) { return '$' + Number(n).toLocaleString('es-AR'); }

    function globo(p) {
        const ofrecidos = p.cps.filter(c => c.ofrecido).length;
        const lista = p.cps.map(c => c.ofrecido ? c.cp : `<s style="color:#9ca3af">${c.cp}</s>`).join(', ');
        const precios = [...new Set(p.cps.map(c => c.precio))].map(fmt).join(' / ');
        const todosTildados = p.tildados === p.cps.length;
        return `<div style="font-size:12px;line-height:1.5;min-width:170px">
            <b style="font-size:13px">${p.localidad}</b> · ${p.cps.length} ${p.cps.length === 1 ? 'código' : 'códigos'}<br>
            ${lista}<br>${p.cps[0].zona} · ${precios}<br>
            ${ofrecidos < p.cps.length ? `<span style="color:#6b7280">${p.cps.length - ofrecidos} no se ofrece${p.cps.length - ofrecidos === 1 ? '' : 'n'}</span><br>` : ''}
            <a href="#" style="color:#dc2626;font-weight:700" onclick="me1CpMapa._tildar('${p.clave.replace(/'/g, "\\'")}', ${todosTildados});return false;">
            ${todosTildados ? '☑ Destildar' : '☐ Tildar'} ${p.cps.length === 1 ? 'este' : 'los ' + p.cps.length}</a></div>`;
    }

    function setPuntos(lista, ajustar) {
        puntos = lista || [];
        if (!map) return;
        capa.clearLayers();
        puntos.forEach(p => {
            const ninguno = p.cps.every(c => !c.ofrecido);
            const tild = p.tildados > 0;
            const m = L.circleMarker([p.lat, p.lng], {
                radius: tild ? 8 : 6,
                color: tild ? '#dc2626' : '#fff',
                weight: tild ? 3 : 1.5,
                fillColor: ninguno ? '#9ca3af' : p.color,
                fillOpacity: ninguno ? 0.75 : 0.95
            });
            m.bindTooltip(`${p.localidad} (${p.cps.length})`, { direction: 'top', offset: [0, -6] });
            m.bindPopup(() => globo(p), { closeButton: false });
            m.addTo(capa);
        });
        if (ajustar && puntos.length) {
            map.fitBounds(L.latLngBounds(puntos.map(p => [p.lat, p.lng])), { padding: [20, 20] });
        }
    }

    function _tildar(clave, destildar) {
        const p = puntos.find(x => x.clave === clave);
        if (!p || !dotnet) return;
        map.closePopup();
        dotnet.invokeMethodAsync(destildar ? 'DestildarDesdeMapa' : 'TildarDesdeMapa', p.cps.map(c => c.cp));
    }

    function destroy() {
        if (map) { map.remove(); map = null; }
        dotnet = null;
    }

    return { init, setModo, setPuntos, destroy, _tildar };
})();

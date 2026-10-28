// 2026-10-28: mapa de la ficha del cliente (sección Entrega). El pin salta solo a la dirección
// escrita; si no es ahí, se arrastra o se toca el mapa. Al lado, la foto de la puerta (Street
// View mirando hacia el pin). Avisa a Blazor con PinMovido(lat, lng).
//   clienteMapa.init(mapaId, fotoId, dotnet, lat, lng)  /  setPin(lat, lng)  /  clearPin()  /  destroy()
(function () {
    let cargando = null;
    let clave = null;
    let mapa = null, pin = null, dotnet = null, fotoId = null, fotoPedida = 0;

    // Misma clave del navegador que usa el mapa de Mapeo (api/mapeo/stops/map-key).
    function asegurarGoogle() {
        if (window.google && window.google.maps && clave) return Promise.resolve();
        if (cargando) return cargando;
        cargando = fetch('/api/mapeo/stops/map-key').then(r => r.json()).then(cfg => {
            clave = cfg && cfg.key;
            if (!clave) throw new Error('Falta la clave del mapa');
            if (window.google && window.google.maps) return;
            // Si el mapa de Mapeo ya pidió la librería, esperamos a que termine en vez de cargarla dos veces.
            if (document.querySelector('script[src*="maps.googleapis.com/maps/api/js"]')) {
                return new Promise((ok, mal) => {
                    let n = 0;
                    const t = setInterval(() => {
                        if (window.google && window.google.maps) { clearInterval(t); ok(); }
                        else if (++n > 100) { clearInterval(t); mal(new Error('Google Maps no cargó')); }
                    }, 100);
                });
            }
            return new Promise((ok, mal) => {
                window.__clienteMapaListo = ok;
                const s = document.createElement('script');
                s.src = 'https://maps.googleapis.com/maps/api/js?key=' + encodeURIComponent(clave) + '&libraries=places,geometry&callback=__clienteMapaListo';
                s.async = true;
                s.onerror = () => mal(new Error('No se pudo cargar Google Maps'));
                document.head.appendChild(s);
            });
        }).catch(e => { cargando = null; throw e; });
        return cargando;
    }

    function moverPin(lat, lng, avisar) {
        const pos = { lat: +lat, lng: +lng };
        if (!pin) {
            pin = new google.maps.Marker({ map: mapa, position: pos, draggable: true, title: 'Arrastrame hasta la puerta' });
            pin.addListener('dragend', e => moverPin(e.latLng.lat(), e.latLng.lng(), true));
        } else {
            pin.setPosition(pos);
            pin.setMap(mapa);
        }
        foto(pos);
        if (avisar && dotnet) dotnet.invokeMethodAsync('PinMovido', pos.lat, pos.lng);
    }

    // Foto de la puerta: busca la vista de calle más cercana (hasta 60 m) y la orienta hacia el pin.
    function foto(pos) {
        const el = fotoId && document.getElementById(fotoId);
        if (!el) return;
        const yo = ++fotoPedida;
        el.innerHTML = '<span class="cm-foto-txt">Buscando la foto de la calle…</span>';
        new google.maps.StreetViewService().getPanorama(
            { location: pos, radius: 60, source: google.maps.StreetViewSource.OUTDOOR },
            (data, status) => {
                if (yo !== fotoPedida) return;
                if (status !== 'OK' || !data || !data.location) {
                    el.innerHTML = '<span class="cm-foto-txt">Google no tiene foto de esta cuadra</span>';
                    return;
                }
                const desde = data.location.latLng;
                const rumbo = google.maps.geometry
                    ? google.maps.geometry.spherical.computeHeading(desde, new google.maps.LatLng(pos.lat, pos.lng)) : 0;
                const url = 'https://maps.googleapis.com/maps/api/streetview?size=480x240&pano=' + encodeURIComponent(data.location.pano)
                    + '&heading=' + Math.round(rumbo) + '&fov=75&pitch=0&key=' + encodeURIComponent(clave);
                const ver = 'https://www.google.com/maps/@?api=1&map_action=pano&pano=' + encodeURIComponent(data.location.pano) + '&heading=' + Math.round(rumbo);
                el.innerHTML = '<a href="' + ver + '" target="_blank" rel="noopener" title="Abrir la vista de calle en Google Maps">'
                    + '<img src="' + url + '" alt="Foto de la calle" /></a>';
            });
    }

    window.clienteMapa = {
        init: async function (mapaId, fotoElId, dotnetRef, lat, lng) {
            dotnet = dotnetRef; fotoId = fotoElId; pin = null;
            await asegurarGoogle();
            const el = document.getElementById(mapaId);
            if (!el) return;
            const hay = lat != null && lng != null;
            mapa = new google.maps.Map(el, {
                center: hay ? { lat: +lat, lng: +lng } : { lat: -34.66, lng: -58.45 },  // sin punto: GBA sur
                zoom: hay ? 17 : 10,
                mapTypeControl: false, streetViewControl: false, fullscreenControl: true,
                clickableIcons: false, gestureHandling: 'cooperative'
            });
            mapa.addListener('click', e => moverPin(e.latLng.lat(), e.latLng.lng(), true));
            if (hay) moverPin(lat, lng, false);
            else if (fotoId && document.getElementById(fotoId))
                document.getElementById(fotoId).innerHTML = '<span class="cm-foto-txt">Acá va a aparecer la foto de la puerta</span>';
        },
        setPin: function (lat, lng) {
            if (!mapa) return;
            moverPin(lat, lng, false);
            mapa.panTo({ lat: +lat, lng: +lng });
            if (mapa.getZoom() < 16) mapa.setZoom(17);
        },
        clearPin: function () {
            if (pin) pin.setMap(null);
            fotoPedida++;
            const el = fotoId && document.getElementById(fotoId);
            if (el) el.innerHTML = '<span class="cm-foto-txt">Acá va a aparecer la foto de la puerta</span>';
        },
        destroy: function () { mapa = null; pin = null; dotnet = null; fotoPedida++; }
    };
})();

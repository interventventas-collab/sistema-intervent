// 2026-10-28: mapa de la ficha del cliente (sección Entrega). El pin salta solo a la dirección
// escrita; si no es ahí, se arrastra o se toca el mapa. Al lado, la foto de la puerta (Street
// View mirando hacia el pin). Avisa a Blazor con PinMovido(lat, lng).
//   clienteMapa.init(mapaId, fotoId, dotnet, lat, lng, buscadorId)  /  setPin(mapaId,…)  /  clearPin(mapaId)  /  destroy(mapaId)
// 2026-11-05: buscadorId = casillero del domicilio con sugerencias tipo Google Maps (LugarElegido).
(function () {
    let cargando = null;
    let clave = null;
    // 2026-11-07: un mapa por ficha abierta (el del domicilio principal y el del alternativo pueden
    // estar a la vez). Cada uno guarda lo suyo acá, por el id de su <div>.
    const mapas = {};

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

    function moverPin(m, lat, lng, avisar) {
        const pos = { lat: +lat, lng: +lng };
        if (!m.pin) {
            m.pin = new google.maps.Marker({ map: m.mapa, position: pos, draggable: true, title: 'Arrastrame hasta la puerta' });
            m.pin.addListener('dragend', e => moverPin(m, e.latLng.lat(), e.latLng.lng(), true));
        } else {
            m.pin.setPosition(pos);
            m.pin.setMap(m.mapa);
        }
        foto(m, pos);
        if (avisar && m.dotnet) m.dotnet.invokeMethodAsync('PinMovido', pos.lat, pos.lng);
    }

    // Foto de la puerta: busca la vista de calle más cercana (hasta 60 m) y la orienta hacia el pin.
    function foto(m, pos) {
        const el = m.fotoId && document.getElementById(m.fotoId);
        if (!el) return;
        const yo = ++m.fotoPedida;
        el.innerHTML = '<span class="cm-foto-txt">Buscando la foto de la calle…</span>';
        new google.maps.StreetViewService().getPanorama(
            { location: pos, radius: 60, source: google.maps.StreetViewSource.OUTDOOR },
            (data, status) => {
                if (yo !== m.fotoPedida) return;
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

    // 2026-11-05: buscador estilo Google Maps en el casillero del domicilio. Mientras escribís
    // sugiere direcciones y lugares (Argentina, primero cerca de CABA/GBA). Al elegir uno mueve el
    // pin y le pasa a Blazor calle y número, barrio/localidad, CP y provincia (LugarElegido).
    function comp(place, tipo, corto) {
        const c = (place.address_components || []).find(x => x.types.indexOf(tipo) >= 0);
        return c ? (corto ? c.short_name : c.long_name) : '';
    }
    // Si Google rechaza la clave, el buscador de Google deja el casillero gris y bloqueado. Lo
    // destrabamos para que se pueda seguir escribiendo el domicilio a mano como siempre.
    function destrabar(input) {
        if (!input) return;
        input.disabled = false;
        input.classList.remove('gm-err-autocomplete');
        input.style.backgroundImage = 'none';
        if (/Oops|went wrong/i.test(input.placeholder)) input.placeholder = 'Calle y número';
    }
    const authPrevio = window.gm_authFailure;
    window.gm_authFailure = function () {
        document.querySelectorAll('[data-ac-cliente="1"]').forEach(destrabar);
        if (typeof authPrevio === 'function') try { authPrevio(); } catch (e) { }
    };
    function engancharBuscador(inputId, m) {
        const input = inputId && document.getElementById(inputId);
        if (!input || !google.maps.places || input.dataset.acCliente === '1') return;
        input.dataset.acCliente = '1';
        const vigia = new MutationObserver(() => {
            if (input.disabled || input.classList.contains('gm-err-autocomplete')) destrabar(input);
        });
        vigia.observe(input, { attributes: true, attributeFilter: ['disabled', 'class', 'placeholder'] });
        const amba = new google.maps.LatLngBounds({ lat: -35.1, lng: -59.1 }, { lat: -34.3, lng: -58.1 });
        const ac = new google.maps.places.Autocomplete(input, {
            fields: ['address_components', 'geometry', 'name', 'formatted_address', 'types'],
            componentRestrictions: { country: 'ar' },
            bounds: amba, strictBounds: false
        });
        // Enter elige la sugerencia, no guarda el formulario.
        input.addEventListener('keydown', e => {
            if (e.key !== 'Enter') return;
            const pac = document.querySelector('.pac-container');
            if (pac && pac.style.display !== 'none' && pac.offsetParent !== null) e.preventDefault();
        });
        ac.addListener('place_changed', () => {
            const p = ac.getPlace();
            if (!p || !p.geometry || !p.geometry.location) return;
            const lat = p.geometry.location.lat(), lng = p.geometry.location.lng();
            const ruta = comp(p, 'route'), nro = comp(p, 'street_number');
            const esLugar = (p.types || []).some(t => t === 'establishment' || t === 'point_of_interest');
            let calle = ruta ? (ruta + (nro ? ' ' + nro : '')) : (p.name || '');
            if (esLugar && p.name && !nro) calle = p.name + (ruta ? ' (' + ruta + ')' : '');
            const provincia = comp(p, 'administrative_area_level_1');
            const enCaba = /Aut[oó]noma/i.test(provincia);
            const barrio = comp(p, 'neighborhood') || comp(p, 'sublocality_level_1');
            const localidad = enCaba ? (barrio && !/^Comuna/i.test(barrio) ? barrio : '')
                : (comp(p, 'locality') || comp(p, 'administrative_area_level_2'));
            const cpm = (comp(p, 'postal_code') || '').match(/\d{4}/);
            const cp = cpm ? cpm[0] : '';
            const texto = (p.formatted_address || '').replace(', Argentina', '');
            if (m && m.mapa) {
                moverPin(m, lat, lng, false);
                m.mapa.panTo({ lat: lat, lng: lng });
                if (m.mapa.getZoom() < 16) m.mapa.setZoom(17);
            }
            if (m && m.dotnet) m.dotnet.invokeMethodAsync('LugarElegido', calle, localidad, cp, provincia, lat, lng, texto);
        });
    }

    window.clienteMapa = {
        init: async function (mapaId, fotoElId, dotnetRef, lat, lng, buscadorId) {
            const m = mapas[mapaId] = { mapa: null, pin: null, dotnet: dotnetRef, fotoId: fotoElId, fotoPedida: 0 };
            await asegurarGoogle();
            const el = document.getElementById(mapaId);
            if (!el || mapas[mapaId] !== m) return;
            const hay = lat != null && lng != null;
            m.mapa = new google.maps.Map(el, {
                center: hay ? { lat: +lat, lng: +lng } : { lat: -34.66, lng: -58.45 },  // sin punto: GBA sur
                zoom: hay ? 17 : 10,
                mapTypeControl: false, streetViewControl: false, fullscreenControl: true,
                clickableIcons: false, gestureHandling: 'cooperative'
            });
            m.mapa.addListener('click', e => moverPin(m, e.latLng.lat(), e.latLng.lng(), true));
            if (hay) moverPin(m, lat, lng, false);
            else if (m.fotoId && document.getElementById(m.fotoId))
                document.getElementById(m.fotoId).innerHTML = '<span class="cm-foto-txt">Acá va a aparecer la foto de la puerta</span>';
            engancharBuscador(buscadorId, m);
        },
        setPin: function (mapaId, lat, lng) {
            const m = mapas[mapaId];
            if (!m || !m.mapa) return;
            moverPin(m, lat, lng, false);
            m.mapa.panTo({ lat: +lat, lng: +lng });
            if (m.mapa.getZoom() < 16) m.mapa.setZoom(17);
        },
        clearPin: function (mapaId) {
            const m = mapas[mapaId];
            if (!m) return;
            if (m.pin) m.pin.setMap(null);
            m.fotoPedida++;
            const el = m.fotoId && document.getElementById(m.fotoId);
            if (el) el.innerHTML = '<span class="cm-foto-txt">Acá va a aparecer la foto de la puerta</span>';
        },
        destroy: function (mapaId) {
            const m = mapas[mapaId];
            if (m) { m.dotnet = null; m.fotoPedida++; }
            delete mapas[mapaId];
        }
    };
})();

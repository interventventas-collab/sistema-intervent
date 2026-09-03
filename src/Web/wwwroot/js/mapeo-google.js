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

    // La clave del navegador que trae ensureGoogle. La guardamos acá para poder pedir
    // también la fotito de Street View del domicilio cuando se abre un globito.
    let browserKey = null;

    // Carga la librería de Google Maps UNA sola vez (trae la clave del navegador desde el server).
    function ensureGoogle() {
        if (window.google && window.google.maps) return Promise.resolve();
        if (googleReady) return googleReady;
        googleReady = fetch('/api/mapeo/stops/map-key')
            .then(r => r.json())
            .then(cfg => new Promise((resolve, reject) => {
                const key = cfg && cfg.key;
                if (!key) { reject(new Error('Falta la clave del mapa (GOOGLE_MAPS_BROWSER_KEY).')); return; }
                browserKey = key; // la reutilizamos para la fotito de Street View
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
        //   circle=Flex · square=ME1 · diamond=Alquiler · triangle=Venta por fuera · hexagon=cargada a mano.
        // El color sigue indicando repartidor/asignación; la forma indica el TIPO.
        const shape = first.shape || 'circle';
        let headPath, labelY;
        switch (shape) {
            case 'square':   // ME1 — cuadrado con puntita
                headPath = 'M13 4 H35 Q38 4 38 7 V27 Q38 30 35 30 H29 L24 44 L19 30 H13 Q10 30 10 27 V7 Q10 4 13 4 Z';
                labelY = 17;
                break;
            case 'hexagon':  // Cargada a MANO (o favorito) — hexágono con puntita
                // Techo y piso rectos + puntas a los costados: a 28px se distingue tanto de la
                // gota (redonda) como del cuadrado (esquinas redondeadas). Adentro entra el número.
                headPath = 'M16 5 H32 L38 18 L32 31 H28 L24 44 L20 31 H16 L10 18 Z';
                labelY = 18;
                break;
            case 'diamond':  // Alquiler — rombo (cometa)
                headPath = 'M24 3 L39 19 L24 44 L9 19 Z';
                labelY = 15;
                break;
            case 'triangle': // Venta por fuera — triángulo apuntando a la ubicación
                headPath = 'M10 7 Q10 5 12 5 H36 Q38 5 38 7 L24 44 Z';
                labelY = 13;
                break;
            default:         // circle — Flex (gota redonda de siempre)
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

        // Arriba a la izquierda del pin va UN solo sello, nunca los dos:
        //   ✓ verde  = entregado (lo confirmó MeLi o lo marcaron a mano).
        //   ✗ roja   = "cerrada sin entregar": cancelada, no la encontró, o MercadoLibre avisó que
        //              el envío no se entregó / vuelve al remitente. Ya no hay nada que hacer, pero
        //              NUNCA llegó al cliente — por eso va en rojo.
        // Si en el mismo domicilio hay una entregada y una cancelada, gana el tilde.
        const delivered = group.some(x => x.delivered === true);
        const failed = !delivered && group.some(x => x.failed === true);
        // 2026-09-03: ATRASADO — MercadoLibre lo prometio para un dia anterior y sigue sin entregar.
        // Va en el MISMO lugar que el tilde y la cruz porque los tres se excluyen: un atrasado no
        // esta entregado (no hay tilde) ni cerrado (no hay cruz), asi que ese espacio esta libre.
        // Naranja y no rojo: el rojo del mapa significa "no se pudo entregar", esto es "todavia puedo".
        const lateDias = (!delivered && !failed)
            ? Math.max.apply(null, group.map(x => (typeof x.late === 'number' && x.late > 0) ? x.late : 0))
            : 0;
        const check = delivered
            ? `<circle cx="10" cy="8" r="8.5" fill="#16a34a" stroke="#ffffff" stroke-width="2"/>` +
              `<path d="M5.8 8.2 L8.6 11 L14 5.4" fill="none" stroke="#ffffff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>`
            : failed
            ? `<circle cx="10" cy="8" r="8.5" fill="#dc2626" stroke="#ffffff" stroke-width="2"/>` +
              `<path d="M6.9 4.9 L13.1 11.1 M13.1 4.9 L6.9 11.1" fill="none" stroke="#ffffff" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>`
            : lateDias > 0
            ? `<circle cx="10" cy="8" r="9" fill="#ea580c" stroke="#ffffff" stroke-width="2"/>` +
              `<text x="10" y="11.4" text-anchor="middle" font-size="${lateDias > 9 ? 8 : 9.5}" font-weight="800" fill="#ffffff" font-family="Inter,Arial,sans-serif">${lateDias > 99 ? '+' : lateDias}d</text>`
            : '';

        // Cartelito "OJO, calle no asfaltada" abajo a la izquierda del pin: para estar atentos
        // en ese envío sin abrir el globito. Marrón = tierra · gris piedra = empedrado.
        // Lo prende el pase de fondo que consulta el tipo de calle (first.surface).
        const comercial = group.some(x => x.comercial === true);
        const surf = first.surface;
        const surfaceBadge = (surf === 'tierra' || surf === 'empedrado')
            ? `<circle cx="10" cy="30" r="8.5" fill="${surf === 'tierra' ? '#b45309' : '#57534e'}" stroke="#ffffff" stroke-width="2"/>` +
              `<text x="10" y="34" text-anchor="middle" font-size="12" font-weight="900" fill="#ffffff" font-family="Inter,Arial,sans-serif">!</text>`
            : '';

        return `<svg xmlns="http://www.w3.org/2000/svg" width="${PIN_VB_W}" height="${PIN_VB_H}" viewBox="0 0 ${PIN_VB_W} ${PIN_VB_H}">` +
            `<defs><filter id="sh" x="-40%" y="-40%" width="180%" height="180%"><feDropShadow dx="0" dy="1.5" stdDeviation="1.5" flood-opacity="0.4"/></filter></defs>` +
            `${ring}` +
            // 2026-09-03: si el domicilio es COMERCIAL el pin lleva borde oscuro en vez de blanco.
            // Es la mitad de la señal (la otra es el toldo de arriba): aunque el toldo quede tapado
            // por otro pin, el borde ya te dice que ahi hay un negocio.
            `<path d="${headPath}" fill="${body}" stroke="${comercial ? '#1f2937' : '#ffffff'}" stroke-width="2" filter="url(#sh)"/>` +
            `<text x="${PIN_HEAD_CX}" y="${labelY + fs * 0.35}" text-anchor="middle" font-size="${fs}" font-weight="800" fill="${txt}" font-family="Inter,Arial,sans-serif">${label}</text>` +
            `${badge}${check}${surfaceBadge}</svg>`;
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

    // ══════════ Fotito de Street View para el globito ══════════
    // Cuando se abre un globito de un domicilio, le agregamos arriba una miniatura de
    // "cómo se ve la calle" en esa dirección (Street View estático de Google). Primero
    // consultamos el metadata (gratis) para saber si hay foto: si no hay, no mostramos
    // nada (así nunca aparece el cartel gris de "sin imágenes").
    // Cada apertura de globito incrementa svSeq: si el usuario abre otro antes de que
    // llegue la foto, la respuesta vieja se descarta y no pisa el globito nuevo.
    let svSeq = 0;

    // Bumpea el contador para cancelar cualquier foto de Street View en vuelo.
    function cancelStreetView() { svSeq++; }

    // Cartelito de TIPO DE CALLE según lo que dedujo la IA (asfalto/tierra/empedrado).
    // Solo se muestra cuando hay una respuesta clara; en 'no_seguro'/'sin_foto' no ensucia.
    function surfaceChipHtml(tipo) {
        let emoji, label, bg, fg;
        switch (tipo) {
            case 'asfalto':   emoji = '🛣️'; label = 'Asfalto';   bg = '#e5e7eb'; fg = '#374151'; break;
            case 'tierra':    emoji = '🟤'; label = 'Tierra';    bg = '#fdead0'; fg = '#92400e'; break;
            case 'empedrado': emoji = '🧱'; label = 'Empedrado'; bg = '#e7e0d8'; fg = '#78350f'; break;
            default: return ''; // no_seguro / sin_foto → no mostramos nada
        }
        return '<div style="display:inline-flex;align-items:center;gap:4px;margin:0 0 7px;'
            + 'padding:2px 9px;border-radius:999px;font-size:0.72rem;font-weight:800;'
            + 'background:' + bg + ';color:' + fg + ';font-family:Inter,Arial,sans-serif;" '
            + 'title="Tipo de calle deducido de la foto de Street View">'
            + emoji + ' ' + label + '</div>';
    }

    // iw = InfoWindow ya abierto; baseHtml = el contenido actual (sin foto ni cartelito).
    // enabled=false solo cancela pedidos viejos (para clusters o el punto de partida).
    // Trae DOS cosas en paralelo y las va mostrando arriba del contenido a medida que llegan:
    //   1) la fotito de Street View de la fachada
    //   2) el cartelito de tipo de calle (lo deduce el servidor con IA, cacheado por domicilio)
    // El guard svSeq descarta respuestas viejas si se abrió otro globito mientras tanto.
    // 2026-08-12: la foto de Street View NO se pide sola (Google cobra por cada foto). En su lugar
    // mostramos un botón "Ver la calle" y recién al tocarlo se trae la foto. svLoader guarda el
    // cargador del globito abierto; window.__mapeoVerCalle lo dispara desde el botón.
    let svLoader = null;
    function streetView(iw, lat, lng, baseHtml, enabled) {
        const myId = ++svSeq;
        svLoader = null;
        if (!enabled || lat == null || lng == null) return;
        const loc = (+lat).toFixed(6) + ',' + (+lng).toFixed(6);
        const slot = { chip: '', thumb: '' };
        const render = function () { if (myId === svSeq) iw.setContent(wrapIw(slot.chip + slot.thumb + baseHtml)); };

        // 1) Street View: botón "Ver la calle" (no gasta foto hasta que lo aprietan).
        if (browserKey) {
            slot.thumb = '<button type="button" onclick="window.__mapeoVerCalle()" '
                + 'style="display:block;width:100%;margin:0 0 8px;padding:8px;border:1px solid #d1d5db;'
                + 'background:#f9fafb;border-radius:8px;cursor:pointer;font-size:13px;font-weight:600;color:#374151;">'
                + '👁️ Ver la calle</button>';
            render();
            svLoader = function () {
                if (myId !== svSeq) return;
                slot.thumb = '<div style="margin:0 0 8px;padding:8px;text-align:center;color:#6b7280;font-size:12px;">Cargando la calle…</div>';
                render();
                const metaUrl = 'https://maps.googleapis.com/maps/api/streetview/metadata?location='
                    + loc + '&source=outdoor&key=' + encodeURIComponent(browserKey);
                fetch(metaUrl)
                    .then(r => r.json())
                    .then(meta => {
                        if (myId !== svSeq) return;
                        if (!meta || meta.status !== 'OK') {
                            slot.thumb = '<div style="margin:0 0 8px;padding:8px;text-align:center;color:#92400e;'
                                + 'background:#fffbeb;border:1px solid #fde68a;border-radius:8px;font-size:12px;">'
                                + '📷 No hay Street View en este punto</div>';
                            render();
                            return;
                        }
                        // Foto al DOBLE de tamaño (600x340) mostrada a ~150px: queda más nítida.
                        const imgUrl = 'https://maps.googleapis.com/maps/api/streetview?size=600x340&location='
                            + loc + '&fov=80&source=outdoor&key=' + encodeURIComponent(browserKey);
                        const panoUrl = 'https://www.google.com/maps/@?api=1&map_action=pano&viewpoint=' + loc;
                        slot.thumb = '<a href="' + panoUrl + '" target="_blank" rel="noopener" '
                            + 'title="Ver la calle en Google Street View" '
                            + 'style="display:block;margin:0 0 8px;border-radius:8px;overflow:hidden;'
                            + 'border:1px solid #e5e7eb;line-height:0;">'
                            + '<img src="' + imgUrl + '" alt="Vista de la calle en el domicilio" '
                            + 'style="width:100%;height:150px;object-fit:cover;display:block;"/></a>';
                        render();
                    })
                    .catch(function () { slot.thumb = ''; render(); });
            };
        }

        // 2) Cartelito de tipo de calle (lo calcula el servidor con IA; queda cacheado).
        fetch('/api/mapeo/stops/surface?lat=' + encodeURIComponent(loc.split(',')[0])
                + '&lng=' + encodeURIComponent(loc.split(',')[1]), { credentials: 'same-origin' })
            .then(r => r.ok ? r.json() : null)
            .then(s => {
                if (myId !== svSeq || !s) return;
                const chip = surfaceChipHtml(s.tipo);
                if (chip) { slot.chip = chip; render(); }
            })
            .catch(function () { });
    }
    // Lo llama el botón "Ver la calle" del globito: trae la foto del punto abierto.
    window.__mapeoVerCalle = function () { if (svLoader) svLoader(); };

    // Exponer helpers al scope de este archivo (los dos módulos de abajo los usan).
    // Envuelve el contenido del globito con un tope de alto (scroll adentro): así, aunque el envío tenga
    // mucho texto (mensajes + moliendas + foto), la ventanita nunca se pasa de la pantalla y la X para
    // cerrar siempre queda a la vista.
    function wrapIw(html) { return '<div class="mapeo-iw">' + (html || '') + '</div>'; }


    // ── Cartelito con las 3 primeras letras del repartidor, al lado del globito ──
    // 2026-08-31: de un vistazo se ve DE QUIÉN es cada envío sin abrirlo ni mirar el color.
    // Devuelve el icono de Google listo, con el anchor corrido para que el cartelito
    // quede a la DERECHA de la cabeza del pin (nunca encima).
    const TAG_H = 18;                  // alto del cartelito en px (ni grande ni chico)
    function tagIcon(text, color) {
        const t = escapeXml(('' + text).toUpperCase());
        const w = Math.round(12 + t.length * 8.2);
        const bg = color || '#1d4ed8';
        const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + TAG_H + '" viewBox="0 0 ' + w + ' ' + TAG_H + '">' +
            '<rect x="1" y="1" width="' + (w - 2) + '" height="' + (TAG_H - 2) + '" rx="6" ry="6" fill="' + bg + '" stroke="#ffffff" stroke-width="2"/>' +
            '<text x="' + (w / 2) + '" y="' + (TAG_H / 2 + 4) + '" text-anchor="middle" font-size="11" font-weight="800" fill="#ffffff" font-family="Inter,Arial,sans-serif" letter-spacing="0.4">' + t + '</text>' +
            '</svg>';
        return {
            url: 'data:image/svg+xml;charset=UTF-8,' + encodeURIComponent(svg),
            scaledSize: new google.maps.Size(w, TAG_H),
            // anchor negativo en X = el cartelito se dibuja a la derecha del punto;
            // en Y lo subimos a la altura de la cabeza del pin.
            anchor: new google.maps.Point(-16, TAG_H / 2 + 28)
        };
    }

    // ── Banderita a cuadros: "este chofer terminó su recorrido" ──
    // 2026-09-02: se planta sobre la ÚLTIMA parada que entregó un chofer que ya entregó TODAS
    // las suyas (llegó a la meta). Es blanco y negro a propósito: los colores del mapa son de
    // los repartidores y de las alarmas, la banderita no compite con ellos.
    // Mismo patrón que tagIcon: un segundo Marker con SVG inline (sin imágenes externas) y el
    // anchor corrido, en este caso a la IZQUIERDA de la cabeza del pin (a la derecha ya están
    // el cartelito de las 3 letras y el autito 🚗, así no se pisan).
    const FLAG_W = 32, FLAG_H = 32;
    function flagIcon() {
        const cell = 5, x0 = 8, y0 = 3, cols = 4, rows = 3;
        let cuadros = '';
        for (let r = 0; r < rows; r++) {
            for (let c = 0; c < cols; c++) {
                if ((r + c) % 2 === 0) {
                    cuadros += '<rect x="' + (x0 + c * cell) + '" y="' + (y0 + r * cell) + '" width="' + cell + '" height="' + cell + '" fill="#111827"/>';
                }
            }
        }
        const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="' + FLAG_W + '" height="' + FLAG_H + '" viewBox="0 0 ' + FLAG_W + ' ' + FLAG_H + '">' +
            // Mástil (con halo blanco para que se lea sobre cualquier mapa).
            '<rect x="4.2" y="2" width="3" height="28" rx="1.4" fill="#111827" stroke="#ffffff" stroke-width="1.2"/>' +
            // Paño: fondo blanco + cuadraditos negros alternados + borde negro.
            '<rect x="' + x0 + '" y="' + y0 + '" width="' + (cols * cell) + '" height="' + (rows * cell) + '" fill="#ffffff"/>' +
            cuadros +
            '<rect x="' + x0 + '" y="' + y0 + '" width="' + (cols * cell) + '" height="' + (rows * cell) + '" fill="none" stroke="#111827" stroke-width="1.4"/>' +
            '</svg>';
        return {
            url: 'data:image/svg+xml;charset=UTF-8,' + encodeURIComponent(svg),
            scaledSize: new google.maps.Size(FLAG_W, FLAG_H),
            // anchor.x mayor que el ancho = la banderita se dibuja a la IZQUIERDA del punto;
            // en Y queda a la altura de la cabeza del pin (nunca encima de la punta).
            anchor: new google.maps.Point(FLAG_W + 12, FLAG_H / 2 + 28)
        };
    }

    // ── Cartelito "COM" = el domicilio es COMERCIAL (así sale en la etiqueta del Flex) ──
    // 2026-09-02: se ve DESDE AFUERA, sin abrir el globito, porque cambia con qué se encuentra
    // el repartidor y a qué hora conviene ir (un negocio no atiende igual que una casa).
    // Mismo patrón que tagIcon/flagIcon (segundo Marker con SVG inline y el anchor corrido), pero
    // ARRIBA de la cabeza del pin: a la derecha ya viven las 3 letras del repartidor y el autito,
    // y a la izquierda la banderita de "terminó".
    // Gris grafito a propósito: no es una alarma, es un dato — los colores son de los repartidores.
    // 2026-09-03 (2da vuelta): el toldo arrancó del ANCHO DEL PIN ENTERO y en el mapa real, con
    // 40 pines juntos, tapaba media Capital ("es una brutalidad"). Ahora mide el ancho de la CABEZA
    // del pin, la mitad de alto, y va DETRÁS del pin (zIndex por debajo): asoma solo el techo, como
    // un toldo de verdad. De paso el pin siempre gana, así que nunca tapa el número ni la chapita
    // naranja de atrasado, y no hace falta correrlo para esquivarlas.
    const TOLDO_W = 30, TOLDO_H = 16;
    function toldoIcon() {
        const n = 5, w = 26, h = 8, sw = w / n, x0 = 2, y0 = 4;
        let rayas = '';
        for (let i = 0; i < n; i++) {
            const c = (i % 2) ? '#ffffff' : '#dc2626';
            rayas += '<rect x="' + (x0 + i * sw).toFixed(2) + '" y="' + y0 + '" width="' + sw.toFixed(2) + '" height="' + h + '" fill="' + c + '"/>';
        }
        for (let i = 0; i < n; i++) {
            const c = (i % 2) ? '#ffffff' : '#dc2626';
            rayas += '<circle cx="' + (x0 + i * sw + sw / 2).toFixed(2) + '" cy="' + (y0 + h) + '" r="' + (sw / 2).toFixed(2) + '" fill="' + c + '"/>';
        }
        const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="' + TOLDO_W + '" height="' + TOLDO_H + '" viewBox="0 0 ' + TOLDO_W + ' ' + TOLDO_H + '">' +
            rayas +
            // barral de arriba, oscuro, que le da el remate de toldo
            '<rect x="' + (x0 - 1.5) + '" y="' + (y0 - 3.2) + '" width="' + (w + 3) + '" height="3.4" rx="1.7" fill="#1f2937"/>' +
            '</svg>';
        return {
            url: 'data:image/svg+xml;charset=UTF-8,' + encodeURIComponent(svg),
            scaledSize: new google.maps.Size(TOLDO_W, TOLDO_H),
            // centrado en X y METIDO detrás de la cabeza del pin (la punta cae en 44): asoma el techo.
            anchor: new google.maps.Point(TOLDO_W / 2, TOLDO_H + 34)
        };
    }

    window.__mapeoHelpers = { ensureGoogle, ZONE_COLORS, escapeXml, markerSvg, markerIcon, tagIcon, flagIcon, toldoIcon, streetView, cancelStreetView, wrapIw };
})();

// ══════════ Mapa grande (pantalla Mapeo) ══════════
window.mapeoFlex = (function () {
    const H = window.__mapeoHelpers;
    let map = null;
    let markers = [];
    let snapMarkers = [];         // pines del histórico (foto de un día anterior) — modo solo mirar
    let infoWindow = null;
    let infoOpen = false;         // ¿hay un globito (popup) abierto? el refresco automático no lo pisa
    // 2026-08-15: relojitos del LATIDO de los autitos. Se guardan para poder apagarlos en cada
    // redibujo: si no, cada refresco del mapa dejaba uno nuevo corriendo y se iban acumulando.
    let autoBlinkTimers = [];
    let dotNetRef = null;
    let zonePolygon = null;       // polígono que el usuario dibuja a mano (esquina por esquina) — el relleno del área
    let zoneLine = null;          // línea que UNE los puntos mientras dibujás (la que se ve trazándose)
    let zonePath = null;          // lista de puntos (MVCArray) del polígono — la manejamos nosotros
    let zoneClickListener = null; // listener de clicks del mapa mientras dibuja
    let zoneVertexMarkers = [];   // puntitos que se ven en cada esquina tocada (feedback visual)
    let routeLines = [];          // líneas de ruta dibujadas (una por repartidor)
    let routeLabels = [];         // cartelitos flotantes de tiempo/km sobre cada línea (estilo Google Maps)
    let routeInfo = null;         // popup que se abre al tocar una línea (muestra distancia del tramo + total)
    let trafficLayer = null;      // capa de tráfico de Google (rojo/amarillo/verde en las calles)
    let lastFitStops = -1; // cuántas paradas (sin contar el punto de partida) había en el último auto-encuadre

    // ── Armar ruta INTERACTIVO (estilo Google Maps: pinchás punto por punto y se va dibujando) ──
    // Mientras armarOn está encendido, tocar un pin lo AGREGA a la ruta (no abre el globito): se dibuja
    // el tramo nuevo por las calles (una consulta chica al server) con su cartelito de tiempo/km, y arriba
    // se va sumando el total. Es distinto de drawRoutes (que dibuja la ruta YA guardada de una).
    let armarOn = false;            // ¿estamos armando una ruta a mano?
    let armarDesdeDeposito = false; // arrancar la línea desde el depósito (si no, desde el 1er pin tocado)
    let armarDepot = null;          // {lat,lng} del depósito
    let armarSeq = [];              // pines tocados EN ORDEN: [{id, lat, lng}]
    let armarLegs = [];             // tramos dibujados: [{lines:[Polyline...], label, sec, m}]
    let armarNums = [];             // marcadores con el numerito (1,2,3…) en cada punto tocado
    let armarDepotMarker = null;    // marcador de la casita de arranque (si arranca del depósito)
    let armarTotSec = 0, armarTotM = 0; // total acumulado (segundos y metros)
    let armarBusy = false;          // true mientras se trae un tramo de Google (evita doble-toque)
    let armarMostrarTiempos = true; // ¿mostrar los cartelitos de tiempo/km de cada tramo? (botón "Tiempos")
    const ARMAR_COLOR = '#1d4ed8';

    // ── Cartelito de "calle no asfaltada" en los pines (tierra/empedrado) ──
    // Al dibujar los pines lanzamos un pase de fondo que le pregunta al servidor el tipo de
    // calle de cada domicilio; cuando es tierra o empedrado, le agregamos el cartelito ! al pin.
    // Así el usuario ve DE UN VISTAZO cuáles envíos van por calle no asfaltada, sin abrir el globito.
    const surfaceMem = {};        // "lat,lng" -> tipo (memoria de la sesión, para no repreguntar)
    let surfaceQueue = [];        // pines pendientes de consultar { marker, group, loc, ver }
    let surfaceActive = 0;        // consultas en vuelo (limitamos cuántas a la vez)
    let markersVersion = 0;       // sube en cada renderMarkers: descarta pines de un dibujo viejo
    const SURFACE_MAX_PARALELO = 4;

    // Le pone (o saca) el cartelito al pin según el tipo de calle que llegó.
    function applySurfaceBadge(job, tipo) {
        if (job.ver !== markersVersion) return; // se redibujó el mapa: este pin ya no existe
        if (tipo !== 'tierra' && tipo !== 'empedrado') return;
        job.group.forEach(g => { g.surface = tipo; });
        try { job.marker.setIcon(H.markerIcon(H.markerSvg(job.group), 50)); } catch (e) {}
    }

    // Procesa la cola de consultas de tipo de calle respetando el límite de paralelo.
    function pumpSurface() {
        while (surfaceActive < SURFACE_MAX_PARALELO && surfaceQueue.length) {
            const job = surfaceQueue.shift();
            if (job.ver !== markersVersion) continue; // ya viejo
            // Si ya lo sabemos de esta sesión, aplicamos sin volver a pedir.
            if (Object.prototype.hasOwnProperty.call(surfaceMem, job.loc)) {
                applySurfaceBadge(job, surfaceMem[job.loc]);
                continue;
            }
            surfaceActive++;
            const parts = job.loc.split(',');
            fetch('/api/mapeo/stops/surface?lat=' + encodeURIComponent(parts[0]) + '&lng=' + encodeURIComponent(parts[1]),
                { credentials: 'same-origin' })
                .then(r => r.ok ? r.json() : null)
                .then(s => {
                    const t = (s && s.tipo) ? s.tipo : 'no_seguro';
                    surfaceMem[job.loc] = t;
                    applySurfaceBadge(job, t);
                })
                .catch(function () { })
                .finally(function () { surfaceActive--; pumpSurface(); });
        }
    }
    // "Ver dirección al tocar" (geocodificación inversa): cuando está encendido, un click en el
    // mapa muestra la calle+número más cercanos. Es un modo que se prende/apaga desde el buscador.
    let reverseGeoMode = false;
    let reverseGeoListener = null;
    let lastReverseAddr = '';

    // Limpia el estado del dibujo de zona (saca el polígono, los puntitos, el listener y el cursor).
    function cleanupZone() {
        if (zoneClickListener) { google.maps.event.removeListener(zoneClickListener); zoneClickListener = null; }
        if (zonePolygon) { zonePolygon.setMap(null); zonePolygon = null; }
        if (zoneLine) { zoneLine.setMap(null); zoneLine = null; }
        zonePath = null;
        for (const m of zoneVertexMarkers) m.setMap(null);
        zoneVertexMarkers = [];
        if (map) map.setOptions({ draggableCursor: null });
    }

    // Vista por defecto: todo el AMBA (CABA + conurbano + La Plata), como pidió el usuario.
    const AMBA_CENTER = { lat: -34.72, lng: -58.52 };
    const AMBA_ZOOM = 10;

    // ── "Qué compró" + "Mensajes de la venta" dentro del globito (solo paradas Flex/ME1) ──
    // El globito trae un cajón vacío <div class="mapeo-vinfo" data-ship="{nºenvío}">. Al abrirse el
    // globito (evento domready) pedimos /api/mapeo/stops/venta-info y lo llenamos. Guardamos el
    // resultado por envío (vinfoStore) para no volver a pedirlo si el globito se re-dibuja (p. ej.
    // cuando entra la fotito de Street View, que re-setea el contenido y vuelve a disparar domready).
    const vinfoStore = {};                 // ship -> { status:'loading'|'done', html }
    const VINFO_LOADING = "<div style='color:#94a3b8;font-size:0.72rem;margin-top:0.4rem;'>Buscando la venta…</div>";

    function buildVentaHtml(data) {
        if (!data || !data.ok) return '';
        let html = '';
        const prods = data.productos || [];
        if (prods.length) {
            html += "<div style='margin-top:0.45rem;border-top:1px solid #e5e7eb;padding-top:0.4rem;'>";
            html += "<div style='font-weight:700;font-size:0.72rem;color:#334155;margin-bottom:0.3rem;'>🛒 Qué compró</div>";
            for (const p of prods) {
                const img = p.thumbnail
                    ? "<img src='" + H.escapeXml(p.thumbnail) + "' style='width:34px;height:34px;object-fit:cover;border-radius:5px;flex:0 0 auto;border:1px solid #e5e7eb;'/>"
                    : "";
                const cant = p.cantidad ? "<span style='font-weight:700;'>×" + p.cantidad + "</span> " : "";
                html += "<div style='display:flex;gap:0.4rem;align-items:center;margin-bottom:0.3rem;'>" + img
                     + "<span style='font-size:0.72rem;line-height:1.25;'>" + cant + H.escapeXml(p.titulo || '') + "</span></div>";
            }
            html += "</div>";
        }
        const msgs = data.mensajes || [];
        if (msgs.length) {
            html += "<div style='margin-top:0.45rem;border-top:1px solid #e5e7eb;padding-top:0.4rem;'>";
            html += "<div style='font-weight:700;font-size:0.72rem;color:#334155;margin-bottom:0.3rem;'>📩 Mensajes de la venta</div>";
            for (const m of msgs) {
                const esComp = m.de === 'comprador';
                const quien = esComp ? '🛒 Comprador' : '🏪 Vos';
                const bg = esComp ? '#eff6ff' : '#f0fdf4';
                html += "<div style='background:" + bg + ";border-radius:6px;padding:0.3rem 0.45rem;margin-bottom:0.25rem;font-size:0.72rem;line-height:1.3;'>"
                     + "<span style='font-weight:700;'>" + quien + ":</span> " + H.escapeXml(m.texto || '') + "</div>";
            }
            html += "</div>";
        }
        return html;
    }

    // Busca todos los cajones .mapeo-vinfo del globito abierto y los llena (con caché por envío).
    function fillVentaInfo() {
        const nodes = document.querySelectorAll('.mapeo-vinfo[data-ship]');
        nodes.forEach(node => {
            const ship = node.getAttribute('data-ship');
            if (!ship || ship === '0') return;
            const cached = vinfoStore[ship];
            if (cached && cached.status === 'done') { node.innerHTML = cached.html; return; }
            if (cached && cached.status === 'loading') { node.innerHTML = VINFO_LOADING; return; }
            vinfoStore[ship] = { status: 'loading', html: '' };
            node.innerHTML = VINFO_LOADING;
            fetch('/api/mapeo/stops/venta-info?shipmentId=' + encodeURIComponent(ship), { credentials: 'same-origin' })
                .then(r => r.ok ? r.json() : null)
                .then(data => {
                    const html = buildVentaHtml(data);
                    vinfoStore[ship] = { status: 'done', html };
                    // El globito puede haberse re-dibujado mientras tanto: buscamos el cajón actual.
                    const n = document.querySelector(".mapeo-vinfo[data-ship='" + ship + "']");
                    if (n) n.innerHTML = html;
                })
                .catch(() => {
                    vinfoStore[ship] = { status: 'done', html: '' };
                    const n = document.querySelector(".mapeo-vinfo[data-ship='" + ship + "']");
                    if (n) n.innerHTML = '';
                });
        });
    }

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

    // ── Tránsito sobre la línea (rojo/amarillo donde hay embotellamiento, tal cual Google Maps) ──
    // Google nos manda tramos {start,end,speed}: NORMAL (sin pintar, queda azul), SLOW (amarillo),
    // TRAFFIC_JAM (rojo). Los índices son sobre la polilínea YA decodificada (path).
    function trafficColor(speed) {
        if (speed === 'TRAFFIC_JAM') return '#dc2626'; // rojo = embotellado
        if (speed === 'SLOW') return '#f59e0b';        // amarillo/naranja = lento
        return null;                                    // NORMAL / desconocido → se ve el azul de base
    }
    // Dibuja los pedacitos de color por encima de la línea base. Empuja las polilíneas creadas a 'store'
    // (para poder borrarlas después). zBase = zIndex (va por encima de la línea base).
    // Cada tramo de tránsito lleva un BORDE BLANCO abajo: así el aviso SIEMPRE se ve, aunque la ruta sea
    // del mismo color (ej. una zona roja con un embotellamiento rojo) — el contorno blanco lo despega.
    function paintTrafficSlices(path, intervals, store, zBase) {
        if (!path || !intervals || !intervals.length) return;
        for (const iv of intervals) {
            const col = trafficColor(iv.speed);
            if (!col) continue;
            const a = Math.max(0, iv.start | 0);
            const b = Math.min(path.length - 1, iv.end | 0);
            if (b <= a) continue;
            const slice = path.slice(a, b + 1);
            if (slice.length < 2) continue;
            // 1) borde blanco (más ancho) para separar el aviso de la línea de la ruta
            store.push(new google.maps.Polyline({
                path: slice, map: map, clickable: false,
                strokeColor: '#ffffff', strokeOpacity: 1, strokeWeight: 10, zIndex: zBase
            }));
            // 2) el color del tránsito (amarillo/rojo) encima del borde blanco
            store.push(new google.maps.Polyline({
                path: slice, map: map, clickable: false,
                strokeColor: col, strokeOpacity: 1, strokeWeight: 6, zIndex: zBase + 1
            }));
        }
    }

    // ── Helpers del modo "Armar ruta" interactivo ──
    const armarFmtKm = (m) => (m / 1000).toFixed(1).replace('.', ',');
    const armarFmtMin = (s) => {
        const min = Math.round(s / 60);
        if (min < 60) return min + ' min';
        const h = Math.floor(min / 60), mm = min % 60;
        return mm ? (h + 'h ' + mm + 'min') : (h + 'h');
    };

    // Cartelito flotante (tiempo + km) posado sobre un tramo, igual estilo que drawRoutes.
    function armarLabelOverlay(position, text, color) {
        const ov = new google.maps.OverlayView();
        ov.onAdd = function () {
            const div = document.createElement('div');
            div.style.cssText = 'position:absolute; transform:translate(-50%,-50%); background:#fff; color:#111827; ' +
                'border:2px solid ' + color + '; border-radius:14px; padding:2px 9px; font-size:12px; font-weight:800; ' +
                'white-space:nowrap; box-shadow:0 2px 7px rgba(0,0,0,0.35); pointer-events:none;';
            div.textContent = text;
            this._div = div;
            this.getPanes().floatPane.appendChild(div);
        };
        ov.draw = function () {
            const proj = this.getProjection();
            if (!proj || !this._div) return;
            const p = proj.fromLatLngToDivPixel(position);
            if (p) { this._div.style.left = p.x + 'px'; this._div.style.top = p.y + 'px'; }
        };
        ov.onRemove = function () { if (this._div) { this._div.remove(); this._div = null; } };
        return ov;
    }

    // Marcador con el numerito del orden (1, 2, 3…) en cada punto tocado.
    function armarNumberMarker(lat, lng, n, color) {
        return new google.maps.Marker({
            position: { lat: lat, lng: lng }, map: map, clickable: false, zIndex: 6,
            label: { text: String(n), color: '#ffffff', fontSize: '12px', fontWeight: '800' },
            icon: { path: google.maps.SymbolPath.CIRCLE, scale: 13, fillColor: color, fillOpacity: 1, strokeColor: '#ffffff', strokeWeight: 2 }
        });
    }

    // Le avisa a Blazor el estado actual (cantidad de paradas + total tiempo/metros) para la barra flotante.
    function armarNotify() {
        if (dotNetRef) dotNetRef.invokeMethodAsync('OnArmarUpdate', armarSeq.length, Math.round(armarTotSec), Math.round(armarTotM));
    }

    // Trae el tramo de->to a Google (por las calles) y lo dibuja. Devuelve el objeto del tramo
    // {lines, label, sec, m} (o null si falla). NO lo agrega a la lista ni suma al total: de eso se
    // encarga quien llama (armarAddPoint lo agrega al final; el depósito lo inserta al principio).
    async function armarBuildLeg(from, to, color) {
        try {
            const url = '/api/mapeo/stops/leg?fromLat=' + from.lat + '&fromLng=' + from.lng +
                '&toLat=' + to.lat + '&toLng=' + to.lng;
            const r = await fetch(url, { credentials: 'same-origin' });
            const data = r.ok ? await r.json() : null;
            if (!data || !data.ok || !data.encoded || !google.maps.geometry) return null;
            const path = google.maps.geometry.encoding.decodePath(data.encoded);
            const casing = new google.maps.Polyline({
                path: path, map: map, strokeColor: '#ffffff', strokeOpacity: 0.9, strokeWeight: 9, zIndex: 4, clickable: false
            });
            const line = new google.maps.Polyline({
                path: path, map: map, strokeColor: color, strokeOpacity: 0.95, strokeWeight: 5, zIndex: 5, clickable: true,
                icons: [{ icon: { path: google.maps.SymbolPath.FORWARD_CLOSED_ARROW, scale: 2.6, strokeColor: '#ffffff', strokeWeight: 1.2, fillColor: color, fillOpacity: 1 }, offset: '0', repeat: '110px' }]
            });
            // Pintamos rojo/amarillo los pedacitos con embotellamiento sobre esta línea (queda azul lo normal).
            const trafficLines = [];
            paintTrafficSlices(path, data.transito, trafficLines, 6);
            const mid = path.length ? path[Math.floor(path.length / 2)] : new google.maps.LatLng(to.lat, to.lng);
            const txt = armarFmtKm(data.meters) + ' km · ' + armarFmtMin(data.seconds);
            // Tocar la línea del tramo muestra su tiempo/km (útil cuando los cartelitos están ocultos).
            line.addListener('click', function (e) {
                if (!routeInfo) routeInfo = new google.maps.InfoWindow();
                routeInfo.setContent('<div style="font-size:14px; font-weight:800; font-family:system-ui,sans-serif; color:#111827;">' + txt + '</div>');
                routeInfo.setPosition(e.latLng);
                routeInfo.open(map);
            });
            // El cartelito flotante con el tiempo/km: solo si están ENCENDIDOS (botón "Tiempos").
            let label = null;
            if (armarMostrarTiempos) { label = armarLabelOverlay(mid, txt, color); label.setMap(map); }
            return { lines: [casing, line].concat(trafficLines), label: label, sec: data.seconds, m: data.meters, mid: mid, txt: txt };
        } catch (e) { return null; }
    }

    // Borra TODO lo dibujado del armado (tramos, numeritos, casita) y resetea el estado.
    function armarClearInternal() {
        for (const leg of armarLegs) { leg.lines.forEach(l => l.setMap(null)); if (leg.label) leg.label.setMap(null); }
        armarLegs = [];
        for (const m of armarNums) m.setMap(null);
        armarNums = [];
        if (armarDepotMarker) { armarDepotMarker.setMap(null); armarDepotMarker = null; }
        armarSeq = [];
        armarTotSec = 0; armarTotM = 0;
        armarBusy = false;
    }

    // Dibuja (o saca) la casita del depósito en el mapa.
    function armarDrawDepotMarker() {
        if (armarDepotMarker || !armarDepot) return;
        armarDepotMarker = new google.maps.Marker({
            position: armarDepot, map: map, clickable: false, zIndex: 6,
            label: { text: '🏁', fontSize: '15px' },
            icon: { path: google.maps.SymbolPath.CIRCLE, scale: 13, fillColor: '#111827', fillOpacity: 1, strokeColor: '#ffffff', strokeWeight: 2 }
        });
    }

    // Agrega el pin tocado a la ruta: dibuja el tramo desde el punto anterior (o el depósito) hasta acá.
    async function armarAddPoint(id, lat, lng) {
        if (!armarOn || armarBusy) return;
        let prev = null;
        if (armarSeq.length === 0) {
            if (armarDesdeDeposito && armarDepot) { prev = armarDepot; armarDrawDepotMarker(); }
        } else {
            prev = armarSeq[armarSeq.length - 1];
        }
        armarSeq.push({ id: id, lat: lat, lng: lng });
        armarNums.push(armarNumberMarker(lat, lng, armarSeq.length, ARMAR_COLOR));
        if (prev) {
            armarBusy = true;
            armarNotify();               // muestra el nuevo conteo mientras Google calcula el tramo
            const leg = await armarBuildLeg(prev, { lat: lat, lng: lng }, ARMAR_COLOR);
            if (leg) { armarLegs.push(leg); armarTotSec += leg.sec; armarTotM += leg.m; }
            armarBusy = false;
        }
        armarNotify();
    }

    return {
        async init(elementId, dotnetHelper) {
            dotNetRef = dotnetHelper;
            window.mapeoActions = {
                toggleRoute: function (markerId) {
                    if (dotNetRef) dotNetRef.invokeMethodAsync('ToggleMarkerInRoute', markerId);
                },
                // 2026-07-29: cambiar/asignar el repartidor desde el globito del pin.
                assignDriver: function (markerId, driverId) {
                    if (dotNetRef) dotNetRef.invokeMethodAsync('AsignarRepartidorDesdePopup', markerId, parseInt(driverId, 10) || 0);
                },
                // 2026-08-06: mandar el envío a una zona desde el globito. 0 = sin zona, -1 = nueva zona.
                assignZone: function (markerId, slot) {
                    var n = parseInt(slot, 10);
                    if (isNaN(n)) return;
                    if (infoWindow) { infoWindow.close(); infoOpen = false; }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('AsignarZonaDesdePopup', markerId, n);
                },
                // 2026-07-30: corregir/mover la ubicación de este pin desde el globito.
                // Cerramos el globito primero: el modo edición redibuja SOLO ese pin con
                // keepView, y renderMarkers ignora el redibujo si hay un globito abierto.
                correctLocation: function (markerId) {
                    if (infoWindow) { infoWindow.close(); infoOpen = false; }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('CorregirUbicacionDesdePopup', markerId);
                },
                // 2026-08-15: la estrellita del globito. Un toque = guardar este lugar en favoritos
                // (o sacarlo, si ya lo era). NO cerramos el globito: el botón se repinta solo con la
                // estrella llena/vacía, así se ve al toque que quedó guardado.
                favStop: function (markerId, btn) {
                    // El globito abierto bloquea el redibujo del mapa (para no pisarte lo que estás
                    // mirando), así que la estrella la damos vuelta acá mismo: se ve al instante.
                    if (btn) {
                        var ahora = btn.getAttribute('data-fav') !== '1';
                        btn.setAttribute('data-fav', ahora ? '1' : '0');
                        btn.textContent = ahora ? '⭐ Favorito' : '☆ Hacer favorito';
                        btn.style.background = ahora ? '#f59e0b' : 'white';
                        btn.style.color = ahora ? '#ffffff' : '#92400e';
                        btn.title = ahora ? 'Ya está en tus favoritos — tocá para sacarlo' : 'Guardar este lugar en tus favoritos';
                    }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('ToggleFavoritoDesdePopup', markerId);
                },
                // 2026-08-15: editar nombre/contacto/teléfono/notas de esta parada desde el globito
                // (y vincularla a un cliente del sistema). Cerramos el globito: la ficha se abre
                // arriba del mapa, igual que cuando recién agregás una dirección con el buscador.
                editStop: function (markerId) {
                    if (infoWindow) { infoWindow.close(); infoOpen = false; }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('EditarParadaDesdePopup', markerId);
                },
                // 2026-07-30: quitar esta parada del mapa desde el globito.
                removeStop: function (markerId) {
                    if (infoWindow) { infoWindow.close(); infoOpen = false; }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('EliminarParadaDesdePopup', markerId);
                },
                // 2026-08-04: poner esta parada en un puesto puntual de la ruta y correr las demás.
                // Cerramos el globito antes de recalcular así renderMarkers puede redibujar los números.
                setOrder: function (markerId, pos) {
                    var n = parseInt(pos, 10);
                    if (!n || n < 1) { alert('Escribí un número de puesto (1, 2, 3…).'); return; }
                    if (infoWindow) { infoWindow.close(); infoOpen = false; }
                    if (dotNetRef) dotNetRef.invokeMethodAsync('PonerEnPuestoDesdePopup', markerId, n);
                },
                // "Ver dirección al tocar": copia al portapapeles la dirección del globito.
                copyReverseAddress: function (btn) {
                    if (!lastReverseAddr) return;
                    try { navigator.clipboard.writeText(lastReverseAddr); } catch (e) {}
                    if (btn) { btn.textContent = '✓ Copiado'; btn.disabled = true; btn.style.background = '#16a34a'; }
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
            infoWindow.addListener('closeclick', () => { infoOpen = false; });
            // Cada vez que el globito muestra su contenido (incluye el re-dibujo por la fotito de
            // Street View), llenamos el cajón "qué compró + mensajes" de las paradas Flex/ME1.
            infoWindow.addListener('domready', () => fillVentaInfo());
            markers = [];
            lastFitStops = -1;
            // Capa de tráfico: arranca APAGADA (como el Google Maps original). Se prende/apaga
            // con el botón del dock (setTraffic). Rojo=congestionado, amarillo=lento, verde=fluido.
            // NOTA: no cargamos el overlay de zonas AMBA (loadZones) — el usuario quiere el mapa
            // lo más parecido posible al Google Maps original, sin los tonos de colores encima.
        },

        // keepView = true: redibuja los globitos SIN mover el mapa (ni zoom ni centro).
        // Se usa en el refresco automático, para que los envíos escaneados aparezcan solos
        // sin saltarte la vista mientras estás mirando una zona.
        // flags = banderitas 🏁 de los choferes que YA terminaron todo su recorrido: [{lat, lng, title}].
        // Vienen aparte de los globitos porque la parada donde va la bandera está entregada y puede
        // estar escondida por el filtro "no mostrar entregados" (si viniera en items, además, se
        // agruparía con el pin y lo haría parecer "2 entregas en este domicilio").
        renderMarkers(items, keepView, flags) {
            if (!map || !window.google) return;
            // Refresco automático con un globito abierto: no lo pisamos, lo dejamos como está.
            if (keepView && infoOpen) return;
            for (const t of autoBlinkTimers) clearInterval(t);
            autoBlinkTimers = [];
            for (const m of markers) m.setMap(null);
            markers = [];
            this.clearRoutes(); // las líneas viejas se borran; se redibujan al optimizar

            // Nuevo dibujo: descartamos las consultas de tipo de calle del dibujo anterior.
            markersVersion++;
            surfaceQueue = [];

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
                // Mostramos la fotito de Street View solo en un domicilio único (no en clusters
                // ni en la casita del punto de partida, que se puede arrastrar).
                const conStreetView = !isCluster && !esArrastrable;
                // Abrir el globito de esta parada. Lo dejamos en una función aparte porque lo
                // dispara tanto el pin como el cartelito COM que le ponemos al lado.
                const abrirGlobito = () => {
                    // Modo "Armar ruta": el toque agrega el envío a la ruta (no abre el globito).
                    if (armarOn) { armarAddPoint(first.id, +first.lat, +first.lng); return; }
                    if (infoWindow) {
                        infoWindow.setContent(H.wrapIw(popupHtml));
                        infoWindow.open(map, marker);
                        infoOpen = true;
                        H.streetView(infoWindow, first.lat, first.lng, popupHtml, conStreetView);
                    }
                    if (!dotNetRef) return;
                    if (isCluster) dotNetRef.invokeMethodAsync('OnClusterClicked', ids);
                    else dotNetRef.invokeMethodAsync('OnMarkerClicked', first.id);
                };
                marker.addListener('click', abrirGlobito);

                marker.__ids = ids; // para poder "hacerlo saltar" cuando tocás su fila en el listado
                markers.push(marker);

                // 2026-08-31: CARTELITO con las 3 primeras letras del repartidor, pegado al globito.
                // Sale del campo 'tag' que manda Blazor (vacío = no se dibuja nada).
                if (first.tag) {
                    markers.push(new google.maps.Marker({
                        position: pos, map: map, clickable: false, zIndex: 1100,
                        icon: H.tagIcon(first.tag, first.tagColor || first.color)
                    }));
                }

                // 2026-09-02: CARTELITO "COM" arriba del pin cuando el domicilio es COMERCIAL
                // (dato de MercadoLibre, el mismo que sale en la etiqueta del Flex). Se ve sin abrir
                // el globito, y si lo pinchás se abre el mismo globito que el pin (ahí está el
                // comentario del comprador, que es donde avisan los horarios del negocio).
                if (group.some(g => g.comercial === true)) {
                    const comMarker = new google.maps.Marker({
                        position: pos, map: map, clickable: true, zIndex: 0,
                        title: 'Domicilio comercial — tocá para ver el comentario del comprador',
                        icon: H.toldoIcon()
                    });
                    comMarker.addListener('click', abrirGlobito);
                    markers.push(comMarker);
                }

                // 2026-08-15: AUTITO. Al lado de la ULTIMA parada que cada repartidor marco como
                // entregada le ponemos un 🚗 con su color: de un vistazo se ve por donde va cada uno,
                // sin GPS ni que el repartidor tenga que hacer nada extra (sale de lo que ya marca al
                // entregar). El anchor en negativo corre el circulito a la DERECHA del globito para
                // que no lo tape.
                const conAuto = group.find(g => g.ultimaEntrega);
                if (conAuto) {
                    const autoMarker = new google.maps.Marker({
                        position: pos,
                        map: map,
                        clickable: false,
                        zIndex: 1200,               // siempre por encima de los globitos
                        label: { text: '🚗', fontSize: '15px' },
                        icon: {
                            path: google.maps.SymbolPath.CIRCLE,
                            scale: 13,
                            fillColor: conAuto.color || '#111827',
                            fillOpacity: 1,
                            strokeColor: '#ffffff',
                            strokeWeight: 2,
                            anchor: new google.maps.Point(-2.0, 0.4)
                        }
                    });
                    markers.push(autoMarker);
                    // LATIDO: el circulito crece y se achica suave, como un pulso. Llama el ojo sin
                    // ser un parpadeo molesto (prender/apagar cansa la vista y se pierde de leer).
                    let paso = 0;
                    const baseIcon = autoMarker.getIcon();
                    autoBlinkTimers.push(setInterval(function () {
                        paso = (paso + 1) % 4;
                        const escalas = [13, 15.5, 17, 15.5];
                        autoMarker.setIcon(Object.assign({}, baseIcon, { scale: escalas[paso] }));
                    }, 380));
                }
                bounds.extend(pos);
                any = true;
                if (!esArrastrable) realStops++;

                // Encolamos este domicilio único para consultar su tipo de calle en el pase de fondo.
                if (conStreetView && first.lat != null && first.lng != null) {
                    surfaceQueue.push({
                        marker: marker, group: group,
                        loc: (+first.lat).toFixed(6) + ',' + (+first.lng).toFixed(6),
                        ver: markersVersion
                    });
                }
            }

            // 2026-09-02: 🏁 BANDERITA A CUADROS sobre la última entrega de cada chofer que terminó
            // TODO su recorrido. Se guardan en 'markers' para que el próximo dibujo las borre solas.
            // OJO: no tocamos routeLines ni llamamos a clearRoutes acá — las líneas de ruta las
            // repinta Blazor después de este render.
            if (Array.isArray(flags)) {
                for (const f of flags) {
                    if (!f || f.lat == null || f.lng == null) continue;
                    markers.push(new google.maps.Marker({
                        position: { lat: +f.lat, lng: +f.lng },
                        map: map,
                        clickable: false,
                        zIndex: 1300,           // por encima del pin, del cartelito y del autito
                        title: f.title || '',
                        icon: H.flagIcon()
                    }));
                }
            }

            // Arranca el pase de fondo que le pone el cartelito a los pines de tierra/empedrado.
            pumpSurface();

            // Encuadre inteligente:
            //  - Sin paradas: mostramos TODO el AMBA (aunque haya punto de partida).
            //  - Con paradas: encuadramos para que entren todas + el punto de partida.
            //    Solo reajustamos cuando ENTRÓ una parada nueva (no al asignar/tocar), para no
            //    pisarle el zoom al usuario. Al soltar la casita tampoco reencuadra.
            // Refresco automático: NO tocamos la vista, solo registramos el conteo.
            if (keepView) { lastFitStops = realStops; return; }
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
            infoOpen = false;
        },

        // Resalta el globito de un envío/parada: lo centra y lo hace "saltar" un ratito (para cuando
        // tocás su fila en el listado). id = mismo id que usa el marcador (shipment o -1000-stopId).
        highlightMarker(id) {
            if (!map) return;
            for (const m of markers) {
                if (m.__ids && m.__ids.indexOf(id) >= 0) {
                    try {
                        map.panTo(m.getPosition());
                        if (map.getZoom() < 15) map.setZoom(16);
                        m.setAnimation(google.maps.Animation.BOUNCE);
                        setTimeout(function () { try { m.setAnimation(null); } catch (e) {} }, 1500);
                    } catch (e) {}
                    return;
                }
            }
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

        // "Ver dirección al tocar" (geocodificación inversa). Mientras está ENCENDIDO, tocar
        // cualquier punto del mapa abre un globito con la calle+número más cercanos y un botón
        // para copiar. No pisa el dibujo de zonas (si estás dibujando, gana el dibujo) ni los
        // clicks sobre pines (esos abren su propio globito). Se apaga tocando de nuevo el botón.
        setReverseGeoMode(on) {
            reverseGeoMode = !!on;
            if (reverseGeoListener) { google.maps.event.removeListener(reverseGeoListener); reverseGeoListener = null; }
            if (!map) return;
            if (!reverseGeoMode) {
                map.setOptions({ draggableCursor: null });
                if (infoWindow) { infoWindow.close(); infoOpen = false; }
                return;
            }
            map.setOptions({ draggableCursor: 'help' }); // cursor con "?" = modo "¿qué dirección es?"
            reverseGeoListener = map.addListener('click', function (e) {
                if (zonePath) return; // si estás dibujando una zona, no interferimos
                const ll = e.latLng;
                const lat = ll.lat(), lng = ll.lng();
                H.cancelStreetView(); // por si venía una foto en vuelo de otro globito
                // Globito "Buscando…" inmediato, para que se sienta que registró el toque.
                infoWindow.setContent('<div style="font-family:Inter,sans-serif;font-size:0.85rem;color:#6b7280;padding:2px 4px;">Buscando dirección…</div>');
                infoWindow.setPosition(ll);
                infoWindow.open(map);
                infoOpen = true;
                // La geocodificación inversa la hace el SERVIDOR (clave con Geocoding API habilitada).
                fetch('/api/mapeo/stops/reverse-geocode?lat=' + lat + '&lng=' + lng, { credentials: 'same-origin' })
                    .then(function (r) { return r.ok ? r.json() : null; })
                    .then(function (data) {
                        const addr = data && data.address ? data.address : null;
                        lastReverseAddr = addr || '';
                        const shown = addr || 'No se encontró una dirección para este punto.';
                        let html = '<div style="font-family:Inter,sans-serif;min-width:190px;max-width:270px;">' +
                            '<div style="font-size:0.66rem;color:#6b7280;font-weight:700;margin-bottom:2px;letter-spacing:.03em;">📍 DIRECCIÓN APROXIMADA</div>' +
                            '<div style="font-size:0.92rem;color:#111827;font-weight:600;margin-bottom:7px;line-height:1.3;">' + H.escapeXml(shown) + '</div>';
                        if (addr) {
                            html += '<button onclick="window.mapeoActions.copyReverseAddress(this)" ' +
                                'style="font-size:0.78rem;padding:4px 11px;background:#2563eb;color:#fff;border:none;border-radius:6px;cursor:pointer;font-weight:600;">📋 Copiar</button>';
                        }
                        html += '</div>';
                        infoWindow.setContent(html);
                    })
                    .catch(function () {
                        infoWindow.setContent('<div style="font-family:Inter,sans-serif;font-size:0.85rem;color:#b91c1c;padding:2px 4px;">No se pudo consultar la dirección.</div>');
                    });
            });
        },

        // Dibujar una ZONA a mano: el usuario toca cada esquina en el mapa y se va armando un polígono.
        // (No usamos DrawingManager porque Google lo deprecó/quitó en v3.65+.)
        startDrawZone() {
            if (!map) return;
            cleanupZone();
            map.setOptions({ draggableCursor: 'crosshair' }); // cursor de cruz = estás dibujando
            zonePath = new google.maps.MVCArray();
            // Línea gruesa que UNE los puntos a medida que tocás. Es lo ÚNICO que dibujamos mientras
            // se marca (nada de polígono: compartir la lista de puntos con un polígono rompía el trazo).
            // El área encerrada la calcula el backend al Terminar (ray-casting sobre estos puntos).
            zoneLine = new google.maps.Polyline({
                map: map, path: zonePath,
                strokeColor: '#dc2626', strokeOpacity: 0.95, strokeWeight: 4,
                clickable: false, zIndex: 2
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

        // Dibuja las líneas de ruta (una por repartidor). routes = [{color, segments:[encoded...]}].
        // Compatibilidad: también acepta {color, encoded} (una sola línea).
        drawRoutes(routes) {
            this.clearRoutes();
            if (!map || !routes || !google.maps.geometry) return;
            // Flechita de sentido que se repite a lo largo de la línea (para qué lado va el recorrido).
            const flecha = {
                path: google.maps.SymbolPath.FORWARD_CLOSED_ARROW,
                scale: 2.6, strokeColor: '#ffffff', strokeWeight: 1.2, fillOpacity: 1
            };
            // Cartelito flotante (tiempo + km) posado sobre la línea, estilo Google Maps.
            const makeLabel = (position, text, color) => {
                const ov = new google.maps.OverlayView();
                ov.onAdd = function () {
                    const div = document.createElement('div');
                    div.style.cssText = 'position:absolute; transform:translate(-50%,-50%); background:#fff; color:#111827; ' +
                        'border:2px solid ' + color + '; border-radius:14px; padding:2px 9px; font-size:12px; font-weight:800; ' +
                        'white-space:nowrap; box-shadow:0 2px 7px rgba(0,0,0,0.35); pointer-events:none;';
                    div.textContent = text;
                    this._div = div;
                    this.getPanes().floatPane.appendChild(div);
                };
                ov.draw = function () {
                    const proj = this.getProjection();
                    if (!proj || !this._div) return;
                    const p = proj.fromLatLngToDivPixel(position);
                    if (p) { this._div.style.left = p.x + 'px'; this._div.style.top = p.y + 'px'; }
                };
                ov.onRemove = function () { if (this._div) { this._div.remove(); this._div = null; } };
                return ov;
            };
            // Popup reutilizable para mostrar la distancia al tocar la línea.
            if (!routeInfo) routeInfo = new google.maps.InfoWindow();
            const fmtKm = (m) => (m / 1000).toFixed(1).replace('.', ',');
            const fmtMin = (s) => {
                const min = Math.round(s / 60);
                if (min < 60) return min + ' min';
                const h = Math.floor(min / 60), mm = min % 60;
                return mm ? (h + 'h ' + mm + 'min') : (h + 'h');
            };

            for (const r of routes) {
                if (!r) continue;
                const color = r.color || '#1d4ed8';
                const total = r.label || '';
                const allPts = [];
                // Tramos (parada→parada) con su propia línea codificada: nos deja tocar cada tramo por separado.
                const legs = (r.legs || []).filter(l => l && l.encoded);

                // 2026-08-21: la LINEA se dibuja SIEMPRE ENTERA, igual de gorda de punta a punta.
                // Antes (2026-08-15) los tramos ya recorridos se apagaban (finitos, transparentes, sin
                // flechas) y el dueño lo vio como que "se iba borrando la línea". Lo que se mueve para
                // mostrar por dónde va el reparto es el 🚗, no la línea. El dato de cuántos tramos ya
                // hizo (r.legsHechos) sigue llegando pero acá no se usa.

                // Dibuja una línea (casing blanco + color con flechas) y le engancha el click.
                const dibujarLinea = (path, onClick) => {
                    routeLines.push(new google.maps.Polyline({
                        path: path, map: map,
                        strokeColor: '#ffffff', strokeOpacity: 0.9, strokeWeight: 9, zIndex: 4
                    }));
                    const line = new google.maps.Polyline({
                        path: path, map: map, clickable: true,
                        strokeColor: color,
                        strokeOpacity: 0.95,
                        strokeWeight: 5,
                        zIndex: 5,
                        // Flechas blancas cada ~110px indicando el sentido de circulación.
                        icons: [{ icon: Object.assign({}, flecha, { fillColor: color }), offset: '0', repeat: '110px' }]
                    });
                    if (onClick) line.addListener('click', onClick);
                    routeLines.push(line);
                    for (const pt of path) allPts.push(pt);
                };

                if (legs.length) {
                    // Modo detallado: una línea por tramo, cada una clickeable con su distancia.
                    for (let li = 0; li < legs.length; li++) {
                        const leg = legs[li];
                        const path = google.maps.geometry.encoding.decodePath(leg.encoded);
                        const titulo = 'Tramo ' + (leg.from || '') + ' → ' + (leg.to || '');
                        const html = '<div style="font-size:13px; line-height:1.45; font-family:system-ui,sans-serif;">' +
                            '<div style="font-weight:800; color:' + color + ';">' + titulo + '</div>' +
                            '<div style="font-size:15px; font-weight:800;">' + fmtKm(leg.meters) + ' km · ' + fmtMin(leg.seconds) + '</div>' +
                            (total ? '<div style="margin-top:5px; padding-top:5px; border-top:1px solid #eee; color:#6b7280;">Ruta completa: <strong>' + total + '</strong></div>' : '') +
                            '</div>';
                        dibujarLinea(path, (e) => { routeInfo.setContent(html); routeInfo.setPosition(e.latLng); routeInfo.open(map); });
                        // Pinta rojo/amarillo los pedacitos con embotellamiento de este tramo (lo normal queda azul).
                        paintTrafficSlices(path, leg.transito, routeLines, 6);
                    }
                } else {
                    // Respaldo: sin tramos, dibujamos la línea entera (una o varias) y al tocarla mostramos el total.
                    const encodeds = (r.segments && r.segments.length) ? r.segments : (r.encoded ? [r.encoded] : []);
                    const htmlTotal = total ? '<div style="font-size:13px; font-family:system-ui,sans-serif;"><div style="font-weight:800; color:' + color + ';">Ruta completa</div><div style="font-size:15px; font-weight:800;">' + total + '</div></div>' : null;
                    for (const enc of encodeds) {
                        if (!enc) continue;
                        const path = google.maps.geometry.encoding.decodePath(enc);
                        dibujarLinea(path, htmlTotal ? ((e) => { routeInfo.setContent(htmlTotal); routeInfo.setPosition(e.latLng); routeInfo.open(map); }) : null);
                    }
                }

                // Cartelito con el tiempo/km, posado en el medio del recorrido.
                // 2026-08-21: solo se muestra si la pantalla lo pide (mostrarCartel). En Mapeo el
                // tiempo/km ya sale en la tarjeta de la zona, asi que el cartel sobre el mapa
                // tapaba calles al pedo. En el mapa flotante y en el celular no hay tarjeta:
                // ahi sigue apareciendo. Tocar la linea muestra el dato igual en todos lados.
                if (total && allPts.length && r.mostrarCartel) {
                    const mid = allPts[Math.floor(allPts.length / 2)];
                    const lbl = makeLabel(mid, total, color);
                    lbl.setMap(map);
                    routeLabels.push(lbl);
                }
            }
        },

        clearRoutes() {
            for (const l of routeLines) l.setMap(null);
            routeLines = [];
            for (const lb of routeLabels) lb.setMap(null);
            routeLabels = [];
            if (routeInfo) routeInfo.close();
        },

        // ── Modo "Armar ruta" interactivo (estilo Google Maps) ──
        // Arranca el modo. fromDepot=true empieza la línea desde el depósito (depotLat/Lng).
        armarStart(fromDepot, depotLat, depotLng) {
            this.clearRoutes();
            armarClearInternal();
            armarDepot = (depotLat != null && depotLng != null) ? { lat: +depotLat, lng: +depotLng } : null;
            armarDesdeDeposito = !!fromDepot && !!armarDepot;
            armarOn = true;
            if (map) map.setOptions({ draggableCursor: 'pointer' });
            if (infoWindow) { infoWindow.close(); infoOpen = false; }
        },

        // Prender/apagar el arranque desde el depósito EN CUALQUIER MOMENTO (aunque ya hayas empezado):
        // al prenderlo agrega la casita 🏁 + el tramo depósito→1ª parada; al apagarlo los saca. El resto
        // de la ruta y la numeración no cambian.
        async armarToggleDepot(on) {
            on = !!on && !!armarDepot;
            if (on === armarDesdeDeposito) return;
            if (armarBusy) return;
            if (armarSeq.length === 0) { armarDesdeDeposito = on; return; } // sin puntos aún: solo guardar la elección
            if (on) {
                armarBusy = true;
                armarNotify();
                armarDrawDepotMarker();
                const leg = await armarBuildLeg(armarDepot, armarSeq[0], ARMAR_COLOR);
                if (leg) {
                    armarLegs.unshift(leg); armarTotSec += leg.sec; armarTotM += leg.m;
                    armarDesdeDeposito = true;
                } else {
                    // No se pudo traer el tramo: deshacemos la casita y dejamos el arranque en el 1er pin.
                    if (armarDepotMarker) { armarDepotMarker.setMap(null); armarDepotMarker = null; }
                    armarDesdeDeposito = false;
                }
                armarBusy = false;
            } else {
                armarDesdeDeposito = false;
                if (armarDepotMarker) { armarDepotMarker.setMap(null); armarDepotMarker = null; }
                const first = armarLegs.shift(); // el 1er tramo es depósito→1ª parada
                if (first) {
                    first.lines.forEach(l => l.setMap(null));
                    if (first.label) first.label.setMap(null);
                    armarTotSec -= first.sec; armarTotM -= first.m;
                }
            }
            armarNotify();
        },

        // Deshacer el último punto tocado (saca su tramo, su numerito y descuenta del total).
        armarUndo() {
            if (armarSeq.length === 0) return;
            armarSeq.pop();
            const nm = armarNums.pop(); if (nm) nm.setMap(null);
            // Cuántos tramos DEBERÍAN quedar según el arranque elegido.
            const expected = armarDesdeDeposito ? armarSeq.length : Math.max(0, armarSeq.length - 1);
            while (armarLegs.length > expected) {
                const leg = armarLegs.pop();
                if (!leg) break;
                leg.lines.forEach(l => l.setMap(null));
                if (leg.label) leg.label.setMap(null);
                armarTotSec -= leg.sec; armarTotM -= leg.m;
            }
            if (armarSeq.length === 0 && armarDepotMarker) { armarDepotMarker.setMap(null); armarDepotMarker = null; }
            armarNotify();
        },

        // Botón "Tiempos": muestra u oculta los cartelitos de tiempo/km de cada tramo (la línea igual se
        // puede tocar para ver el tiempo). Recrea o saca los cartelitos de los tramos ya dibujados.
        armarToggleTiempos(on) {
            armarMostrarTiempos = !!on;
            for (const leg of armarLegs) {
                if (armarMostrarTiempos && !leg.label && leg.mid) {
                    leg.label = armarLabelOverlay(leg.mid, leg.txt, ARMAR_COLOR);
                    leg.label.setMap(map);
                } else if (!armarMostrarTiempos && leg.label) {
                    leg.label.setMap(null);
                    leg.label = null;
                }
            }
        },

        // Terminar: le pasa a Blazor los IDs de los pines EN ORDEN (para guardar la ruta) y limpia el dibujo.
        armarFinish() {
            const ids = armarSeq.map(p => p.id);
            armarOn = false;
            if (map) map.setOptions({ draggableCursor: null });
            armarClearInternal();
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnArmarFinished', ids);
        },

        // Cancelar: sale del modo y borra todo lo dibujado sin guardar nada.
        armarCancel() {
            armarOn = false;
            armarClearInternal();
            if (map) map.setOptions({ draggableCursor: null });
        },

        // ── Ver una FOTO (snapshot) de un día anterior sobre el mapa (modo SOLO MIRAR) ──
        // items = [{lat,lng,label,color,shape,delivered,popupHtml}]. Oculta los pines de HOY,
        // dibuja los del histórico (no arrastrables, sin botones de ruta) y encuadra todo.
        showSnapshot(items) {
            if (!map || !window.google) return;
            // Escondemos lo de hoy (paradas + rutas) para no mezclar.
            for (const m of markers) m.setMap(null);
            this.clearRoutes();
            this.clearSnapshot();
            if (infoWindow) { infoWindow.close(); infoOpen = false; }

            const bounds = new google.maps.LatLngBounds();
            let any = false;
            for (const it of (items || [])) {
                if (it.lat == null || it.lng == null) continue;
                const pos = { lat: +it.lat, lng: +it.lng };
                const marker = new google.maps.Marker({
                    position: pos, map: map,
                    icon: H.markerIcon(H.markerSvg([it]), 50),
                    draggable: false, zIndex: 1
                });
                const html = it.popupHtml || '';
                const svLat = it.lat, svLng = it.lng;
                marker.addListener('click', () => {
                    if (infoWindow) {
                        infoWindow.setContent(H.wrapIw(html));
                        infoWindow.open(map, marker);
                        infoOpen = true;
                        H.streetView(infoWindow, svLat, svLng, html, true);
                    }
                });
                snapMarkers.push(marker);
                bounds.extend(pos);
                any = true;
            }
            if (any) {
                if (snapMarkers.length === 1) { map.setCenter(bounds.getCenter()); map.setZoom(15); }
                else map.fitBounds(bounds, 60);
            }
        },

        // Saca los pines del histórico (para volver al mapa de hoy). El re-dibujo de hoy lo hace Blazor.
        clearSnapshot() {
            for (const m of snapMarkers) m.setMap(null);
            snapMarkers = [];
            if (infoWindow) { infoWindow.close(); infoOpen = false; }
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
            for (const m of snapMarkers) m.setMap(null);
            snapMarkers = [];
            map = null;
            infoWindow = null;
            dotNetRef = null;
            lastFitStops = -1;
            armarOn = false;
            armarClearInternal();
            cleanupZone();
            for (const l of routeLines) l.setMap(null);
            routeLines = [];
            for (const lb of routeLabels) lb.setMap(null);
            routeLabels = [];
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

// 2026-07-30: barra negra (dock) arrastrable. Aparece a la derecha por defecto;
// el usuario la arrastra de la manija a cualquier lado y la posición queda guardada
// en el navegador (localStorage). Reengancha solo al re-render de Blazor.
// Usa Pointer Events + setPointerCapture para que el arrastre no se pierda cuando el
// puntero pasa por encima del mapa de Google (que si no se "come" los movimientos).
window.mapeoDock = (function () {
    const KEY = 'mapeoDockPos';
    function clamp(v, min, max) { return Math.max(min, Math.min(max, v)); }

    // Marca de qué lado del mapa quedó el dock, para que los desplegables abran hacia adentro.
    function updateAnchor(dock, parent) {
        if (!parent) return;
        const pr = parent.getBoundingClientRect();
        const dr = dock.getBoundingClientRect();
        const enDerecha = (dr.left + dr.right) / 2 > (pr.left + pr.right) / 2;
        dock.classList.toggle('anchor-right', enDerecha);
        dock.classList.toggle('anchor-left', !enDerecha);
    }

    // La barra arranca centrada con transform:translateY(-50%). Ese transform hace que los
    // menuitos "fijos" se posicionen respecto de la barra y no de la pantalla (y se iban lejos).
    // Acá pasamos esa posición centrada a un left/top concretos, sin mover nada a la vista.
    function normalizarPos(dock, parent) {
        if (!parent) return;
        const t = dock.style.transform;
        if (!t || t === 'none') return;
        const pr = parent.getBoundingClientRect();
        const dr = dock.getBoundingClientRect();
        const cs = getComputedStyle(parent);
        const bl = parseFloat(cs.borderLeftWidth) || 0;
        const bt = parseFloat(cs.borderTopWidth) || 0;
        dock.style.left = (dr.left - pr.left - bl) + 'px';
        dock.style.top = (dr.top - pr.top - bt) + 'px';
        dock.style.right = 'auto';
        dock.style.transform = 'none';
    }

    // 2026-08-31: la barra ahora se desliza por dentro cuando no entra en la pantalla.
    // Los menuitos que se abren AL COSTADO (elegir repartidor, tramos de la ruta…) quedarían
    // cortados por ese deslizamiento, así que los sacamos del recorte: los pasamos a posición
    // fija y les calculamos el lugar al lado de su tarjeta.
    function posicionarFlyouts(dock) {
        const flys = dock.querySelectorAll('.mapeo-flyout');
        if (!flys.length) return;
        const abreALaIzquierda = !dock.classList.contains('anchor-left');
        for (const fly of flys) {
            const card = fly.parentElement;
            if (!card) continue;
            fly.style.position = 'fixed';
            fly.style.right = 'auto';
            fly.style.bottom = 'auto';
            fly.style.marginLeft = '0';
            fly.style.marginRight = '0';
            const cr = card.getBoundingClientRect();
            const w = fly.offsetWidth, h = fly.offsetHeight;
            let left = abreALaIzquierda ? (cr.left - w - 8) : (cr.right + 8);
            left = clamp(left, 8, Math.max(8, window.innerWidth - w - 8));
            let top = clamp(cr.top, 8, Math.max(8, window.innerHeight - h - 8));
            fly.style.left = left + 'px';
            fly.style.top = top + 'px';
        }
    }

    // Aplica la posición guardada (si existe), clampeada dentro del mapa.
    function applySaved(dock, parent) {
        let pos = null;
        try { pos = JSON.parse(localStorage.getItem(KEY) || 'null'); } catch (e) { pos = null; }
        if (!pos || !parent) return;
        const pr = parent.getBoundingClientRect();
        const left = clamp(pos.left, 0, Math.max(0, pr.width - dock.offsetWidth));
        const top = clamp(pos.top, 0, Math.max(0, pr.height - dock.offsetHeight));
        dock.style.left = left + 'px';
        dock.style.top = top + 'px';
        dock.style.right = 'auto';
        dock.style.transform = 'none';
    }

    return {
        enableDrag() {
            const dock = document.getElementById('mapeoDock');
            if (!dock) return;
            const parent = dock.offsetParent || dock.parentElement;
            const handle = dock.querySelector('.mapeo-dock-handle');
            if (!handle) return;

            applySaved(dock, parent);
            normalizarPos(dock, parent);
            updateAnchor(dock, parent);
            posicionarFlyouts(dock);

            if (dock.__dragBound) return; // no reenganchar dos veces
            dock.__dragBound = true;

            // Al deslizar la barra por dentro (o al cambiar el tamaño de la ventana) los
            // menuitos abiertos tienen que seguir a su tarjeta.
            dock.addEventListener('scroll', function () { posicionarFlyouts(dock); }, { passive: true });
            window.addEventListener('resize', function () { posicionarFlyouts(dock); });

            let startX = 0, startY = 0, startLeft = 0, startTop = 0, dragging = false;

            function onMove(e) {
                if (!dragging) return;
                const pr = parent.getBoundingClientRect();
                let nl = clamp(startLeft + (e.clientX - startX), 0, pr.width - dock.offsetWidth);
                let nt = clamp(startTop + (e.clientY - startY), 0, pr.height - dock.offsetHeight);
                dock.style.left = nl + 'px';
                dock.style.top = nt + 'px';
                updateAnchor(dock, parent);
                posicionarFlyouts(dock);
                e.preventDefault();
            }
            function onUp(e) {
                if (!dragging) return;
                dragging = false;
                try { handle.releasePointerCapture(e.pointerId); } catch (x) { }
                handle.removeEventListener('pointermove', onMove);
                handle.removeEventListener('pointerup', onUp);
                handle.removeEventListener('pointercancel', onUp);
                try {
                    localStorage.setItem(KEY, JSON.stringify({
                        left: parseFloat(dock.style.left) || 0,
                        top: parseFloat(dock.style.top) || 0
                    }));
                } catch (x) { /* localStorage lleno o bloqueado: no pasa nada */ }
            }
            function onDown(e) {
                dragging = true;
                const pr = parent.getBoundingClientRect();
                const dr = dock.getBoundingClientRect();
                // Fijamos left/top numéricos (convertimos desde right/transform del arranque).
                startLeft = dr.left - pr.left;
                startTop = dr.top - pr.top;
                dock.style.left = startLeft + 'px';
                dock.style.top = startTop + 'px';
                dock.style.right = 'auto';
                dock.style.transform = 'none';
                startX = e.clientX; startY = e.clientY;
                // Capturamos el puntero: aunque pase sobre el mapa, los pointermove siguen llegando acá.
                try { handle.setPointerCapture(e.pointerId); } catch (x) { }
                handle.addEventListener('pointermove', onMove);
                handle.addEventListener('pointerup', onUp);
                handle.addEventListener('pointercancel', onUp);
                e.preventDefault();
            }

            handle.addEventListener('pointerdown', onDown);
        }
    };
})();

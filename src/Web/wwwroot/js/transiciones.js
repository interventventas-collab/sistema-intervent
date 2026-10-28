/*
 * 2026-10-28: animacion al abrir y cerrar ventanas, en todo el sistema (pedido del dueño: "que de la
 * sensacion de que va rapido"). Eligio la combinacion "A + D" de la prueba:
 *   A) la ventana nueva CRECE desde el boton que tocaste, y al cerrarse vuelve a meterse ahi.
 *   D) lo de adentro entra en CASCADA: los renglones aparecen uno atras del otro, muy rapido.
 *
 * COMO FUNCIONA, SIN TOCAR CADA PANTALLA: casi todas las ventanas del sistema comparten la misma
 * clase ("modal-overlay" y las del celu del repartidor). Miramos cuando una de esas aparece o
 * desaparece de la pagina (MutationObserver) y la animamos desde aca.
 *
 *  - Abrir: nos acordamos de donde fue el ultimo toque (pointerdown). Si la ventana aparece enseguida
 *    despues, sale de ese boton. Si no hubo toque (se abrio sola, o con el teclado), aparece suave.
 *  - Cerrar: Blazor la saca de golpe. Ponemos una COPIA quieta en su lugar (no se puede tocar) que se
 *    achica hacia el boton y se borra. La ventana de verdad ya no esta: no hay que esperar a nada.
 *
 * NO HACE MAS LENTO NADA: la animacion la hace la placa de video (solo se mueve y se transparenta) y
 * los datos se siguen cargando al mismo tiempo. Dura ~0,3 s.
 *
 * Tambien al CAMBIAR DE PANTALLA desde el menu: lo nuevo entra en cascada (ver "Parte 3" abajo).
 *
 * Para apagarla en una ventana puntual: data-sin-anim en el overlay. En equipos con "reducir
 * movimiento" activado no se anima nada.
 */
(function () {
    // Ventanas que se animan. WhatsApp, menus desplegables y el candado del operador quedan afuera a proposito.
    var SEL = [
        '.modal-overlay', '.modal-backdrop', '.mp-modal-overlay', '.mpc-overlay', '.mpc-ver',
        '.mpc-asig-fondo', '.mp-cuenta-fondo', '.cam-fondo', '.kio-modal-overlay', '.avdep-fondo',
        '.vj-liq-fondo', '.meli-q-overlay', '.prep-qr-overlay'
    ].join(',');
    // Las que ocupan toda la pantalla (no tienen una "tarjeta" en el medio): se abren como un recorte que crece.
    var PANTALLA_COMPLETA = '.mpc-overlay, .mpc-ver';

    var DUR_ABRIR = 340, DUR_CERRAR = 240, PASO_CASCADA = 28, MAX_CASCADA = 14;
    var CURVA = 'cubic-bezier(.2,.8,.2,1)';
    var CURVA_REBOTE = 'cubic-bezier(.34,1.4,.64,1)';

    var reducir = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reducir || !document.body || !Element.prototype.animate) return;
    document.documentElement.classList.add('tx-on');

    var ultimoToque = null;               // { rect, el, t }
    var origenes = new WeakMap();         // overlay -> { rect, el } desde donde se abrio

    document.addEventListener('pointerdown', function (e) {
        var el = e.target && e.target.closest
            ? e.target.closest('button, a, [role=button], .ofi-tile, .mp-calcu, tr, li, label, .vh-link') || e.target
            : null;
        if (!el || !el.getBoundingClientRect) return;
        ultimoToque = { rect: el.getBoundingClientRect(), el: el, t: Date.now() };
    }, true);

    function origenReciente() {
        if (!ultimoToque || Date.now() - ultimoToque.t > 1500) return null;
        var r = ultimoToque.rect;
        if (!r || r.width === 0 || r.height === 0) return null;
        return ultimoToque;
    }

    /** El rectangulo del boton de origen: el de ahora si sigue en pantalla, si no el que tenia al tocarlo. */
    function rectOrigen(o) {
        if (!o) return null;
        if (o.el && o.el.isConnected) {
            var r = o.el.getBoundingClientRect();
            if (r.width > 0 && r.height > 0) return r;
        }
        return o.rect;
    }

    function tarjetaDe(ov) {
        if (ov.matches(PANTALLA_COMPLETA)) return null;
        // La tarjeta es el primer hijo con tamaño propio (modal-card, mpc-asig-hoja, etc.).
        for (var i = 0; i < ov.children.length; i++) {
            var c = ov.children[i];
            if (c.tagName === 'STYLE' || c.tagName === 'SCRIPT') continue;
            var r = c.getBoundingClientRect();
            if (r.width > 0 && r.height > 0) return c;
        }
        return null;
    }

    /** Los renglones para la cascada: los hijos de la tarjeta, abriendo los bloques grandes (el cuerpo). */
    function renglones(cont) {
        var alto = cont.getBoundingClientRect().height || 1;
        var lista = [];
        function juntar(el, nivel) {
            for (var i = 0; i < el.children.length && lista.length < MAX_CASCADA; i++) {
                var c = el.children[i];
                if (c.tagName === 'STYLE' || c.tagName === 'SCRIPT') continue;
                var r = c.getBoundingClientRect();
                if (r.width === 0 || r.height === 0) continue;
                if (r.top > window.innerHeight) break;     // lo que no se ve no se anima
                if (nivel < 2 && c.children.length >= 2 && r.height > alto * 0.4) juntar(c, nivel + 1);
                else lista.push(c);
            }
        }
        juntar(cont, 0);
        return lista;
    }

    function cascada(cont, demora) {
        renglones(cont).forEach(function (el, k) {
            try {
                el.animate([{ opacity: 0, transform: 'translateY(10px)' }, { opacity: 1, transform: 'none' }],
                    { duration: 300, delay: demora + k * PASO_CASCADA, easing: CURVA_REBOTE, fill: 'backwards' });
            } catch (e) { }
        });
    }

    /** Transformacion que lleva el rectangulo "r" a quedar encima del rectangulo "o". */
    function haciaOrigen(r, o) {
        var sx = Math.max(o.width / r.width, 0.04), sy = Math.max(o.height / r.height, 0.04);
        var dx = (o.left + o.width / 2) - (r.left + r.width / 2);
        var dy = (o.top + o.height / 2) - (r.top + r.height / 2);
        return 'translate(' + dx + 'px,' + dy + 'px) scale(' + sx + ',' + sy + ')';
    }

    function recorte(o) {
        var W = window.innerWidth, H = window.innerHeight;
        return 'inset(' + Math.max(0, o.top) + 'px ' + Math.max(0, W - o.right) + 'px ' +
            Math.max(0, H - o.bottom) + 'px ' + Math.max(0, o.left) + 'px round 12px)';
    }

    function abrir(ov) {
        if (ov.hasAttribute('data-sin-anim') || ov.hasAttribute('data-tx-copia')) return;
        var o = origenReciente();
        origenes.set(ov, o ? { rect: o.rect, el: o.el } : null);
        var r0 = o ? rectOrigen(o) : null;
        try {
            if (ov.matches(PANTALLA_COMPLETA)) {
                if (r0) ov.animate([{ clipPath: recorte(r0) }, { clipPath: 'inset(0px 0px 0px 0px round 0px)' }],
                    { duration: DUR_ABRIR + 60, easing: CURVA });
                else ov.animate([{ opacity: 0, transform: 'scale(.97)' }, { opacity: 1, transform: 'none' }],
                    { duration: 220, easing: CURVA });
                var wrap = ov.querySelector('.mpc-wrap, .mpc-ver-scroll') || ov;
                cascada(wrap, r0 ? 120 : 40);
                return;
            }
            ov.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 200 });
            var card = tarjetaDe(ov);
            if (!card) return;
            var r = card.getBoundingClientRect();
            if (r0) card.animate([{ transform: haciaOrigen(r, r0), opacity: 0.5 }, { transform: 'none', opacity: 1 }],
                { duration: DUR_ABRIR, easing: CURVA });
            else card.animate([{ transform: 'scale(.94)', opacity: 0 }, { transform: 'none', opacity: 1 }],
                { duration: 240, easing: CURVA_REBOTE });
            cascada(card, r0 ? 110 : 40);
        } catch (e) { }
    }

    function cerrar(ov, estilos) {
        if (ov.hasAttribute('data-sin-anim') || ov.hasAttribute('data-tx-copia')) return;
        if (!origenes.has(ov)) return;                 // no la vimos abrir (o es un repintado raro): nada
        var o = origenes.get(ov);
        origenes.delete(ov);
        var copia = ov.cloneNode(true);
        copia.setAttribute('data-tx-copia', '');
        copia.style.pointerEvents = 'none';
        copia.removeAttribute('id');
        copia.querySelectorAll('[id]').forEach(function (x) { x.removeAttribute('id'); });
        // Los estilos propios de la pantalla que se fue (si se fueron junto con ella), para que la copia se vea igual.
        var copiasEstilo = (estilos || []).map(function (s) { var c = s.cloneNode(true); document.head.appendChild(c); return c; });
        document.body.appendChild(copia);
        var r0 = o ? rectOrigen(o) : null;
        var fin = function () {
            copia.remove();
            copiasEstilo.forEach(function (c) { c.remove(); });
        };
        try {
            var a;
            if (copia.matches(PANTALLA_COMPLETA)) {
                // Se achica hacia el boton y a mitad de camino ya se desvanece: si no, se ven las dos pantallas encimadas.
                a = r0 ? copia.animate([{ clipPath: 'inset(0px 0px 0px 0px round 0px)', opacity: 1 },
                                        { opacity: 0.9, offset: 0.35 },
                                        { clipPath: recorte(r0), opacity: 0 }],
                        { duration: DUR_CERRAR + 40, easing: CURVA })
                    : copia.animate([{ opacity: 1 }, { opacity: 0, transform: 'scale(.97)' }], { duration: 180 });
            } else {
                copia.animate([{ opacity: 1 }, { opacity: 0 }], { duration: DUR_CERRAR, easing: 'ease-in', fill: 'forwards' });
                var card = tarjetaDe(copia);
                if (card) {
                    var r = card.getBoundingClientRect();
                    a = r0 ? card.animate([{ transform: 'none', opacity: 1 }, { transform: haciaOrigen(r, r0), opacity: 0.3 }],
                            { duration: DUR_CERRAR, easing: CURVA, fill: 'forwards' })
                        : card.animate([{ transform: 'none' }, { transform: 'scale(.94)' }], { duration: DUR_CERRAR, fill: 'forwards' });
                }
            }
            if (a) a.onfinish = fin; else setTimeout(fin, DUR_CERRAR + 20);
            setTimeout(fin, DUR_CERRAR + 400);         // por las dudas: la copia nunca queda colgada
        } catch (e) { fin(); }
    }

    function overlaysEn(nodo) {
        if (nodo.nodeType !== 1) return [];
        if (nodo.matches(SEL)) return [nodo];
        // Pantallas del celu cuyo componente envuelve al overlay en un div: solo un nivel, nunca una pagina entera.
        var r = [];
        for (var i = 0; i < nodo.children.length; i++) if (nodo.children[i].matches(SEL)) r.push(nodo.children[i]);
        return r;
    }

    // ═══ Parte 3: cambiar de pantalla desde el menú (de Ventas a Clientes, etc.) ═══
    // No hay "botón de origen" que agrandar: lo nuevo de la pantalla entra en cascada. Vale durante
    // 1,5 s después de cambiar de dirección, así también entran en cascada los renglones que llegan
    // un toque después (la tabla aparece cuando terminan de llegar los datos).
    // Ojo: el WhatsApp del celu (wa-atras.js) deja marcas con la MISMA dirección: no cuentan.
    var rutaActual = location.pathname, navAt = 0, navCuenta = 0;
    var yaAnimados = new WeakSet();
    var VENTANA_NAV = 1500, MAX_NAV = 30;

    function quizasNavego() {
        if (location.pathname === rutaActual) return;
        rutaActual = location.pathname;
        navAt = Date.now();
        navCuenta = 0;
    }
    ['pushState', 'replaceState'].forEach(function (f) {
        var orig = history[f];
        history[f] = function () { var r = orig.apply(this, arguments); quizasNavego(); return r; };
    });
    window.addEventListener('popstate', quizasNavego);

    function enPagina(n) {
        var c = n.parentElement && n.parentElement.closest ? n.parentElement.closest('main .content') : null;
        return !!c && !n.closest(SEL) && !n.closest('[data-tx-copia]');
    }

    function cascadaPagina(nodos) {
        if (!nodos.length || Date.now() - navAt > VENTANA_NAV) return;
        var lista = [];
        nodos.forEach(function (n) {
            if (lista.length >= MAX_NAV) return;
            var r = n.getBoundingClientRect();
            if (r.width === 0 || r.height === 0 || r.top > window.innerHeight) return;
            // Un bloque grande (la página entera, una tabla): entran sus partes, no el bloque de una.
            if (n.children.length >= 2 && r.height > window.innerHeight * 0.35) {
                renglones(n).forEach(function (x) { if (lista.length < MAX_NAV) lista.push(x); });
            } else lista.push(n);
        });
        lista.forEach(function (el) {
            if (yaAnimados.has(el) || navCuenta >= MAX_NAV) return;
            yaAnimados.add(el);
            var k = navCuenta++;
            try {
                el.animate([{ opacity: 0, transform: 'translateY(12px)' }, { opacity: 1, transform: 'none' }],
                    { duration: 320, delay: Math.min(k * PASO_CASCADA, 420), easing: CURVA_REBOTE, fill: 'backwards' });
            } catch (e) { }
        });
    }

    new MutationObserver(function (lista) {
        var nuevosPagina = [];
        var navegando = Date.now() - navAt < VENTANA_NAV;
        lista.forEach(function (m) {
            var estilosIdos = [];
            m.removedNodes.forEach(function (n) { if (n.nodeType === 1 && n.tagName === 'STYLE') estilosIdos.push(n); });
            m.removedNodes.forEach(function (n) { overlaysEn(n).forEach(function (ov) { cerrar(ov, estilosIdos); }); });
            m.addedNodes.forEach(function (n) {
                var ovs = overlaysEn(n);
                ovs.forEach(abrir);
                if (navegando && !ovs.length && n.nodeType === 1 && n.tagName !== 'STYLE' && n.tagName !== 'SCRIPT' && enPagina(n))
                    nuevosPagina.push(n);
            });
        });
        if (nuevosPagina.length) cascadaPagina(nuevosPagina);
    }).observe(document.body, { childList: true, subtree: true });
})();

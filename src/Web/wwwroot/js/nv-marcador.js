// 2026-09-17: MARCADOR DE COLORES para el "Comentario para armado" de Nueva Venta.
// Osmar va resaltando lo que ya cargó para no perderse, pero lo resaltado era sólo la selección
// del navegador y se borraba al tocar el buscador de productos. Ahora elige un color y
// selecciona con el mouse la parte del renglón que quiere pintar (a veces está todo de corrido).
//
// Los colores son SOLO VISUALES y SOLO PARA ÉL: el texto que se guarda sigue siendo el mismo
// (el textarea no cambia), no se guardan en la base y el que arma no los ve.
//
// Cómo funciona: el textarea queda con fondo transparente y detrás tiene un "fondo" (div) con el
// mismo texto, la misma letra y el mismo ancho, donde los pedazos pintados llevan color. El texto
// del fondo es invisible; lo que se lee es el del textarea, y abajo asoman los colores.
//
// Markup esperado (Blazor):
//   <div style="position:relative">
//     <div class="nvm-back" data-nvm-bg="#fefce8"></div>
//     <textarea data-nvm-key="clave" style="background:transparent; position:relative; z-index:1">
//   </div>
//   Botones: <button data-nvm-key="clave" data-nvm-color="#bbf7d0" onmousedown="return nvMarcador.tocar(this, event)">
//   ("borrar" usa data-nvm-color="borrar"). Aviso opcional: <span data-nvm-aviso="clave">.
// Varias cajas con la misma clave (la del formulario y la ventanita flotante) comparten los colores.
window.nvMarcador = (function () {
    const stores = {};   // clave -> { text, rangos: [{s, e, c}], modo: null | color | 'borrar' }

    function store(key) {
        return stores[key] || (stores[key] = { text: null, rangos: [], modo: null });
    }

    function escapar(t) {
        return t.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    // Pinta (o borra, si color es null) el tramo [s, e). Lo que ya estaba pintado ahí se reemplaza.
    function pintar(st, s, e, color) {
        const nuevos = [];
        for (const r of st.rangos) {
            if (r.e <= s || r.s >= e) { nuevos.push(r); continue; }
            if (r.s < s) nuevos.push({ s: r.s, e: s, c: r.c });
            if (r.e > e) nuevos.push({ s: e, e: r.e, c: r.c });
        }
        if (color) nuevos.push({ s: s, e: e, c: color });
        nuevos.sort((a, b) => a.s - b.s);
        // juntar pedazos pegados del mismo color
        const juntos = [];
        for (const r of nuevos) {
            const u = juntos[juntos.length - 1];
            if (u && u.c === r.c && u.e === r.s) u.e = r.e; else juntos.push({ s: r.s, e: r.e, c: r.c });
        }
        st.rangos = juntos;
    }

    // El texto cambió porque ÉL escribió: correr los colores para que sigan pegados a sus palabras.
    function acomodar(st, viejo, nuevo) {
        let p = 0;
        const min = Math.min(viejo.length, nuevo.length);
        while (p < min && viejo[p] === nuevo[p]) p++;
        let sf = 0;
        while (sf < min - p && viejo[viejo.length - 1 - sf] === nuevo[nuevo.length - 1 - sf]) sf++;
        const finViejo = viejo.length - sf;
        const delta = nuevo.length - viejo.length;
        const ins = nuevo.length - sf - p;
        const res = [];
        for (const r of st.rangos) {
            const s = r.s < p ? r.s : (r.s >= finViejo ? r.s + delta : p + ins);
            const e = r.e <= p ? r.e : (r.e >= finViejo ? r.e + delta : p);
            if (e > s) res.push({ s: s, e: e, c: r.c });
        }
        st.rangos = res;
    }

    function dibujar(ta) {
        const back = ta.previousElementSibling;
        if (!back || !back.classList.contains('nvm-back')) return;
        const st = store(ta.dataset.nvmKey);
        const txt = ta.value;
        let html = '', pos = 0;
        for (const r of st.rangos) {
            if (r.s >= txt.length) break;
            const e = Math.min(r.e, txt.length);
            html += escapar(txt.slice(pos, r.s));
            html += '<mark style="background:' + r.c + '; color:transparent; border-radius:3px;">' + escapar(txt.slice(r.s, e)) + '</mark>';
            pos = e;
        }
        html += escapar(txt.slice(pos)) + '\n ';   // el renglón vacío final también ocupa lugar
        const inner = back.firstElementChild;
        inner.innerHTML = html;
        inner.style.transform = 'translateY(' + (-ta.scrollTop) + 'px)';
    }

    function medir(ta) {
        const back = ta.previousElementSibling;
        if (!back || !back.classList.contains('nvm-back')) return;
        const cs = getComputedStyle(ta);
        const bl = parseFloat(cs.borderLeftWidth) || 0, br = parseFloat(cs.borderRightWidth) || 0;
        const scrollbar = Math.max(0, ta.offsetWidth - ta.clientWidth - bl - br);
        Object.assign(back.style, {
            position: 'absolute', zIndex: '0', overflow: 'hidden', pointerEvents: 'none', boxSizing: 'border-box',
            top: ta.offsetTop + 'px', left: ta.offsetLeft + 'px',
            width: ta.offsetWidth + 'px', height: ta.offsetHeight + 'px',
            background: back.dataset.nvmBg || 'transparent',
            borderStyle: 'solid', borderColor: 'transparent',
            borderTopWidth: cs.borderTopWidth, borderBottomWidth: cs.borderBottomWidth,
            borderLeftWidth: cs.borderLeftWidth, borderRightWidth: cs.borderRightWidth,
            borderRadius: cs.borderRadius,
            paddingTop: cs.paddingTop, paddingBottom: cs.paddingBottom, paddingLeft: cs.paddingLeft,
            paddingRight: (parseFloat(cs.paddingRight) + scrollbar) + 'px',
            fontFamily: cs.fontFamily, fontSize: cs.fontSize, fontWeight: cs.fontWeight,
            lineHeight: cs.lineHeight, letterSpacing: cs.letterSpacing,
            whiteSpace: 'pre-wrap', overflowWrap: 'break-word', wordBreak: cs.wordBreak,
            color: 'transparent', textAlign: cs.textAlign
        });
        if (!back.firstElementChild) back.appendChild(document.createElement('div'));
        dibujar(ta);
    }

    function marcarBotones(key) {
        const st = store(key);
        document.querySelectorAll('[data-nvm-color][data-nvm-key="' + key + '"]').forEach(b => {
            const activo = st.modo === b.dataset.nvmColor;
            b.style.outline = activo ? '2px solid #78350f' : 'none';
            b.style.outlineOffset = '1px';
        });
        document.querySelectorAll('[data-nvm-aviso="' + key + '"]').forEach(a => {
            a.textContent = st.modo ? (st.modo === 'borrar' ? 'seleccioná qué despintar' : 'seleccioná qué pintar') : '';
        });
    }

    function aplicarSeleccion(ta) {
        const st = store(ta.dataset.nvmKey);
        if (!st.modo) return false;
        const s = ta.selectionStart, e = ta.selectionEnd;
        if (s === e) return false;
        pintar(st, s, e, st.modo === 'borrar' ? null : st.modo);
        // soltar la selección para que se vea el color (la selección azul lo tapa)
        ta.setSelectionRange(e, e);
        document.querySelectorAll('textarea[data-nvm-key="' + ta.dataset.nvmKey + '"]').forEach(dibujar);
        // 2026-09-22: el lápiz se apaga solo después de cada pintada. Con el lápiz prendido todo lo que
        // seleccionaba se pintaba y no podía COPIAR un pedazo. Para pintar otro, vuelve a tocar el color.
        st.modo = null;
        marcarBotones(ta.dataset.nvmKey);
        return true;
    }

    function enganchar(ta) {
        if (ta.dataset.nvmOk === '1') return;
        ta.dataset.nvmOk = '1';
        const st = store(ta.dataset.nvmKey);
        if (st.text !== ta.value) { st.text = ta.value; st.rangos = []; }

        ta.addEventListener('input', () => {
            acomodar(st, st.text || '', ta.value);
            st.text = ta.value;
            dibujar(ta);
        });
        ta.addEventListener('scroll', () => dibujar(ta));
        ta.addEventListener('keyup', (ev) => { if (ev.shiftKey) aplicarSeleccion(ta); });
        if (window.ResizeObserver) new ResizeObserver(() => medir(ta)).observe(ta);

        // Si el texto lo cambia el SISTEMA (venta nueva, traer pedido de WhatsApp, duplicar…) y no él
        // tipeando, los colores ya no corresponden: se limpian.
        const timer = setInterval(() => {
            if (!ta.isConnected) { clearInterval(timer); return; }
            if (ta.value !== st.text) { st.text = ta.value; st.rangos = []; dibujar(ta); }
        }, 400);

        medir(ta);
        marcarBotones(ta.dataset.nvmKey);
    }

    function buscar() {
        document.querySelectorAll('textarea[data-nvm-key]').forEach(enganchar);
    }

    // Soltar el mouse DESPUÉS de seleccionar (aunque se suelte afuera de la caja) pinta lo elegido.
    document.addEventListener('mouseup', () => {
        setTimeout(() => {
            const ta = document.activeElement;
            if (ta && ta.tagName === 'TEXTAREA' && ta.dataset.nvmKey) aplicarSeleccion(ta);
        }, 0);
    });

    let pendiente = false;
    function alCambiarPagina() {
        if (pendiente) return;
        pendiente = true;
        requestAnimationFrame(() => { pendiente = false; buscar(); });
    }
    if (document.body) new MutationObserver(alCambiarPagina).observe(document.body, { childList: true, subtree: true });
    else document.addEventListener('DOMContentLoaded', () => new MutationObserver(alCambiarPagina).observe(document.body, { childList: true, subtree: true }));

    return {
        // Botón de color. mousedown + preventDefault: así el textarea NO pierde la selección.
        tocar: function (btn, ev) {
            if (ev) ev.preventDefault();
            const key = btn.dataset.nvmKey, color = btn.dataset.nvmColor;
            const st = store(key);
            const ta = document.querySelector('textarea[data-nvm-key="' + key + '"]');
            const haySeleccion = ta && ta.selectionStart !== ta.selectionEnd && document.activeElement === ta;
            if (st.modo === color && !haySeleccion) st.modo = null;   // tocar el mismo color lo apaga
            else st.modo = color;
            marcarBotones(key);
            if (haySeleccion) aplicarSeleccion(ta);
            if (ta && st.modo) ta.focus();
            return false;
        },
        limpiar: function (key) {
            const st = store(key);
            st.rangos = []; st.modo = null;
            document.querySelectorAll('textarea[data-nvm-key="' + key + '"]').forEach(dibujar);
            marcarBotones(key);
        }
    };
})();

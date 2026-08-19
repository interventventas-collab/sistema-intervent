// 2026-08-03: Enter para enviar en el cuadro del chat, como WhatsApp.
// El problema viejo: Blazor manejaba el Enter pero NO frenaba el salto de línea
// del navegador, así que Enter mandaba el mensaje Y metía un renglón nuevo al mismo
// tiempo. Como el envío tarda un toquito, los dos se pisaban y quedaba medio mensaje
// colgado / se duplicaba (el "relay" raro que reportó Osmar).
//
// Solución: escuchamos el keydown nosotros (en fase de captura, antes que nadie),
// y si es Enter sin Shift: frenamos el salto de línea, VACIAMOS el cuadro al instante
// (como WhatsApp) y le pasamos el texto a .NET para que lo mande limpio.
// Shift+Enter sigue haciendo renglón nuevo, sin tocar nada.
window.waComposer = {
    _handler: null,
    _grow: null,
    // 2026-08-17: alto MAXIMO del cuadro al crecer. Pasado esto el texto scrollea adentro,
    // como WhatsApp Web: si no, un mensaje largo se comeria toda la conversacion otra vez.
    MAX_ALTO: 240,
    // El cuadro arranca de un renglon y CRECE SOLO a medida que escribis. Antes tenia
    // 145px de alto fijo, ocupando ese lugar aunque escribieras una palabra.
    ajustarAlto: function (el) {
        if (!el) return;
        el.style.height = 'auto';
        var alto = Math.min(el.scrollHeight, window.waComposer.MAX_ALTO);
        el.style.height = alto + 'px';
        el.style.overflowY = (el.scrollHeight > window.waComposer.MAX_ALTO) ? 'auto' : 'hidden';
    },
    // Despues de enviar, .NET vacia el texto pero el navegador NO tira evento 'input',
    // asi que el cuadro quedaria grande y vacio. Esto lo devuelve a un renglon.
    // 2026-08-17: ancho de la ventana, para decidir si el panel derecho arranca abierto
    // (compu) o cerrado (celular) la primera vez, cuando el usuario todavía no eligió.
    anchoPantalla: function () {
        try { return window.innerWidth || 0; } catch (e) { return 0; }
    },
    // 2026-08-19: el cuadro de escribir del CELU se maneja desde JS para no pisar lo que el
    // teclado del teléfono está proponiendo (el predictivo). Blazor ya no le escribe el value en
    // cada dibujo: cuando .NET necesita cambiarlo (mandar, respuesta rápida, error) llama acá.
    setValor: function (el, txt) {
        try {
            if (!el) return;
            el.value = txt || '';
            window.waComposer.ajustarAlto(el);
        } catch (e) { }
    },

    /** El cuadro crece solo mientras escribís, como WhatsApp (hasta el máximo y después scrollea). */
    autoCrecer: function (el) {
        try {
            if (!el || el._waCrece) return;
            el._waCrece = true;
            el.addEventListener('input', function () { window.waComposer.ajustarAlto(el); });
            window.waComposer.ajustarAlto(el);
        } catch (e) { }
    },

    resetSize: function () {
        try {
            var el = document.querySelector('.wa-composer-input');
            if (el) { el.style.height = ''; el.style.overflowY = 'hidden'; }
        } catch (e) { }
    },
    register: function (dotNetRef) {
        // Si ya había uno (re-render / volver a entrar), lo sacamos primero.
        this.unregister();
        // Crecimiento automatico del cuadro de escribir.
        var grow = function (e) {
            var el = e.target;
            if (!el || !el.classList || !el.classList.contains('wa-composer-input')) return;
            window.waComposer.ajustarAlto(el);
        };
        this._grow = grow;
        document.addEventListener('input', grow);
        const handler = function (e) {
            const el = e.target;
            if (!el || !el.classList || !el.classList.contains('wa-composer-input')) return;
            // Teclados que "componen" caracteres (acentos, japonés, etc.): no interrumpir.
            if (e.isComposing || e.keyCode === 229) return;
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();          // clave: que el navegador NO agregue el renglón
                const texto = el.value || '';
                if (!texto.trim()) return;   // cuadro vacío = no hacemos nada
                el.value = '';               // se vacía al toque, como WhatsApp
                window.waComposer.resetSize();  // y vuelve a un renglón
                dotNetRef.invokeMethodAsync('EnviarDesdeComposer', texto);
                return;
            }
            // 2026-08-06: PRESENCIA — avisar "estoy escribiendo" (el .NET lo apaga solo a los 4 s).
            // Throttle a 1 llamada cada 800 ms para no saturar el puente JS↔.NET. Ignoramos teclas
            // que no son de "tipear" (flechas, Ctrl, Shift, etc. sueltas) para no marcar falsa actividad.
            var ignorar = e.ctrlKey || e.metaKey || e.altKey
                || ['Shift','Control','Alt','Meta','ArrowLeft','ArrowRight','ArrowUp','ArrowDown','Home','End','PageUp','PageDown','Tab','Escape','CapsLock'].indexOf(e.key) !== -1;
            if (!ignorar) {
                var ahora = (window.performance && performance.now) ? performance.now() : 0;
                if (ahora - (window.waComposer._lastTyping || 0) > 800) {
                    window.waComposer._lastTyping = ahora;
                    dotNetRef.invokeMethodAsync('NotifyTyping');
                }
            }
        };
        this._handler = handler;
        // capture=true: corremos antes que cualquier otro handler del textarea.
        document.addEventListener('keydown', handler, true);
    },
    unregister: function () {
        if (this._handler) {
            document.removeEventListener('keydown', this._handler, true);
            this._handler = null;
        }
        if (this._grow) {
            document.removeEventListener('input', this._grow);
            this._grow = null;
        }
    },
    // 2026-08-06: al abrir un chat, poner el cursor solo en el cuadro de escribir (como WhatsApp Web),
    // asi se empieza a tipear sin tener que hacer clic. SOLO en compu (pointer: fine) para no forzar
    // el teclado en el celular (taparia la conversacion). El cursor queda al final del texto.
    focus: function () {
        try {
            if (!window.matchMedia || !window.matchMedia('(pointer: fine)').matches) return;
            // esperamos un toque a que Blazor termine de renderizar el cuadro del chat abierto
            setTimeout(function () {
                var el = document.querySelector('.wa-composer-input');
                if (!el) return;
                el.focus();
                var n = el.value ? el.value.length : 0;
                try { el.setSelectionRange(n, n); } catch (e) { }
            }, 60);
        } catch (e) { }
    }
};

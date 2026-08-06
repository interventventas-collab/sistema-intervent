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
    register: function (dotNetRef) {
        // Si ya había uno (re-render / volver a entrar), lo sacamos primero.
        this.unregister();
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
                dotNetRef.invokeMethodAsync('EnviarDesdeComposer', texto);
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

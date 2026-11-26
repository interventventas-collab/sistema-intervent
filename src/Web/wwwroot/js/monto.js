// 26/11/2026: casilleros de plata (formas de cobro y de pago) que se escriben "a la argentina"
// mientras se tipea: los puntos de miles se ponen solos y el PUNTO que toca el usuario funciona
// como COMA (centavos). Ej.: tipea 155.50 -> ve 155,50; tipea 200000 -> ve 200.000.
// Se activa poniendo data-monto en el <input type="text">. El texto que queda es el mismo que
// entiende MontoInput.TryParse en C# ("1.500.000,50"), asi que el @bind no cambia.
(function () {
    function esMonto(el) { return el && el.tagName === 'INPUT' && el.hasAttribute('data-monto'); }

    // Cuantos caracteres "que importan" (digitos, coma, signo) hay antes de la posicion pos.
    function significativos(s, pos) {
        var n = 0;
        for (var i = 0; i < pos && i < s.length; i++) if (/[\d,\-]/.test(s[i])) n++;
        return n;
    }

    function formatear(s) {
        var neg = /^\s*-/.test(s);
        s = s.replace(/[^\d,]/g, '');
        var i = s.indexOf(',');
        var ent = i < 0 ? s : s.slice(0, i);
        var dec = i < 0 ? null : s.slice(i + 1).replace(/,/g, '').slice(0, 2);
        ent = ent.replace(/^0+(?=\d)/, '');
        if (ent === '' && dec !== null) ent = '0';
        ent = ent.replace(/\B(?=(\d{3})+(?!\d))/g, '.');
        var r = dec === null ? ent : ent + ',' + dec;
        return (neg ? '-' : '') + r;
    }

    function aplicar(el) {
        var v = el.value, pos = el.selectionStart == null ? v.length : el.selectionStart;
        var k = significativos(v, pos);
        var nuevo = formatear(v);
        if (nuevo === v) return;
        el.value = nuevo;
        var p = 0, c = 0;
        while (p < nuevo.length && c < k) { if (/[\d,\-]/.test(nuevo[p])) c++; p++; }
        try { el.setSelectionRange(p, p); } catch (e) { }
    }

    // Lo pegado ("$ 1.234,56", "1234.56", "392.040") se interpreta con las mismas reglas que
    // MontoInput.TryParse y se escribe ya formateado.
    function textoPegado(t) {
        var s = (t || '').replace(/[$\s']/g, '');
        var neg = s.indexOf('-') === 0;
        s = s.replace(/^-/, '');
        var up = s.lastIndexOf('.'), uc = s.lastIndexOf(',');
        if (up >= 0 && uc >= 0) {
            s = up > uc ? s.replace(/,/g, '').replace('.', ',') : s.replace(/\./g, '');
        } else if (up >= 0) {
            var veces = s.split('.').length - 1, desp = s.length - up - 1;
            s = (veces > 1 || desp === 3) ? s.replace(/\./g, '') : s.replace('.', ',');
        }
        return (neg ? '-' : '') + s;
    }

    window.addEventListener('beforeinput', function (e) {
        var el = e.target;
        if (!esMonto(el)) return;
        var ini = el.selectionStart, fin = el.selectionEnd;
        if (ini == null) return;
        if (e.inputType === 'insertText' && (e.data === '.' || e.data === ',')) {
            e.preventDefault();
            var sinSel = el.value.slice(0, ini) + el.value.slice(fin);
            if (sinSel.indexOf(',') >= 0) return;   // ya hay coma: no hace nada
            el.setRangeText(',', ini, fin, 'end');
            aplicar(el);
            el.dispatchEvent(new Event('input', { bubbles: true }));
        } else if (e.inputType === 'insertFromPaste' && e.dataTransfer) {
            e.preventDefault();
            el.setRangeText(textoPegado(e.dataTransfer.getData('text')), ini, fin, 'end');
            aplicar(el);
            el.dispatchEvent(new Event('input', { bubbles: true }));
        }
    }, true);

    window.addEventListener('input', function (e) {
        if (esMonto(e.target)) aplicar(e.target);
    }, true);

    // Un casillero en "0,00": al entrar se vacia, asi lo que se tipea no queda pegado al 0,00.
    window.addEventListener('focusin', function (e) {
        var el = e.target;
        if (esMonto(el) && /^-?0(,0*)?$/.test(el.value.replace(/\./g, ''))) { el.dataset.montoVacio = el.value; el.value = ''; }
    }, true);

    // Al salir sin tipear nada: Blazor no se entera (no hubo cambio), asi que se le devuelve el 0,00.
    window.addEventListener('focusout', function (e) {
        var el = e.target;
        if (!esMonto(el)) return;
        if (el.value === '' && el.dataset.montoVacio !== undefined) el.value = el.dataset.montoVacio;
        delete el.dataset.montoVacio;
    }, true);
})();

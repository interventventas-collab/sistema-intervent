// 2026-11-13: el título de cada pantalla lleva el MISMO ícono que el dueño eligió para esa
// pantalla con el lápiz ✏ del menú (pedido: "en cada sitio se vea siempre el ícono que corresponde").
// No toca las 181 pantallas: busca el primer título (h1/h2) de la página y le cambia el emoji
// del principio. No agrega nodos dentro de lo que maneja Blazor: solo cambia el texto del emoji
// y pinta el ícono con ::before (atributo data-mi-ico). Si Blazor vuelve a escribir el título,
// el observador lo corrige otra vez.
(function () {
    var mapa = {};          // dirección → emoji, o "__svg:N" para dibujos
    var estilos = null;     // <style> con los dibujos (taxi)
    var EMOJI = /^\s*(?:\p{Extended_Pictographic}|\p{Regional_Indicator}{2})(?:️|⃣|\p{Emoji_Modifier}|‍\p{Extended_Pictographic}️?)*\s*/u;
    var pendiente = false;

    function iconoDeAca() {
        var p = location.pathname.toLowerCase().replace(/\/+$/, '') || '/';
        return mapa[(p + location.search).toLowerCase()] || mapa[p] || null;
    }
    function primerTexto(el) {
        var w = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, null);
        var n; while ((n = w.nextNode())) { if (n.data.trim() !== '') return n; }
        return null;
    }
    function aplicar() {
        pendiente = false;
        var cont = document.querySelector('main .content');
        if (!cont) return;
        var h = cont.querySelector('h1, h2');
        var ico = iconoDeAca();
        // Títulos que marcamos antes y ya no corresponden (otra pantalla u otro título): sacar la marca.
        document.querySelectorAll('[data-mi-ico]').forEach(function (x) {
            if (x !== h || !ico) x.removeAttribute('data-mi-ico');
        });
        if (!h || !ico) return;
        var t = primerTexto(h);
        if (t && EMOJI.test(t.data)) t.data = t.data.replace(EMOJI, '');
        if (h.getAttribute('data-mi-ico') !== ico) h.setAttribute('data-mi-ico', ico);
    }
    function programar() { if (!pendiente) { pendiente = true; requestAnimationFrame(aplicar); } }

    window.icoTitulo = {
        // iconos: { "/viajes": "🚕" | {svg:"<svg…>"} }
        set: function (iconos) {
            mapa = {};
            var css = '[data-mi-ico]::before{content:attr(data-mi-ico);margin-right:.3em}' +
                      '[data-mi-ico^="__svg"]::before{content:"";display:inline-block;width:1.25em;height:1em;vertical-align:-.12em;background:no-repeat center/contain}';
            var i = 0;
            Object.keys(iconos || {}).forEach(function (k) {
                if (!k || k.charAt(0) !== '/') return; // las secciones (Logística…) no son pantallas
                var v = iconos[k];
                if (v && typeof v === 'object' && v.svg) {
                    var id = '__svg' + (i++);
                    mapa[k.toLowerCase()] = id;
                    css += '[data-mi-ico="' + id + '"]::before{background-image:url("data:image/svg+xml;charset=utf-8,' +
                           encodeURIComponent(v.svg.indexOf('xmlns')<0 ? v.svg.replace('<svg ','<svg xmlns="http://www.w3.org/2000/svg" ') : v.svg).replace(/'/g, '%27') + '")}';
                } else if (v) mapa[k.toLowerCase()] = String(v);
            });
            if (!estilos) { estilos = document.createElement('style'); document.head.appendChild(estilos); }
            estilos.textContent = css;
            programar();
        }
    };
    new MutationObserver(programar).observe(document.documentElement, { childList: true, subtree: true, characterData: true });
})();

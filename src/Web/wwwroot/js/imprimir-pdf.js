// 2026-11-28: imprimir un PDF sin descargarlo. Se carga en un iframe invisible
// (mismo sitio) y se abre el cuadro de impresión del navegador directo.
window.imprimirPdfSinBajar = function (url) {
    var viejo = document.getElementById('imprimir-pdf-frame');
    if (viejo) viejo.remove();
    var f = document.createElement('iframe');
    f.id = 'imprimir-pdf-frame';
    f.style.cssText = 'position:fixed;right:0;bottom:0;width:1px;height:1px;border:0;opacity:0;pointer-events:none;';
    f.onload = function () {
        setTimeout(function () {
            try { f.contentWindow.focus(); f.contentWindow.print(); }
            catch (e) { window.open(url, '_blank'); }
        }, 400);
    };
    f.src = url;
    document.body.appendChild(f);
};

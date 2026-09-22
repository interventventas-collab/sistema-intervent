// 2026-09-22: visor de listas de precios (Shared/ListasPreciosFlotante.razor).
// El PDF de la lista sale de /api/cafe/listas-custom/{id}/pdf, que lo manda "para descargar":
// puesto directo en un <iframe> se bajaria en vez de verse. Lo traemos aca y lo convertimos en
// un enlace local (blob) que el navegador SI muestra adentro de la ventanita, sin descargar nada.
window.listaVisor = {
    abrir: async function (url) {
        try {
            const r = await fetch(url, { credentials: 'same-origin' });
            if (!r.ok) return null;
            const b = await r.blob();
            return URL.createObjectURL(new Blob([b], { type: 'application/pdf' }));
        } catch (e) { return null; }
    },
    soltar: function (u) { try { if (u) URL.revokeObjectURL(u); } catch (e) { } },
    grande: function (u) { if (u) window.open(u, '_blank'); }
};

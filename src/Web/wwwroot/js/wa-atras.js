/*
 * 2026-08-27: el boton "atras" del celular (y el gesto de deslizar desde el borde) en la
 * pantalla de WhatsApp del telefono ("frisaap", /wa).
 *
 * EL PROBLEMA: /wa es UNA sola pantalla. Abrir un chat no cambia de direccion, solo cambia lo
 * que se muestra adentro. Entonces el boton atras de Android no encontraba nada para volver y
 * hacia lo unico que sabe: SALIR de la aplicacion, aunque estuvieras leyendo una charla.
 *
 * LA SOLUCION: cada vez que la pantalla tiene algo abierto (un chat, el panel de la letra, el
 * buscador de la charla) le dejamos una marca al telefono. El atras consume esa marca, nos avisa,
 * y nosotros cerramos esa capa — igual que el WhatsApp de verdad. Cuando ya no queda ninguna
 * capa abierta, el atras sale de la aplicacion, como siempre.
 *
 * COMO SE MANTIENE DERECHO: la pantalla no nos avisa "abri" / "cerre" una por una. Despues de
 * cada dibujado nos dice CUANTAS cosas tiene abiertas y nosotros acomodamos las marcas para que
 * coincidan (ponemos las que falten, sacamos las que sobren). Asi, aunque algo se abra o se
 * cierre por un camino que no conocemos, en el dibujado siguiente se acomoda solo y el boton
 * atras nunca queda "pegado".
 */
window.waAtras = {
    ref: null,        // la pantalla de .NET a la que hay que avisarle
    capas: 0,         // cuantas marcas dejamos puestas en el historial del telefono
    _puesto: false,   // el escucha del "atras" se pone UNA sola vez
    _propio: false,   // el proximo "atras" lo pedimos nosotros: no hay que cerrar nada

    /** Empieza a escuchar el boton atras. `dotnet` es la pantalla que quiere enterarse. */
    iniciar: function (dotnet) {
        window.waAtras.ref = dotnet;
        if (window.waAtras._puesto) return;
        window.waAtras._puesto = true;

        window.addEventListener('popstate', function () {
            // Marca que sacamos nosotros al acomodar (ver sincronizar): ya esta cerrado, no tocar.
            if (window.waAtras._propio) { window.waAtras._propio = false; return; }
            // No hay nada abierto: dejamos que el telefono haga lo suyo (salir de la aplicacion).
            if (window.waAtras.capas <= 0) return;
            window.waAtras.capas--;
            var r = window.waAtras.ref;
            if (!r) return;
            try { r.invokeMethodAsync('AtrasDelCelular'); } catch (e) { }
        });
    },

    /**
     * La pantalla dice cuantas cosas tiene abiertas; acomodamos las marcas para que coincidan.
     * Si faltan, las agregamos. Si sobran (cerraste con un boton de la pantalla), las sacamos
     * avisando que ese "atras" es nuestro, para no cerrar dos veces.
     */
    sincronizar: function (n) {
        n = n || 0;
        if (n < 0) n = 0;
        while (window.waAtras.capas < n) {
            window.waAtras.capas++;
            // Misma direccion de siempre: no navegamos a ningun lado, solo dejamos la marca.
            try { history.pushState({ wa: window.waAtras.capas }, '', location.href); } catch (e) { }
        }
        if (window.waAtras.capas > n) {
            var sobran = window.waAtras.capas - n;
            window.waAtras.capas = n;
            window.waAtras._propio = true;
            try { history.go(-sobran); } catch (e) { window.waAtras._propio = false; }
        }
    },

    /** La pantalla se cerro: dejar de avisarle (el objeto de .NET ya no sirve). */
    soltar: function () { window.waAtras.ref = null; window.waAtras.capas = 0; }
};

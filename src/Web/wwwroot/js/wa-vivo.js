/*
 * 2026-08-20: "volví a la pantalla" para el WhatsApp del celular.
 *
 * El problema: cuando bloqueás el teléfono (o te vas a otra aplicación) el navegador congela la
 * página y CORTA la conexión en vivo con el servidor. Al volver, la pantalla se quedaba mostrando
 * la lista vieja hasta que le tocaba la consulta de respaldo — hasta un minuto mirando algo que ya
 * no era cierto, que es exactamente lo que se sentía como "el celular va lento".
 *
 * Esto avisa a la aplicación apenas volvés a mirar la pantalla (y también cuando vuelve internet),
 * para que reconecte y traiga lo nuevo en el acto.
 */
window.waVivo = {
    ref: null,
    _puesto: false,

    /** Empieza a vigilar. `dotnet` es la pantalla que quiere enterarse. */
    vigilar: function (dotnet) {
        window.waVivo.ref = dotnet;
        if (window.waVivo._puesto) return;     // los escuchas se ponen UNA sola vez
        window.waVivo._puesto = true;

        var avisar = function () {
            if (document.visibilityState !== 'visible') return;
            var r = window.waVivo.ref;
            if (!r) return;
            try { r.invokeMethodAsync('VolviAlaPantalla'); } catch (e) { }
        };

        document.addEventListener('visibilitychange', avisar);
        window.addEventListener('focus', avisar);      // algunos Android no disparan el de arriba
        window.addEventListener('online', avisar);     // volvió internet
    },

    /** La pantalla se cerró: dejar de avisarle (el objeto de .NET ya no sirve). */
    soltar: function () { window.waVivo.ref = null; }
};

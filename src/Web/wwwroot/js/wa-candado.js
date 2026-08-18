/*
 * 2026-08-18: candado por INACTIVIDAD de la pantalla de WhatsApp del celu.
 *
 * Antes: ponías el código de 4 dígitos UNA vez y el teléfono se acordaba para siempre.
 * O sea que el candado servía solo el primer día: después, cualquiera que agarrara el
 * celu desbloqueado entraba a todos los chats de la empresa.
 *
 * Ahora: se marca la última vez que se usó y, si pasaron más de X minutos, vuelve a pedir
 * el código (o la huella, que es un toque). Mientras la estás usando no molesta nunca.
 */
window.waCandado = {
    KEY: 'wamovil.ultimoUso',

    /** Anota "recién la usé". */
    marcar: function () {
        try { localStorage.setItem(window.waCandado.KEY, String(Date.now())); } catch (e) { }
    },

    /** ¿Pasaron más de `minutos` sin tocarla? */
    vencido: function (minutos) {
        try {
            var t = parseInt(localStorage.getItem(window.waCandado.KEY) || '0', 10);
            if (!t) return true;                       // nunca se marcó: pedir código
            return (Date.now() - t) > minutos * 60000;
        } catch (e) { return false; }
    },

    /**
     * Empieza a vigilar. Marca actividad con cada toque (como mucho una vez cada 20 s para no
     * escribir de más) y, cuando el usuario vuelve a la pantalla después de un rato, avisa a
     * la aplicación para que vuelva a pedir el código.
     */
    iniciar: function (dotnet, minutos) {
        window.waCandado.marcar();
        var ultimo = 0;
        var marcar = function () {
            var n = Date.now();
            if (n - ultimo > 20000) { ultimo = n; window.waCandado.marcar(); }
        };
        ['touchstart', 'click', 'keydown'].forEach(function (ev) {
            document.addEventListener(ev, marcar, true);
        });
        document.addEventListener('visibilitychange', function () {
            if (document.visibilityState === 'hidden') {
                window.waCandado.marcar();             // al irse, deja la hora de salida
            } else if (window.waCandado.vencido(minutos)) {
                try { dotnet.invokeMethodAsync('Bloquear'); } catch (e) { }
            }
        });
    }
};

/*
 * 2026-08-18: alta/baja de los avisos de WhatsApp CON LA PANTALLA CERRADA.
 *
 * Cómo funciona, en criollo: el teléfono le pide permiso al usuario, se anota en el
 * servicio de notificaciones de su propio navegador (Google en Android, Apple en iPhone)
 * y nos devuelve una dirección ("endpoint"). Nosotros guardamos esa dirección y, cuando
 * entra un mensaje, le pedimos a ese servicio que despierte al teléfono.
 *
 * OJO iPhone: Apple solo deja recibir estos avisos si la pantalla está AGREGADA A LA
 * PANTALLA DE INICIO (Compartir → "Agregar a inicio"). En Android alcanza con dar permiso.
 */
window.waPush = {
    /** ¿Este teléfono puede recibir avisos con la pantalla cerrada? */
    soportado: function () {
        return ('serviceWorker' in navigator) && ('PushManager' in window) && (typeof Notification !== 'undefined');
    },

    /** En iPhone hace falta tener la pantalla instalada en el inicio. */
    necesitaInstalar: function () {
        try {
            var esIOS = /iPad|iPhone|iPod/.test(navigator.userAgent);
            var instalada = window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
            return esIOS && !instalada;
        } catch (e) { return false; }
    },

    /** ¿Ya está anotado este teléfono? Devuelve el endpoint o null. */
    estado: async function () {
        try {
            if (!window.waPush.soportado()) return null;
            var reg = await navigator.serviceWorker.getRegistration('/wa');
            if (!reg) return null;
            var sub = await reg.pushManager.getSubscription();
            return sub ? sub.endpoint : null;
        } catch (e) { return null; }
    },

    /**
     * Anota este teléfono. Devuelve { ok, endpoint, motivo }.
     * clavePublica: la que da el servidor (base64url).
     */
    activar: async function (clavePublica) {
        try {
            if (!window.waPush.soportado()) return { ok: false, motivo: 'Este teléfono no soporta avisos con la pantalla cerrada.' };
            if (window.waPush.necesitaInstalar())
                return { ok: false, motivo: 'En iPhone primero hay que agregar la pantalla al inicio: tocá Compartir y después "Agregar a inicio".' };

            var permiso = await Notification.requestPermission();
            if (permiso !== 'granted') return { ok: false, motivo: 'Hay que permitir los avisos en el navegador.' };

            var reg = await navigator.serviceWorker.register('/sw.js', { scope: '/wa' });
            await navigator.serviceWorker.ready;

            var sub = await reg.pushManager.getSubscription();
            if (!sub) {
                sub = await reg.pushManager.subscribe({
                    userVisibleOnly: true,
                    applicationServerKey: window.waPush._b64ToBytes(clavePublica)
                });
            }
            return { ok: true, endpoint: sub.endpoint };
        } catch (e) {
            console.warn('waPush.activar', e);
            return { ok: false, motivo: 'No se pudo activar en este teléfono (' + (e && e.message ? e.message : e) + ').' };
        }
    },

    /** Da de baja este teléfono. Devuelve el endpoint que tenía (para borrarlo en el server). */
    desactivar: async function () {
        try {
            var reg = await navigator.serviceWorker.getRegistration('/wa');
            if (!reg) return null;
            var sub = await reg.pushManager.getSubscription();
            if (!sub) return null;
            var ep = sub.endpoint;
            await sub.unsubscribe();
            return ep;
        } catch (e) { return null; }
    },

    /** base64url → Uint8Array (lo que pide el navegador para la clave). */
    _b64ToBytes: function (base64url) {
        var pad = '='.repeat((4 - (base64url.length % 4)) % 4);
        var b64 = (base64url + pad).replace(/-/g, '+').replace(/_/g, '/');
        var raw = atob(b64);
        var arr = new Uint8Array(raw.length);
        for (var i = 0; i < raw.length; i++) arr[i] = raw.charCodeAt(i);
        return arr;
    }
};

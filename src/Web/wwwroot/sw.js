/*
 * 2026-06-05: Service Worker minimalista para hacer instalables las PWAs
 * (Mis Pedidos y Fichador). Chrome exige un SW con fetch handler para
 * mostrar el prompt "Instalar app".
 *
 * No hace caching offline — solo pasa-through las requests al network.
 * Si en el futuro queremos modo offline, agregar logica de cache aca.
 */

self.addEventListener('install', (event) => {
    // Activa el SW nuevo inmediatamente sin esperar a que se cierren las pestañas viejas.
    self.skipWaiting();
});

self.addEventListener('activate', (event) => {
    // Toma control de todas las pestañas abiertas inmediatamente.
    event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', (event) => {
    // Pass-through: no interceptamos nada, solo dejamos que el navegador haga su request normal.
    // El handler vacío basta para que Chrome considere la pagina como PWA instalable.
    return;
});

/* ════════════════════════════════════════════════════════════════════════
 * 2026-08-18: AVISOS DE WHATSAPP CON LA PANTALLA CERRADA (Web Push).
 *
 * El servidor manda un empujón VACÍO (sin texto adentro) — ver
 * Api/Services/WaPushService.cs para el motivo. El aviso lo armamos acá:
 * al recibirlo le preguntamos al sistema quién escribió último (la sesión
 * del usuario viaja sola, es el mismo dominio) y mostramos su nombre.
 *
 * Si esa consulta falla (sin señal, sesión vencida), igual mostramos un
 * aviso genérico: es preferible a que no suene nada.
 * ════════════════════════════════════════════════════════════════════════ */

self.addEventListener('push', (event) => {
    event.waitUntil((async () => {
        let titulo = 'WhatsApp';
        let cuerpo = 'Tenés un mensaje nuevo';
        try {
            const r = await fetch('/api/whatsapp/twilio/conversaciones', { credentials: 'include' });
            if (r.ok) {
                const convs = await r.json();
                const entrantes = (convs || [])
                    .filter(c => c.ultimoDireccion === 'INCOMING' && !c.archivado)
                    .sort((a, b) => new Date(b.ultimoAt) - new Date(a.ultimoAt));
                if (entrantes.length > 0) {
                    const c = entrantes[0];
                    titulo = c.clienteNombre || c.nombrePerfil || (c.numero || '').replace('whatsapp:', '');
                    cuerpo = c.ultimoMensaje || (c.ultimoMediaUrl ? '📷 Te mandaron un archivo' : 'Mensaje nuevo');
                    if (cuerpo.length > 120) cuerpo = cuerpo.slice(0, 120) + '…';
                }
            }
        } catch (e) { /* sin datos: queda el aviso genérico */ }

        await self.registration.showNotification(titulo, {
            body: cuerpo,
            icon: '/img/wa-icon.png',
            badge: '/img/wa-icon.png',
            tag: 'wa-nuevo',       // los avisos seguidos se pisan, no se apilan de a 20
            renotify: true,
            data: { url: '/wa' }
        });
    })());
});

/* Al tocar el aviso: si la pantalla ya está abierta en alguna pestaña, la trae al frente;
   si no, la abre. */
self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    event.waitUntil((async () => {
        const url = (event.notification.data && event.notification.data.url) || '/wa';
        const abiertas = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const c of abiertas) {
            if (c.url.includes('/wa') && 'focus' in c) return c.focus();
        }
        if (self.clients.openWindow) return self.clients.openWindow(url);
    })());
});

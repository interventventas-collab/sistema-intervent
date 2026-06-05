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

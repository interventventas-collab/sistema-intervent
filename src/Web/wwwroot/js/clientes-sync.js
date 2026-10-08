// 2026-10-08: avisar entre PESTAÑAS que cambió algún cliente.
//
// Las pantallas con buscador de clientes (Nueva Venta, Reservas, Cobranzas, ...) traen la
// lista UNA vez al abrirse. Si en otra pestaña se carga o corrige un cliente, esa lista quedaba
// vieja y el cliente "no aparecía" hasta apretar F5 (caso Jimena Gutierrez en Reservas).
//
// BroadcastChannel llega a todas las pestañas del MISMO navegador (no a otras compus; para eso
// cada buscador vuelve a pedir la lista cuando no encuentra nada).
window.clientesSync = (function () {
    var canal = null;
    try { canal = new BroadcastChannel('aiml-clientes'); } catch (e) { }
    var suscriptos = {};
    var proximo = 1;

    if (canal) {
        canal.onmessage = function () {
            Object.keys(suscriptos).forEach(function (k) {
                try { suscriptos[k].invokeMethodAsync('OnClientesCambiaron'); } catch (e) { }
            });
        };
    }

    return {
        // Lo llama quien guardó un cliente. No se avisa a sí misma (BroadcastChannel no
        // entrega el mensaje a la pestaña que lo manda): esa pantalla ya recarga sola.
        avisar: function () { try { if (canal) canal.postMessage({ t: Date.now() }); } catch (e) { } },
        suscribir: function (dotnetRef) { var id = proximo++; suscriptos[id] = dotnetRef; return id; },
        desuscribir: function (id) { delete suscriptos[id]; }
    };
})();

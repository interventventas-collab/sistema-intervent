// 2026-09-24: imprimir la etiqueta de MeLi DIRECTO en la Zebra, como hace MercadoLibre en la PC del
// deposito. MeLi usa "Zebra Browser Print", un programita de Zebra instalado en esa compu que escucha
// en la propia maquina (https://localhost:9101, o http://localhost:9100) y le pasa el texto ZPL a la
// impresora. Aca hacemos lo mismo: pedimos a nuestra API el .txt (ZPL, identico al de MeLi) y se lo
// mandamos a Browser Print. La primera vez, Browser Print pregunta si deja imprimir a esta pagina.
//
// 2026-09-24 (tarde): si esa compu no tiene Browser Print, se prueba con QZ Tray (programa gratuito,
// sirve para cualquier impresora; lo usa la HPRT HD600, que tambien entiende ZPL). QZ Tray confia en
// nosotros por el certificado de /api/qz/certificado (se carga una vez en su Site Manager) y cada
// impresion va firmada por /api/qz/firmar.
//
// window.zebraImprimir(url) -> "ok" | "sin-zebra" (ni Browser Print ni QZ Tray: bajar el .txt)
//                              | "error:<texto>" (la API no devolvio etiqueta, etc.)
(function () {
    var BASES = ["https://localhost:9101", "http://localhost:9100"];

    function conTiempo(promesa, ms) {
        return Promise.race([promesa, new Promise(function (_, rej) { setTimeout(function () { rej(new Error("tiempo")); }, ms); })]);
    }

    async function buscarImpresora() {
        for (var i = 0; i < BASES.length; i++) {
            try {
                var r = await conTiempo(fetch(BASES[i] + "/default?type=printer"), 2500);
                if (!r.ok) continue;
                var txt = await r.text();
                if (!txt) continue;
                var dev = JSON.parse(txt);
                if (dev && dev.name) return { base: BASES[i], dev: dev };
            } catch (e) { /* probar la siguiente */ }
        }
        return null;
    }

    // ── QZ Tray ──
    var qzConfigurado = false;

    function textoDe(ruta, opciones) {
        return fetch(ruta, Object.assign({ credentials: "same-origin" }, opciones || {}))
            .then(function (r) { return r.ok ? r.text() : Promise.reject(new Error("HTTP " + r.status)); });
    }

    async function qzConectar() {
        if (typeof qz === "undefined") return false;
        if (!qzConfigurado) {
            qz.security.setCertificatePromise(function (resolve, reject) {
                textoDe("/api/qz/certificado").then(resolve, reject);
            });
            qz.security.setSignatureAlgorithm("SHA512");
            qz.security.setSignaturePromise(function (aFirmar) {
                return function (resolve, reject) {
                    textoDe("/api/qz/firmar", { method: "POST", body: aFirmar }).then(resolve, reject);
                };
            });
            qzConfigurado = true;
        }
        if (qz.websocket.isActive()) return true;
        try {
            await conTiempo(qz.websocket.connect({ retries: 0, delay: 0 }), 5000);
            return true;
        } catch (e) { return false; }
    }

    // La térmica de esa compu: la que se llame HPRT / ZPL / Zebra; si no, la predeterminada.
    async function qzImpresora() {
        var todas = await qz.printers.find();
        if (!Array.isArray(todas)) todas = todas ? [todas] : [];
        var termica = todas.find(function (n) { return /hprt|zpl|zebra|zdesigner/i.test(n); });
        return termica || await qz.printers.getDefault();
    }

    async function traerZpl(url) {
        var resp = await fetch(url, { credentials: "same-origin" });
        var zpl = await resp.text();
        if (!resp.ok || zpl.indexOf("^XA") < 0) {
            // la API devuelve una pagina de error legible: sacarle el texto
            var tmp = document.createElement("div");
            tmp.innerHTML = zpl;
            var p = tmp.querySelector("p");
            return { error: "error:" + (p ? p.textContent : "MercadoLibre no devolvio la etiqueta.") };
        }
        return { zpl: zpl };
    }

    window.zebraImprimir = async function (url) {
        var z = await buscarImpresora();
        if (!z) {
            // Sin Browser Print: probar QZ Tray (HPRT u otra térmica que entienda ZPL).
            if (!(await qzConectar())) return "sin-zebra";
            var t = await traerZpl(url);
            if (t.error) return t.error;
            try {
                var impresora = await qzImpresora();
                if (!impresora) return "error:QZ Tray no encontró ninguna impresora en esta compu.";
                await qz.print(qz.configs.create(impresora), [{ type: "raw", format: "command", flavor: "plain", data: t.zpl }]);
                return "ok";
            } catch (e) {
                return "error:QZ Tray no pudo imprimir: " + (e && e.message ? e.message : e);
            }
        }

        var r = await traerZpl(url);
        if (r.error) return r.error;
        var zpl = r.zpl;

        // Igual que la libreria oficial de Zebra: JSON como texto plano (sin preflight).
        var w = await conTiempo(fetch(z.base + "/write", {
            method: "POST",
            body: JSON.stringify({ device: z.dev, data: zpl })
        }), 15000);
        if (!w.ok) return "error:La Zebra no acepto la etiqueta (" + w.status + ").";
        return "ok";
    };

    // Sin Zebra: bajar el .txt con un link (una ventana nueva la bloquearia el navegador, porque ya
    // pasaron unos segundos desde el clic buscando la impresora).
    window.zebraBajar = function (url) {
        var a = document.createElement("a");
        a.href = url;
        a.download = "";
        document.body.appendChild(a);
        a.click();
        a.remove();
    };
})();

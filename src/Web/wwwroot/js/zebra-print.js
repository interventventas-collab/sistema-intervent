// 2026-09-24: imprimir la etiqueta de MeLi DIRECTO en la Zebra, como hace MercadoLibre en la PC del
// deposito. MeLi usa "Zebra Browser Print", un programita de Zebra instalado en esa compu que escucha
// en la propia maquina (https://localhost:9101, o http://localhost:9100) y le pasa el texto ZPL a la
// impresora. Aca hacemos lo mismo: pedimos a nuestra API el .txt (ZPL, identico al de MeLi) y se lo
// mandamos a Browser Print. La primera vez, Browser Print pregunta si deja imprimir a esta pagina.
//
// window.zebraImprimir(url) -> "ok" | "sin-zebra" (no hay programa o impresora: bajar el .txt)
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

    window.zebraImprimir = async function (url) {
        var z = await buscarImpresora();
        if (!z) return "sin-zebra";

        var resp = await fetch(url, { credentials: "same-origin" });
        var zpl = await resp.text();
        if (!resp.ok || zpl.indexOf("^XA") < 0) {
            // la API devuelve una pagina de error legible: sacarle el texto
            var tmp = document.createElement("div");
            tmp.innerHTML = zpl;
            var p = tmp.querySelector("p");
            return "error:" + (p ? p.textContent : "MercadoLibre no devolvio la etiqueta.");
        }

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

// 16/10/2026: "Comprobantes" en el link del repartidor. Comparte el PDF ADJUNTO (no un link)
// con el menú de compartir del celu (27/10/2026: también fotos/Excel de Documentación, con su tipo real), así eligen WhatsApp y el contacto y listo.
// El celu solo deja abrir ese menú justo después de un toque: si el PDF tardó en armarse,
// el permiso se vence. En ese caso el PDF ya queda guardado y se pide tocar de nuevo.
window.mpCompartir = (function () {
  const cache = {};   // url -> File

  function nombreDe(resp, porDefecto) {
    const cd = resp.headers.get('Content-Disposition') || '';
    let m = /filename\*=UTF-8''([^;]+)/i.exec(cd);
    if (m) { try { return decodeURIComponent(m[1]); } catch (e) {} }
    m = /filename="?([^";]+)"?/i.exec(cd);
    return m ? m[1] : porDefecto;
  }

  async function traer(url, porDefecto) {
    if (cache[url]) return cache[url];
    const resp = await fetch(url, { credentials: 'same-origin' });
    if (!resp.ok) throw new Error('http ' + resp.status);
    const blob = await resp.blob();
    const f = new File([blob], nombreDe(resp, porDefecto), { type: blob.type || 'application/pdf' });
    cache[url] = f;
    return f;
  }

  function bajar(file) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(file);
    a.download = file.name;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 30000);
  }

  // Devuelve: "ok" | "cancelado" | "tocar-de-nuevo" | "bajado" (el celu no comparte archivos) | "error"
  async function compartir(url, porDefecto, texto) {
    let file;
    try { file = await traer(url, porDefecto); } catch (e) { return 'error'; }
    if (!(navigator.canShare && navigator.canShare({ files: [file] }))) { bajar(file); return 'bajado'; }
    try {
      await navigator.share({ files: [file], title: file.name, text: texto || '' });
      return 'ok';
    } catch (e) {
      if (e && e.name === 'AbortError') return 'cancelado';
      if (e && e.name === 'NotAllowedError') return 'tocar-de-nuevo';
      return 'error';
    }
  }

  // 17/10/2026: ver el PDF ADENTRO de la app (Chrome de Android no muestra un PDF dentro de una
  // página: lo baja). Se dibuja cada hoja con pdf.js, que se carga recién la primera vez que se usa.
  // El archivo queda guardado, así que después "WhatsApp" sale al instante.
  let pdfjsCargando = null;
  function cargarPdfjs() {
    if (window.pdfjsLib) return Promise.resolve();
    if (pdfjsCargando) return pdfjsCargando;
    pdfjsCargando = new Promise((ok, mal) => {
      const s = document.createElement('script');
      s.src = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.min.js';
      s.onload = () => {
        window.pdfjsLib.GlobalWorkerOptions.workerSrc = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/3.11.174/pdf.worker.min.js';
        ok();
      };
      s.onerror = () => { pdfjsCargando = null; mal(new Error('no cargó el visor')); };
      document.head.appendChild(s);
    });
    return pdfjsCargando;
  }

  // Devuelve "ok" o "error".
  async function ver(url, porDefecto, contenedorId) {
    try {
      const [file] = await Promise.all([traer(url, porDefecto), cargarPdfjs()]);
      const cont = document.getElementById(contenedorId);
      if (!cont) return 'error';
      const doc = await window.pdfjsLib.getDocument({ data: await file.arrayBuffer() }).promise;
      cont.innerHTML = '';
      const ancho = cont.clientWidth || 340;
      const dpr = Math.min(window.devicePixelRatio || 1, 3);
      for (let n = 1; n <= doc.numPages; n++) {
        const page = await doc.getPage(n);
        const base = page.getViewport({ scale: 1 });
        const vp = page.getViewport({ scale: (ancho / base.width) * dpr });
        const c = document.createElement('canvas');
        c.width = vp.width; c.height = vp.height;
        c.style.width = '100%'; c.style.display = 'block'; c.style.marginBottom = '8px';
        c.style.background = '#fff'; c.style.boxShadow = '0 1px 4px rgba(0,0,0,.15)';
        cont.appendChild(c);
        await page.render({ canvasContext: c.getContext('2d'), viewport: vp }).promise;
      }
      return 'ok';
    } catch (e) { return 'error'; }
  }

  async function bajarUrl(url, porDefecto) {
    try { bajar(await traer(url, porDefecto)); return 'ok'; } catch (e) { return 'error'; }
  }

  return { compartir, ver, bajarUrl };
})();

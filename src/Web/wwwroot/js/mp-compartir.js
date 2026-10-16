// 16/10/2026: "Comprobantes" en el link del repartidor. Comparte el PDF ADJUNTO (no un link)
// con el menú de compartir del celu, así eligen WhatsApp y el contacto y listo.
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
    const f = new File([blob], nombreDe(resp, porDefecto), { type: 'application/pdf' });
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

  return { compartir };
})();

// 2026-11-27: "Dónde está todo" en 3D para el armado de pedidos (CardPedido → 🧭).
// Se abre arriba de toda la página (se agrega al <body>, fuera de Blazor), con el plano real del
// depósito en 3D (CSS 3D, sin librerías): arranca mostrando todo el recorrido y la cámara vuela al
// estante de cada producto; el estante parpadea en naranja y arriba sale el cartel con el código.
// "Lo tengo · siguiente" tilda el producto también en la tarjeta del pedido (tilde visual, como el de siempre).
//
// window.plano3d.abrir({ titulo, subtitulo, plantas: { PB: [elementos], P1: [...] },
//                        items: [{ id, sku, n, c, est, planta }], inicio: "D2-3" | null,
//                        hechos: [ids ya tildados], dotnet: ref con Tildar3D(id),
//                        etiqueta: "Dónde está", soloVer: true (sin "Lo tengo"; lo usa el planito de Productos),
//                        buscar: true (2026-11-27: buscador de productos arriba de la lista; vuela al estante elegido),
//                        editar: true (2026-11-28: en el panel del estante se pueden agregar y sacar productos) })
// 2026-11-28: pasando el mouse por una altura muestra lo que hay; tocándola abre el estante en el panel de la
// derecha (agregar / sacar). "👁 Qué ver" prende y apaga capas (mismo localStorage "plano_capas" que el plano 2D).
// Elementos = PlanoElementoDto tal cual llega de la API (camelCase).
(function () {
    const S = 40; // 1 metro = 40 px
    // 2026-11-28: capas que se pueden prender y apagar (mismas claves que el plano 2D)
    const CAPAS = [['ambientes', 'Ambientes'], ['estructura', 'Puertas, paredes y escaleras'], ['racks', 'Racks'], ['estanterias', 'Estanterías'],
        ['piso', 'Lugares en el piso'], ['pcs', 'PCs'], ['muebles', 'Muebles y escritorios'], ['otros', 'Vehículos, pallets, sillas y otros']];
    const CAPA_TIPO = { pc: 'pcs', escritorio: 'muebles', mesa: 'muebles', mueble: 'muebles', heladera: 'muebles',
        vehiculo: 'otros', pallet: 'otros', contenedor: 'otros', montacargas: 'otros', sillas: 'otros',
        tostadora: 'muebles', zaranda: 'muebles', campana: 'muebles', tolva: 'muebles', dispensador: 'muebles',
        molino: 'muebles', balanza: 'muebles', selladora: 'muebles', tacho: 'muebles', mesada: 'muebles' };
    // 2026-11-28: lo que se puede dibujar en un estante aunque no tenga stock
    const DIBUJOS = [['bolsas', 'Bolsas de café'], ['cajas', 'Cajas'], ['bidones', 'Bidones'], ['sillas', 'Sillas apiladas'],
        ['pallet', 'Pallet vacío'], ['rollos', 'Rollos'], ['bultos', 'Bultos'], ['foto', 'Con una foto']];
    // 2026-11-28: textura de bolsa de yute (como las de café crudo): arpillera, costura y las rayas verde y amarilla
    const svgUrl = (x) => 'url("data:image/svg+xml;utf8,' + encodeURIComponent(x) + '")';
    const YUTE_LADO = svgUrl(`<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 120 40' preserveAspectRatio='none'>
      <defs><linearGradient id='g' x1='0' y1='0' x2='0' y2='1'><stop offset='0' stop-color='#e7d9b6'/><stop offset='.55' stop-color='#d4bf93'/><stop offset='1' stop-color='#a99067'/></linearGradient>
      <pattern id='t' width='3' height='3' patternUnits='userSpaceOnUse'><path d='M0 0H3M0 0V3' stroke='#8e7a55' stroke-width='.35' opacity='.45'/></pattern></defs>
      <rect width='120' height='40' fill='url(#g)'/><rect width='120' height='40' fill='url(#t)'/>
      <path d='M2 20 Q60 25 118 20' fill='none' stroke='#7d6845' stroke-width='1.1' stroke-dasharray='3 2'/>
      <rect x='52' y='0' width='2.6' height='40' fill='#3f8f3a'/><rect x='56' y='0' width='2.6' height='40' fill='#d9b520'/>
      <rect x='98' y='0' width='1.6' height='40' fill='#c56a7a' opacity='.7'/></svg>`);
    const YUTE_TAPA = svgUrl(`<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 120 60' preserveAspectRatio='none'>
      <defs><radialGradient id='g' cx='.5' cy='.45' r='.7'><stop offset='0' stop-color='#ecdfbe'/><stop offset='1' stop-color='#bfa77b'/></radialGradient>
      <pattern id='t' width='3' height='3' patternUnits='userSpaceOnUse'><path d='M0 0H3M0 0V3' stroke='#8e7a55' stroke-width='.35' opacity='.4'/></pattern></defs>
      <rect width='120' height='60' fill='url(#g)'/><rect width='120' height='60' fill='url(#t)'/>
      <rect x='52' y='0' width='2.6' height='60' fill='#3f8f3a'/><rect x='56' y='0' width='2.6' height='60' fill='#d9b520'/>
      <text x='70' y='26' font-family='Arial' font-size='9' font-weight='700' fill='#c0392b' opacity='.75'>CAFÉ</text></svg>`);
    const nombreDibujo = (t) => (DIBUJOS.find(x => x[0] === t) || [t, t])[1];
    function capasApagadas() {
        try {
            const v = localStorage.getItem('plano_capas'); if (v == null) return new Set();
            const on = new Set(v.split(',').filter(Boolean));
            return new Set(CAPAS.map(c => c[0]).filter(c => !on.has(c)));
        } catch { return new Set(); }
    }
    function guardarCapas(off) {
        try {
            const v = localStorage.getItem('plano_capas');
            const on = new Set(v == null ? ['estructura', 'ambientes', 'pasillos', 'racks', 'estanterias', 'piso', 'pcs', 'muebles', 'otros'] : v.split(',').filter(Boolean));
            CAPAS.forEach(c => { if (off.has(c[0])) on.delete(c[0]); else on.add(c[0]); });
            localStorage.setItem('plano_capas', [...on].join(','));
        } catch { }
    }
    const CSS = `
.p3 { position: fixed; inset: 0; z-index: 4000; display: grid; grid-template-columns: 1fr 380px; background: #0b1220; font-family: Inter, system-ui, -apple-system, Segoe UI, Arial, sans-serif; }
.p3 * { box-sizing: border-box; }
.p3-esc { position: relative; overflow: hidden; background: radial-gradient(ellipse at 50% 30%, #1e293b 0%, #0b1220 75%); perspective: 1700px; perspective-origin: 50% 35%; touch-action: none; }
.p3-mundo { position: absolute; left: 50%; top: 52%; width: 0; height: 0; transform-style: preserve-3d; transition: transform 2.2s cubic-bezier(.65,.02,.25,1); }
.p3-mundo * { transform-style: preserve-3d; }
.p3-piso { position: absolute; left: 0; top: 0; background: #f8fafc; box-shadow: 0 0 0 2px #334155, 0 30px 80px rgba(0,0,0,.6); }
.p3-zona { position: absolute; border: 1px solid rgba(100,116,139,.45); }
.p3-znom { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; font-weight: 800; color: rgba(51,65,85,.5); letter-spacing: .02em; text-transform: uppercase; text-align: center; pointer-events: none; }
.p3-cosa { position: absolute; border-radius: 3px; }
.p3-pared { position: absolute; background: linear-gradient(#cbd5e1, #94a3b8); opacity: .92; transform-origin: 0 0; }
.p3-rack { position: absolute; }
.p3-poste { position: absolute; width: 5px; background: #475569; transform-origin: 0 0; }
.p3-niv { position: absolute; left: 0; top: 0; background: rgba(250,204,21,.92); border: 1.5px solid #a16207; }
.p3-caja { position: absolute; }
.p3-caja > i { position: absolute; display: block; }
.p3-rcod { position: absolute; left: 0; top: 0; display: flex; align-items: center; justify-content: center; font-weight: 900; color: #78350f; }
.p3-rack.meta .p3-poste { background: #ea580c; box-shadow: 0 0 14px #f97316; }
.p3-rack.meta .p3-niv { background: rgba(253,186,116,.9); border-color: #c2410c; }
.p3-rack.meta .p3-niv.este { background: #f97316; border-color: #7c2d12; box-shadow: 0 0 30px 6px rgba(249,115,22,.9); animation: p3-titila 1.1s ease-in-out infinite; }
@keyframes p3-titila { 50% { box-shadow: 0 0 6px 1px rgba(249,115,22,.4); background: #fb923c; } }
.p3-rack.otro .p3-niv { opacity: .35; }
.p3-rack.otro .p3-caja > i { opacity: .3; }
.p3-rack.otro .p3-poste { opacity: .45; }
.p3-rack.otro .p3-rcod { opacity: .6; }
.p3-marca { position: absolute; width: 0; height: 0; }
.p3-rack.piso .p3-niv { background: rgba(254,243,199,.75); border: 3px dashed #a16207; }
.p3-rack.meta.piso .p3-niv.este { background: rgba(249,115,22,.75); }
.p3-rack.tapa { opacity: .07; pointer-events: none; }
.p3-mundo.frente .p3-pared { opacity: .06; }
.p3-alt { position: absolute; transform: translate(-100%, -50%); background: #0f172a; color: #fde68a; font: 800 12px ui-monospace, monospace; padding: 1px 4px; border-radius: 4px; border: 1px solid #a16207; white-space: nowrap; pointer-events: none; }
.p3-alt.este { background: #f97316; color: #fff; border-color: #fff; }
.p3-ctl button.on { background: #f97316; border-color: #f97316; color: #fff; }
.p3-ctl button[hidden] { display: none; }
.p3-cartel { position: absolute; transition: transform 2.2s cubic-bezier(.65,.02,.25,1); }
.p3-globo { position: absolute; left: 0; top: -48px; transform: translate(-50%, -100%); white-space: nowrap; pointer-events: none; }
.p3-globo .g { background: #0f172a; color: #fff; border-radius: 14px; padding: 10px 16px 11px; box-shadow: 0 12px 30px rgba(0,0,0,.45); border: 2px solid #f97316; }
.p3-globo .g b { display: block; font-family: ui-monospace, monospace; font-size: 34px; color: #fdba74; letter-spacing: .02em; }
.p3-globo .g span { font-size: 15px; color: #e2e8f0; }
.p3-globo .pico { width: 0; height: 0; margin: 0 auto; border: 12px solid transparent; border-top-color: #f97316; }
.p3-pin { position: absolute; transform: translate(-50%, -100%); pointer-events: none; }
.p3-pin div { width: 40px; height: 40px; border-radius: 50% 50% 50% 0; transform: rotate(-45deg); background: #64748b; display: flex; align-items: center; justify-content: center; box-shadow: 0 6px 14px rgba(0,0,0,.4); border: 2px solid #fff; }
.p3-pin span { transform: rotate(45deg); color: #fff; font-weight: 900; font-size: 18px; }
.p3-pin.ok div { background: #16a34a; }
.p3-pin.act div { background: #f97316; }
.p3-ruta { position: absolute; left: 0; top: 0; pointer-events: none; overflow: visible; }
.p3-ruta path { fill: none; stroke: #f97316; stroke-width: 9; stroke-linecap: round; stroke-linejoin: round; stroke-dasharray: 2 22; filter: drop-shadow(0 0 6px rgba(249,115,22,.8)); animation: p3-camina 1s linear infinite; }
@keyframes p3-camina { to { stroke-dashoffset: -24; } }
.p3-hud { position: absolute; left: 18px; top: 16px; right: 70px; color: #e2e8f0; font-size: 14px; pointer-events: none; }
.p3-hud .t { font-size: 13px; color: #94a3b8; text-transform: uppercase; letter-spacing: .08em; font-weight: 700; }
.p3-hud .d { font-size: 26px; font-weight: 800; color: #fff; margin-top: 2px; }
.p3-hud .d em { font-style: normal; color: #fdba74; font-family: ui-monospace, monospace; }
.p3-hud .s { color: #cbd5e1; margin-top: 4px; }
.p3-ctl { position: absolute; left: 18px; bottom: 18px; display: flex; gap: 8px; flex-wrap: wrap; }
.p3-ctl button, .p3-x { background: rgba(15,23,42,.85); color: #e2e8f0; border: 1px solid #334155; border-radius: 10px; padding: 9px 14px; font-size: 14px; font-weight: 700; cursor: pointer; font-family: inherit; }
.p3-ctl button:hover, .p3-x:hover { border-color: #f97316; }
.p3-x { position: absolute; right: 14px; top: 12px; width: 44px; height: 44px; padding: 0; font-size: 18px; }
.p3-pan { background: #fff; display: flex; flex-direction: column; border-left: 1px solid #e2e8f0; min-height: 0; color: #0f172a; }
.p3-cab { padding: 18px 18px 12px; border-bottom: 1px solid #f1f5f9; }
.p3-cab .t { font-size: 12px; color: #64748b; text-transform: uppercase; letter-spacing: .08em; font-weight: 700; }
.p3-cab h2 { margin: 3px 0 2px; font-size: 20px; }
.p3-cab .s { color: #64748b; font-size: 13px; }
.p3-barra { height: 6px; background: #f1f5f9; border-radius: 99px; margin-top: 10px; overflow: hidden; }
.p3-barra i { display: block; height: 100%; width: 0; background: #16a34a; border-radius: 99px; transition: width .4s; }
.p3-items { flex: 1; overflow: auto; padding: 8px 10px; min-height: 0; }
.p3-it { display: flex; gap: 10px; align-items: center; padding: 11px 10px; border-radius: 12px; cursor: pointer; border: 2px solid transparent; margin-bottom: 4px; }
.p3-it:hover { background: #f8fafc; }
.p3-it.act { border-color: #f97316; background: #fff7ed; }
.p3-it.ok { opacity: .55; }
.p3-it.sin { cursor: default; }
.p3-num { width: 30px; height: 30px; border-radius: 50%; background: #e2e8f0; color: #334155; display: flex; align-items: center; justify-content: center; font-weight: 900; flex-shrink: 0; }
.p3-it.act .p3-num { background: #f97316; color: #fff; }
.p3-it.ok .p3-num { background: #16a34a; color: #fff; }
.p3-it .n { font-size: 14px; font-weight: 700; line-height: 1.25; }
.p3-it .k { font-size: 12px; color: #64748b; margin-top: 2px; }
.p3-lug { display: inline-flex; margin-top: 5px; border: 1px solid #cbd5e1; border-radius: 99px; overflow: hidden; font-size: 12px; font-weight: 700; }
.p3-lug b { padding: 1px 8px; font-family: ui-monospace, monospace; color: #334155; }
.p3-lug span { padding: 1px 8px; background: #eff6ff; color: #1d4ed8; border-left: 1px solid #dbeafe; }
.p3-it.act .p3-lug { border-color: #fb923c; }
.p3-it.act .p3-lug span { background: #f97316; color: #fff; border-color: #f97316; }
.p3-sinlug { margin-top: 5px; font-size: 12px; color: #94a3b8; }
.p3-bus { padding: 10px 10px 0; }
.p3-bus input { width: 100%; box-sizing: border-box; border: 2px solid #0f172a; border-radius: 12px; padding: 10px 12px; font: inherit; font-size: 15px; }
.p3-res { max-height: 45vh; overflow: auto; margin-top: 6px; }
.p3-r { padding: 9px 10px; border-radius: 10px; border: 1px solid #e2e8f0; margin-bottom: 5px; background: #fff; }
.p3-r .n { font-size: 13.5px; font-weight: 700; line-height: 1.25; }
.p3-r .k { font-size: 12px; color: #64748b; margin-top: 2px; }
.p3-r .p3-lug { cursor: pointer; margin-right: 4px; }
.p3-r .p3-lug:hover { border-color: #f97316; }
.p3-r.sin { opacity: .6; }
.p3-pie { padding: 14px; border-top: 1px solid #f1f5f9; display: flex; gap: 8px; }
.p3-pie button { flex: 1; border-radius: 12px; padding: 13px; font-size: 15px; font-weight: 800; cursor: pointer; border: 1px solid #cbd5e1; background: #fff; font-family: inherit; color: #0f172a; }
.p3-pie .lo { background: #16a34a; border-color: #16a34a; color: #fff; }
.p3-pie button:disabled { opacity: .4; cursor: default; }
.p3-tip { position: absolute; z-index: 6; pointer-events: none; background: #0f172a; color: #e2e8f0; border: 1px solid #f97316; border-radius: 10px; padding: 8px 11px; font-size: 12.5px; line-height: 1.35; max-width: 320px; box-shadow: 0 10px 24px rgba(0,0,0,.45); }
.p3-tip b { font-family: ui-monospace, monospace; color: #fdba74; font-size: 13.5px; }
.p3-tip .v { color: #94a3b8; font-style: italic; }
.p3-tip .h { color: #94a3b8; font-size: 11px; margin-top: 4px; }
.p3-tip + .p3-tip { margin-top: 4px; }
.p3-caja.saco > i { background-size: 100% 100% !important; }
.p3-caja.saco > i:nth-child(1) { border-radius: 22% / 34%; }
.p3-caja.saco > i:nth-child(2), .p3-caja.saco > i:nth-child(3) { border-radius: 14% / 48%; }
.p3-caja.saco > i:nth-child(4), .p3-caja.saco > i:nth-child(5) { border-radius: 48%; }
.p3-caja.rollo > i { border-radius: 50%; }
.p3-alt img { display: inline-block; vertical-align: middle; height: 2.4em; border-radius: 3px; margin-left: 4px; }
.p3-tip img { display: block; max-height: 110px; max-width: 220px; border-radius: 6px; margin-top: 4px; }
.p3-est .dib { display: grid; grid-template-columns: repeat(4, 1fr); gap: 4px; margin: 6px 0; }
.p3-est .dib button { border: 1px solid #e7e5e4; background: #fff; border-radius: 7px; padding: 4px 2px; font: inherit; font-size: 11px; cursor: pointer; }
.p3-est .dib button.on { border: 2px solid #ea580c; background: #fff7ed; font-weight: 700; }
.p3-est .ft { display: flex; gap: 8px; align-items: center; margin: 4px 0 6px; flex-wrap: wrap; font-size: 12.5px; }
.p3-est .ft img { height: 70px; border-radius: 6px; border: 1px solid #e7e5e4; cursor: zoom-in; }
.p3-est .ft label { border: 1px solid #d6d3d1; background: #fff; border-radius: 99px; padding: 4px 10px; font-weight: 700; cursor: pointer; }
.p3-est .ve { color: #92400e; font-size: 13px; margin: 2px 0; }
.p3-mundo .fuera { display: none !important; }
.p3-eqtxt { position: absolute; display: flex; align-items: center; justify-content: center; font-weight: 900; letter-spacing: .08em; white-space: nowrap; pointer-events: none; }
.p3-niv.hov { outline: 3px solid #38bdf8; outline-offset: -2px; }
.p3-est { border-bottom: 2px solid #fde68a; padding: 12px 14px; max-height: 58vh; overflow: auto; background: #fffbeb; }
.p3-est .tt { display: flex; align-items: center; gap: 8px; font-size: 12px; color: #92400e; text-transform: uppercase; letter-spacing: .06em; font-weight: 800; }
.p3-est .tt button { margin-left: auto; border: 1px solid #fcd34d; background: #fff; border-radius: 8px; width: 30px; height: 30px; cursor: pointer; }
.p3-est .bl { margin-top: 8px; }
.p3-est .cod { font-family: ui-monospace, monospace; font-weight: 900; color: #78350f; font-size: 20px; }
.p3-est .ds { color: #78716c; font-size: 12.5px; margin-bottom: 4px; }
.p3-est .pr { display: flex; align-items: center; gap: 8px; padding: 7px 0; border-bottom: 1px solid #fde68a; font-size: 13.5px; }
.p3-est .pr > div { flex: 1; min-width: 0; }
.p3-est .pr .k { color: #78716c; font-size: 12px; }
.p3-est .x { border: 1px solid #e7e5e4; background: #fff; border-radius: 8px; width: 32px; height: 32px; cursor: pointer; color: #6b7280; flex-shrink: 0; }
.p3-est .vac { color: #a8a29e; font-style: italic; font-size: 13px; padding: 4px 0; }
.p3-est input { width: 100%; box-sizing: border-box; border: 1px solid #d6d3d1; border-radius: 10px; padding: 8px 10px; font: inherit; font-size: 14px; margin-top: 8px; background: #fff; }
.p3-est .rs { margin-top: 4px; }
.p3-est .rs div { padding: 7px 9px; border: 1px solid #e7e5e4; border-radius: 8px; margin-bottom: 4px; background: #fff; cursor: pointer; font-size: 13px; }
.p3-est .rs div:hover { border-color: #f97316; }
.p3-est .rs div span { color: #78716c; font-size: 12px; }
.p3-capas { position: absolute; left: 18px; bottom: 68px; background: #fff; color: #0f172a; border-radius: 12px; padding: 10px 14px; box-shadow: 0 12px 30px rgba(0,0,0,.45); min-width: 240px; }
.p3-capas label { display: flex; align-items: center; gap: 8px; padding: 4px 0; font-size: 14px; cursor: pointer; }
.p3-capas .t { font-size: 11px; color: #64748b; text-transform: uppercase; letter-spacing: .06em; font-weight: 800; margin-bottom: 4px; }
${CAPAS.map(c => `.p3-mundo.sin-${c[0]} [data-capa="${c[0]}"] { display: none !important; }`).join('\n')}
@media (max-width: 820px) {
  .p3 { grid-template-columns: 1fr; grid-template-rows: 58vh 1fr; }
  .p3-hud .d { font-size: 21px; }
  .p3-globo .g b { font-size: 28px; }
}`;

    let estado = null;

    function estilos() {
        if (document.getElementById('p3-css')) return;
        const st = document.createElement('style'); st.id = 'p3-css'; st.textContent = CSS; document.head.appendChild(st);
    }
    const h = (tag, cls, css, padre) => { const d = document.createElement(tag); if (cls) d.className = cls; if (css) Object.assign(d.style, css); if (padre) padre.appendChild(d); return d; };
    const esc = (s) => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

    // código "C3-R4-A3" (calle, rack de esa calle, altura, -P2 si hay posiciones) / sin calle "D2-3" /
    // viejos "D2-3B" y "D2-1-3" → { calle, nro, rack, pos, niv }
    function partir(cod) {
        const c = String(cod || '').trim().toUpperCase();
        // C3-F4-R2-A3 (calle, fila, rack de la fila, altura) / F4-R2-A3 / anterior C3-R4-A3
        let m = c.match(/^(?:C(\d{1,2})-)?F(\d{1,2})-R(\d{1,2})-A(\d{1,2})(?:-P(\d{1,2}))?$/);
        if (m) return { calle: m[1] ? +m[1] : null, fila: +m[2], nro: +m[3], rack: null, niv: +m[4], pos: m[5] ? +m[5] : 1, letra: null };
        // 2026-11-27: lugar en el piso de una calle (C3-PISO1) y estantería suelta (TOST-A3)
        m = c.match(/^(?:C(\d{1,2})-)?PISO(\d{1,2})(?:-P(\d{1,2}))?$/);
        if (m) return { calle: m[1] ? +m[1] : null, piso: +m[2], rack: null, niv: 1, pos: m[3] ? +m[3] : 1, letra: null };
        m = c.match(/^([A-Z][A-Z0-9]*)-A(\d{1,2})(?:-P(\d{1,2}))?$/);
        if (m) return { rack: m[1], est: true, niv: +m[2], pos: m[3] ? +m[3] : 1, letra: null };
        m = c.match(/^C(\d{1,2})-R(\d{1,2})-A(\d{1,2})(?:-P(\d{1,2}))?$/);
        if (m) return { calle: +m[1], nro: +m[2], viejo: true, rack: null, niv: +m[3], pos: m[4] ? +m[4] : 1, letra: null };
        m = c.match(/^([A-Z0-9]+)-(\d{1,3})([A-Z])?$/);
        if (m) return { rack: m[1], niv: +m[2], pos: m[3] ? m[3].charCodeAt(0) - 64 : 1, letra: m[3] || null };
        m = c.match(/^([A-Z0-9]+)-(\d{1,3})-(\d{1,3})$/);
        if (m) return { rack: m[1], pos: +m[2], niv: +m[3], letra: null };
        return null;
    }
    // escrito para leerlo: "Calle 3 · Rack 4 · Altura 3 (el piso)"
    function desglose(p) {
        if (!p) return '';
        if (p.piso != null) return `${p.calle != null ? 'Calle ' + p.calle + ' · ' : ''}Lugar en el piso ${p.piso}${p.pos > 1 ? ' · Posición ' + p.pos : ''}`;
        if (p.est) return `Estantería ${p.rack} · Altura ${p.niv}${p.pos > 1 ? ' · Posición ' + p.pos : ''}`;
        const alt = `Altura ${p.niv}${p.niv === 1 ? ' (el piso)' : ''}`;
        if (p.fila != null) return `${p.calle != null ? 'Calle ' + p.calle + ' · ' : ''}Fila ${p.fila} · Rack ${p.nro} · ${alt}${p.pos > 1 ? ' · Posición ' + p.pos : ''}`;
        if (p.calle != null) return `Calle ${p.calle} · Rack ${p.nro} · ${alt}${p.pos > 1 ? ' · Posición ' + p.pos : ''}`;
        return `Rack ${p.rack} · ${alt}${p.letra ? ' · Posición ' + p.letra : ''}`;
    }

    // ───────────── escena de una planta ─────────────
    function armarEscena(els) {
        const T = els.find(e => e.tipo === 'terreno') || { ancho: 10, largo: 44 };
        const WT = +T.ancho, LT = +T.largo;
        const gir = e => e.rotacion === 90 || e.rotacion === 270;
        const R = e => { const a = gir(e) ? +e.largo : +e.ancho, l = gir(e) ? +e.ancho : +e.largo; return { x: +e.y * S, y: (WT - +e.x - a) * S, w: l * S, h: a * S }; };
        const mundo = h('div', 'p3-mundo');
        const div = (cls, css, padre) => h('div', cls, css, padre || mundo);

        function caja(x, y, w, hh, alto, color, padre, z0) {
            const c = div('p3-caja', { left: x + 'px', top: y + 'px', width: w + 'px', height: hh + 'px', transform: `translateZ(${z0 || 0}px)` }, padre);
            const f = (css) => { const i = document.createElement('i'); Object.assign(i.style, css); c.appendChild(i); };
            const osc = (k) => `color-mix(in srgb, ${color} ${100 - k}%, black)`;
            f({ left: 0, top: 0, width: w + 'px', height: hh + 'px', background: color, transform: `translateZ(${alto}px)` });
            f({ left: 0, top: hh + 'px', width: w + 'px', height: alto + 'px', background: osc(18), transformOrigin: '0 0', transform: 'rotateX(90deg)' });
            f({ left: 0, top: 0, width: w + 'px', height: alto + 'px', background: osc(30), transformOrigin: '0 0', transform: 'rotateX(90deg)' });
            f({ left: 0, top: 0, width: alto + 'px', height: hh + 'px', background: osc(25), transformOrigin: '0 0', transform: 'rotateY(-90deg)' });
            f({ left: w + 'px', top: 0, width: alto + 'px', height: hh + 'px', background: osc(12), transformOrigin: '0 0', transform: 'rotateY(-90deg)' });
            return c;
        }

        // 2026-11-28: llena una altura con el dibujo elegido (o las cajitas de siempre). Devuelve nada; marca data-niv.
        function llenar(d, r, z, hz, niv, tipo, seed) {
            d.querySelectorAll(':scope > .p3-caja[data-niv="' + niv + '"]').forEach(x => x.remove());
            const put = (x, y, w, hh, alto, color, z2, cls) => { const c = caja(x, y, w, hh, alto, color, d, z2); c.dataset.niv = niv; if (cls) c.classList.add(cls); return c; };
            const ch = r.h * 0.72, y0 = (r.h - ch) / 2;
            if (!tipo || tipo === 'cajas' || tipo === 'foto') {
                const k = Math.floor(azar(seed * 7 + niv) * 3) + 1;
                for (let j = 0; j < k; j++)
                    put(6 + j * (r.w - 12) / k + azar(seed + j) * 4, (r.h - ch) / 2, Math.min(r.w / 3.4, (r.w - 16) / k), ch, hz * (0.6 + azar(niv + j + seed) * 0.4), '#c9a26b', z);
                return;
            }
            const n = Math.max(1, Math.floor((r.w - 8) / 46)), w = (r.w - 8) / n;
            if (tipo === 'bolsas' || tipo === 'pallet') {
                for (let j = 0; j < n; j++) put(4 + j * w + 2, y0, w - 4, ch, 4, '#8a6a40', z);   // pallet
                if (tipo === 'pallet') return;
                // bolsas de yute apiladas: un poco más anchas que el pallet, cada una corrida un poquito (como en la foto)
                // cada bolsa ~1,1 m de largo y ~0,22 m de alto (60/70 kg): en un rack de 2,45 m entran DOS a lo ancho (dato del dueño)
                const nb = Math.max(1, Math.round((r.w - 8) / (1.1 * S))), wb = (r.w - 8) / nb;
                // máximo 5 bolsas estibadas (pedido del dueño); cada una ~25 cm de alto
                const capas = Math.max(1, Math.min(5, Math.floor((hz - 4) / (0.22 * S)))), ac = Math.min((hz - 4) / capas, 0.26 * S);
                for (let c = 0; c < capas; c++)
                    for (let j = 0; j < nb; j++) {
                        const corr = (azar(seed * 13 + c * 7 + j) - 0.5) * 4;
                        const b = put(4 + j * wb + corr + 0.5, y0 - 3 + azar(c + j) * 2, wb - 1, ch + 5, ac * 1.12, '#d8c69e', z + 4 + c * ac, 'saco');
                        const caras = b.children;
                        caras[0].style.background = YUTE_TAPA;
                        caras[1].style.background = YUTE_LADO; caras[2].style.background = YUTE_LADO;
                        caras[3].style.background = 'radial-gradient(ellipse at 50% 45%, #dccb9f, #a99067)';
                        caras[4].style.background = 'radial-gradient(ellipse at 50% 45%, #dccb9f, #a99067)';
                    }
                return;
            }
            if (tipo === 'bidones') {
                const m = Math.max(2, Math.floor((r.w - 8) / 16));
                for (let j = 0; j < m; j++) put(5 + j * (r.w - 10) / m, y0 + ch * 0.15, (r.w - 10) / m - 3, ch * 0.7, hz * 0.9, '#60a5fa', z, 'rollo');
                return;
            }
            if (tipo === 'sillas') {
                const capas = Math.max(4, Math.floor(hz / 2.2));
                for (let c = 0; c < capas; c++) for (let j = 0; j < n; j++) put(4 + j * w + 3, y0 + 2, w - 6, ch - 4, 1.6, c % 2 ? '#f1f5f9' : '#e2e8f0', z + c * 2.2);
                return;
            }
            if (tipo === 'rollos') {
                const m = Math.max(2, Math.floor((r.w - 8) / 22));
                for (let j = 0; j < m; j++) put(5 + j * (r.w - 10) / m, y0, (r.w - 10) / m - 2, ch, hz * 0.7, '#cbd5e1', z, 'rollo');
                return;
            }
            // bultos
            for (let j = 0; j < n; j++) put(4 + j * w + 2, y0, w - 4, ch, hz * 0.85, '#e9d8a6', z);
        }
        const dibujoDe = (e, niv, cara) => (e.dibujos || []).filter(x => x.nivel === niv && (cara == null || x.cara === cara)).sort((a, b) => !!b.dibujo - !!a.dibujo)[0] || null;

        div('p3-piso', { width: LT * S + 'px', height: WT * S + 'px' });
        const zonas = els.filter(e => e.tipo === 'zona');
        for (const z of zonas) {
            const r = R(z);
            const d = div('p3-zona', { left: r.x + 'px', top: r.y + 'px', width: r.w + 'px', height: r.h + 'px', background: z.color || '#f1f5f9' });
            d.dataset.capa = 'ambientes';
            if (z.puntos) {
                try {
                    const p = JSON.parse(z.puntos);
                    d.style.clipPath = 'polygon(' + p.map(q => (((q[1] * S) - r.x) / r.w * 100).toFixed(2) + '% ' + ((((WT - q[0]) * S) - r.y) / r.h * 100).toFixed(2) + '%').join(',') + ')';
                } catch (e) { }
            }
            if (z.nombre && r.w * r.h > 3 * S * S) {
                const n = div('p3-znom', { fontSize: Math.max(16, Math.min(40, Math.min(r.w, r.h) * 0.16)) + 'px' }, d);
                n.textContent = z.nombre;
            }
        }
        // ───── 2026-11-28: equipos del tostadero dibujados con su forma (Royal, zaranda, campana, tolvas…) ─────
        // Cada equipo se arma en coordenadas "a lo largo" (L) y "de fondo" (A); si en el plano es más largo
        // hacia arriba, el grupo se gira 90°. y = 0 es el frente (el lado que mira al ambiente cuando está girado).
        function grupoL(r, z) {
            const alongX = r.w >= r.h, L = alongX ? r.w : r.h, A = alongX ? r.h : r.w;
            const g = div('p3-eq', alongX
                ? { position: 'absolute', left: r.x + 'px', top: r.y + 'px', width: L + 'px', height: A + 'px', transform: `translateZ(${z || 0}px)` }
                : { position: 'absolute', left: (r.x + r.w) + 'px', top: r.y + 'px', width: L + 'px', height: A + 'px', transformOrigin: '0 0', transform: `translateZ(${z || 0}px) rotateZ(90deg)` });
            return { g, L, A };
        }
        const pieza = (padre, css) => h('i', '', Object.assign({ position: 'absolute', display: 'block' }, css), padre);
        // cilindro parado (tachos, chimenea, molino)
        function cilV(padre, cx, cy, z, rad, alto, color, tapa, n) {
            n = n || 14;
            const g = h('div', '', { position: 'absolute', left: cx + 'px', top: cy + 'px', width: 0, height: 0, transform: `translateZ(${z}px)` }, padre);
            const w = 2 * rad * Math.tan(Math.PI / n) + 0.8;
            for (let i = 0; i < n; i++) {
                const a = 360 * i / n, luz = Math.round(70 + 30 * Math.cos((a - 210) * Math.PI / 180));
                pieza(g, { left: (-w / 2) + 'px', top: 0, width: w + 'px', height: alto + 'px', background: `color-mix(in srgb, ${color} ${luz}%, black)`, transformOrigin: '50% 0', transform: `rotateZ(${a}deg) translateY(${rad}px) rotateX(90deg)` });
            }
            if (tapa !== false) pieza(g, { left: -rad + 'px', top: -rad + 'px', width: 2 * rad + 'px', height: 2 * rad + 'px', borderRadius: '50%', background: tapa || color, transform: `translateZ(${alto}px)` });
            return g;
        }
        // cilindro acostado a lo largo (el tambor de la tostadora)
        function cilX(padre, x0, cy, zc, rad, largo, color, n) {
            n = n || 18;
            const g = h('div', '', { position: 'absolute', left: x0 + 'px', top: cy + 'px', width: 0, height: 0, transform: `translateZ(${zc}px)` }, padre);
            const w = 2 * rad * Math.tan(Math.PI / n) + 0.8;
            for (let i = 0; i < n; i++) {
                const a = 360 * i / n, luz = Math.round(66 + 34 * Math.cos(a * Math.PI / 180));
                pieza(g, { left: 0, top: (-w / 2) + 'px', width: largo + 'px', height: w + 'px', background: `linear-gradient(90deg, color-mix(in srgb, ${color} ${luz - 10}%, black), color-mix(in srgb, ${color} ${luz}%, white 6%) 50%, color-mix(in srgb, ${color} ${luz - 14}%, black))`, transformOrigin: '0 50%', transform: `rotateX(${a}deg) translateZ(${rad}px)` });
            }
            for (const xx of [0, largo]) pieza(g, { left: -rad + 'px', top: -rad + 'px', width: 2 * rad + 'px', height: 2 * rad + 'px', borderRadius: '50%', background: `radial-gradient(circle, color-mix(in srgb, ${color} 55%, black), color-mix(in srgb, ${color} 80%, black))`, transform: `translateX(${xx}px) rotateY(90deg)` });
            return g;
        }
        // plano parado en el frente (y = 0) para dibujar puertas, carteles, botones
        const frente = (padre, x, z, w, alto, css, txt) => {
            const p = pieza(padre, Object.assign({ left: x + 'px', top: 0, width: w + 'px', height: alto + 'px', transformOrigin: '0 0', transform: `translateZ(${z + alto}px) rotateX(-90deg)` }, css));
            if (txt) { p.className = 'p3-eqtxt'; p.textContent = txt; }
            return p;
        };
        const GRANO = 'radial-gradient(circle at 30% 30%, #6b3f1f 0 1.4px, transparent 1.6px) 0 0 / 5px 5px, radial-gradient(circle at 70% 70%, #4a2a14 0 1.4px, transparent 1.6px) 0 0 / 5px 5px, #5a3519';
        const GRANITO = 'radial-gradient(circle, #6b7280 0 1px, transparent 1.3px) 0 0 / 6px 6px, radial-gradient(circle, #f5f5f4 0 .8px, transparent 1px) 3px 2px / 7px 7px, #a8a29e';
        const EQUIPOS = {
            tostadora: (c, r) => {
                const { g, L, A } = grupoL(r, 0), base = 0.32 * S, rad = Math.min(A * 0.42, 0.34 * S), zc = base + rad;
                caja(L * 0.08, A * 0.18, L * 0.7, A * 0.64, base, '#3f3f46', g);                         // bastidor
                cilX(g, L * 0.05, A * 0.5, zc, rad, L * 0.72, c.color || '#8a8178');                       // tambor
                caja(L * 0.77, A * 0.08, L * 0.1, A * 0.84, base + 2 * rad + 4, '#292524', g);             // frente / quemador
                caja(L * 0.36, A * 0.32, 0.26 * S, A * 0.36, 0.22 * S, '#a8a29e', g, base + 2 * rad - 2);  // tolva de carga
                cilV(g, L * 0.58, A * 0.5, base + 2 * rad - 2, 0.07 * S, 1.0 * S, '#9ca3af', false);       // chimenea
                caja(0, A * 0.02, 0.3 * S, 0.14 * S, 0.3 * S, '#f1f5f9', g, 0.78 * S);                    // tablero
                caja(0.06 * S, -1, 0.06 * S, 1.5, 0.06 * S, '#dc2626', g, 0.98 * S);                      // botón rojo
                caja(0.06 * S, -1, 0.06 * S, 1.5, 0.06 * S, '#16a34a', g, 0.86 * S);                      // botón verde
                frente(g, L * 0.2, zc - 8, L * 0.4, 16, { color: '#fff7ed', fontSize: '12px', textShadow: '0 1px 2px #000' }, c.nombre && /royal/i.test(c.nombre) ? 'ROYAL' : 'TOSTADORA');
                return g;
            },
            zaranda: (c, r) => {
                const { g, L, A } = grupoL(r, 0), h0 = 0.55 * S;
                for (const [x, y] of [[2, 2], [L - 5, 2], [2, A - 5], [L - 5, A - 5]]) caja(x, y, 3, 3, h0, '#52525b', g);
                caja(0, 0, L, A, 0.14 * S, '#a1a1aa', g, h0);
                pieza(g, { left: '3px', top: '3px', width: (L - 6) + 'px', height: (A - 6) + 'px', background: GRANO, transform: `translateZ(${h0 + 0.12 * S}px)` });
                caja(L * 0.4, A * 0.4, 0.2 * S, 0.2 * S, 0.25 * S, '#3f3f46', g, h0 + 0.14 * S);           // motor del removedor
                return g;
            },
            campana: (c, r) => {
                const { g, L, A } = grupoL(r, 0), z = 1.95 * S;
                caja(0, 0, L, A, 0.12 * S, c.color || '#78716c', g, z);
                caja(L * 0.12, A * 0.12, L * 0.76, A * 0.76, 0.28 * S, c.color || '#8b8178', g, z + 0.12 * S);
                caja(L * 0.38, A * 0.3, L * 0.24, A * 0.4, 0.5 * S, '#9ca3af', g, z + 0.4 * S);            // conducto
                for (const x of [L * 0.15, L * 0.85]) caja(x, A / 2, 1.2, 1.2, 0.9 * S, '#d4d4d8', g, z + 0.4 * S);  // cadenas
                return g;
            },
            tolva: (c, r) => {
                const { g, L, A } = grupoL(r, 0), alto = (+c.alto || 1.9) * S, col = c.color || '#c97a26';
                caja(0, 0, L, A, alto, col, g);
                frente(g, L * 0.06, alto * 0.08, L * 0.88, alto * 0.84, { background: `linear-gradient(${col}, ${col}) padding-box`, border: '2px solid color-mix(in srgb, ' + col + ' 70%, black)', boxShadow: 'inset 0 0 0 3px color-mix(in srgb, ' + col + ' 85%, black)' });
                frente(g, L * 0.52, alto * 0.62, L * 0.38, alto * 0.25, { background: '#1c1917', borderRadius: '2px' });
                frente(g, L * 0.2, alto * 0.7, 0.18 * S, 0.18 * S, { background: '#0c0a09', borderRadius: '50%', border: '2px solid #e7e5e4' });
                return g;
            },
            dispensador: (c, r) => {
                const { g, L, A } = grupoL(r, 0), z = 1.12 * S, alto = 0.6 * S;
                for (let i = 0; i < 3; i++) {
                    caja(i * L / 3 + 1, 1, L / 3 - 2, A - 2, alto * 0.75, '#4a2c17', g, z + 2);
                    pieza(g, { left: (i * L / 3 + 1) + 'px', top: '1px', width: (L / 3 - 2) + 'px', height: (A - 2) + 'px', background: GRANO, transform: `translateZ(${z + 2 + alto * 0.75}px)` });
                    cilV(g, i * L / 3 + L / 6, A * 0.3, z - 0.14 * S, 0.05 * S, 0.14 * S, '#d4d4d8');
                }
                caja(0, 0, L, A, alto, 'rgba(226,232,240,.28)', g, z);
                return g;
            },
            molino: (c, r) => {
                const { g, L, A } = grupoL(r, 0), z = 0.9 * S, m = Math.min(L, A);
                caja(L * 0.1, A * 0.1, L * 0.8, A * 0.8, 0.12 * S, '#3b2416', g, z);
                cilV(g, L / 2, A / 2, z + 0.12 * S, m * 0.28, 0.28 * S, '#4a3122', null);
                cilV(g, L / 2, A / 2, z + 0.4 * S, m * 0.45, 0.22 * S, 'rgba(231,229,228,.55)', 'rgba(120,80,40,.6)');  // tolva transparente
                return g;
            },
            balanza: (c, r) => {
                const { g, L, A } = grupoL(r, 0), z = 0.9 * S;
                caja(0, 0, L, A, 0.07 * S, '#e5e7eb', g, z);
                caja(L * 0.08, A * 0.2, L * 0.84, A * 0.7, 0.02 * S, '#d4d4d8', g, z + 0.07 * S);
                frente(g, L * 0.1, z + 0.01 * S, L * 0.8, 0.05 * S, { background: '#1e3a8a', color: '#86efac', fontSize: '6px' }, '0,000 kg');
                return g;
            },
            selladora: (c, r) => {
                const { g, L, A } = grupoL(r, 0), z = 0.9 * S;
                caja(0, 0, L, A, 0.04 * S, '#f1f5f9', g, z);
                caja(0, A * 0.1, L, A * 0.5, 0.05 * S, '#2563eb', g, z + 0.04 * S);
                return g;
            },
            tacho: (c, r) => {
                const { g, L, A } = grupoL(r, 0), m = Math.min(L, A);
                cilV(g, L / 2, A / 2, 0, m / 2, (+c.alto || 0.75) * S, c.color || '#1f2937', '#f8fafc', 16);
                return g;
            },
            mesada: (c, r) => {
                const { g, L, A } = grupoL(r, 0), alto = (+c.alto || 0.9) * S;
                caja(0, 0, L, A, 0.25 * S, '#78716c', g);                                                // zócalo / estante de abajo
                for (const x of [1, L - 4]) caja(x, 1, 3, A - 2, alto, '#44403c', g);                     // patas
                caja(0, 0, L, A, 0.05 * S, '#a8a29e', g, alto - 0.05 * S);
                pieza(g, { left: 0, top: 0, width: L + 'px', height: A + 'px', background: GRANITO, transform: `translateZ(${alto + 0.3}px)` });
                return g;
            },
        };

        const colores = { pc: '#1f2937', pallet: '#b45309', escritorio: '#a8a29e', mueble: '#334155', vehiculo: '#94a3b8', contenedor: '#3b6d11', sillas: '#d6d3d1', montacargas: '#a8a29e', heladera: '#e2e8f0', mesa: '#d6d3d1' };
        const altos = { pc: 8, pallet: 6, escritorio: 30, mueble: 70, vehiculo: 60, contenedor: 50, sillas: 40, montacargas: 50, heladera: 72, mesa: 30 };
        for (const c of els) {
            const r = R(c);
            if (c.tipo === 'puerta' || c.tipo === 'porton') { div('p3-cosa', { left: r.x + 'px', top: r.y + 'px', width: r.w + 'px', height: r.h + 'px', background: '#38bdf8' }).dataset.capa = 'estructura'; continue; }
            if (c.tipo === 'escalera') { div('p3-cosa', { left: r.x + 'px', top: r.y + 'px', width: r.w + 'px', height: r.h + 'px', background: 'repeating-linear-gradient(90deg,#cbd5e1 0 10px,#94a3b8 10px 12px)' }).dataset.capa = 'estructura'; continue; }
            if (EQUIPOS[c.tipo]) { const g = EQUIPOS[c.tipo](c, r); if (g) g.dataset.capa = 'muebles'; continue; }
            if (colores[c.tipo]) caja(r.x, r.y, r.w, r.h, altos[c.tipo], c.color || colores[c.tipo]).dataset.capa = CAPA_TIPO[c.tipo] || 'otros';
        }

        const racks = {};
        const porCalle = {}; // "3-4" (calle-rack) → código del rack
        const azar = (s) => { const x = Math.sin(s) * 10000; return x - Math.floor(x); };
        for (const e of els.filter(e => e.tipo === 'rack' && e.codigo && e.forma === 'piso')) {
            // 2026-11-27: lugar en el piso de una calle: rectángulo punteado con cajas apoyadas
            const r = R(e), cod = (e.calle1 != null ? 'C' + e.calle1 + '-' : '') + 'PISO' + (e.nroFila || 1);
            const d = div('p3-rack piso', { left: r.x + 'px', top: r.y + 'px', width: r.w + 'px', height: r.h + 'px' });
            d.dataset.cod = e.codigo.toUpperCase(); d.dataset.capa = 'piso';
            const niveles = [div('p3-niv', { width: r.w + 'px', height: r.h + 'px', transform: 'translateZ(1px)' }, d)];
            niveles[0].dataset.niv = 1;
            const k = 2 + Math.floor(azar(e.id * 3) * 3), H = Math.max(14, (+e.alto || 0.6) * S);
            const dbp = dibujoDe(e, 1, null);
            if (dbp && dbp.dibujo && dbp.dibujo !== 'cajas' && dbp.dibujo !== 'foto') llenar(d, r, 2, H, 1, dbp.dibujo, e.id);
            else for (let j = 0; j < k; j++) {
                const cw = (r.w - 12) / k - 3, ch = r.h * (0.45 + azar(e.id + j) * 0.3);
                caja(6 + j * (r.w - 12) / k, (r.h - ch) / 2, cw, ch, H * (0.6 + azar(j + e.id * 5) * 0.4), '#c9a26b', d, 2).dataset.niv = 1;
            }
            const top = div('p3-rcod', { width: r.w + 'px', height: r.h + 'px', transform: `translateZ(${H + 4}px)` }, d);
            top.textContent = cod;
            top.style.fontSize = Math.max(9, Math.min(18, r.h * 0.3, r.w / (cod.length * 0.62))) + 'px';
            racks[e.codigo.toUpperCase()] = { e, r, H, N: 1, d, niveles, z0: 0, cod, piso: true, llenar: (niv, t) => llenar(d, r, 2, H, 1, t, e.id) };
            porCalle['P' + (e.calle1 != null ? e.calle1 : '') + '-' + (e.nroFila || 1)] = e.codigo.toUpperCase();
            porCalle['P-' + (e.nroFila || 1)] = porCalle['P-' + (e.nroFila || 1)] || e.codigo.toUpperCase();
        }
        for (const e of els.filter(e => e.tipo === 'rack' && e.codigo && e.forma !== 'piso')) {
            // 2026-11-27: estantería amurada: empieza a "elevacion" metros del piso
            const z0 = e.forma === 'estanteria' ? (+e.elevacion || 0) * S : 0;
            const r = R(e), N = Math.max(1, e.niveles || 1);
            // 2026-11-28: alto real de cada altura (cm, de abajo hacia arriba); si no está, todas iguales
            const cms = String(e.alturasCm || '').split(',').map(x => +x).filter(x => x > 0);
            const reales = cms.length === N;
            const H = (+e.alto || (reales ? cms.reduce((a, b) => a + b, 0) / 100 : (e.forma === 'estanteria' ? 1 : 2.6))) * S;
            const zNiv = [], gapNiv = [];
            for (let i = 0, acc = 0; i < N; i++) {
                if (reales) { zNiv.push(4 + acc / 100 * S); gapNiv.push(cms[i] / 100 * S); acc += cms[i]; }
                else { zNiv.push(4 + i * (H - 10) / N); gapNiv.push((H - 10) / N); }
            }
            const d = div('p3-rack', { left: r.x + 'px', top: r.y + 'px', width: r.w + 'px', height: r.h + 'px', transform: `translateZ(${z0}px)` });
            d.dataset.cod = e.codigo.toUpperCase(); d.dataset.capa = e.forma === 'estanteria' ? 'estanterias' : 'racks';
            for (const [px, py] of [[0, 0], [r.w - 5, 0], [0, r.h], [r.w - 5, r.h]]) div('p3-poste', { left: px + 'px', top: py + 'px', height: H + 'px', transform: 'rotateX(90deg)' }, d);
            const niveles = [];
            for (let i = 0; i < N; i++) {
                const z = zNiv[i]; // (relativo al rack; el rack ya está subido z0)
                niveles.push(div('p3-niv', { width: r.w + 'px', height: r.h + 'px', transform: `translateZ(${z}px)` }, d));
                niveles[i].dataset.niv = i + 1;
                const k = Math.floor(azar(e.id * 7 + i) * 3) + 1, hz = Math.min(30, gapNiv[i] - 8);
                const db = dibujoDe(e, i + 1, null);
                if (db && db.dibujo && db.dibujo !== 'cajas' && db.dibujo !== 'foto') { llenar(d, r, z + 2, Math.max(hz, gapNiv[i] - 4), i + 1, db.dibujo, e.id); continue; }
                for (let j = 0; j < k; j++) {
                    const ch = r.h * 0.7;
                    caja(6 + j * (r.w - 12) / k + azar(e.id + j) * 4, (r.h - ch) / 2, Math.min(r.w / 3.4, (r.w - 16) / k), ch, hz * (0.6 + azar(i + j + e.id) * 0.4), '#c9a26b', d, z + 2).dataset.niv = i + 1;
                }
            }
            const top = div('p3-rcod', { width: r.w + 'px', height: r.h + 'px', transform: `translateZ(${H + 2}px)`, fontSize: Math.max(12, Math.min(26, r.h * 0.5)) + 'px' }, d);
            // 2026-11-27: el rack se llama fila-rack ("F4-R2"); sin fila, el código de siempre
            top.textContent = (e.fila != null && e.nroFila != null) ? 'F' + e.fila + '-R' + e.nroFila : e.codigo;
            top.style.fontSize = Math.max(10, Math.min(22, r.h * 0.42, r.w / 3.2)) + 'px';
            // 2026-11-28: la estantería se mira desde el lado del ambiente (no desde la pared)
            let dir = null;
            if (e.forma === 'estanteria') {
                const cx = r.x + r.w / 2, cy = r.y + r.h / 2;
                const zz = zonas.map(z => ({ z, q: R(z) })).filter(o => cx >= o.q.x && cx <= o.q.x + o.q.w && cy >= o.q.y && cy <= o.q.y + o.q.h).sort((a, b) => a.q.w * a.q.h - b.q.w * b.q.h)[0];
                if (zz) {
                    const zx = zz.q.x + zz.q.w / 2, zy = zz.q.y + zz.q.h / 2;
                    dir = r.w >= r.h ? (zy < cy ? { nx: 0, ny: -1, rz: 180 } : { nx: 0, ny: 1, rz: 0 }) : (zx < cx ? { nx: -1, ny: 0, rz: -90 } : { nx: 1, ny: 0, rz: 90 });
                }
            }
            racks[e.codigo.toUpperCase()] = { e, r, H, N, d, niveles, z0, cod: top.textContent, est: e.forma === 'estanteria', dir,
                zNiv, gapNiv,
                llenar: (niv, t) => llenar(d, r, zNiv[niv - 1] + 2, t && t !== 'cajas' && t !== 'foto' ? Math.max(Math.min(30, gapNiv[niv - 1] - 8), gapNiv[niv - 1] - 4) : Math.min(30, gapNiv[niv - 1] - 8), niv, t, e.id) };
            if (e.calle1 != null && e.nro1 != null) porCalle[e.calle1 + '-' + e.nro1] = e.codigo.toUpperCase();
            if (e.calle2 != null && e.nro2 != null) porCalle[e.calle2 + '-' + e.nro2] = e.codigo.toUpperCase();
            if (e.fila != null && e.nroFila != null) porCalle['F' + e.fila + '-' + e.nroFila] = e.codigo.toUpperCase();
        }

        // paredes bajitas alrededor de los ambientes con racks (para que se entienda el espacio)
        function pared(x1, y1, x2, y2, alto) {
            const len = Math.hypot(x2 - x1, y2 - y1), ang = Math.atan2(y2 - y1, x2 - x1) * 180 / Math.PI;
            div('p3-pared', { left: x1 + 'px', top: y1 + 'px', width: len + 'px', height: alto + 'px', transform: `rotateZ(${ang}deg) rotateX(90deg)` }).dataset.capa = 'estructura';
        }
        const conRacks = zonas.filter(z => { const zr = R(z); return Object.values(racks).some(k => k.r.x + k.r.w / 2 >= zr.x && k.r.x + k.r.w / 2 <= zr.x + zr.w && k.r.y + k.r.h / 2 >= zr.y && k.r.y + k.r.h / 2 <= zr.y + zr.h); });
        for (const z of conRacks) { const zr = R(z); pared(zr.x, zr.y, zr.x + zr.w, zr.y, 46); pared(zr.x + zr.w, zr.y, zr.x + zr.w, zr.y + zr.h, 46); }

        const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('class', 'p3-ruta'); svg.setAttribute('width', LT * S); svg.setAttribute('height', WT * S);
        const camino = document.createElementNS('http://www.w3.org/2000/svg', 'path'); svg.appendChild(camino);
        div('', { position: 'absolute', left: 0, top: 0, transform: 'translateZ(2px)' }).appendChild(svg);

        // camino en el piso: desde la entrada del ambiente (lado de la calle) por el pasillo del rack
        function ruta(k) {
            if (!k) { camino.setAttribute('d', ''); return; }
            const zona = zonas.map(z => ({ z, r: R(z) }))
                .filter(t => k.r.x + k.r.w / 2 >= t.r.x && k.r.x + k.r.w / 2 <= t.r.x + t.r.w && k.r.y + k.r.h / 2 >= t.r.y && k.r.y + k.r.h / 2 <= t.r.y + t.r.h)
                .sort((a, b) => a.r.w * a.r.h - b.r.w * b.r.h)[0];
            if (!zona) { camino.setAttribute('d', ''); return; }
            const otros = Object.values(racks).filter(o => o !== k);
            const libre = (yy) => yy > zona.r.y + 8 && yy < zona.r.y + zona.r.h - 8 && !otros.some(o => yy >= o.r.y - 6 && yy <= o.r.y + o.r.h + 6 && o.r.x < k.r.x + k.r.w / 2 && o.r.x + o.r.w > zona.r.x + 10);
            const arriba = k.r.y - 0.45 * S, abajo = k.r.y + k.r.h + 0.45 * S;
            const yP = libre(abajo) ? abajo : libre(arriba) ? arriba : (abajo < zona.r.y + zona.r.h ? abajo : arriba);
            const x0 = zona.r.x + 12, xR = k.r.x + k.r.w / 2;
            camino.setAttribute('d', `M ${x0.toFixed(0)} ${yP.toFixed(0)} L ${xR.toFixed(0)} ${yP.toFixed(0)}`);
        }
        return { mundo, racks, porCalle, WT, LT, ruta, zonas: zonas.filter(z => z.nombre).map(z => ({ nombre: z.nombre, r: R(z) })) };
    }

    // ───────────── pantalla ─────────────
    function abrir(d) {
        cerrar();
        estilos();
        const root = h('div', 'p3', null, document.body);
        root.innerHTML = `
          <div class="p3-esc">
            <div class="p3-hud"></div>
            <button class="p3-x" title="Cerrar (Esc)">✕</button>
            <div class="p3-ctl">
              <button data-a="todo">🗺 Ver todo el recorrido</button>
              <button data-a="frente" hidden>👁 Ver de frente</button>
              <button data-a="lado" hidden>↔ El otro lado</button>
              <button data-a="izq">⟲ Girar</button>
              <button data-a="der">⟳ Girar</button>
              <button data-a="capas">👁 Qué ver</button>
              <button data-a="entrar">🚪 Entrar a…</button>
            </div>
            <div class="p3-capas p3-amb" hidden></div>
            <div class="p3-capas" hidden></div>
            <div class="p3-tip" hidden></div>
          </div>
          <div class="p3-pan">
            <div class="p3-cab"><div class="t">${esc(d.etiqueta || 'Armado de pedido')}</div><h2>${esc(d.titulo)}</h2><div class="s"></div>${d.soloVer ? '' : '<div class="p3-barra"><i></i></div>'}</div>
            ${d.buscar ? '<div class="p3-bus"><input type="search" placeholder="🔎 Buscar producto (nombre, SKU o código)" autocomplete="off" /><div class="p3-res"></div></div>' : ''}
            <div class="p3-items"></div>
            ${d.soloVer ? '' : '<div class="p3-pie"><button data-a="todo">Recorrido</button><button class="lo" data-a="lo">✓ Lo tengo · siguiente</button></div>'}
          </div>`;
        const escD = root.querySelector('.p3-esc');

        // productos: primero los que están en un estante, en orden de recorrido (planta, desde la calle)
        const escenas = {};
        const escenaDe = (pl) => {
            if (!escenas[pl]) { const els = (d.plantas || {})[pl]; if (!els || !els.length) return null; escenas[pl] = armarEscena(els); }
            return escenas[pl];
        };
        const prep = (it, i) => {
            const p = partir(it.est);
            const pl = (it.planta || 'PB').toUpperCase();
            const esc = p ? escenaDe(pl) : null;
            if (p && esc && p.piso != null) p.rack = esc.porCalle['P' + (p.calle != null ? p.calle : '') + '-' + p.piso] || esc.porCalle['P-' + p.piso] || null;
            else if (p && esc && p.fila != null) p.rack = esc.porCalle['F' + p.fila + '-' + p.nro] || null;
            else if (p && esc && p.calle != null) p.rack = esc.porCalle[p.calle + '-' + p.nro] || null;
            const k = esc && p && p.rack ? esc.racks[p.rack] : null;
            return { ...it, i, p, pl, k, ok: !!k };
        };
        const items = (d.items || []).map(prep);
        items.sort((a, b) => (b.ok - a.ok) || (a.ok && b.ok ? (a.pl.localeCompare(b.pl) || (a.k.r.x - b.k.r.x) || (a.p.niv - b.p.niv)) : a.i - b.i));
        const conLugar = items.filter(x => x.ok);

        let act = -1, arriba = true, cam = { rx: 0, rz: 0, s: .5, cx: 0, cy: 0, cz: 0 }, plantaVista = null;
        // 2026-11-27: "de frente": { k, cara, niv } mirando la cara del rack/estantería derecho
        let frenteDe = null, ultK = null;
        const altTags = [];
        const hechos = new Set();
        // los que ya estaban tildados en la tarjeta del pedido
        const yaTildados = new Set(d.hechos || []);
        items.forEach((it, idx) => { if (it.id != null && yaTildados.has(it.id)) hechos.add(idx); });
        const pines = [];

        // ───── 2026-11-28: qué hay en cada altura (pasando el mouse / tocando) y capas ─────
        const rackDatos = {};          // id del rack → promesa con { estantes } (api/deposito/plano/racks/{id})
        let panelDe = null;            // { k, niv } del estante abierto en el panel de la derecha
        const capasOff = capasApagadas();
        const aplicarCapas = (mundo) => CAPAS.forEach(c => mundo.classList.toggle('sin-' + c[0], capasOff.has(c[0])));
        function datosRack(k, fresco) {
            const id = k.e.id;
            if (fresco || !rackDatos[id])
                rackDatos[id] = fetch('/api/deposito/plano/racks/' + id, { credentials: 'same-origin' })
                    .then(r => r.ok ? r.json() : null).catch(() => null);
            return rackDatos[id];
        }
        // los estantes de esa altura: del lado que se está mirando de frente, o de todos los lados
        function estantesDe(dat, k, niv) {
            if (!dat) return [];
            const cara = frenteDe && frenteDe.k === k ? frenteDe.cara : null;
            return (dat.estantes || []).filter(x => x.nivel === niv && (cara == null || x.cara === cara))
                .sort((a, b) => (a.cara - b.cara) || (a.posicion - b.posicion));
        }
        const nombreProd = (p) => esc(p.nombre) + (p.cantidad ? ' · ' + esc(p.cantidad) : '');
        function pintarCapas() {
            const c = root.querySelector('.p3-capas');
            c.innerHTML = '<div class="t">Qué ver</div>' + CAPAS.map(x => `<label><input type="checkbox" data-capa="${x[0]}" ${capasOff.has(x[0]) ? '' : 'checked'} /> ${esc(x[1])}</label>`).join('');
        }
        root.addEventListener('change', async (ev) => {
            const fi = ev.target.closest('.p3-est input[data-foto]');
            if (fi && fi.files && fi.files[0] && panelDe) {
                const k = panelDe.k, fd = new FormData(); fd.append('file', fi.files[0]);
                fi.parentElement.firstChild.textContent = 'Subiendo… ';
                const r = await fetch('/api/deposito/plano/estante/' + encodeURIComponent(fi.dataset.foto) + '/foto', { method: 'POST', credentials: 'same-origin', body: fd }).catch(() => null);
                if (!r || !r.ok) { fi.parentElement.firstChild.textContent = 'No se pudo subir '; return; }
                await recargarEstante(k);
                return;
            }
            const dt = ev.target.closest('.p3-est input[data-dtexto]');
            if (dt && panelDe) {
                const k = panelDe.k, dat = await datosRack(k);
                const est = (dat && dat.estantes || []).find(e => e.codigo === dt.dataset.dtexto) || {};
                const r = await fetch('/api/deposito/plano/estante/' + encodeURIComponent(dt.dataset.dtexto) + '/dibujo', {
                    method: 'PUT', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ dibujo: est.dibujo, cantidad: est.dibujoCant, texto: dt.value })
                }).catch(() => null);
                if (r && r.ok) await recargarEstante(k);
                return;
            }
            const cb = ev.target.closest('.p3-capas input[data-capa]'); if (!cb) return;
            if (cb.checked) capasOff.delete(cb.dataset.capa); else capasOff.add(cb.dataset.capa);
            guardarCapas(capasOff);
            Object.values(escenas).forEach(x => aplicarCapas(x.mundo));
        });

        // globito al pasar el mouse por una altura
        const tip = root.querySelector('.p3-tip');
        let hov = null, hovKey = '';
        function sacarHover() { if (hov) hov.classList.remove('hov'); hov = null; hovKey = ''; tip.hidden = true; }
        escD.addEventListener('pointermove', async (ev) => {
            if (arr || ev.pointerType === 'touch') return;
            const nv = ev.target.closest ? ev.target.closest('[data-niv]') : null;
            const rk = nv ? nv.closest('.p3-rack') : null;
            const e = escenas[plantaVista];
            const k = rk && e ? e.racks[rk.dataset.cod] : null;
            if (!k) { sacarHover(); return; }
            const niv = +nv.dataset.niv, key = rk.dataset.cod + '|' + niv + '|' + (frenteDe ? frenteDe.cara : '');
            const b = escD.getBoundingClientRect();
            tip.style.left = Math.min(ev.clientX - b.left + 16, b.width - 330) + 'px';
            tip.style.top = Math.max(8, ev.clientY - b.top + 14) + 'px';
            if (key === hovKey) return;
            sacarHover(); hovKey = key;
            hov = k.niveles[niv - 1] || null; if (hov) hov.classList.add('hov');
            tip.innerHTML = `<b>${esc(k.cod)}</b> · altura ${niv} <span class="v">cargando…</span>`; tip.hidden = false;
            const dat = await datosRack(k);
            if (hovKey !== key) return;
            const ests = estantesDe(dat, k, niv);
            tip.innerHTML = (ests.length ? ests.map(x => `<div><b>${esc(x.codigo)}</b>`
                + (x.dibujo ? `<div style="color:#fdba74">${esc(nombreDibujo(x.dibujo))}${x.dibujoCant != null ? ' · ' + x.dibujoCant : ''}${x.dibujoTexto ? ' · ' + esc(x.dibujoTexto) : ''}</div>` : '')
                + (x.productos.length ? x.productos.slice(0, 6).map(p => `<div>• ${nombreProd(p)}</div>`).join('') + (x.productos.length > 6 ? `<div class="v">+${x.productos.length - 6} más</div>` : '')
                                      : x.dibujo ? '' : '<div class="v">vacío</div>')
                + (x.nota ? `<div class="v">📝 ${esc(x.nota)}</div>` : '')
                + (x.foto ? `<img src="${esc(x.foto)}" />` : '') + '</div>').join('')
                : `<b>${esc(k.cod)}</b> · altura ${niv}`) + '<div class="h">Tocá para ver o agregar</div>';
        });
        escD.addEventListener('pointerleave', sacarHover);

        // panel del estante (arriba de la lista, a la derecha)
        async function pintarEstante() {
            let box = root.querySelector('.p3-est');
            if (!panelDe) { if (box) box.remove(); return; }
            if (!box) { box = h('div', 'p3-est'); root.querySelector('.p3-cab').after(box); }
            const { k, niv } = panelDe;
            const dat = await datosRack(k);
            if (!panelDe || panelDe.k !== k) return;
            const ests = estantesDe(dat, k, niv);
            box.innerHTML = `<div class="tt">${k.piso ? 'Lugar en el piso' : k.est ? 'Estantería' : 'Rack ' + esc(k.cod)} · altura ${niv}<button data-a="cerrar-est" title="Cerrar">✕</button></div>`
                + (ests.length ? '' : '<div class="vac">No encontré este estante.</div>')
                + ests.map(x => `<div class="bl">
                    <div class="cod">${esc(x.codigo)}</div><div class="ds">${esc(x.desglose || '')}</div>
                    ${x.nota ? `<div class="ve">📝 ${esc(x.nota)}</div>` : ''}
                    ${x.dibujo ? `<div class="ve">Acá se ve: <b>${esc(nombreDibujo(x.dibujo))}</b>${x.dibujoCant != null ? ' · ' + x.dibujoCant : ''}${x.dibujoTexto ? ' · ' + esc(x.dibujoTexto) : ''}</div>` : ''}
                    ${d.editar ? `<div class="ds" style="margin-top:6px">Qué se ve acá (aunque no tenga stock):</div>
                        <div class="dib">${DIBUJOS.map(t => `<button data-dib="${t[0]}" data-cod="${esc(x.codigo)}" class="${x.dibujo === t[0] ? 'on' : ''}">${esc(t[1])}</button>`).join('')}</div>
                        ${x.dibujo ? `<input data-dtexto="${esc(x.codigo)}" placeholder="texto, ej. Café crudo de importación" value="${esc(x.dibujoTexto || '')}" style="margin-top:0" />` : ''}` : ''}
                    <div class="ft">${x.foto ? `<img src="${esc(x.foto)}" data-grande="${esc(x.foto)}" title="Ver grande" />` : ''}
                      ${d.editar ? `<label>${x.foto ? '📷 Cambiar foto' : '📷 Agregar foto'}<input type="file" accept="image/*" data-foto="${esc(x.codigo)}" hidden /></label>${x.foto ? `<label data-sinfoto="${esc(x.codigo)}">Sacar foto</label>` : ''}` : ''}</div>
                    ${x.productos.length ? x.productos.map(p => `<div class="pr"><div><b>${esc(p.nombre)}</b>${p.cantidad ? ' · ' + esc(p.cantidad) : ''}<div class="k">${esc([p.sku, 'stock ' + (p.stock ?? 0)].filter(Boolean).join(' · '))}</div></div>${d.editar ? `<button class="x" data-sacar="${p.id}" title="Sacar de este estante">✕</button>` : ''}</div>`).join('')
                                         : '<div class="vac">Vacío</div>'}
                    ${d.editar ? `<input type="search" data-cod="${esc(x.codigo)}" placeholder="+ Agregar un producto acá (nombre o SKU)" autocomplete="off" /><div class="rs"></div>` : ''}
                  </div>`).join('');
            box.scrollTop = 0;
        }
        async function recargarEstante(k) {
            const dat = await datosRack(k, true);
            hovKey = '';
            // 2026-11-28: el dibujo nuevo se ve enseguida en el 3D
            if (dat) {
                k.e.dibujos = (dat.estantes || []).filter(x => x.dibujo || x.foto)
                    .map(x => ({ cara: x.cara, posicion: x.posicion, nivel: x.nivel, dibujo: x.dibujo, cant: x.dibujoCant, texto: x.dibujoTexto, foto: x.foto }));
                if (panelDe && panelDe.k === k && k.llenar) {
                    const cara = frenteDe && frenteDe.k === k ? frenteDe.cara : null;
                    const db = (k.e.dibujos || []).filter(x => x.nivel === panelDe.niv && (cara == null || x.cara === cara)).find(x => x.dibujo) || null;
                    k.llenar(panelDe.niv, db ? db.dibujo : null);
                }
            }
            await pintarEstante();
            if (d.dotnet) d.dotnet.invokeMethodAsync('Cambio3D', k.e.id).catch(() => { });
        }
        let tmoEst = null, nroEst = 0;
        root.addEventListener('input', (ev) => {
            const inp = ev.target.closest('.p3-est input[data-cod]'); if (!inp) return;
            const rs = inp.nextElementSibling, q = inp.value.trim();
            clearTimeout(tmoEst);
            if (q.length < 2) { rs.innerHTML = ''; return; }
            tmoEst = setTimeout(async () => {
                const yo = ++nroEst;
                try {
                    const r = await fetch('/api/deposito/plano/buscar?q=' + encodeURIComponent(q), { credentials: 'same-origin' });
                    if (yo !== nroEst || !r.ok) return;
                    const l = await r.json();
                    rs.innerHTML = l.slice(0, 12).map(p => `<div data-pid="${p.productoId}"><b>${esc(p.nombre)}</b><br /><span>${esc([p.sku, 'stock ' + (p.stock ?? 0), p.lugares.length ? 'ya en ' + p.lugares.map(x => x.codigo).join(', ') : null].filter(Boolean).join(' · '))}</span></div>`).join('')
                        || '<div style="cursor:default"><span>No encontré nada.</span></div>';
                } catch { }
            }, 300);
        });
        root.addEventListener('click', async (ev) => {
            if (!panelDe) return;
            const k = panelDe.k;
            const add = ev.target.closest('.p3-est .rs [data-pid]');
            if (add) {
                const cod = add.parentElement.previousElementSibling.dataset.cod;
                add.style.opacity = .5;
                const r = await fetch('/api/deposito/plano/estante/' + encodeURIComponent(cod) + '/productos', {
                    method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ productoId: +add.dataset.pid })
                }).catch(() => null);
                if (!r || !r.ok) { add.style.opacity = 1; add.querySelector('span').textContent = 'No se pudo guardar'; return; }
                await recargarEstante(k);
                return;
            }
            const bd = ev.target.closest('.p3-est [data-dib]');
            if (bd) {
                const cod = bd.dataset.cod, dat = await datosRack(k);
                const est = (dat && dat.estantes || []).find(e => e.codigo === cod) || {};
                const nuevo = est.dibujo === bd.dataset.dib ? null : bd.dataset.dib;
                const r = await fetch('/api/deposito/plano/estante/' + encodeURIComponent(cod) + '/dibujo', {
                    method: 'PUT', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ dibujo: nuevo, cantidad: nuevo ? est.dibujoCant : null, texto: nuevo ? est.dibujoTexto : null })
                }).catch(() => null);
                if (r && r.ok) await recargarEstante(k);
                return;
            }
            const gr = ev.target.closest('.p3-est [data-grande]');
            if (gr) { window.open(gr.dataset.grande, '_blank'); return; }
            const sf = ev.target.closest('.p3-est [data-sinfoto]');
            if (sf) {
                const r = await fetch('/api/deposito/plano/estante/' + encodeURIComponent(sf.dataset.sinfoto) + '/foto', { method: 'DELETE', credentials: 'same-origin' }).catch(() => null);
                if (r && r.ok) await recargarEstante(k);
                return;
            }
            const x = ev.target.closest('.p3-est [data-sacar]');
            if (x) {
                x.disabled = true;
                const r = await fetch('/api/deposito/plano/asignaciones/' + x.dataset.sacar, { method: 'DELETE', credentials: 'same-origin' }).catch(() => null);
                if (!r || !r.ok) { x.disabled = false; return; }
                await recargarEstante(k);
            }
        });

        function mostrarPlanta(pl) {
            if (plantaVista === pl) return;
            const e = escenaDe(pl); if (!e) return;
            const viejo = escD.querySelector('.p3-mundo'); if (viejo) viejo.remove();
            escD.insertBefore(e.mundo, escD.firstChild);
            aplicarCapas(e.mundo);
            plantaVista = pl;
        }
        const contra = () => `rotateZ(${-cam.rz}deg) rotateX(${-cam.rx}deg)`;
        function aplicar() {
            const e = escenas[plantaVista]; if (!e) return;
            e.mundo.style.transform = `rotateX(${cam.rx}deg) rotateZ(${cam.rz}deg) scale3d(${cam.s}, ${cam.s}, ${cam.s}) translate3d(${-cam.cx}px, ${-cam.cy}px, ${-(cam.cz || 0)}px)`;
            e.mundo.querySelectorAll('.p3-cartel').forEach(c => { c.style.transform = `translateZ(${c.dataset.z}px) ` + contra(); });
        }
        function pintarPines() {
            pines.forEach(p => p.remove()); pines.length = 0;
            const e = escenas[plantaVista]; if (!e) return;
            if (frenteDe) return; // de frente: sin pines ni globo (lo dice arriba)
            const porRack = {};
            items.forEach((it, idx) => { if (it.ok && it.pl === plantaVista) (porRack[it.p.rack] = porRack[it.p.rack] || []).push(idx); });
            for (const [rk, idx] of Object.entries(porRack)) {
                const k = e.racks[rk];
                const m = h('div', 'p3-marca', { left: (k.r.x + k.r.w / 2) + 'px', top: (k.r.y + k.r.h / 2) + 'px' }, e.mundo);
                const c = h('div', 'p3-cartel', null, m); c.dataset.z = k.H + 26;
                c.style.transform = `translateZ(${k.H + 26}px) ` + contra();
                const esAct = !arriba && idx.includes(act);
                const p = h('div', 'p3-pin' + (esAct ? ' act' : idx.every(i => hechos.has(i)) ? ' ok' : ''), null, c);
                const nums = idx.map(i => i + 1).join(',');
                p.innerHTML = `<div><span style="font-size:${nums.length > 3 ? 13 : 18}px">${nums}</span></div>`;
                if (esAct) {
                    const it = items[act];
                    const g = h('div', 'p3-globo', null, c);
                    g.innerHTML = `<div class="g"><b>${esc(it.est)}</b><span>${esc(desglose(it.p))}</span></div><div class="pico"></div>`;
                }
                pines.push(m);
            }
        }
        function hud() {
            const el = root.querySelector('.p3-hud');
            if (frenteDe) { hudFrente(); return; }
            if (arriba && d.buscar && !conLugar.length) {
                el.innerHTML = `<div class="t">Plano del depósito${plantaVista === 'P1' ? ' · planta alta' : ''}</div><div class="d">Buscá un producto</div><div class="s">Te llevo volando hasta su estante</div>`;
                return;
            }
            if (arriba) {
                const nr = new Set(conLugar.map(x => x.pl + x.p.rack)).size;
                el.innerHTML = `<div class="t">Recorrido del armado${plantaVista === 'P1' ? ' · planta alta' : ''}</div><div class="d">${conLugar.length} ${conLugar.length === 1 ? 'producto' : 'productos'} · ${nr} ${nr === 1 ? 'rack' : 'racks'}</div><div class="s">Tocá un producto y te llevo hasta el estante</div>`;
                return;
            }
            const it = items[act];
            el.innerHTML = `<div class="t">Producto ${act + 1} de ${items.length}${it.pl === 'P1' ? ' · planta alta' : ''}</div><div class="d">Andá a <em>${esc(it.est)}</em></div><div class="s" style="color:#fdba74;font-weight:700">${esc(desglose(it.p))}</div><div class="s">${esc(it.n)}${it.c ? ' · <b>' + esc(it.c) + '</b>' : ''}</div>`;
        }
        function lista() {
            const L = root.querySelector('.p3-items');
            L.innerHTML = items.map((it, i) => `
              <div class="p3-it ${i === act && !arriba ? 'act' : ''} ${hechos.has(i) ? 'ok' : ''} ${it.ok ? '' : 'sin'}" data-i="${i}">
                <div class="p3-num">${hechos.has(i) ? '✓' : i + 1}</div>
                <div><div class="n">${esc(it.n)}</div><div class="k">${esc([it.sku, it.c].filter(Boolean).join(' · '))}</div>
                ${it.ok ? `<div class="p3-lug" title="${esc(desglose(it.p))}"><b>📍 ${esc(it.est)}</b><span>🗺 Ver</span></div>`
                        : `<div class="p3-sinlug">${it.est ? 'Lugar: ' + esc(it.est) + ' (no es un estante del plano)' : 'Sin estante en el plano'}</div>`}
                </div></div>`).join('');
            root.querySelector('.p3-cab .s').textContent = d.soloVer
                ? (items.length <= 1 ? '' : d.buscar ? `${items.length} productos` : `${items.length} productos en este estante`)
                : `${hechos.size} de ${items.length} juntados · ordenado por recorrido`;
            const barra = root.querySelector('.p3-barra i');
            if (barra) barra.style.width = (items.length ? hechos.size / items.length * 100 : 0) + '%';
            const a = L.querySelector('.p3-it.act'); if (a) a.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
        }
        function vistaArriba(pl) {
            arriba = true;
            mostrarPlanta(pl || plantaVista || (conLugar[0] ? conLugar[0].pl : 'PB'));
            const e = escenas[plantaVista]; if (!e) return;
            marcarFuera(e, null);
            const b = escD.getBoundingClientRect();
            // encuadre: los racks del pedido (o todo el terreno si no hay)
            const ks = conLugar.filter(x => x.pl === plantaVista).map(x => x.k);
            let x0 = 0, y0 = 0, x1 = e.LT * S, y1 = e.WT * S;
            if (ks.length) {
                x0 = Math.min(...ks.map(k => k.r.x)) - 6 * S; x1 = Math.max(...ks.map(k => k.r.x + k.r.w)) + 6 * S;
                y0 = 0; y1 = e.WT * S;
            }
            const s = Math.min(b.width / (x1 - x0) * 0.9, b.height / (y1 - y0) * 1.1, 1.1);
            cam = { rx: 42, rz: 0, s, cx: (x0 + x1) / 2, cy: (y0 + y1) / 2, cz: 0 };
            frenteDe = null; ultK = null; limpiarFrente();
            Object.values(e.racks).forEach(x => { x.d.classList.remove('meta', 'otro', 'tapa'); x.niveles.forEach(n => n.classList.remove('este')); });
            e.ruta(null);
            pintarPines(); aplicar(); hud(); lista();
        }
        function ir(i) {
            const it = items[i]; if (!it || !it.ok) return;
            act = i; arriba = false;
            mostrarPlanta(it.pl);
            if (escenas[it.pl]) marcarFuera(escenas[it.pl], null);
            const e = escenas[it.pl], k = it.k;
            const seguirDeFrente = !!frenteDe;
            frenteDe = null; limpiarFrente();
            Object.values(e.racks).forEach(x => { x.d.classList.remove('meta', 'tapa'); x.d.classList.add('otro'); x.niveles.forEach(n => n.classList.remove('este')); });
            k.d.classList.remove('otro'); k.d.classList.add('meta');
            (k.niveles[it.p.niv - 1] || k.niveles[0]).classList.add('este');
            ultK = { k, cara: caraDe(k, it.p), niv: it.p.niv };
            botones();
            if (seguirDeFrente) { e.ruta(k); pintarPines(); lista(); frente(ultK.k, ultK.cara, ultK.niv); return; }
            const b = escD.getBoundingClientRect();
            cam = { rx: 60, rz: cam.rz && !arriba ? cam.rz : -30, s: Math.min(1.6, Math.max(0.9, b.width / 900)), cx: k.r.x + k.r.w / 2, cy: k.r.y + k.r.h / 2 - 20, cz: k.z0 || 0 };
            e.ruta(k);
            pintarPines(); aplicar(); hud(); lista();
        }
        // ───── 2026-11-27: ver de frente ─────
        // lado 1 = el que mira a la pared izq. (y grande en la escena) o, girado, a la entrada (x chica)
        // 2026-11-28: caras 3 y 4 = las puntas (cabeceras) que dan a una calle
        function calleCara(k, cara) { return cara === 2 ? k.e.calle2 : cara === 3 ? k.e.callePunta1 : cara === 4 ? k.e.callePunta2 : k.e.calle1; }
        function caraDe(k, p) {
            if (!p || p.calle == null) return 1;
            for (const c of [1, 2, 3, 4]) if (calleCara(k, c) === p.calle) return c;
            return 1;
        }
        function carasDe(k) {
            if (k.piso || k.est) return [1];
            const l = [k.e.calle1 != null ? 1 : null, k.e.calle2 != null ? 2 : null].filter(Boolean);
            if (!l.length) l.push(1);
            if (k.e.callePunta1 != null) l.push(3);
            if (k.e.callePunta2 != null) l.push(4);
            return l;
        }
        // hacia dónde mira esa cara en la escena + el giro de cámara para tenerla de frente
        function normal(k, cara) {
            if (k.dir) return k.dir;
            if (cara >= 3) {
                // punta 1 = lado entrada (x chica en la escena) o, girado, pared izq. (y grande)
                const gir = k.e.rotacion === 90 || k.e.rotacion === 270;
                if (!gir) return cara === 3 ? { nx: -1, ny: 0, rz: -90 } : { nx: 1, ny: 0, rz: 90 };
                return cara === 3 ? { nx: 0, ny: 1, rz: 0 } : { nx: 0, ny: -1, rz: 180 };
            }
            const largoX = k.r.w >= k.r.h;
            if (largoX) return cara === 2 ? { nx: 0, ny: -1, rz: 180 } : { nx: 0, ny: 1, rz: 0 };
            return cara === 2 ? { nx: 1, ny: 0, rz: 90 } : { nx: -1, ny: 0, rz: -90 };
        }
        function limpiarFrente() {
            altTags.forEach(t => t.remove()); altTags.length = 0;
            Object.values(escenas).forEach(x => x.mundo.classList.remove('frente'));
            root.querySelectorAll('.p3-rack.tapa').forEach(x => x.classList.remove('tapa'));
            botones();
        }
        function botones() {
            const bf = root.querySelector('[data-a="frente"]'), bl = root.querySelector('[data-a="lado"]');
            const k = frenteDe ? frenteDe.k : ultK ? ultK.k : null;
            bf.hidden = !k; bf.classList.toggle('on', !!frenteDe);
            bf.textContent = frenteDe ? '↩ Volver a la vista de arriba' : '👁 Ver de frente';
            bl.hidden = !(frenteDe && carasDe(frenteDe.k).length > 1);
        }
        function frente(k, cara, niv) {
            const e = escenas[plantaVista]; if (!e || !k) return;
            limpiarFrente();
            frenteDe = { k, cara, niv }; arriba = false;
            pintarPines();
            const n = normal(k, cara);
            const cx = k.r.x + k.r.w / 2, cy = k.r.y + k.r.h / 2;
            // lo que tapa la vista (racks entre la cámara y la cara) se vuelve casi transparente
            const L = Math.max(k.r.w, k.r.h);
            Object.values(e.racks).forEach(o => {
                if (o === k) return;
                const ox = o.r.x + o.r.w / 2 - cx, oy = o.r.y + o.r.h / 2 - cy;
                const delante = ox * n.nx + oy * n.ny, lado = Math.abs(ox * n.ny - oy * n.nx);
                if (delante > 0 && lado < L / 2 + Math.max(o.r.w, o.r.h) / 2 + 40) o.d.classList.add('tapa');
            });
            // cartelitos A1, A2… al costado de cada altura
            const izq = n.rz === 0 ? [k.r.x - 14, k.r.y + k.r.h] : n.rz === 180 ? [k.r.x + k.r.w + 14, k.r.y] : n.rz === -90 ? [k.r.x, k.r.y - 14] : [k.r.x + k.r.w, k.r.y + k.r.h + 14];
            e.mundo.classList.add('frente');
            const paso = k.piso ? 20 : k.gapNiv ? Math.min(...k.gapNiv) : (k.H - 10) / k.N;
            const zs = k.piso ? [k.H / 2] : k.niveles.map((_, i) => (k.z0 || 0) + (k.zNiv ? k.zNiv[i] + Math.min(14, k.gapNiv[i] / 2) : 4 + i * paso + Math.min(14, paso / 2)));
            const fz = Math.max(6, Math.min(12, paso * 0.55));
            zs.forEach((z, i) => {
                const m = h('div', 'p3-marca', { left: izq[0] + 'px', top: izq[1] + 'px' }, e.mundo);
                const c = h('div', 'p3-cartel', null, m); c.dataset.z = z;
                const t = h('div', 'p3-alt' + (niv === i + 1 ? ' este' : ''), { fontSize: fz + 'px' }, c);
                t.textContent = k.piso ? 'PISO' : 'A' + (i + 1);
                // 2026-11-28: si esa altura tiene foto, chiquita al lado del cartel
                const fo = (k.e.dibujos || []).find(x => x.nivel === i + 1 && x.cara === cara && x.foto);
                if (fo) { const im = document.createElement('img'); im.src = fo.foto; t.appendChild(im); }
                altTags.push(m);
            });
            const b = escD.getBoundingClientRect();
            const s = Math.max(0.5, Math.min(3.2, b.width * 0.6 / (L + 60), b.height * 0.5 / (k.H + 30)));
            cam = { rx: k.piso ? 62 : 80, rz: n.rz, s, cx: cx + n.nx * 10, cy: cy + n.ny * 10, cz: k.piso ? 0 : (k.z0 || 0) + k.H * 0.5 };
            botones(); aplicar(); hudFrente();
            if (panelDe && panelDe.k === k) pintarEstante();
        }
        function hudFrente() {
            if (!frenteDe) return;
            const k = frenteDe.k, el = root.querySelector('.p3-hud');
            const it = act >= 0 && items[act] && items[act].k === k ? items[act] : null;
            const titulo = k.piso ? 'Lugar en el piso ' + k.cod : k.est ? (k.e.nombre || 'Estantería ' + k.cod) : 'Rack ' + k.cod;
            const calle = k.piso ? null : calleCara(k, frenteDe.cara);
            el.innerHTML = `<div class="t">De frente${frenteDe.cara >= 3 ? ' · la punta' : ''}${calle != null ? ' · desde la calle ' + calle : ''}</div><div class="d">${esc(titulo)}</div>`
                + (it ? `<div class="s" style="color:#fdba74;font-weight:700">${esc(it.est)} · ${esc(it.n)}</div>` : '<div class="s">Arrastrá para girar · tocá otro rack para ir ahí</div>');
        }
        // 2026-11-28: "entrar" a un ambiente: la cámara se para adentro, a la altura de los ojos,
        // mirando a lo largo del ambiente (arrastrando se mira para los costados)
        // lo que no está adentro del ambiente se esconde (racks, equipos, muebles, puertas, paredes)
        function marcarFuera(e, r) {
            e.mundo.querySelectorAll(':scope > .p3-rack, :scope > .p3-eq, :scope > .p3-caja, :scope > .p3-cosa, :scope > .p3-pared').forEach(el => {
                if (!r) { el.classList.remove('fuera'); return; }
                const x = parseFloat(el.style.left) || 0, y = parseFloat(el.style.top) || 0, w = parseFloat(el.style.width) || 0, hh = parseFloat(el.style.height) || 0;
                let cx = x + w / 2, cy = y + hh / 2;
                if (el.classList.contains('p3-eq') && (el.style.transform || '').includes('rotateZ(90deg)')) { cx = x - hh / 2; cy = y + w / 2; }
                if (el.classList.contains('p3-pared')) { el.classList.add('fuera'); return; }
                el.classList.toggle('fuera', !(cx >= r.x - 4 && cx <= r.x + r.w + 4 && cy >= r.y - 4 && cy <= r.y + r.h + 4));
            });
        }
        function entrar(i) {
            const e = escenas[plantaVista]; if (!e) return;
            const z = e.zonas[i]; if (!z) return;
            marcarFuera(e, z.r);
            frenteDe = null; limpiarFrente(); act = -1; arriba = false; ultK = null;
            Object.values(e.racks).forEach(x => { x.d.classList.remove('meta', 'otro', 'tapa'); x.niveles.forEach(n => n.classList.remove('este')); });
            e.ruta(null); pintarPines(); lista(); botones();
            const r = z.r, largoY = r.h >= r.w, b = escD.getBoundingClientRect();
            const ancho = largoY ? r.w : r.h;
            const s = Math.max(1, Math.min(4.2, b.width * 0.95 / (ancho + 40)));
            cam = largoY ? { rx: 74, rz: 0, s, cx: r.x + r.w / 2, cy: r.y + r.h * 0.6, cz: 1.35 * S }
                         : { rx: 74, rz: -90, s, cx: r.x + r.w * 0.4, cy: r.y + r.h / 2, cz: 1.35 * S };
            aplicar();
            root.querySelector('.p3-hud').innerHTML = `<div class="t">Adentro</div><div class="d">${esc(z.nombre)}</div><div class="s">Arrastrá para mirar a los costados · tocá una estantería para ver qué hay</div>`;
        }
        function tocarRack(cod, niv) {
            const e = escenas[plantaVista]; if (!e) return;
            const k = e.racks[cod]; if (!k) return;
            // 2026-11-28: tocando una altura se abre ese estante en el panel (y si ya está de frente, no vuela de nuevo)
            if (niv) {
                panelDe = { k, niv };
                k.niveles.forEach(n => n.classList.remove('este'));
                (k.niveles[niv - 1] || k.niveles[0]).classList.add('este');
                pintarEstante();
                if (frenteDe && frenteDe.k === k) { frenteDe.niv = niv; frente(k, frenteDe.cara, niv); return; }
            }
            Object.values(e.racks).forEach(x => { x.d.classList.remove('meta'); x.d.classList.add('otro'); x.niveles.forEach(n => n.classList.remove('este')); });
            k.d.classList.remove('otro'); k.d.classList.add('meta');
            if (niv) (k.niveles[niv - 1] || k.niveles[0]).classList.add('este');
            act = -1; ultK = { k, cara: carasDe(k)[0], niv: niv || null };
            e.ruta(k); pintarPines(); lista();
            frente(k, ultK.cara, niv || null);
        }

        function loTengo() {
            if (act >= 0 && !arriba && !hechos.has(act)) {
                hechos.add(act);
                // también se tilda en la tarjeta del pedido
                if (d.dotnet && items[act].id != null) d.dotnet.invokeMethodAsync('Tildar3D', items[act].id).catch(() => { });
            }
            const sig = items.findIndex((x, i) => x.ok && !hechos.has(i));
            if (sig >= 0) ir(sig); else vistaArriba();
        }

        root.addEventListener('click', (ev) => {
            const b = ev.target.closest('[data-a]');
            if (b) {
                const a = b.dataset.a;
                if (a === 'todo') vistaArriba();
                else if (a === 'izq') { cam.rz -= 25; aplicar(); }
                else if (a === 'der') { cam.rz += 25; aplicar(); }
                else if (a === 'lo') loTengo();
                else if (a === 'entrar') {
                    const m = root.querySelector('.p3-amb'); m.hidden = !m.hidden; b.classList.toggle('on', !m.hidden);
                    const e = escenas[plantaVista];
                    if (!m.hidden && e) m.innerHTML = '<div class="t">Entrar a un ambiente</div>' + e.zonas.map((z, i) => `<label data-amb="${i}" style="cursor:pointer">🚪 ${esc(z.nombre)}</label>`).join('');
                }
                else if (a === 'capas') { const c = root.querySelector('.p3-capas'); c.hidden = !c.hidden; b.classList.toggle('on', !c.hidden); if (!c.hidden) pintarCapas(); }
                else if (a === 'cerrar-est') { panelDe = null; pintarEstante(); }
                else if (a === 'frente') {
                    if (frenteDe) { const f = frenteDe; frenteDe = null; limpiarFrente(); if (act >= 0 && items[act] && items[act].k === f.k) ir(act); else vistaArriba(); }
                    else if (ultK) frente(ultK.k, ultK.cara, ultK.niv);
                }
                else if (a === 'lado' && frenteDe) {
                    const cs = carasDe(frenteDe.k), otra = cs.find(c => c !== frenteDe.cara) || frenteDe.cara;
                    frente(frenteDe.k, otra, frenteDe.niv);
                }
                return;
            }
            const am = ev.target.closest('[data-amb]');
            if (am) { const m = root.querySelector('.p3-amb'); m.hidden = true; root.querySelector('[data-a="entrar"]').classList.remove('on'); entrar(+am.dataset.amb); return; }
            const it = ev.target.closest('.p3-it');
            if (it) ir(+it.dataset.i);
        });
        root.querySelector('.p3-x').addEventListener('click', cerrar);

        // 2026-11-27: buscador adentro del 3D: elegís un producto (o uno de sus estantes) y vuela ahí
        if (d.buscar) {
            const inp = root.querySelector('.p3-bus input'), res = root.querySelector('.p3-res');
            let ult = [], tmo = null, nro = 0;
            const pintar = () => {
                res.innerHTML = ult.map((r, i) => `
                  <div class="p3-r ${r.lugares.length ? '' : 'sin'}">
                    <div class="n">${esc(r.nombre)}</div>
                    <div class="k">${esc([r.sku, r.stock != null ? 'stock ' + r.stock : null].filter(Boolean).join(' · '))}</div>
                    ${r.lugares.length ? r.lugares.map((l, j) => `<div class="p3-lug" data-r="${i}" data-l="${j}" title="${esc(l.desglose || '')}"><b>📍 ${esc(l.codigo)}</b><span>🗺 Ir</span></div>`).join('')
                                       : '<div class="p3-sinlug">Sin estante en el plano</div>'}
                  </div>`).join('') || (inp.value.trim().length >= 2 ? '<div class="p3-sinlug" style="padding:6px">No encontré nada.</div>' : '');
            };
            inp.addEventListener('input', () => {
                clearTimeout(tmo);
                const q = inp.value.trim();
                if (q.length < 2) { ult = []; pintar(); return; }
                tmo = setTimeout(async () => {
                    const yo = ++nro;
                    try {
                        const r = await fetch('/api/deposito/plano/buscar?q=' + encodeURIComponent(q), { credentials: 'same-origin' });
                        if (yo !== nro || !r.ok) return;
                        ult = await r.json(); pintar();
                    } catch { }
                }, 300);
            });
            res.addEventListener('click', (ev) => {
                const c = ev.target.closest('.p3-lug'); if (!c) return;
                const r = ult[+c.dataset.r], l = r && r.lugares[+c.dataset.l]; if (!l) return;
                let idx = items.findIndex(x => x.est === l.codigo && x.n === r.nombre);
                if (idx < 0) {
                    const it = prep({ id: null, sku: r.sku, n: r.nombre, c: r.stock != null ? 'stock ' + r.stock : null, est: l.codigo, planta: l.planta }, items.length);
                    if (!it.ok) { c.querySelector('span').textContent = 'no está en el plano'; return; }
                    items.push(it); conLugar.push(it); idx = items.length - 1;
                }
                ult = []; inp.value = ''; pintar();
                ir(idx);
            });
            setTimeout(() => inp.focus(), 50);
        }
        const tecla = (ev) => { if (ev.key === 'Escape') cerrar(); };
        document.addEventListener('keydown', tecla);

        // arrastrar con el dedo / mouse para girar
        let arr = null;
        escD.addEventListener('pointerdown', (ev) => { if (ev.target.closest('button, .p3-capas')) return; arr = { x: ev.clientX, y: ev.clientY, rz: cam.rz, t: ev.target }; escD.setPointerCapture(ev.pointerId); });
        escD.addEventListener('pointermove', (ev) => {
            if (!arr) return;
            const e = escenas[plantaVista]; if (!e) return;
            cam.rz = arr.rz + (ev.clientX - arr.x) * 0.3;
            e.mundo.style.transition = 'none'; e.mundo.querySelectorAll('.p3-cartel').forEach(c => c.style.transition = 'none');
            aplicar();
        });
        const soltar = (ev) => {
            if (!arr) return;
            const a0 = arr; arr = null;
            // 2026-11-27: un toque (sin arrastrar) sobre un rack → lo muestra de frente
            if (ev && ev.type === 'pointerup' && Math.abs(ev.clientX - a0.x) < 6 && Math.abs(ev.clientY - a0.y) < 6) {
                const rk = a0.t && a0.t.closest ? a0.t.closest('.p3-rack') : null;
                const nv = a0.t && a0.t.closest ? a0.t.closest('[data-niv]') : null;
                if (rk && rk.dataset.cod) setTimeout(() => tocarRack(rk.dataset.cod, nv ? +nv.dataset.niv : null), 0);
            }
            const e = escenas[plantaVista]; if (!e) return;
            e.mundo.style.transition = ''; e.mundo.querySelectorAll('.p3-cartel').forEach(c => c.style.transition = '');
        };
        escD.addEventListener('pointerup', soltar); escD.addEventListener('pointercancel', soltar);

        estado = { root, tecla };
        // arranca: primero todo el recorrido y después vuela al producto pedido (o al primero)
        const ini = d.inicio ? items.findIndex(x => x.ok && String(x.est).toUpperCase() === String(d.inicio).toUpperCase()) : -1;
        const primero = ini >= 0 ? items[ini] : conLugar[0];
        vistaArriba(primero ? primero.pl : 'PB');
        if (primero) {
            const e = escenas[plantaVista];
            if (e) { e.mundo.style.transition = 'none'; aplicar(); void e.mundo.offsetWidth; e.mundo.style.transition = ''; }
            estado.t = setTimeout(() => ir(items.indexOf(primero)), d.directo ? 50 : 1300);
        }
    }

    function cerrar() {
        if (!estado) return;
        clearTimeout(estado.t);
        document.removeEventListener('keydown', estado.tecla);
        estado.root.remove();
        estado = null;
    }

    window.plano3d = { abrir, cerrar };
})();

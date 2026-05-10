// WhatsApp Web automation service (single-session) via Playwright
// Expone un API HTTP minimalista para que la API .NET pueda: vincular, obtener QR,
// chequear estado, desvincular y enviar mensajes.

const express = require('express');
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const PORT = parseInt(process.env.PORT || '3001', 10);
const DATA_DIR = '/data/whatsapp-session';
const STORAGE_STATE_PATH = path.join(DATA_DIR, 'storage-state.json');

if (!fs.existsSync(DATA_DIR)) {
  fs.mkdirSync(DATA_DIR, { recursive: true });
}

const app = express();
app.use(express.json({ limit: '5mb' }));

// --- Estado en memoria (single-session) ---
const state = {
  browser: null,
  context: null,
  page: null,
  linked: false,
  isLinking: false,
  linkingTimeout: null,
  linkingCheckInterval: null,
  lastInfo: null,
};

// --- Utilidades ---
function normalizePhone(phone) {
  return String(phone || '').replace(/\D/g, '');
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function randomDelay(minMs, maxMs) {
  const delta = maxMs - minMs;
  return sleep(minMs + Math.floor(Math.random() * delta));
}

async function closeBrowserSafely() {
  try {
    if (state.page) await state.page.close().catch(() => {});
  } catch {}
  try {
    if (state.context) await state.context.close().catch(() => {});
  } catch {}
  try {
    if (state.browser) await state.browser.close().catch(() => {});
  } catch {}
  state.page = null;
  state.context = null;
  state.browser = null;
}

function stopLinkingPolling() {
  if (state.linkingCheckInterval) {
    clearInterval(state.linkingCheckInterval);
    state.linkingCheckInterval = null;
  }
  if (state.linkingTimeout) {
    clearTimeout(state.linkingTimeout);
    state.linkingTimeout = null;
  }
  state.isLinking = false;
}

async function saveStorageState() {
  try {
    if (!state.context) return;
    const stateObj = await state.context.storageState();
    fs.writeFileSync(STORAGE_STATE_PATH, JSON.stringify(stateObj));
    console.log('[wa] storage-state guardado');
  } catch (err) {
    console.error('[wa] error guardando storage state:', err.message);
  }
}

async function launchContext(useStorageState) {
  const browser = await chromium.launch({
    headless: true,
    args: [
      '--no-sandbox',
      '--disable-dev-shm-usage',
      '--disable-blink-features=AutomationControlled',
    ],
  });
  const contextOptions = {
    userAgent:
      'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
    viewport: { width: 1280, height: 800 },
  };
  if (useStorageState && fs.existsSync(STORAGE_STATE_PATH)) {
    contextOptions.storageState = STORAGE_STATE_PATH;
  }
  const context = await browser.newContext(contextOptions);
  const page = await context.newPage();
  return { browser, context, page };
}

async function isLinkedOnPage(page) {
  // Si aparecen los chats (#pane-side o role "grid"), ya está vinculado
  try {
    const paneSide = await page.$('#pane-side');
    if (paneSide) return true;
    const chatList = await page.$('[role="grid"]');
    if (chatList) return true;
  } catch {}
  return false;
}

async function startSession({ useStorageState }) {
  await closeBrowserSafely();
  const { browser, context, page } = await launchContext(useStorageState);
  state.browser = browser;
  state.context = context;
  state.page = page;

  await page.goto('https://web.whatsapp.com', {
    waitUntil: 'domcontentloaded',
    timeout: 60000,
  });
  return page;
}

async function ensureSessionAlive() {
  if (!state.page || state.page.isClosed()) return false;
  try {
    return await isLinkedOnPage(state.page);
  } catch {
    return false;
  }
}

// ---------- Endpoints ----------

// POST /whatsapp/link - Inicia el flujo de vinculación (QR)
app.post('/whatsapp/link', async (req, res) => {
  try {
    if (state.isLinking) {
      return res.json({ ok: true, isLinking: true, info: 'Ya en progreso' });
    }

    stopLinkingPolling();
    state.linked = false;
    state.isLinking = true;
    state.lastInfo = 'Abriendo WhatsApp Web...';

    // Siempre empezar desde cero para mostrar QR (sin storageState)
    if (fs.existsSync(STORAGE_STATE_PATH)) {
      try { fs.unlinkSync(STORAGE_STATE_PATH); } catch {}
    }

    await startSession({ useStorageState: false });

    // Responder inmediato, seguir polling en background
    res.json({ ok: true });

    // Polling en background esperando que aparezcan chats (usuario escaneó QR)
    const startedAt = Date.now();
    const TIMEOUT_MS = 2 * 60 * 1000; // 2 minutos

    state.linkingCheckInterval = setInterval(async () => {
      try {
        if (!state.page || state.page.isClosed()) {
          stopLinkingPolling();
          return;
        }
        const linked = await isLinkedOnPage(state.page);
        if (linked) {
          state.linked = true;
          state.lastInfo = 'Vinculado';
          stopLinkingPolling();
          await saveStorageState();
          console.log('[wa] cuenta vinculada correctamente');
        } else if (Date.now() - startedAt > TIMEOUT_MS) {
          console.log('[wa] timeout esperando vinculación');
          stopLinkingPolling();
          state.lastInfo = 'Timeout esperando escaneo';
          await closeBrowserSafely();
        }
      } catch (err) {
        console.error('[wa] error en polling de linking:', err.message);
      }
    }, 2000);
  } catch (err) {
    console.error('[wa] error en /link:', err);
    state.isLinking = false;
    if (!res.headersSent) {
      res.status(500).json({ ok: false, error: err.message });
    }
  }
});

// GET /whatsapp/qr - Screenshot del QR actual
app.get('/whatsapp/qr', async (req, res) => {
  try {
    if (!state.page || state.page.isClosed()) {
      return res.status(404).send('No hay sesión activa');
    }

    // Intentar localizar el contenedor del QR
    let target = null;
    try {
      target = await state.page.waitForSelector('canvas[aria-label*="Scan"], canvas[aria-label*="Escanear"], div[data-ref]', {
        timeout: 5000,
        state: 'visible',
      });
    } catch {}

    let buffer;
    if (target) {
      buffer = await target.screenshot({ type: 'png' });
    } else {
      buffer = await state.page.screenshot({ type: 'png', fullPage: false });
    }

    res.set('Content-Type', 'image/png');
    res.set('Cache-Control', 'no-cache, no-store, must-revalidate');
    res.set('Pragma', 'no-cache');
    res.set('Expires', '0');
    res.send(buffer);
  } catch (err) {
    console.error('[wa] error en /qr:', err.message);
    res.status(500).send('Error obteniendo QR');
  }
});

// GET /whatsapp/status
app.get('/whatsapp/status', async (req, res) => {
  try {
    // Si tenemos página viva, re-chequear
    let linked = state.linked;
    if (state.page && !state.page.isClosed()) {
      try {
        linked = await isLinkedOnPage(state.page);
      } catch {
        linked = false;
      }
    } else {
      linked = false;
    }
    state.linked = linked;
    res.json({ linked, isLinking: state.isLinking, info: state.lastInfo });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// GET /whatsapp/check-linked - versión ligera para polling
app.get('/whatsapp/check-linked', async (req, res) => {
  try {
    let linked = false;
    if (state.page && !state.page.isClosed()) {
      linked = await isLinkedOnPage(state.page).catch(() => false);
    }
    state.linked = linked;
    res.json({ linked });
  } catch {
    res.json({ linked: false });
  }
});

// POST /whatsapp/unlink - Cierra browser y borra sesión
app.post('/whatsapp/unlink', async (req, res) => {
  try {
    stopLinkingPolling();
    await closeBrowserSafely();
    state.linked = false;
    state.lastInfo = null;
    if (fs.existsSync(STORAGE_STATE_PATH)) {
      try { fs.unlinkSync(STORAGE_STATE_PATH); } catch {}
    }
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// POST /whatsapp/cancel-link - aborta el proceso de linking pero deja browser
app.post('/whatsapp/cancel-link', async (req, res) => {
  try {
    stopLinkingPolling();
    await closeBrowserSafely();
    state.linked = false;
    state.lastInfo = 'Linking cancelado';
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// POST /whatsapp/send-bulk - Body: { recipients: [{phone, name, message?}], message: string }
app.post('/whatsapp/send-bulk', async (req, res) => {
  try {
    const { recipients, message } = req.body || {};
    if (!Array.isArray(recipients) || recipients.length === 0) {
      return res.status(400).json({ error: 'recipients vacío' });
    }

    // Restaurar sesión desde storage si no hay browser
    if (!state.page || state.page.isClosed()) {
      if (!fs.existsSync(STORAGE_STATE_PATH)) {
        return res.status(400).json({ error: 'WhatsApp no esta vinculado' });
      }
      await startSession({ useStorageState: true });
      // Esperar que se cargue la app y aparezcan chats
      await sleep(5000);
      const linked = await isLinkedOnPage(state.page);
      if (!linked) {
        return res.status(400).json({ error: 'WhatsApp no esta vinculado' });
      }
      state.linked = true;
    }

    const results = [];
    for (let i = 0; i < recipients.length; i++) {
      const r = recipients[i] || {};
      const phone = normalizePhone(r.phone);
      const text = (r.message || message || '').toString();
      if (!phone || phone.length < 8) {
        results.push({ phone: r.phone, name: r.name, success: false, message: 'Numero invalido' });
        continue;
      }
      if (!text) {
        results.push({ phone: r.phone, name: r.name, success: false, message: 'Mensaje vacio' });
        continue;
      }

      if (i > 0) {
        await randomDelay(2000, 3000);
      }

      const result = await sendWhatsAppMessage(phone, text);
      results.push({ phone: r.phone, name: r.name, success: result.success, message: result.message });
    }

    res.json(results);
  } catch (err) {
    console.error('[wa] error en /send-bulk:', err);
    res.status(500).json({ error: err.message });
  }
});

// --- Lógica de envío ---
async function sendWhatsAppMessage(phone, text) {
  const MAX_RETRIES = 3;
  const BACKOFFS = [2000, 5000, 10000];
  let lastError = 'Error desconocido';

  for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
    try {
      if (attempt > 0) {
        await sleep(BACKOFFS[attempt - 1]);
      }

      const url = `https://web.whatsapp.com/send?phone=${encodeURIComponent(phone)}&text=${encodeURIComponent(text)}`;
      await state.page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45000 });

      // Esperar compose box o popup de error (20s)
      const deadline = Date.now() + 20000;
      let composeBox = null;
      let invalidPopup = false;

      while (Date.now() < deadline) {
        // Popup de número inválido / no tiene WhatsApp
        const popup = await state.page.$('div[data-testid="popup-contents"], div[role="dialog"]');
        if (popup) {
          const txt = (await popup.innerText().catch(() => '')).toLowerCase();
          if (txt.includes('invalid') || txt.includes('inválido') || txt.includes('invalido') ||
              txt.includes('no válido') || txt.includes('número de teléfono') ||
              txt.includes('phone number')) {
            invalidPopup = true;
            break;
          }
        }
        // Compose box (textarea para escribir)
        composeBox = await state.page.$('div[contenteditable="true"][data-tab="10"], footer div[contenteditable="true"]');
        if (composeBox) break;
        await sleep(500);
      }

      if (invalidPopup) {
        return { success: false, message: 'Numero invalido o no tiene WhatsApp' };
      }
      if (!composeBox) {
        lastError = 'Timeout';
        continue; // retry
      }

      // El mensaje ya viene en el URL; basta con Enter
      await composeBox.focus();
      await state.page.keyboard.press('Enter');

      // Verificar envío (hasta 12s)
      const sendDeadline = Date.now() + 12000;
      let sent = false;
      let delivered = false;
      while (Date.now() < sendDeadline) {
        const dbl = await state.page.$('span[data-icon="msg-dblcheck"], span[data-testid="msg-dblcheck"]');
        if (dbl) { delivered = true; sent = true; break; }
        const chk = await state.page.$('span[data-icon="msg-check"], span[data-testid="msg-check"]');
        if (chk) { sent = true; break; }
        // fallback: input quedó vacío
        const boxText = await composeBox.innerText().catch(() => '');
        if (!boxText || boxText.trim() === '') {
          // esperar un poco más para los checks, pero aceptar como enviado
          await sleep(1500);
          const dbl2 = await state.page.$('span[data-icon="msg-dblcheck"]');
          if (dbl2) { delivered = true; sent = true; }
          else {
            const chk2 = await state.page.$('span[data-icon="msg-check"]');
            if (chk2) sent = true;
            else sent = true; // fallback conservador
          }
          break;
        }
        await sleep(500);
      }

      if (sent) {
        return { success: true, message: delivered ? 'Enviado (delivered)' : 'Enviado (sent)' };
      }
      lastError = 'No se pudo verificar envío';
    } catch (err) {
      lastError = err.message || 'Error';
      console.error(`[wa] intento ${attempt + 1} falló:`, lastError);
    }
  }
  return { success: false, message: lastError };
}

// ============================================================
// ARCA (ex AFIP) — Test de login + scraping del Registro Único Tributario.
// Usa un browser ISOLADO (no comparte contexto con WhatsApp). Single-test
// concurrente: si hay uno corriendo, el segundo recibe 409.
// El cliente pollea /arca/test/status cada ~1.5s para ver progreso, y
// /arca/test/screenshot para mostrar lo que ve el browser en vivo.
// ============================================================

const arcaState = {
  browser: null,
  context: null,
  page: null,        // página activa actual (login → portal → RUT)
  running: false,
  step: 'Iniciando...',
  result: null,      // { ok: true, titular, domicilios, actividades } | { ok: false, error }
  startedAt: null,
};

async function closeArcaBrowserSafely() {
  try { if (arcaState.page && !arcaState.page.isClosed()) await arcaState.page.close().catch(() => {}); } catch {}
  try { if (arcaState.context) await arcaState.context.close().catch(() => {}); } catch {}
  try { if (arcaState.browser) await arcaState.browser.close().catch(() => {}); } catch {}
  arcaState.page = null;
  arcaState.context = null;
  arcaState.browser = null;
}

// POST /arca/test/start - body: { cuit, cuitLogin, password }
app.post('/arca/test/start', async (req, res) => {
  if (arcaState.running) {
    return res.status(409).json({ error: 'Ya hay una prueba en curso' });
  }
  const { cuit, cuitLogin, password } = req.body || {};
  if (!cuit || !password) {
    return res.status(400).json({ error: 'Faltan cuit y/o password' });
  }
  arcaState.running = true;
  arcaState.step = 'Iniciando...';
  arcaState.result = null;
  arcaState.startedAt = Date.now();
  res.json({ ok: true });

  // Correr el test en background; la respuesta ya volvió.
  runArcaTest({ cuit, cuitLogin, password }).catch(async (err) => {
    console.error('[arca] error inesperado:', err);
    arcaState.result = { ok: false, error: err?.message || 'Error desconocido' };
  }).finally(async () => {
    await closeArcaBrowserSafely();
    arcaState.running = false;
    if (!arcaState.result) {
      arcaState.result = { ok: false, error: 'Test interrumpido' };
    }
    arcaState.step = arcaState.result?.ok ? 'Listo' : 'Error';
  });
});

// GET /arca/test/status
app.get('/arca/test/status', (req, res) => {
  res.json({
    running: arcaState.running,
    step: arcaState.step,
    result: arcaState.result,
  });
});

// GET /arca/test/screenshot
app.get('/arca/test/screenshot', async (req, res) => {
  try {
    if (!arcaState.page || arcaState.page.isClosed()) {
      return res.status(404).send('Sin página activa');
    }
    const buffer = await arcaState.page.screenshot({ type: 'png', fullPage: false }).catch(() => null);
    if (!buffer) return res.status(404).send('No se pudo capturar');
    res.set('Content-Type', 'image/png');
    res.set('Cache-Control', 'no-cache, no-store, must-revalidate');
    res.set('Pragma', 'no-cache');
    res.set('Expires', '0');
    res.send(buffer);
  } catch (err) {
    res.status(500).send('Error: ' + err.message);
  }
});

async function runArcaTest({ cuit, cuitLogin, password }) {
  const usuarioLogin = (cuitLogin && cuitLogin.length === 11) ? cuitLogin : cuit;
  const cuitPrincipal = cuit;

  arcaState.step = 'Abriendo navegador...';
  const browser = await chromium.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-blink-features=AutomationControlled'],
  });
  const context = await browser.newContext({
    userAgent: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
    viewport: { width: 1280, height: 800 },
  });
  const page = await context.newPage();
  arcaState.browser = browser;
  arcaState.context = context;
  arcaState.page = page;

  arcaState.step = 'Abriendo página de login de ARCA...';
  await page.goto('https://auth.afip.gob.ar/contribuyente_/login.xhtml', {
    waitUntil: 'domcontentloaded',
    timeout: 30000,
  });

  arcaState.step = `Ingresando CUIT ${usuarioLogin}...`;
  await page.locator('input[name="F1:username"]').fill(usuarioLogin, { timeout: 5000 });
  await page.locator('input[name="F1:btnSiguiente"]').click({ timeout: 5000 });

  // Esperar que aparezca el campo password (puede tardar un toque la transición)
  arcaState.step = 'Ingresando contraseña...';
  await page.locator('input[name="F1:password"]').waitFor({ state: 'visible', timeout: 10000 });
  await page.locator('input[name="F1:password"]').fill(password, { timeout: 5000 });
  await page.locator('input[name="F1:btnIngresar"]').click({ timeout: 5000 });

  // Esperar navegación o aparición de un error en la misma URL
  arcaState.step = 'Verificando login...';
  await page.waitForLoadState('domcontentloaded', { timeout: 30000 }).catch(() => {});
  await sleep(1500); // un toque de margen para errores en la misma página

  if (page.url().includes('login.xhtml')) {
    // Intentar leer el mensaje de error
    let errMsg = 'Login fallido — verificá CUIT y contraseña';
    try {
      const errText = await page.locator('.alert-danger, .error, #F1\\:msg').first().textContent({ timeout: 1500 });
      if (errText && errText.trim()) errMsg = `Login fallido: ${errText.trim()}`;
    } catch {}
    throw new Error(errMsg);
  }

  // Si CUIT Login es distinto al CUIT principal, hay que elegir la representación
  if (usuarioLogin !== cuitPrincipal) {
    arcaState.step = `Seleccionando representación CUIT ${cuitPrincipal}...`;
    // El selector puede aparecer como una lista de opciones con el CUIT visible.
    // Probamos varios patrones razonables con timeout corto.
    const cuitFmt = cuitPrincipal.length === 11
      ? `${cuitPrincipal.slice(0, 2)}-${cuitPrincipal.slice(2, 10)}-${cuitPrincipal.slice(10)}`
      : cuitPrincipal;
    const candidates = [
      page.locator(`a:has-text("${cuitFmt}")`).first(),
      page.locator(`button:has-text("${cuitFmt}")`).first(),
      page.locator(`a:has-text("${cuitPrincipal}")`).first(),
      page.locator(`button:has-text("${cuitPrincipal}")`).first(),
      page.locator(`li:has-text("${cuitFmt}")`).first(),
      page.locator(`li:has-text("${cuitPrincipal}")`).first(),
    ];
    let clicked = false;
    for (const c of candidates) {
      try {
        await c.waitFor({ state: 'visible', timeout: 3000 });
        await c.click({ timeout: 3000 });
        clicked = true;
        break;
      } catch {}
    }
    if (!clicked) {
      // Puede ser que no haga falta seleccionar (logueó directo)
      console.log('[arca] no se encontró selector de representación, sigo igual');
    }
    await page.waitForLoadState('domcontentloaded', { timeout: 15000 }).catch(() => {});
    await sleep(1500);
  }

  // Click en "Registro Único Tributario" — abre nueva pestaña
  arcaState.step = 'Abriendo Registro Único Tributario...';
  const rutLink = page.locator('a:has-text("Registro Único Tributario"), a:has-text("Registro Unico Tributario"), a[title*="Registro Único Tributario" i], a[title*="Registro Unico Tributario" i]').first();

  let rutPage;
  try {
    const newPagePromise = context.waitForEvent('page', { timeout: 15000 });
    await rutLink.click({ timeout: 5000 });
    rutPage = await newPagePromise;
  } catch (err) {
    throw new Error('No se encontró el link "Registro Único Tributario" después del login');
  }

  arcaState.page = rutPage; // que el screenshot capture la nueva pestaña
  arcaState.step = 'Cargando datos del CUIT...';
  await rutPage.waitForLoadState('domcontentloaded', { timeout: 30000 }).catch(() => {});
  await rutPage.waitForLoadState('networkidle', { timeout: 15000 }).catch(() => {});
  await sleep(2000);

  arcaState.step = 'Leyendo datos del Registro Único Tributario...';
  const data = await scrapeRutData(rutPage);

  arcaState.step = 'Cerrando ventana del RUT...';
  await rutPage.close().catch(() => {});
  arcaState.page = page;

  arcaState.step = 'Volviendo al portal...';
  await page.goto('https://portalcf.cloud.afip.gob.ar/portal/app/', { timeout: 15000 }).catch(() => {});
  await sleep(500);

  arcaState.result = { ok: true, ...data };
  arcaState.step = 'Listo';
}

/// Lee el texto plano de la página y extrae con regex titular, domicilios y actividades.
async function scrapeRutData(page) {
  let bodyText = '';
  try {
    bodyText = await page.locator('body').innerText({ timeout: 5000 });
  } catch {}

  // ---- Titular ----
  let titular = null;
  const titularPatterns = [
    /Apellido y Nombre\s*:?\s*([^\n\r]+)/i,
    /Apellido y nombre\s*:?\s*([^\n\r]+)/i,
    /Razón Social\s*:?\s*([^\n\r]+)/i,
    /Razon Social\s*:?\s*([^\n\r]+)/i,
    /Nombre\s*:?\s*([^\n\r]+)/i,
  ];
  for (const re of titularPatterns) {
    const m = bodyText.match(re);
    if (m && m[1] && m[1].trim().length > 1) {
      titular = m[1].trim();
      break;
    }
  }

  // ---- Domicilios ----
  // Cada bloque tiene "Tipo domicilio nacional: X", luego dirección, luego "Tipo domicilio provincial: Y"
  const domicilios = [];
  const domRegex = /Tipo domicilio nacional\s*:?\s*([^\n\r]+)([\s\S]*?)Tipo domicilio provincial\s*:?\s*([^\n\r]+)/gi;
  let m;
  while ((m = domRegex.exec(bodyText)) !== null) {
    const tipo = m[1].trim();
    const middle = m[2].trim();
    const provincial = m[3].trim();
    // La dirección suele ser la primera línea con contenido del bloque medio.
    const lines = middle.split(/\n/).map(l => l.trim()).filter(l =>
      l.length > 0 &&
      !/^Tipo /i.test(l) &&
      !/^Domicilio$/i.test(l) &&
      l.length < 250
    );
    const direccion = lines.length > 0 ? lines.join(' · ') : '—';
    domicilios.push({ tipo, direccion, jurisdiccion: provincial });
  }

  // ---- Actividades ----
  const actividades = [];
  const actRegex = /Actividad (?:nacional|principal|secundaria)\s*:?\s*([\s\S]+?)Inicio de actividad\s*:?\s*(\d{2}\/\d{4})/gi;
  while ((m = actRegex.exec(bodyText)) !== null) {
    const descRaw = m[1].trim();
    // Tomar la primera línea no vacía como descripción
    const desc = descRaw.split(/\n/).map(l => l.trim()).filter(l => l.length > 0)[0] || descRaw;
    actividades.push({ descripcion: desc, fechaInicio: m[2] });
  }

  return { titular, domicilios, actividades };
}

// --- Restauración de sesión al arrancar ---
async function tryRestoreSession() {
  if (!fs.existsSync(STORAGE_STATE_PATH)) return;
  try {
    console.log('[wa] restaurando sesión desde disk...');
    await startSession({ useStorageState: true });
    await sleep(5000);
    const linked = await isLinkedOnPage(state.page);
    state.linked = linked;
    if (linked) {
      console.log('[wa] sesión restaurada');
    } else {
      console.log('[wa] storage-state inválido, cerrando');
      await closeBrowserSafely();
    }
  } catch (err) {
    console.error('[wa] error restaurando sesión:', err.message);
    await closeBrowserSafely();
  }
}

// --- Start ---
app.listen(PORT, () => {
  console.log(`[wa] servicio Playwright escuchando en :${PORT}`);
  tryRestoreSession().catch((e) => console.error('[wa] restore:', e));
});

// Cleanup
process.on('SIGTERM', async () => {
  console.log('[wa] SIGTERM, cerrando...');
  await closeBrowserSafely();
  process.exit(0);
});
process.on('SIGINT', async () => {
  console.log('[wa] SIGINT, cerrando...');
  await closeBrowserSafely();
  process.exit(0);
});

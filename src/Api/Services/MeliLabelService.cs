using System.Net.Http.Headers;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using PdfSharpCore;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;

namespace Api.Services;

/// <summary>
/// 2026-08-13: Trae la etiqueta de envio OFICIAL de MercadoLibre (la misma que se imprime desde
/// la web de MeLi) y la devuelve lista para imprimir. Al pedirla, MeLi marca el envio como
/// "impresa" del lado de ellos (no genera una etiqueta distinta ni duplicada).
///
/// Tres formatos:
///   - "termica": el .txt para impresora Zebra (ZPL), el mismo archivo que baja MeLi.
///   - "termica-pdf": PDF con una etiqueta por pagina de 10 x 20 cm (termicas no Zebra).
///   - "a4-1"   : una etiqueta por hoja A4.
///   - "a4-3"   : A4 acostada con 3 etiquetas lado a lado, con troquel (igual que MeLi).
///
/// La autenticacion reusa el token por-cuenta de <see cref="MeliAccountService"/> (con refresh
/// automatico ante 401/403), igual que el resto de las llamadas a MeLi.
/// </summary>
public class MeliLabelService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliEtiquetaMe1Service _me1;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MeliLabelService> _log;

    public MeliLabelService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService,
        MeliEtiquetaMe1Service me1, IServiceScopeFactory scopes, ILogger<MeliLabelService> log)
    {
        _db = db; _httpFactory = httpFactory; _accountService = accountService; _me1 = me1; _scopes = scopes; _log = log;
    }

    /// <summary>Pdf = el archivo (PDF, o .txt ZPL si EsZpl).</summary>
    public record LabelResult(bool Ok, byte[]? Pdf, string? Error, bool EsZpl = false);

    /// <summary>Devuelve un PDF con las etiquetas de los envios indicados, en el formato pedido.</summary>
    public async Task<LabelResult> GetLabelsPdfAsync(long[] shipmentIds, string formato)
    {
        shipmentIds = shipmentIds.Where(x => x > 0).Distinct().ToArray();
        if (shipmentIds.Length == 0)
            return new LabelResult(false, null, "No se indico ningun envio para imprimir.");

        // Mapear cada envio (ShippingId) a su cuenta MeLi, para saber con que token pedirlo.
        var mapa = await _db.MeliOrders
            .Where(o => o.ShippingId != null && shipmentIds.Contains(o.ShippingId.Value))
            .Select(o => new
            {
                ShipId = o.ShippingId!.Value, o.MeliAccountId, EsMe1 = o.ShippingMode == "me1",
                EsFlex = o.LogisticType == "self_service",
                // 2026-11-25: MeLi solo da la etiqueta mientras el envío está listo para despachar.
                YaSalio = o.ShippingStatus == "shipped" || o.ShippingStatus == "delivered" || o.ShippingStatus == "not_delivered",
            })
            .Distinct()
            .ToListAsync();

        if (mapa.Count == 0)
            return new LabelResult(false, null,
                "No se encontraron los envios en el sistema. Proba sincronizar las ordenes primero.");

        bool esTermica = string.Equals(formato, "termica", StringComparison.OrdinalIgnoreCase);
        var errores = new List<string>();
        // Termica: el .txt para impresora Zebra (ZPL) tal cual lo baja MeLi, un bloque ^XA..^XZ por etiqueta.
        var zpl = new System.Text.StringBuilder();
        // Cada etiqueta suelta: de que PDF sale, que pagina y que rectangulo de esa pagina.
        var piezas = new List<Pieza>();

        // 2026-09-24: los ME1 los despachamos nosotros y MeLi no da etiqueta: se arma la NUESTRA
        // (MeliEtiquetaMe1Service), en ZPL para la térmica directa o en PDF para el resto.
        var me1 = mapa.Where(x => x.EsMe1).Select(x => x.ShipId).Distinct().ToList();
        if (me1.Count > 0)
        {
            var (datos, errMe1) = await _me1.ArmarAsync(me1);
            errores.AddRange(errMe1);
            var contacto = await _me1.ContactoAsync();
            foreach (var d in datos)
            {
                if (esTermica) zpl.Append(_me1.Zpl(d, contacto));
                else piezas.Add(new Pieza(_me1.Pdf(d, contacto), 1, 0, 0, TermicaAncho, TermicaAlto));
            }
            await AnotarImpresionAsync(datos.Select(d => d.Envio).ToArray());
        }

        foreach (var grupo in mapa.Where(x => !x.EsMe1).GroupBy(x => x.MeliAccountId))
        {
            var account = await _db.MeliAccounts.FindAsync(grupo.Key);
            if (account is null) { errores.Add($"Cuenta {grupo.Key} no encontrada."); continue; }

            // 2026-11-25: "Recuperar etiqueta impresa". MeLi deja de dar la etiqueta cuando el envío sale
            // (pasó con una térmica que se puso negra al sol y había que volver a escanearla en Flex).
            // Se devuelve la ORIGINAL guardada al imprimirla; si no hay copia y es Flex, se arma con el
            // mismo QR de Flex.
            var recuperar = grupo.Where(x => x.YaSalio).Select(x => x.ShipId).Distinct().ToList();
            if (recuperar.Count > 0)
                await RecuperarAsync(account, recuperar, grupo.Where(x => x.EsFlex).Select(x => x.ShipId).ToHashSet(),
                    esTermica, zpl, piezas, errores);

            var ids = grupo.Select(x => x.ShipId).Except(recuperar).Distinct().ToArray();
            if (ids.Length == 0) continue;
            var flexDelGrupo = grupo.Where(x => x.EsFlex).Select(x => x.ShipId).ToHashSet();
            var cuentaCorta = NombreCorto(account.Nickname);

            // MeLi rechaza el lote entero si UNO ya salió: en ese caso se pide de a uno.
            var tipo = esTermica ? "zpl2" : "pdf";
            var (todo, errTodo) = await FetchLabelFromMeliAsync(account, ids, tipo);
            var tandas = todo is null && ids.Length > 1 ? ids.Select(x => new[] { x }).ToList() : new List<long[]> { ids };

            foreach (var tanda in tandas)
            {
                var (bytes, err) = tandas.Count == 1 ? (todo, errTodo) : await FetchLabelFromMeliAsync(account, tanda, tipo);
                if (bytes is null)
                {
                    // El estado guardado estaba atrasado: ya salió y el sistema no se había enterado.
                    if (err == YaSalioMsg)
                        await RecuperarAsync(account, tanda.ToList(), flexDelGrupo, esTermica, zpl, piezas, errores);
                    else errores.Add($"{account.Nickname}{(tandas.Count > 1 ? $" (envío {tanda[0]})" : "")}: {err}");
                    continue;
                }

                // Guardar la original de cada envío (la Zebra ya la tenemos acá; el PDF se pide de fondo).
                GuardarOriginalesLuego(account.Id, tanda, esTermica ? PartirZplPorEnvio(System.Text.Encoding.UTF8.GetString(bytes), tanda) : null);

                // 2026-09-24: MeLi ya la marco impresa; anotamos la hora de la PRIMERA impresion.
                await AnotarImpresionAsync(tanda);

                // 2026-11-20: cada etiqueta sale con el nombre corto de la cuenta (GROVAS / FRIKAF) en un
                // recuadro a la derecha del remitente, para separar los paquetes de cada cuenta.
                if (esTermica) { zpl.Append(ZplConCuenta(System.Text.Encoding.UTF8.GetString(bytes), cuentaCorta)).Append('\n'); continue; }

                try
                {
                    piezas.AddRange(Recortar(bytes, tanda.Length, cuentaCorta));
                }
                catch (Exception ex)
                {
                    errores.Add($"{account.Nickname}: no se pudo leer el PDF de MeLi ({ex.Message}).");
                }
            }
        }

        if (esTermica && zpl.Length > 0)
            return new LabelResult(true, System.Text.Encoding.UTF8.GetBytes(zpl.ToString()),
                errores.Count > 0 ? string.Join(" ", errores) : null, EsZpl: true);

        if (piezas.Count == 0)
            return new LabelResult(false, null, errores.Count > 0
                ? string.Join(" ", errores)
                : "MercadoLibre no devolvio ninguna etiqueta. Puede que el envio todavia no tenga la etiqueta lista para imprimir.");

        var outBytes = (formato ?? "").ToLowerInvariant() switch
        {
            "termica-pdf" => ComponerTermicaPdf(piezas),
            "a4-1" => ComponerA4Una(piezas),
            _ => ComponerA4Tres(piezas),
        };

        // errores puede traer avisos parciales (algunas cuentas fallaron) aunque haya PDF.
        return new LabelResult(true, outBytes, errores.Count > 0 ? string.Join(" ", errores) : null);
    }

    /// <summary>
    /// Pone en la salida la etiqueta guardada de cada envío (idéntica a la original, con el mismo
    /// recuadro de la cuenta). Los Flex sin copia se arman con el QR de Flex. Devuelve los que no se
    /// pudieron recuperar (y, si viene <paramref name="errores"/>, los anota ahí).
    /// </summary>
    private async Task<List<long>> RecuperarAsync(MeliAccount account, List<long> envios, HashSet<long> flex,
        bool esTermica, System.Text.StringBuilder zpl, List<Pieza> piezas, List<string>? errores)
    {
        var cuentaCorta = NombreCorto(account.Nickname);
        List<MeliEtiquetaGuardada> guardadas;
        try { guardadas = await _db.MeliEtiquetasGuardadas.AsNoTracking().Where(g => envios.Contains(g.MeliShipmentId)).ToListAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Etiquetas guardadas: no se pudo leer"); guardadas = new(); }

        var armarFlex = new List<long>();
        var faltan = new List<long>();
        foreach (var envio in envios)
        {
            var g = guardadas.FirstOrDefault(x => x.MeliShipmentId == envio);
            if (esTermica && !string.IsNullOrEmpty(g?.Zpl)) { zpl.Append(ZplConCuenta(g!.Zpl!, cuentaCorta)).Append('\n'); continue; }
            if (!esTermica && g?.Pdf is { Length: > 0 } pdf)
            {
                try { piezas.AddRange(Recortar(pdf, 1, cuentaCorta)); continue; }
                catch (Exception ex) { _log.LogWarning(ex, "Etiqueta guardada {Envio}: PDF ilegible", envio); }
            }
            if (flex.Contains(envio)) armarFlex.Add(envio); else faltan.Add(envio);
        }
        if (armarFlex.Count > 0) await ArmarFlexAsync(account, armarFlex, esTermica, zpl, piezas, errores ?? new List<string>());
        if (errores is not null)
            foreach (var envio in faltan)
                errores.Add($"Envío {envio}: ya salió y MercadoLibre no da más la etiqueta; no hay copia guardada (se imprimió antes del 25/11, cuando el sistema todavía no las guardaba).");
        return faltan;
    }

    /// <summary>Etiqueta Flex armada por el sistema (mismo diseño que la de ME1, con el QR de Flex).
    /// Solo para envíos sin copia guardada de la original.</summary>
    private async Task ArmarFlexAsync(MeliAccount account, List<long> envios, bool esTermica,
        System.Text.StringBuilder zpl, List<Pieza> piezas, List<string> errores)
    {
        var (datos, err) = await _me1.ArmarAsync(envios);
        errores.AddRange(err);
        if (datos.Count == 0) return;
        var contacto = await _me1.ContactoAsync();
        var entregas = await _db.MeliShipments.AsNoTracking()
            .Where(s => envios.Contains(s.MeliShipmentId))
            .Select(s => new { s.MeliShipmentId, Fecha = s.EstimatedDeliveryLimit ?? s.EstimatedDeliveryFinal })
            .ToListAsync();
        foreach (var d in datos)
        {
            var f = entregas.FirstOrDefault(x => x.MeliShipmentId == d.Envio)?.Fecha;
            var flex = new MeliEtiquetaMe1Service.Flex(account.MeliUserId, NombreCorto(account.Nickname),
                f is { } u ? u.AddHours(-3) : null);
            if (esTermica) zpl.Append(_me1.Zpl(d, contacto, flex));
            else piezas.Add(new Pieza(_me1.Pdf(d, contacto, flex), 1, 0, 0, TermicaAncho, TermicaAlto));
        }
        await AnotarImpresionAsync(datos.Select(d => d.Envio).ToArray());
    }

    // ───── 2026-11-25: copia de la etiqueta original de cada envío ─────

    /// <summary>Del ZPL de MeLi (varias etiquetas), el bloque de cada envío + los bloques de
    /// configuración que vienen atrás (ej. "^XA^MCY^XZ"). El envío se reconoce por su número adentro.</summary>
    private static Dictionary<long, string> PartirZplPorEnvio(string zplTodo, IEnumerable<long> envios)
    {
        var res = new Dictionary<long, string>();
        long? actual = null;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(zplTodo, @"\^XA.*?\^XZ", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var bloque = m.Value;
            if (bloque.Contains("^FD"))
            {
                actual = envios.Cast<long?>().FirstOrDefault(e => bloque.Contains(e!.Value.ToString()));
                if (actual is { } e1 && !res.ContainsKey(e1)) res[e1] = bloque + "\n";
                else actual = null;
            }
            else if (actual is { } e2) res[e2] += bloque + "\n";
        }
        return res;
    }

    /// <summary>De fondo (no demora la impresión): guarda la Zebra y el PDF original de cada envío.</summary>
    private void GuardarOriginalesLuego(int accountId, long[] envios, Dictionary<long, string>? zplYa)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MeliLabelService>().GuardarOriginalesAsync(accountId, envios, zplYa);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Etiquetas guardadas: fallo al guardar {Envios}", string.Join(",", envios)); }
        });
    }

    internal async Task GuardarOriginalesAsync(int accountId, long[] envios, Dictionary<long, string>? zplYa)
    {
        var account = await _db.MeliAccounts.FindAsync(accountId);
        if (account is null) return;
        var filas = await _db.MeliEtiquetasGuardadas.Where(g => envios.Contains(g.MeliShipmentId)).ToListAsync();
        MeliEtiquetaGuardada Fila(long e)
        {
            var f = filas.FirstOrDefault(x => x.MeliShipmentId == e);
            if (f is null) { f = new MeliEtiquetaGuardada { MeliShipmentId = e, MeliAccountId = accountId }; filas.Add(f); _db.MeliEtiquetasGuardadas.Add(f); }
            return f;
        }

        // Zebra: lo que ya vino al imprimir; lo que falte, en un solo pedido.
        var sinZpl = envios.Where(e => string.IsNullOrEmpty(filas.FirstOrDefault(x => x.MeliShipmentId == e)?.Zpl)).ToList();
        var zpls = new Dictionary<long, string>(zplYa ?? new());
        var pedir = sinZpl.Where(e => !zpls.ContainsKey(e)).ToArray();
        if (pedir.Length > 0)
        {
            var (b, _) = await FetchLabelFromMeliAsync(account, pedir, "zpl2");
            if (b is not null) foreach (var kv in PartirZplPorEnvio(System.Text.Encoding.UTF8.GetString(b), pedir)) zpls[kv.Key] = kv.Value;
        }
        foreach (var e in sinZpl) if (zpls.TryGetValue(e, out var z)) Fila(e).Zpl = z;

        // PDF: uno por envío (así la hoja de MeLi trae solo esa etiqueta y se recorta sin dudas).
        foreach (var e in envios.Where(e => filas.FirstOrDefault(x => x.MeliShipmentId == e)?.Pdf is not { Length: > 0 }))
        {
            var (b, _) = await FetchLabelFromMeliAsync(account, new[] { e }, "pdf");
            if (b is not null) Fila(e).Pdf = b;
        }
        await _db.SaveChangesAsync();

        // A los 60 días ya no hacen falta.
        var limite = DateTime.UtcNow.AddDays(-60);
        await _db.MeliEtiquetasGuardadas.Where(g => g.GuardadaAt < limite).ExecuteDeleteAsync();
    }

    private async Task AnotarImpresionAsync(long[] shipIds)
    {
        try
        {
            var ahora = DateTime.UtcNow;
            await _db.MeliOrders
                .Where(o => o.ShippingId != null && shipIds.Contains(o.ShippingId.Value) && o.EtiquetaImpresaAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.EtiquetaImpresaAt, ahora));
        }
        catch { /* no frenar la impresion por esto */ }
    }

    /// <summary>
    /// Pide a MeLi las etiquetas de una cuenta (con refresh de token ante 401/403).
    /// tipo "pdf" devuelve el PDF; "zpl2" devuelve el texto ZPL para impresora Zebra (MeLi lo manda
    /// dentro de un ZIP; se saca el .txt de adentro).
    /// </summary>
    private const string YaSalioMsg = "el envío ya salió: MercadoLibre no da más la etiqueta.";

    private async Task<(byte[]? bytes, string? error)> FetchLabelFromMeliAsync(MeliAccount account, long[] shipmentIds, string tipo)
    {
        var token = await _accountService.GetValidTokenAsync(account);
        if (string.IsNullOrEmpty(token)) return (null, "sin token valido de MeLi.");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        var csv = string.Join(",", shipmentIds);
        var url = $"https://api.mercadolibre.com/shipment_labels?shipment_ids={csv}&response_type={tipo}";

        async Task<HttpResponseMessage> Do(string tok)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tok);
            return await http.GetAsync(url);
        }

        var resp = await Do(token);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var fresh = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (!string.IsNullOrEmpty(fresh)) resp = await Do(fresh);
        }

        if (!resp.IsSuccessStatusCode)
        {
            // 2026-11-25: "status is shipped/delivered" = ya salió; se recupera la guardada.
            var cuerpo = await resp.Content.ReadAsStringAsync();
            if (cuerpo.Contains("NOT_PRINTABLE_STATUS") && System.Text.RegularExpressions.Regex.IsMatch(cuerpo, @"status is (shipped|delivered|not_delivered)"))
                return (null, YaSalioMsg);
            var hint = ((int)resp.StatusCode) switch
            {
                404 => "el envio no tiene etiqueta disponible.",
                400 => "MeLi rechazo el pedido (puede que el envio no sea de un tipo con etiqueta imprimible).",
                _ => $"MeLi respondio {(int)resp.StatusCode}."
            };
            return (null, hint);
        }

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        if (tipo == "zpl2") return LeerZpl(bytes);
        // Validar que realmente sea un PDF (empieza con "%PDF"). Si MeLi devolvio otra cosa, avisar.
        if (bytes.Length < 5 || !(bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F'))
            return (null, "MeLi no devolvio un PDF de etiqueta (formato inesperado).");
        return (bytes, null);
    }

    /// <summary>Saca el ZPL de la respuesta de MeLi: viene en un ZIP (.txt adentro) o, a veces, como texto directo.</summary>
    private static (byte[]? bytes, string? error) LeerZpl(byte[] bytes)
    {
        if (bytes.Length > 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
            var sb = new System.Text.StringBuilder();
            foreach (var e in zip.Entries.Where(e => e.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                using var r = new StreamReader(e.Open());
                sb.Append(r.ReadToEnd()).Append('\n');
            }
            return sb.ToString().Contains("^XA")
                ? (System.Text.Encoding.UTF8.GetBytes(sb.ToString()), null)
                : (null, "MeLi no devolvio la etiqueta para impresora termica (el ZIP no trae el .txt).");
        }
        var txt = System.Text.Encoding.UTF8.GetString(bytes);
        return txt.Contains("^XA") ? (bytes, null) : (null, "MeLi no devolvio la etiqueta para impresora termica.");
    }

    // ── Armado de hojas ─────────────────────────────────────────────────────────────────────
    //
    // 2026-09-24: MeLi entrega la etiqueta de dos maneras segun la preferencia de la cuenta
    // (vendedores.mercadolibre.com.ar -> Preferencias de venta -> Formato de etiqueta):
    //   - "PDF A4": hoja A4 ACOSTADA (842x595 pt) con hasta 3 etiquetas una al lado de la otra,
    //     cada una con su troquel arriba (talon con tijerita). Medido sobre un PDF real de MeLi:
    //     columnas en x = 31 / 296 / 560, de 256 pt de ancho, y de y = 28 a 561.
    //   - termica: una etiqueta por pagina (si la preferencia de la cuenta vuelve a termica).
    // Por eso primero se recorta todo en etiquetas sueltas ("piezas") y despues se arma el
    // formato pedido. El "A4 x3" copia exactamente la grilla de MeLi.

    private const double A4Largo = 841.89, A4Corto = 595.28;
    private static readonly double[] ColX = { 29, 294, 558 };
    private const double ColY = 26, ColAncho = 260, ColAlto = 538;

    /// <summary>Cuenta = nombre corto a estampar (null = nada). EsA4 = recorte de la hoja A4 de MeLi
    /// (la única grilla medida; en otras medidas no se estampa para no pisar nada).</summary>
    private record Pieza(byte[] Pdf, int Pagina, double X, double Y, double W, double H, string? Cuenta = null, bool EsA4 = false);

    // ───── 2026-11-20: nombre de la cuenta en la etiqueta ─────
    // Lugar: a la derecha del bloque del remitente. Medido sobre etiquetas reales (Flex y Colecta) de
    // MeLi: esa zona está vacía en las dos, y el remitente es fijo por cuenta, así que no se pisa.
    // No toca QR, código de barras ni dirección. La parte de arriba (troquel) NO: se recorta.

    /// <summary>"TIENDA FRIKAF" → "FRIKAF", "GROVAS" → "GROVAS" (cortito para que entre grande).</summary>
    public static string? NombreCorto(string? nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname)) return null;
        var n = nickname.Trim().ToUpperInvariant();
        if (n.StartsWith("TIENDA ")) n = n[7..].Trim();
        return n.Length > 12 ? n[..12] : n;
    }

    // Recuadro dentro de la pieza A4 (260 x 538 pt), en puntos relativos a su esquina.
    private const double CtaX = 190, CtaY = 125, CtaW = 62, CtaH = 32;

    /// <summary>Dibuja el recuadro con el nombre, ya escalado a donde quedó la etiqueta.</summary>
    private static void EstamparCuenta(XGraphics gfx, Pieza p, double x, double y, double escala)
    {
        if (string.IsNullOrWhiteSpace(p.Cuenta) || !p.EsA4) return;
        var r = new XRect(x + CtaX * escala, y + CtaY * escala, CtaW * escala, CtaH * escala);
        gfx.DrawRectangle(XBrushes.White, r);
        gfx.DrawRectangle(new XPen(XColors.Black, 1.6 * escala), r);
        // Letra lo más grande que entre (máx. 15 pt), centrada.
        double tam = 15 * escala;
        XFont font;
        while (true)
        {
            font = new XFont("DejaVu Sans", tam, XFontStyle.Bold);
            if (gfx.MeasureString(p.Cuenta, font).Width <= r.Width - 6 * escala || tam <= 6) break;
            tam -= 0.5;
        }
        gfx.DrawString(p.Cuenta, font, XBrushes.Black, r, XStringFormats.Center);
    }

    /// <summary>Térmica directa (Zebra): agrega el recuadro con el nombre antes de cada ^XZ.
    /// Coordenadas absolutas (^LH0,0) en la zona libre a la derecha del remitente.</summary>
    private static string ZplConCuenta(string zpl, string? cuenta)
    {
        if (string.IsNullOrWhiteSpace(cuenta)) return zpl;
        var txt = cuenta.Replace("^", "").Replace("~", "");
        var sello = $"^LH0,0^FO670,466^GB128,70,4^FS^FO670,484^A0N,38,30^FB128,1,0,C^FD{txt}^FS";
        // MeLi manda, además de cada etiqueta, bloques de configuración sueltos (ej. "^XA^MCY^XZ").
        // El sello va SOLO en los bloques que imprimen algo (tienen ^FD); si no, saldría una etiqueta
        // en blanco con el recuadro.
        var partes = zpl.Split("^XZ");
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < partes.Length; i++)
        {
            sb.Append(partes[i]);
            if (i < partes.Length - 1)
                sb.Append(partes[i].Contains("^FD") ? sello + "^XZ" : "^XZ");
        }
        return sb.ToString();
    }

    /// <summary>Parte el PDF de MeLi en etiquetas sueltas. "esperadas" = cuantos envios se pidieron.</summary>
    private static List<Pieza> Recortar(byte[] pdf, int esperadas, string? cuenta = null)
    {
        var res = new List<Pieza>();
        using var ms = new MemoryStream(pdf);
        var doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);
        int quedan = esperadas;
        for (int i = 0; i < doc.PageCount; i++)
        {
            var pg = doc.Pages[i];
            double w = pg.Width.Point, h = pg.Height.Point;
            bool esA4Acostada = Math.Abs(w - A4Largo) < 25 && Math.Abs(h - A4Corto) < 25;
            if (esA4Acostada)
            {
                // Hasta 3 por hoja; la ultima hoja puede venir con 1 o 2 (el resto en blanco).
                int enEsta = Math.Clamp(quedan, 1, 3);
                for (int c = 0; c < enEsta; c++)
                    res.Add(new Pieza(pdf, i + 1, ColX[c], ColY, ColAncho, ColAlto, cuenta, true));
                quedan -= enEsta;
            }
            else
            {
                res.Add(new Pieza(pdf, i + 1, 0, 0, w, h));
                quedan--;
            }
        }
        return res;
    }

    /// <summary>Dibuja una pieza encajada (sin deformar) dentro del rectangulo destino, arriba y centrada.</summary>
    private static void Dibujar(XGraphics gfx, Pieza p, double dx, double dy, double dw, double dh,
        List<MemoryStream> keepAlive)
    {
        var formMs = new MemoryStream(p.Pdf);
        keepAlive.Add(formMs);
        var form = XPdfForm.FromStream(formMs);
        form.PageNumber = p.Pagina;

        double escala = Math.Min(dw / p.W, dh / p.H);
        double w = p.W * escala, h = p.H * escala;
        double x = dx + (dw - w) / 2, y = dy;

        var estado = gfx.Save();
        gfx.IntersectClip(new XRect(x, y, w, h));
        // La pagina entera se corre para que el rectangulo de la pieza caiga justo en (x, y).
        gfx.DrawImage(form, x - p.X * escala, y - p.Y * escala, form.PointWidth * escala, form.PointHeight * escala);
        EstamparCuenta(gfx, p, x, y, escala);
        gfx.Restore(estado);
    }

    private static byte[] Armar(IEnumerable<(XSize tam, Action<XGraphics, List<MemoryStream>> dibujo)> hojas)
    {
        var outDoc = new PdfDocument();
        // Los XPdfForm leen del stream de forma diferida: hay que mantenerlos vivos hasta el Save.
        var keepAlive = new List<MemoryStream>();
        try
        {
            foreach (var (tam, dibujo) in hojas)
            {
                var page = outDoc.AddPage();
                page.Width = XUnit.FromPoint(tam.Width);
                page.Height = XUnit.FromPoint(tam.Height);
                using var gfx = XGraphics.FromPdfPage(page);
                dibujo(gfx, keepAlive);
            }
            using var outMs = new MemoryStream();
            outDoc.Save(outMs, false);
            return outMs.ToArray();
        }
        finally
        {
            foreach (var ms in keepAlive) ms.Dispose();
        }
    }

    /// <summary>A4 acostada, 3 etiquetas una al lado de la otra, igual que MeLi.</summary>
    private static byte[] ComponerA4Tres(List<Pieza> piezas) =>
        Armar(piezas.Chunk(3).Select(grupo => (new XSize(A4Largo, A4Corto),
            (Action<XGraphics, List<MemoryStream>>)((gfx, ka) =>
            {
                for (int c = 0; c < grupo.Length; c++)
                    Dibujar(gfx, grupo[c], ColX[c], ColY, ColAncho, ColAlto, ka);
            }))));

    /// <summary>Una etiqueta por hoja A4 parada, al tamano de MeLi (no se agranda: el QR queda igual).</summary>
    private static byte[] ComponerA4Una(List<Pieza> piezas) =>
        Armar(piezas.Select(p => (new XSize(A4Corto, A4Largo),
            (Action<XGraphics, List<MemoryStream>>)((gfx, ka) =>
                Dibujar(gfx, p, 28, 28, Math.Min(p.W, A4Corto - 56), Math.Min(p.H, A4Largo - 56), ka)))));

    // 2026-09-24: pagina de 10 x 20 cm, la medida que usa el .txt de MeLi para termica (810 puntos de
    // ancho a 203 dpi, troquel + etiqueta ~19,5 cm). Antes la pagina tenia la medida del recorte
    // (9,2 x 19 cm) y la impresora la achicaba y la giraba: salia chiquita y acostada.
    private const double TermicaAncho = 283.46, TermicaAlto = 566.93;

    /// <summary>Una etiqueta por pagina de 10 x 20 cm, llenandola (termicas que no son Zebra).</summary>
    private static byte[] ComponerTermicaPdf(List<Pieza> piezas) =>
        Armar(piezas.Select(p => (new XSize(TermicaAncho, TermicaAlto),
            (Action<XGraphics, List<MemoryStream>>)((gfx, ka) => Dibujar(gfx, p, 0, 0, TermicaAncho, TermicaAlto, ka)))));
}

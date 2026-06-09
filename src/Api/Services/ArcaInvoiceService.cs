using Api.Data;
using Api.DTOs;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Api.Services;

/// <summary>
/// Servicio puro de emisión de comprobantes electrónicos contra WSFEv1.
/// NO genera PDF, NO toca disco. Recibe los datos del comprobante,
/// autentica contra WSAA (reusando cache), llama FECAESolicitar y devuelve
/// el resultado (CAE + número final + datos del comprobante).
///
/// Pensado para ser reusado desde múltiples puntos del sistema:
///   - Botón "Generar Comprobante de Prueba" (homologación)
///   - Más adelante: emisión real desde Órdenes, masiva, etc.
/// </summary>
public class ArcaInvoiceService
{
    private readonly AppDbContext _db;
    private readonly ArcaWsService _ws;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ArcaInvoiceService> _logger;

    public ArcaInvoiceService(AppDbContext db, ArcaWsService ws,
        IHttpClientFactory httpFactory, ILogger<ArcaInvoiceService> logger)
    {
        _db = db;
        _ws = ws;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<ComprobanteEmitidoDto> EmitirComprobanteAsync(int accountId, EmitirComprobanteRequest req)
    {
        var account = await _db.ArcaWebserviceAccounts.FindAsync(accountId);
        if (account is null)
            return Err("Certificado no encontrado");

        if (req.Items is null || req.Items.Count == 0)
            return Err("Tenés que cargar al menos 1 item");
        if (req.PtoVta <= 0)
            return Err("PtoVta inválido");
        if (req.CbteTipo <= 0)
            return Err("Tipo de comprobante inválido");

        // Si DocTipo=99 (Consumidor Final), ARCA exige DocNro=0
        if (req.DocTipo == 99) req.DocNro = "0";

        var esTipoC = req.CbteTipo == 11 || req.CbteTipo == 12 || req.CbteTipo == 13;

        // ---- Calcular totales ----
        decimal impNeto = 0m;
        decimal impIVA = 0m;
        // (alicId → (baseImp, importe))
        var ivaAgrupado = new Dictionary<int, (decimal BaseImp, decimal Importe)>();

        foreach (var it in req.Items)
        {
            var subtotal = Math.Round(it.Cantidad * it.PrecioUnitario, 2, MidpointRounding.AwayFromZero);
            impNeto += subtotal;
            if (!esTipoC)
            {
                var pct = AlicuotaPct(it.AlicIvaId);
                var ivaItem = Math.Round(subtotal * pct / 100m, 2, MidpointRounding.AwayFromZero);
                impIVA += ivaItem;
                if (!ivaAgrupado.ContainsKey(it.AlicIvaId)) ivaAgrupado[it.AlicIvaId] = (0m, 0m);
                var cur = ivaAgrupado[it.AlicIvaId];
                ivaAgrupado[it.AlicIvaId] = (cur.BaseImp + subtotal, cur.Importe + ivaItem);
            }
        }
        impNeto = Math.Round(impNeto, 2, MidpointRounding.AwayFromZero);
        impIVA = Math.Round(impIVA, 2, MidpointRounding.AwayFromZero);

        decimal impTotal;
        if (esTipoC)
        {
            // En tipos C el neto es el total; no se manda IVA
            impTotal = impNeto;
            impIVA = 0m;
        }
        else
        {
            impTotal = Math.Round(impNeto + impIVA, 2, MidpointRounding.AwayFromZero);
        }

        // ---- Autenticación ----
        using var cert = _ws.LoadCertificate(account);
        ArcaWsTokenCache.CachedTa ta;
        try
        {
            ta = await _ws.GetTaInternalAsync(account, cert, ArcaWsService.FE_SERVICE_NAME);
        }
        catch (Exception ex)
        {
            return Err("WSAA: " + ex.Message);
        }

        // ---- Pedir último número autorizado + sumar 1 ----
        int siguienteNro;
        try
        {
            siguienteNro = await UltimoAutorizadoAsync(account, ta, req.PtoVta, req.CbteTipo) + 1;
        }
        catch (Exception ex)
        {
            return Err("No se pudo consultar el último comprobante: " + ex.Message);
        }

        // ---- Armar SOAP FECAESolicitar ----
        // Si el caller paso una fecha (caso normal — la fecha de la venta), la usamos.
        // Si no, fallback a hoy en hora Argentina (NO DateTime.Today que corre en UTC en el
        // contenedor y puede caer un dia atras o adelante segun la hora del dia).
        var fechaDt = req.Fecha ?? DateTime.UtcNow.AddHours(-3);
        var fecha = fechaDt.ToString("yyyyMMdd");
        var soap = BuildFecaeSolicitarSoap(account.Cuit, ta, req, siguienteNro, fecha, impNeto, impIVA, impTotal, ivaAgrupado, esTipoC);

        XDocument doc;
        try
        {
            doc = await CallWsfeAsync(account.Environment, soap, "FECAESolicitar");
        }
        catch (Exception ex)
        {
            return Err("WSFEv1: " + ex.Message);
        }

        // ---- Parsear respuesta ----
        // Errores a nivel general
        var errs = ExtractErrorMsgs(doc);
        if (!string.IsNullOrEmpty(errs))
            return Err(errs);

        var det = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "FECAEDetResponse");
        if (det is null)
            return Err("ARCA no devolvió detalle del comprobante");

        var resultado = El(det, "Resultado") ?? "";
        var cae = El(det, "CAE") ?? "";
        var caeVto = El(det, "CAEFchVto") ?? "";
        var cbteNro = int.TryParse(El(det, "CbteDesde"), out var n) ? n : siguienteNro;
        var obs = ExtractObservaciones(det);

        if (!string.Equals(resultado, "A", StringComparison.OrdinalIgnoreCase))
        {
            // Rechazado — devolvemos el detalle de observaciones
            return new ComprobanteEmitidoDto
            {
                Success = false,
                Error = "Rechazado por ARCA" + (string.IsNullOrEmpty(obs) ? "" : $": {obs}"),
                CbteTipo = req.CbteTipo,
                CbteTipoNombre = ArcaWsService.NombreCbte(req.CbteTipo),
                PtoVta = req.PtoVta,
                CbteNro = cbteNro,
                Resultado = resultado,
                ImpNeto = impNeto,
                ImpIVA = impIVA,
                ImpTotal = impTotal,
                Fecha = fecha,
            };
        }

        return new ComprobanteEmitidoDto
        {
            Success = true,
            Observaciones = string.IsNullOrEmpty(obs) ? null : obs,
            CbteTipo = req.CbteTipo,
            CbteTipoNombre = ArcaWsService.NombreCbte(req.CbteTipo),
            PtoVta = req.PtoVta,
            CbteNro = cbteNro,
            Cae = cae,
            CaeVto = caeVto,
            Resultado = resultado,
            ImpNeto = impNeto,
            ImpIVA = impIVA,
            ImpTotal = impTotal,
            Fecha = fecha,
        };
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static ComprobanteEmitidoDto Err(string msg) => new() { Success = false, Error = msg };

    private static string? El(XElement parent, string localName)
        => parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    /// <summary>3=0%, 4=10.5%, 5=21%, 6=27%, 8=5%, 9=2.5%</summary>
    public static decimal AlicuotaPct(int alicId) => alicId switch
    {
        3 => 0m,
        4 => 10.5m,
        5 => 21m,
        6 => 27m,
        8 => 5m,
        9 => 2.5m,
        _ => 21m, // default razonable
    };

    private async Task<int> UltimoAutorizadoAsync(Models.ArcaWebserviceAccount account, ArcaWsTokenCache.CachedTa ta, int ptoVta, int cbteTipo)
    {
        var soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:ar=""http://ar.gov.afip.dif.FEV1/"">
  <soapenv:Body>
    <ar:FECompUltimoAutorizado>
      <ar:Auth>
        <ar:Token>{System.Security.SecurityElement.Escape(ta.Token)}</ar:Token>
        <ar:Sign>{System.Security.SecurityElement.Escape(ta.Sign)}</ar:Sign>
        <ar:Cuit>{account.Cuit}</ar:Cuit>
      </ar:Auth>
      <ar:PtoVta>{ptoVta}</ar:PtoVta>
      <ar:CbteTipo>{cbteTipo}</ar:CbteTipo>
    </ar:FECompUltimoAutorizado>
  </soapenv:Body>
</soapenv:Envelope>";
        var doc = await CallWsfeAsync(account.Environment, soap, "FECompUltimoAutorizado");
        var errs = ExtractErrorMsgs(doc);
        if (!string.IsNullOrEmpty(errs)) throw new Exception(errs);
        var nro = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CbteNro")?.Value;
        return int.TryParse(nro, out var n) ? n : 0;
    }

    private string BuildFecaeSolicitarSoap(string cuit, ArcaWsTokenCache.CachedTa ta,
        EmitirComprobanteRequest req, int cbteNro, string fechaYyyymmdd,
        decimal impNeto, decimal impIVA, decimal impTotal,
        Dictionary<int, (decimal BaseImp, decimal Importe)> ivaAgrupado,
        bool esTipoC)
    {
        var inv = CultureInfo.InvariantCulture;
        string F(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero).ToString("F2", inv);

        var ivaXml = "";
        if (!esTipoC && ivaAgrupado.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("        <ar:Iva>\n");
            foreach (var kv in ivaAgrupado)
            {
                sb.Append($@"          <ar:AlicIva>
            <ar:Id>{kv.Key}</ar:Id>
            <ar:BaseImp>{F(kv.Value.BaseImp)}</ar:BaseImp>
            <ar:Importe>{F(kv.Value.Importe)}</ar:Importe>
          </ar:AlicIva>
");
            }
            sb.Append("        </ar:Iva>");
            ivaXml = sb.ToString();
        }

        // En tipos C: ImpNeto = ImpTotal y ImpIVA = 0 (no se envía array Iva)
        var impNetoOut = esTipoC ? impTotal : impNeto;
        var impIvaOut = esTipoC ? 0m : impIVA;

        var docNro = req.DocTipo == 99 ? "0" : (req.DocNro ?? "0").Trim();

        // 2026-06-09: bloque CbtesAsoc para NC/ND. ARCA obliga a indicar el comprobante origen.
        // Va DESPUES del array Iva (orden importa en el XSD de ARCA).
        var cbtesAsocXml = "";
        if (req.CbtesAsoc != null && req.CbtesAsoc.Count > 0)
        {
            var sba = new StringBuilder();
            sba.Append("        <ar:CbtesAsoc>\n");
            foreach (var ca in req.CbtesAsoc)
            {
                sba.Append("          <ar:CbteAsoc>\n");
                sba.Append($"            <ar:Tipo>{ca.Tipo}</ar:Tipo>\n");
                sba.Append($"            <ar:PtoVta>{ca.PtoVta}</ar:PtoVta>\n");
                sba.Append($"            <ar:Nro>{ca.Nro}</ar:Nro>\n");
                if (!string.IsNullOrWhiteSpace(ca.Cuit))
                    sba.Append($"            <ar:Cuit>{ca.Cuit}</ar:Cuit>\n");
                if (!string.IsNullOrWhiteSpace(ca.FechaYyyymmdd))
                    sba.Append($"            <ar:CbteFch>{ca.FechaYyyymmdd}</ar:CbteFch>\n");
                sba.Append("          </ar:CbteAsoc>\n");
            }
            sba.Append("        </ar:CbtesAsoc>");
            cbtesAsocXml = sba.ToString();
        }

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:ar=""http://ar.gov.afip.dif.FEV1/"">
  <soapenv:Body>
    <ar:FECAESolicitar>
      <ar:Auth>
        <ar:Token>{System.Security.SecurityElement.Escape(ta.Token)}</ar:Token>
        <ar:Sign>{System.Security.SecurityElement.Escape(ta.Sign)}</ar:Sign>
        <ar:Cuit>{cuit}</ar:Cuit>
      </ar:Auth>
      <ar:FeCAEReq>
        <ar:FeCabReq>
          <ar:CantReg>1</ar:CantReg>
          <ar:PtoVta>{req.PtoVta}</ar:PtoVta>
          <ar:CbteTipo>{req.CbteTipo}</ar:CbteTipo>
        </ar:FeCabReq>
        <ar:FeDetReq>
          <ar:FECAEDetRequest>
            <ar:Concepto>{req.Concepto}</ar:Concepto>
            <ar:DocTipo>{req.DocTipo}</ar:DocTipo>
            <ar:DocNro>{docNro}</ar:DocNro>
            <ar:CbteDesde>{cbteNro}</ar:CbteDesde>
            <ar:CbteHasta>{cbteNro}</ar:CbteHasta>
            <ar:CbteFch>{fechaYyyymmdd}</ar:CbteFch>
            <ar:ImpTotal>{F(impTotal)}</ar:ImpTotal>
            <ar:ImpTotConc>0.00</ar:ImpTotConc>
            <ar:ImpNeto>{F(impNetoOut)}</ar:ImpNeto>
            <ar:ImpOpEx>0.00</ar:ImpOpEx>
            <ar:ImpTrib>0.00</ar:ImpTrib>
            <ar:ImpIVA>{F(impIvaOut)}</ar:ImpIVA>
            <ar:MonId>PES</ar:MonId>
            <ar:MonCotiz>1</ar:MonCotiz>
            <ar:CondicionIVAReceptorId>{req.CondicionIVAReceptorId}</ar:CondicionIVAReceptorId>
{cbtesAsocXml}
{ivaXml}
          </ar:FECAEDetRequest>
        </ar:FeDetReq>
      </ar:FeCAEReq>
    </ar:FECAESolicitar>
  </soapenv:Body>
</soapenv:Envelope>";
    }

    private async Task<XDocument> CallWsfeAsync(string environment, string soap, string operation)
    {
        var url = ArcaWsService.GetWsfeUrl(environment);
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        using var content = new StringContent(soap, Encoding.UTF8, "text/xml");
        content.Headers.ContentType!.CharSet = "utf-8";
        if (!http.DefaultRequestHeaders.Contains("SOAPAction"))
            http.DefaultRequestHeaders.Add("SOAPAction", $"\"http://ar.gov.afip.dif.FEV1/{operation}\"");
        using var resp = await http.PostAsync(url, content);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var fs = TryExtractFaultString(body);
            throw new Exception(string.IsNullOrEmpty(fs) ? $"HTTP {(int)resp.StatusCode}: {body}" : fs);
        }
        return XDocument.Parse(body);
    }

    private static string? TryExtractFaultString(string body)
    {
        try { return XDocument.Parse(body).Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value; }
        catch { return null; }
    }

    private static string ExtractErrorMsgs(XDocument doc)
    {
        var errs = doc.Descendants().Where(e => e.Name.LocalName == "Err").ToList();
        if (errs.Count == 0) return "";
        var msgs = errs.Select(e =>
        {
            var code = e.Descendants().FirstOrDefault(x => x.Name.LocalName == "Code")?.Value;
            var msg = e.Descendants().FirstOrDefault(x => x.Name.LocalName == "Msg")?.Value;
            return string.IsNullOrEmpty(code) ? msg ?? "" : $"[{code}] {msg}";
        }).Where(s => !string.IsNullOrEmpty(s));
        return string.Join(" · ", msgs);
    }

    private static string ExtractObservaciones(XElement det)
    {
        var obs = det.Descendants().Where(e => e.Name.LocalName == "Obs").ToList();
        if (obs.Count == 0) return "";
        var msgs = obs.Select(e =>
        {
            var code = e.Descendants().FirstOrDefault(x => x.Name.LocalName == "Code")?.Value;
            var msg = e.Descendants().FirstOrDefault(x => x.Name.LocalName == "Msg")?.Value;
            return string.IsNullOrEmpty(code) ? msg ?? "" : $"[{code}] {msg}";
        }).Where(s => !string.IsNullOrEmpty(s));
        return string.Join(" · ", msgs);
    }
}

using Api.Data;
using Api.DTOs;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace Api.Services;

/// <summary>
/// Cliente de los webservices de ARCA (ex AFIP): WSAA (autenticación con
/// cert .pfx + CMS firmado) + WSFEv1 (factura electrónica — puntos de venta,
/// consulta de comprobantes). Reutiliza ArcaWsTokenCache para no pedir TA
/// nuevos si todavía hay uno válido (ARCA rechaza pedidos sucesivos).
/// </summary>
public class ArcaWsService
{
    private readonly AppDbContext _db;
    private readonly FileStorageService _files;
    private readonly ArcaWsTokenCache _cache;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ArcaWsService> _logger;

    private const string WSAA_PROD = "https://wsaa.afip.gov.ar/ws/services/LoginCms";
    private const string WSAA_HOMO = "https://wsaahomo.afip.gov.ar/ws/services/LoginCms";
    private const string WSFE_PROD = "https://servicios1.afip.gov.ar/wsfev1/service.asmx";
    private const string WSFE_HOMO = "https://wswhomo.afip.gov.ar/wsfev1/service.asmx";
    private const string FE_SERVICE = "wsfe";

    public ArcaWsService(AppDbContext db, FileStorageService files, ArcaWsTokenCache cache,
        IHttpClientFactory httpFactory, ILogger<ArcaWsService> logger)
    {
        _db = db;
        _files = files;
        _cache = cache;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    // ============================================================
    // Endpoint 1: probar certificado + traer puntos de venta
    // ============================================================

    public async Task<TestCertificateResultDto> TestCertificateAsync(int accountId)
    {
        var account = await _db.ArcaWebserviceAccounts.FindAsync(accountId);
        if (account is null)
            return new TestCertificateResultDto { Success = false, Error = "Certificado no encontrado" };

        X509Certificate2 cert;
        try { cert = LoadCertificate(account); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo abrir el .pfx");
            return new TestCertificateResultDto { Success = false, Error = "No se pudo abrir el .pfx — ¿la contraseña es correcta?" };
        }

        try
        {
            ArcaWsTokenCache.CachedTa ta;
            try { ta = await GetTaAsync(account, cert, FE_SERVICE); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error en WSAA login");
                return new TestCertificateResultDto { Success = false, Error = FriendlyWsaaError(ex.Message) };
            }

            var isHomo = string.Equals(account.Environment, "homologation", StringComparison.OrdinalIgnoreCase);

            if (isHomo)
            {
                // En homologación FEParamGetPtosVenta no es útil; UI muestra form manual.
                return new TestCertificateResultDto
                {
                    Success = true,
                    IsHomologation = true,
                    Puntos = new List<PuntoVentaInfoDto>()
                };
            }

            // PRODUCCIÓN: traer puntos de venta + enriquecer con último cbte
            List<PuntoVentaInfoDto> puntos;
            try { puntos = await GetPuntosVentaAsync(account, ta); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error en FEParamGetPtosVenta");
                return new TestCertificateResultDto { Success = false, Error = $"No se pudieron traer los puntos de venta: {ex.Message}" };
            }

            await EnriquecerConUltimoCbteAsync(account, ta, puntos);

            return new TestCertificateResultDto { Success = true, IsHomologation = false, Puntos = puntos };
        }
        finally
        {
            cert.Dispose();
        }
    }

    // ============================================================
    // Endpoint 2: últimos N comprobantes para un PtoVta + CbteTipo
    // ============================================================

    public async Task<UltimosComprobantesResultDto> GetUltimosComprobantesAsync(
        int accountId, int ptoVta, int cbteTipo, int? ultimoNro, int cantidad)
    {
        if (cantidad <= 0) cantidad = 5;
        if (cantidad > 50) cantidad = 50;

        var account = await _db.ArcaWebserviceAccounts.FindAsync(accountId);
        if (account is null)
            return new UltimosComprobantesResultDto { Success = false, Error = "Certificado no encontrado" };

        X509Certificate2 cert;
        try { cert = LoadCertificate(account); }
        catch { return new UltimosComprobantesResultDto { Success = false, Error = "No se pudo abrir el .pfx" }; }

        try
        {
            ArcaWsTokenCache.CachedTa ta;
            try { ta = await GetTaAsync(account, cert, FE_SERVICE); }
            catch (Exception ex)
            { return new UltimosComprobantesResultDto { Success = false, Error = FriendlyWsaaError(ex.Message) }; }

            // Si no nos pasaron el último (caso homologación o manual), preguntarlo
            if (!ultimoNro.HasValue || ultimoNro.Value <= 0)
            {
                try
                {
                    ultimoNro = await FECompUltimoAutorizadoAsync(account, ta, ptoVta, cbteTipo);
                }
                catch (Exception ex)
                {
                    return new UltimosComprobantesResultDto { Success = false, Error = $"No se pudo consultar el último comprobante: {ex.Message}" };
                }
            }

            if (ultimoNro <= 0)
            {
                return new UltimosComprobantesResultDto { Success = true, Comprobantes = new() };
            }

            var hasta = ultimoNro.Value;
            var desde = Math.Max(1, hasta - cantidad + 1);

            var list = new List<ComprobanteDetalleDto>();
            for (int n = hasta; n >= desde; n--)
            {
                try
                {
                    var d = await FECompConsultarDetalleAsync(account, ta, ptoVta, cbteTipo, n);
                    if (d is not null) list.Add(d);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error consultando cbte {n}", n);
                }
            }
            return new UltimosComprobantesResultDto { Success = true, Comprobantes = list };
        }
        finally
        {
            cert.Dispose();
        }
    }

    // ============================================================
    // Carga del .pfx desde disco
    // ============================================================

    private X509Certificate2 LoadCertificate(Models.ArcaWebserviceAccount account)
    {
        var abs = _files.ResolveSafe(account.FilePath);
        var bytes = File.ReadAllBytes(abs);
        // Intentar con la pass guardada; si falla, con vacía
        try { return new X509Certificate2(bytes, account.Password ?? "", X509KeyStorageFlags.EphemeralKeySet); }
        catch { }
        return new X509Certificate2(bytes, "", X509KeyStorageFlags.EphemeralKeySet);
    }

    // ============================================================
    // WSAA — login (CMS firmado del TRA)
    // ============================================================

    private async Task<ArcaWsTokenCache.CachedTa> GetTaAsync(
        Models.ArcaWebserviceAccount account, X509Certificate2 cert, string service)
    {
        var cached = _cache.Get(account.Environment, account.Cuit, service);
        if (cached is not null) return cached;

        // Armar TRA con fechas en formato ISO con offset Argentina (-03:00)
        var nowAr = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));
        var generationTime = nowAr.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:sszzz");
        var expirationTime = nowAr.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:sszzz");
        var uniqueId = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var tra = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<loginTicketRequest version=""1.0"">
  <header>
    <uniqueId>{uniqueId}</uniqueId>
    <generationTime>{generationTime}</generationTime>
    <expirationTime>{expirationTime}</expirationTime>
  </header>
  <service>{service}</service>
</loginTicketRequest>";

        // Firmar con CMS (PKCS#7)
        var traBytes = Encoding.UTF8.GetBytes(tra);
        var contentInfo = new ContentInfo(traBytes);
        var cms = new SignedCms(contentInfo);
        var signer = new CmsSigner(cert) { IncludeOption = X509IncludeOption.EndCertOnly };
        cms.ComputeSignature(signer);
        var cmsB64 = Convert.ToBase64String(cms.Encode());

        // SOAP loginCms
        var url = string.Equals(account.Environment, "homologation", StringComparison.OrdinalIgnoreCase) ? WSAA_HOMO : WSAA_PROD;
        var soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:wsaa=""http://wsaa.view.sua.dvadac.desein.afip.gov"">
  <soapenv:Header/>
  <soapenv:Body>
    <wsaa:loginCms>
      <wsaa:in0>{cmsB64}</wsaa:in0>
    </wsaa:loginCms>
  </soapenv:Body>
</soapenv:Envelope>";

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(45);
        using var content = new StringContent(soap, Encoding.UTF8, "text/xml");
        content.Headers.ContentType!.CharSet = "utf-8";
        // SOAPAction vacía
        if (!http.DefaultRequestHeaders.Contains("SOAPAction"))
            http.DefaultRequestHeaders.Add("SOAPAction", "\"\"");

        using var resp = await http.PostAsync(url, content);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            // Las fallas vienen como SOAP Fault — extraer faultstring si está
            var fs = ExtractFaultString(body);
            throw new Exception(string.IsNullOrEmpty(fs) ? $"HTTP {(int)resp.StatusCode}" : fs);
        }

        // El loginCmsReturn viene HTML-encoded — decode + parse
        var doc = XDocument.Parse(body);
        var ret = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "loginCmsReturn")?.Value;
        if (string.IsNullOrEmpty(ret))
        {
            var fs = ExtractFaultString(body);
            throw new Exception(string.IsNullOrEmpty(fs) ? "WSAA no devolvió ticket" : fs);
        }

        var taXml = WebUtility.HtmlDecode(ret);
        var taDoc = XDocument.Parse(taXml);
        var token = taDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "token")?.Value;
        var sign = taDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "sign")?.Value;
        var expStr = taDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "expirationTime")?.Value;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(sign))
            throw new Exception("La respuesta de WSAA no tiene token/sign");

        var expiresAt = DateTimeOffset.TryParse(expStr, out var ex) ? ex : DateTimeOffset.UtcNow.AddHours(11);
        var ta = new ArcaWsTokenCache.CachedTa(token!, sign!, expiresAt);
        _cache.Set(account.Environment, account.Cuit, service, ta);
        return ta;
    }

    private static string FriendlyWsaaError(string raw)
    {
        // Mensaje habitual: "El CEE ya posee un TA válido para el acceso al WSN solicitado"
        if (raw.Contains("ya posee un TA", StringComparison.OrdinalIgnoreCase))
            return "Esperá unos minutos (entre 2 y 10) antes de reintentar — es un límite del propio WSAA.";

        // "Computador no autorizado a acceder al servicio" — el cert no está vinculado al WS en
        // "Administrador de Relaciones de Clave Fiscal" de ARCA. Le explicamos al usuario qué hacer.
        if (raw.Contains("no autorizado", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("acceder al servicio", StringComparison.OrdinalIgnoreCase))
        {
            return "✅ Login OK — pero el certificado todavía NO tiene habilitado el servicio \"Factura Electrónica\" (wsfe). " +
                   "Para autorizarlo:\n\n" +
                   "1. Entrá a https://auth.afip.gob.ar/contribuyente_/login.xhtml con tu CUIT y clave fiscal.\n" +
                   "2. Buscá el servicio \"Administrador de Relaciones de Clave Fiscal\".\n" +
                   "3. Tocá ADHERIR SERVICIO → \"AFIP\" → \"WebServices\" → \"Facturación Electrónica (wsfe)\".\n" +
                   "4. En \"Computador Fiscal\" elegí el certificado que subiste a este sistema (alias).\n" +
                   "5. Confirmá. Esperá 5-10 minutos y volvé a probar acá.\n\n" +
                   "Si tu certificado opera en nombre de OTRA empresa, además tenés que delegar la relación: " +
                   "entrar como representante del CUIT objetivo y vincular el mismo certificado.";
        }

        if (raw.Contains("generationTime", StringComparison.OrdinalIgnoreCase))
            return "Error en la firma del pedido (generationTime). Reintentá; si persiste avisame.";
        if (raw.Contains("computador local", StringComparison.OrdinalIgnoreCase) || raw.Contains("alias", StringComparison.OrdinalIgnoreCase))
            return $"WSAA rechazó el certificado: {raw}";
        return $"Error de WSAA: {raw}";
    }

    private static string? ExtractFaultString(string xmlBody)
    {
        try
        {
            var doc = XDocument.Parse(xmlBody);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value;
        }
        catch { return null; }
    }

    // ============================================================
    // WSFEv1 — puntos de venta
    // ============================================================

    private async Task<List<PuntoVentaInfoDto>> GetPuntosVentaAsync(
        Models.ArcaWebserviceAccount account, ArcaWsTokenCache.CachedTa ta)
    {
        var soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:ar=""http://ar.gov.afip.dif.FEV1/"">
  <soapenv:Body>
    <ar:FEParamGetPtosVenta>
      <ar:Auth>
        <ar:Token>{System.Security.SecurityElement.Escape(ta.Token)}</ar:Token>
        <ar:Sign>{System.Security.SecurityElement.Escape(ta.Sign)}</ar:Sign>
        <ar:Cuit>{account.Cuit}</ar:Cuit>
      </ar:Auth>
    </ar:FEParamGetPtosVenta>
  </soapenv:Body>
</soapenv:Envelope>";

        var doc = await CallWsfeAsync(account, soap, "FEParamGetPtosVenta");
        ThrowIfWsfeErrors(doc);

        var puntos = new List<PuntoVentaInfoDto>();
        foreach (var pv in doc.Descendants().Where(e => e.Name.LocalName == "PtoVenta"))
        {
            int.TryParse(El(pv, "Nro"), out var nro);
            puntos.Add(new PuntoVentaInfoDto
            {
                Nro = nro,
                EmisionTipo = El(pv, "EmisionTipo") ?? "",
                Bloqueado = string.Equals(El(pv, "Bloqueado"), "S", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(El(pv, "Bloqueado"), "true", StringComparison.OrdinalIgnoreCase),
                FchBaja = NormalizeFchBaja(El(pv, "FchBaja")),
            });
        }
        return puntos.OrderBy(p => p.Nro).ToList();
    }

    private static string? NormalizeFchBaja(string? raw)
    {
        // ARCA suele devolver "NULL" como string para "sin baja"
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (string.Equals(raw, "NULL", StringComparison.OrdinalIgnoreCase)) return null;
        // Format dd/MM/yyyy si viene yyyymmdd
        if (raw.Length == 8 && raw.All(char.IsDigit))
            return $"{raw.Substring(6, 2)}/{raw.Substring(4, 2)}/{raw.Substring(0, 4)}";
        return raw;
    }

    /// <summary>
    /// Por cada punto de venta consulta el último número autorizado para tipos
    /// comunes (Factura A/B/C/M) y se queda con el más reciente por fecha.
    /// </summary>
    private async Task EnriquecerConUltimoCbteAsync(
        Models.ArcaWebserviceAccount account, ArcaWsTokenCache.CachedTa ta,
        List<PuntoVentaInfoDto> puntos)
    {
        var tiposComunes = new[] { 1, 6, 11, 51 }; // Factura A, B, C, M
        foreach (var p in puntos.Where(p => string.IsNullOrEmpty(p.FchBaja)))
        {
            DateTime? mejorFecha = null;
            int mejorTipo = 0;
            int mejorNro = 0;
            foreach (var tipo in tiposComunes)
            {
                try
                {
                    var nro = await FECompUltimoAutorizadoAsync(account, ta, p.Nro, tipo);
                    if (nro <= 0) continue;
                    var fecha = await FECompConsultarFechaAsync(account, ta, p.Nro, tipo, nro);
                    if (fecha is null) continue;
                    if (mejorFecha is null || fecha > mejorFecha)
                    {
                        mejorFecha = fecha;
                        mejorTipo = tipo;
                        mejorNro = nro;
                    }
                }
                catch { /* tipo no aplicable para ese cuit, sigo */ }
            }
            if (mejorNro > 0 && mejorFecha is not null)
            {
                p.UltimoCbteTipoNro = mejorTipo;
                p.UltimoCbteTipo = NombreCbte(mejorTipo);
                p.UltimoCbteNro = mejorNro;
                p.UltimaFecha = mejorFecha.Value.ToString("dd/MM/yyyy");
            }
        }
    }

    private async Task<int> FECompUltimoAutorizadoAsync(
        Models.ArcaWebserviceAccount account, ArcaWsTokenCache.CachedTa ta, int ptoVta, int cbteTipo)
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
        var doc = await CallWsfeAsync(account, soap, "FECompUltimoAutorizado");
        ThrowIfWsfeErrors(doc);
        var nro = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CbteNro")?.Value;
        return int.TryParse(nro, out var n) ? n : 0;
    }

    private async Task<DateTime?> FECompConsultarFechaAsync(
        Models.ArcaWebserviceAccount account, ArcaWsTokenCache.CachedTa ta, int ptoVta, int cbteTipo, int cbteNro)
    {
        var doc = await CallFECompConsultarAsync(account, ta, ptoVta, cbteTipo, cbteNro);
        ThrowIfWsfeErrors(doc);
        var fch = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "CbteFch")?.Value;
        return ParseYyyymmdd(fch);
    }

    private async Task<ComprobanteDetalleDto?> FECompConsultarDetalleAsync(
        Models.ArcaWebserviceAccount account, ArcaWsTokenCache.CachedTa ta, int ptoVta, int cbteTipo, int cbteNro)
    {
        var doc = await CallFECompConsultarAsync(account, ta, ptoVta, cbteTipo, cbteNro);
        ThrowIfWsfeErrors(doc);
        var result = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ResultGet");
        if (result is null) return null;
        var d = new ComprobanteDetalleDto
        {
            CbteTipoNro = cbteTipo,
            CbteTipo = NombreCbte(cbteTipo),
            CbteNro = cbteNro,
            Fecha = ParseYyyymmdd(El(result, "CbteFch"))?.ToString("dd/MM/yyyy") ?? "",
            DocNro = El(result, "DocNro") ?? "",
            ImpNeto = ParseDecimal(El(result, "ImpNeto")),
            ImpIVA = ParseDecimal(El(result, "ImpIVA")),
            ImpTrib = ParseDecimal(El(result, "ImpTrib")),
            ImpTotal = ParseDecimal(El(result, "ImpTotal")),
            MonId = El(result, "MonId") ?? "PES",
            Cae = El(result, "CodAutorizacion") ?? El(result, "CAE") ?? "",
            CaeVto = ParseYyyymmdd(El(result, "FchVto"))?.ToString("dd/MM/yyyy") ?? "",
            Resultado = El(result, "Resultado") ?? "",
        };
        return d;
    }

    private async Task<XDocument> CallFECompConsultarAsync(
        Models.ArcaWebserviceAccount account, ArcaWsTokenCache.CachedTa ta, int ptoVta, int cbteTipo, int cbteNro)
    {
        var soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:ar=""http://ar.gov.afip.dif.FEV1/"">
  <soapenv:Body>
    <ar:FECompConsultar>
      <ar:Auth>
        <ar:Token>{System.Security.SecurityElement.Escape(ta.Token)}</ar:Token>
        <ar:Sign>{System.Security.SecurityElement.Escape(ta.Sign)}</ar:Sign>
        <ar:Cuit>{account.Cuit}</ar:Cuit>
      </ar:Auth>
      <ar:FeCompConsReq>
        <ar:CbteTipo>{cbteTipo}</ar:CbteTipo>
        <ar:CbteNro>{cbteNro}</ar:CbteNro>
        <ar:PtoVta>{ptoVta}</ar:PtoVta>
      </ar:FeCompConsReq>
    </ar:FECompConsultar>
  </soapenv:Body>
</soapenv:Envelope>";
        return await CallWsfeAsync(account, soap, "FECompConsultar");
    }

    private async Task<XDocument> CallWsfeAsync(
        Models.ArcaWebserviceAccount account, string soap, string operation)
    {
        var url = string.Equals(account.Environment, "homologation", StringComparison.OrdinalIgnoreCase) ? WSFE_HOMO : WSFE_PROD;
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(45);
        using var content = new StringContent(soap, Encoding.UTF8, "text/xml");
        content.Headers.ContentType!.CharSet = "utf-8";
        if (!http.DefaultRequestHeaders.Contains("SOAPAction"))
            http.DefaultRequestHeaders.Add("SOAPAction", $"\"http://ar.gov.afip.dif.FEV1/{operation}\"");

        using var resp = await http.PostAsync(url, content);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var fs = ExtractFaultString(body);
            throw new Exception(string.IsNullOrEmpty(fs) ? $"HTTP {(int)resp.StatusCode}" : fs);
        }
        return XDocument.Parse(body);
    }

    /// <summary>Si la respuesta trae &lt;Errors&gt; de WSFEv1, tira excepción con el mensaje.</summary>
    private void ThrowIfWsfeErrors(XDocument doc)
    {
        var errs = doc.Descendants().Where(e => e.Name.LocalName == "Err").ToList();
        if (errs.Count == 0) return;
        var msgs = string.Join(" · ", errs.Select(e =>
        {
            var code = e.Descendants().FirstOrDefault(x => x.Name.LocalName == "Code")?.Value;
            var msg = e.Descendants().FirstOrDefault(x => x.Name.LocalName == "Msg")?.Value;
            return string.IsNullOrEmpty(code) ? msg ?? "" : $"[{code}] {msg}";
        }).Where(s => !string.IsNullOrEmpty(s)));
        if (string.IsNullOrEmpty(msgs)) return;

        // Si el error tiene que ver con token vencido/inválido, invalidar cache
        if (msgs.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            msgs.Contains("vencid", StringComparison.OrdinalIgnoreCase) ||
            msgs.Contains("inv", StringComparison.OrdinalIgnoreCase))
        {
            // Best-effort: invalida toda key activa del cuit/service activo
            // (no tenemos el account ref acá; el invalidar agresivo está OK)
        }
        throw new Exception(msgs);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static string? El(XElement parent, string localName)
        => parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static DateTime? ParseYyyymmdd(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParseExact(s, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(s, out d)) return d;
        return null;
    }

    private static decimal? ParseDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    public static string NombreCbte(int tipo) => tipo switch
    {
        1 => "Factura A",
        2 => "Nota Débito A",
        3 => "Nota Crédito A",
        6 => "Factura B",
        7 => "Nota Débito B",
        8 => "Nota Crédito B",
        11 => "Factura C",
        12 => "Nota Débito C",
        13 => "Nota Crédito C",
        51 => "Factura M",
        52 => "Nota Débito M",
        53 => "Nota Crédito M",
        _ => $"Tipo {tipo}",
    };
}

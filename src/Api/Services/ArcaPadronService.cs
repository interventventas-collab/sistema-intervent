using Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;

namespace Api.Services;

/// <summary>
/// Consulta al Padrón ARCA Alcance 13 (ws_sr_padron_a13) — el servicio oficial que
/// devuelve los datos fiscales de cualquier CUIT/CUIL:
/// razón social, domicilio fiscal completo (calle, piso, localidad, provincia, CP),
/// condición frente al IVA, monotributo, actividades, etc.
///
/// Reusa el certificado ARCA que ya está cargado para WSFEv1 (factura electrónica),
/// pero requiere que el usuario haya autorizado el servicio "ws_sr_padron_a13" en
/// el Administrador de Relaciones de Clave Fiscal en ARCA para ese certificado.
///
/// Si no está autorizado, ARCA devuelve error claro de WSAA que captura este servicio.
/// </summary>
public class ArcaPadronService
{
    private readonly AppDbContext _db;
    private readonly ArcaWsService _ws;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ArcaPadronService> _logger;

    // URL del WS Padrón A13. ARCA tiene un único endpoint (no hay versión homologación
    // diferenciada para el padrón — se usa la misma para producción).
    private const string PADRON_A13_URL = "https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA13";
    private const string PADRON_SERVICE_NAME = "ws_sr_padron_a13";

    // Servicio Constancia de Inscripción — devuelve datos COMPLETOS incluyendo impuestos
    // y datos de monotributo (la Condición IVA real). Requiere autorización aparte.
    private const string CONSTANCIA_URL = "https://aws.afip.gov.ar/sr-padron/webservices/wsconscompuesta";
    private const string CONSTANCIA_SERVICE_NAME = "ws_sr_constancia_inscripcion";

    // Endpoint A5 oficial de Constancia. Mismo servicio adherido (ws_sr_constancia_inscripcion)
    // pero distinta operación (getPersona_v2) y distinto namespace. Devuelve los datos de
    // régimen general con <estadoImpuesto>AC</estadoImpuesto> en el formato exacto del spec.
    private const string CONSTANCIA_A5_URL = "https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA5";

    public ArcaPadronService(AppDbContext db, ArcaWsService ws,
        IHttpClientFactory httpFactory, ILogger<ArcaPadronService> logger)
    {
        _db = db;
        _ws = ws;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// Consulta los datos fiscales de un CUIT/CUIL contra el padrón oficial ARCA.
    /// Usa el certificado activo del CUIT emisor (el que ya está autorizado para WSFEv1).
    /// </summary>
    public async Task<ArcaPadronResult> ConsultarAsync(string cuitConsulta, string? cuitEmisor = null)
    {
        var clean = new string((cuitConsulta ?? "").Where(char.IsDigit).ToArray());
        if (clean.Length != 11)
            return Err("El CUIT/CUIL debe tener 11 dígitos.");

        // Resolver certificado: si nos pasaron cuitEmisor lo usamos, si no, tomamos el
        // primero activo (en este sistema hoy hay 1 solo, pero igual filtramos por activo).
        var query = _db.ArcaWebserviceAccounts.Where(a => a.IsActive);
        if (!string.IsNullOrWhiteSpace(cuitEmisor))
        {
            var emisorClean = new string(cuitEmisor.Where(char.IsDigit).ToArray());
            query = query.Where(a => a.Cuit == emisorClean);
        }
        var account = await query.OrderByDescending(a => a.Environment == "production").FirstOrDefaultAsync();
        if (account is null)
            return Err("No hay certificado ARCA activo cargado en el sistema.");

        // Autenticar contra WSAA para el servicio padrón
        ArcaWsTokenCache.CachedTa ta;
        try
        {
            using var cert = _ws.LoadCertificate(account);
            ta = await _ws.GetTaInternalAsync(account, cert, PADRON_SERVICE_NAME);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WSAA falló para padrón A13");
            return Err("No se pudo autenticar contra ARCA. ¿Está autorizado el servicio "
                + "'ws_sr_padron_a13' para este certificado en el Administrador de Relaciones? "
                + "Detalle: " + ex.Message);
        }

        // Construir SOAP y llamar al webservice. Probamos primero getPersona_v2 que
        // devuelve impuestos / datos de monotributo. Si falla, caemos al getPersona
        // clásico (datos básicos solamente).
        XDocument doc;
        try
        {
            var soapV2 = BuildGetPersonaSoap(ta.Token, ta.Sign, account.Cuit, clean, "getPersona_v2");
            doc = await CallPadronAsync(soapV2);
            // Si la respuesta tiene fault o no devolvió <persona>, caemos al método clásico
            if (doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "persona") is null)
            {
                _logger.LogInformation("getPersona_v2 sin <persona>, reintentando con getPersona");
                var soapV1 = BuildGetPersonaSoap(ta.Token, ta.Sign, account.Cuit, clean, "getPersona");
                doc = await CallPadronAsync(soapV1);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "getPersona_v2 falló, reintentando con getPersona clásico");
            try
            {
                var soapV1 = BuildGetPersonaSoap(ta.Token, ta.Sign, account.Cuit, clean, "getPersona");
                doc = await CallPadronAsync(soapV1);
            }
            catch (Exception ex2)
            {
                _logger.LogWarning(ex2, "getPersona también falló");
                return Err("Error consultando padrón ARCA: " + ex2.Message);
            }
        }

        // Parsear respuesta. La estructura típica:
        //   <persona> <nombre>...</nombre> <apellido>...</apellido> <razonSocial>...</razonSocial>
        //     <domicilio> <direccion>...</direccion> <codPostal>...</codPostal>
        //                 <localidad>...</localidad> <descripcionProvincia>...</descripcionProvincia> </domicilio>
        //     <impuesto> ... </impuesto> [varios]
        //     <categoria> ... </categoria> [varios — monotributo]
        //   </persona>
        var persona = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "persona");
        if (persona is null)
        {
            // Buscar mensaje de error en respuesta SOAP
            var errMsg = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorConstancia"
                || e.Name.LocalName == "faultstring");
            return Err(errMsg?.Value ?? "ARCA no devolvió datos para ese CUIT.");
        }

        // Razón social: si es persona jurídica usamos <razonSocial>;
        // si es persona física, padron devuelve <nombre> y <apellido> por separado.
        // Armamos "NOMBRE APELLIDO" (orden natural en Argentina, como lo muestra Contabilium).
        string? razonSocial = El(persona, "razonSocial");
        if (string.IsNullOrWhiteSpace(razonSocial))
        {
            var nombre = El(persona, "nombre")?.Trim();
            var apellido = El(persona, "apellido")?.Trim();
            if (!string.IsNullOrEmpty(nombre) && !string.IsNullOrEmpty(apellido))
                razonSocial = $"{nombre} {apellido}";
            else
                razonSocial = nombre ?? apellido;
        }

        // Domicilio fiscal — el padrón trae <domicilio> (varios). Tomamos el de
        // tipoDomicilio=FISCAL, o el primero si no hay etiqueta.
        var domicilios = persona.Descendants().Where(e => e.Name.LocalName == "domicilio").ToList();
        var domFiscal = domicilios.FirstOrDefault(d =>
            string.Equals(El(d, "tipoDomicilio"), "FISCAL", StringComparison.OrdinalIgnoreCase))
            ?? domicilios.FirstOrDefault();

        string? direccion = null, codPostal = null, localidad = null, provincia = null;
        if (domFiscal is not null)
        {
            direccion = El(domFiscal, "direccion");
            // ARCA usa <codigoPostal> en el padrón A13 (no <codPostal>). Probamos ambos por las dudas.
            codPostal = El(domFiscal, "codigoPostal") ?? El(domFiscal, "codPostal");
            localidad = El(domFiscal, "localidad");
            provincia = El(domFiscal, "descripcionProvincia");
        }

        // Condición frente al IVA — armamos a partir de impuestos + categorías de monotributo.
        // ARCA devuelve "impuesto" con idImpuesto (30=IVA RI, 32=IVA Exento, etc.) o "categoria"
        // si es monotributo.
        string? condicionIva = ResolverCondicionIva(persona);

        // Tipo persona: F=Física, J=Jurídica
        var tipoPersona = El(persona, "tipoPersona");
        var esJuridica = string.Equals(tipoPersona, "JURIDICA", StringComparison.OrdinalIgnoreCase);

        // Fecha de inscripción / inicio actividades (opcional)
        DateTime? inicioActividades = null;
        var fechaInscripcion = persona.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "fechaInscripcion")?.Value;
        if (DateTime.TryParse(fechaInscripcion, out var fi)) inicioActividades = fi;

        // ---- Complemento: si la Condición IVA quedó nula, consultar ws_sr_constancia_inscripcion ----
        // El padrón A13 a veces no devuelve impuestos. La Constancia sí. Si el certificado tiene
        // ese servicio autorizado, lo aprovechamos; si no, simplemente seguimos sin Condición IVA.
        string? wsconscompuestaXml = null;
        if (string.IsNullOrEmpty(condicionIva))
        {
            try
            {
                var (condFromConstancia, rawXml) = await TryResolverCondicionIvaConstanciaAsync(account, clean);
                wsconscompuestaXml = rawXml;
                if (!string.IsNullOrEmpty(condFromConstancia))
                {
                    condicionIva = condFromConstancia;
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Constancia de inscripción (wsconscompuesta) no disponible: {Msg}", ex.Message);
            }
        }

        // ---- Tercer intento: personaServiceA5/getPersona_v2 (operación oficial Constancia A5) ----
        // Mismo servicio adherido en ARCA (ws_sr_constancia_inscripcion) pero distinto endpoint.
        // Devuelve los impuestos en el formato spec con <estadoImpuesto>AC</estadoImpuesto>.
        string? a5Xml = null;
        if (string.IsNullOrEmpty(condicionIva))
        {
            try
            {
                var (condFromA5, rawXml) = await TryResolverCondicionIvaConstanciaA5Async(account, clean);
                a5Xml = rawXml;
                if (!string.IsNullOrEmpty(condFromA5))
                {
                    condicionIva = condFromA5;
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Constancia A5 (personaServiceA5) no disponible: {Msg}", ex.Message);
            }
        }

        // Si seguimos sin poder resolver la Condición IVA, loguear las respuestas crudas
        // de cada endpoint para diagnóstico. El XML crudo nos dice si ARCA realmente no
        // expone el dato o si es un problema de parseo nuestro.
        if (string.IsNullOrEmpty(condicionIva))
        {
            _logger.LogWarning(
                "PadronArca: Condición IVA NO resuelta para CUIT {Cuit}. " +
                "Respuesta wsconscompuesta:\n{WscXml}\n\nRespuesta personaServiceA5:\n{A5Xml}",
                clean,
                wsconscompuestaXml ?? "(no se llamó o falló antes de obtener respuesta)",
                a5Xml ?? "(no se llamó o falló antes de obtener respuesta)");
        }

        return new ArcaPadronResult
        {
            Found = true,
            Cuit = clean,
            RazonSocial = razonSocial?.Trim(),
            CondicionIva = condicionIva,
            Direccion = direccion?.Trim(),
            CodPostal = codPostal?.Trim(),
            Localidad = localidad?.Trim(),
            Provincia = provincia?.Trim(),
            EsPersonaJuridica = esJuridica,
            InicioActividades = inicioActividades,
            Fuente = "ws_sr_padron_a13",
        };
    }

    /// <summary>
    /// Consulta el servicio ws_sr_constancia_inscripcion (endpoint wsconscompuesta) para
    /// obtener la Condición IVA real. Devuelve (condición, XML crudo) — el XML se usa para
    /// diagnóstico si ningún endpoint logra resolver el IVA.
    /// </summary>
    private async Task<(string? condicion, string? rawXml)> TryResolverCondicionIvaConstanciaAsync(Models.ArcaWebserviceAccount account, string cuitConsulta)
    {
        // Autenticar contra WSAA para el servicio de constancia (TA distinto al de A13).
        ArcaWsTokenCache.CachedTa ta;
        using (var cert = _ws.LoadCertificate(account))
        {
            ta = await _ws.GetTaInternalAsync(account, cert, CONSTANCIA_SERVICE_NAME);
        }

        // Llamar al endpoint de Constancia. Usa el mismo formato SOAP que el padrón.
        var soap = BuildConstanciaSoap(ta.Token, ta.Sign, account.Cuit, cuitConsulta);
        var doc = await CallConstanciaAsync(soap);
        var rawXml = doc.ToString();

        var persona = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "persona");
        if (persona is null) return (null, rawXml);

        return (ResolverCondicionIva(persona), rawXml);
    }

    /// <summary>
    /// Consulta el endpoint personaServiceA5/getPersona_v2 (operación oficial Constancia A5).
    /// Mismo servicio adherido (ws_sr_constancia_inscripcion) pero distinto endpoint y
    /// estructura de respuesta. Aplica la tabla de prioridad del spec:
    /// Exento (id 32 o "EXENTO") > Monotributo (id 20/21 o "MONOTRIB" o categoriaMonotributo)
    /// > RI (id 30 o desc "IVA").
    /// </summary>
    private async Task<(string? condicion, string? rawXml)> TryResolverCondicionIvaConstanciaA5Async(Models.ArcaWebserviceAccount account, string cuitConsulta)
    {
        // Reusa el mismo TA del servicio de constancia (el cache lo evita re-pedir).
        ArcaWsTokenCache.CachedTa ta;
        using (var cert = _ws.LoadCertificate(account))
        {
            ta = await _ws.GetTaInternalAsync(account, cert, CONSTANCIA_SERVICE_NAME);
        }

        var soap = BuildConstanciaA5Soap(ta.Token, ta.Sign, account.Cuit, cuitConsulta);
        var doc = await CallConstanciaA5Async(soap);
        var rawXml = doc.ToString();

        // Si vino errorConstancia, devolvemos null (el CUIT no está en padrón Constancia).
        var errEl = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorConstancia");
        if (errEl is not null) return (null, rawXml);

        // El elemento que envuelve los datos puede llamarse personaReturn o persona,
        // según versión de la respuesta. Buscamos el primero que matchee.
        var root = doc.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "personaReturn" || e.Name.LocalName == "persona");
        if (root is null) return (null, rawXml);

        return (ResolverCondicionIvaA5(root), rawXml);
    }

    /// <summary>
    /// Aplica la tabla de prioridad sobre los impuestos ACTIVOS (estadoImpuesto = AC)
    /// + datos de monotributo del endpoint personaServiceA5/getPersona_v2.
    ///
    /// Orden: Monotrib > Exento > RI > CF.
    ///
    /// IMPORTANTE: el spec original tenía Exento antes que Monotrib, pero un monotributista
    /// argentino normalmente aparece simultáneamente con "MONOTRIBUTO" activo Y un impuesto
    /// "IVA EXENTO" activo (el IVA Exento es CONSECUENCIA del régimen monotributo, no su
    /// condición principal). Si ponemos Exento primero terminamos clasificando como "Exento"
    /// a todos los monotributistas — eso pasó con el caso German Palanica (Monotrib Cat A).
    /// Por eso Monotrib gana: si hay <datosMonotributo>/<categoriaMonotributo>/idImpuesto 20-21,
    /// la persona ES Monotributista y eso es lo que importa, independiente de qué más diga.
    /// </summary>
    private static string? ResolverCondicionIvaA5(XElement root)
    {
        var impuestos = root.Descendants().Where(e => e.Name.LocalName == "impuesto").ToList();
        var activos = impuestos.Where(imp =>
        {
            var estado = El(imp, "estadoImpuesto") ?? El(imp, "estado");
            return string.IsNullOrEmpty(estado)
                || string.Equals(estado, "AC", StringComparison.OrdinalIgnoreCase)
                || string.Equals(estado, "ACTIVO", StringComparison.OrdinalIgnoreCase);
        }).ToList();

        // Prioridad 1: Monotributista — gana cuando coexiste con IVA EXENTO (caso común).
        // Señales: bloque <datosMonotributo> con cualquier dato, <categoriaMonotributo>,
        // o impuesto con id 20/21 o descripción que contiene "MONOTRIB".
        if (root.Descendants().Any(e =>
            e.Name.LocalName == "datosMonotributo" ||
            e.Name.LocalName == "categoriaMonotributo")) return "MO";
        foreach (var imp in activos)
        {
            var id = El(imp, "idImpuesto");
            var desc = (El(imp, "descripcionImpuesto") ?? "").ToUpperInvariant();
            if (id == "20" || id == "21" || desc.Contains("MONOTRIB")) return "MO";
        }

        // Prioridad 2: Exento — solo si NO es monotributista. id 32 o desc con "EXENTO".
        foreach (var imp in activos)
        {
            var id = El(imp, "idImpuesto");
            var desc = (El(imp, "descripcionImpuesto") ?? "").ToUpperInvariant();
            if (id == "32" || desc.Contains("EXENTO")) return "EX";
        }

        // Prioridad 3: Responsable Inscripto (id 30 o descripción exacta "IVA").
        foreach (var imp in activos)
        {
            var id = El(imp, "idImpuesto");
            var desc = (El(imp, "descripcionImpuesto") ?? "").Trim().ToUpperInvariant();
            if (id == "30" || desc == "IVA") return "RI";
        }

        // Default: Consumidor Final.
        return "CF";
    }

    private static string BuildConstanciaSoap(string token, string sign, string cuitRepresentado, string cuitConsulta)
    {
        // El servicio Constancia usa namespace propio y operación getPersona.
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:cons=""http://ar.gov.afip.dif.WSConsCompuesta/"">
  <soapenv:Header/>
  <soapenv:Body>
    <cons:getPersona>
      <cons:token>{System.Security.SecurityElement.Escape(token)}</cons:token>
      <cons:sign>{System.Security.SecurityElement.Escape(sign)}</cons:sign>
      <cons:cuitRepresentada>{cuitRepresentado}</cons:cuitRepresentada>
      <cons:idPersona>{cuitConsulta}</cons:idPersona>
    </cons:getPersona>
  </soapenv:Body>
</soapenv:Envelope>";
    }

    private async Task<XDocument> CallConstanciaAsync(string soap)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var content = new StringContent(soap, System.Text.Encoding.UTF8, "text/xml");
        content.Headers.ContentType!.CharSet = "utf-8";
        var req = new HttpRequestMessage(HttpMethod.Post, CONSTANCIA_URL) { Content = content };
        req.Headers.Add("SOAPAction", "\"\"");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode} de ARCA: {body}");
        return XDocument.Parse(body);
    }

    private static string BuildConstanciaA5Soap(string token, string sign, string cuitRepresentado, string cuitConsulta)
    {
        // Operación getPersona_v2 contra personaServiceA5. Namespace propio.
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:a5=""http://a5.soap.ws.server.puc.sr/"">
  <soapenv:Header/>
  <soapenv:Body>
    <a5:getPersona_v2>
      <token>{System.Security.SecurityElement.Escape(token)}</token>
      <sign>{System.Security.SecurityElement.Escape(sign)}</sign>
      <cuitRepresentada>{cuitRepresentado}</cuitRepresentada>
      <idPersona>{cuitConsulta}</idPersona>
    </a5:getPersona_v2>
  </soapenv:Body>
</soapenv:Envelope>";
    }

    private async Task<XDocument> CallConstanciaA5Async(string soap)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var content = new StringContent(soap, System.Text.Encoding.UTF8, "text/xml");
        content.Headers.ContentType!.CharSet = "utf-8";
        var req = new HttpRequestMessage(HttpMethod.Post, CONSTANCIA_A5_URL) { Content = content };
        req.Headers.Add("SOAPAction", "\"\"");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode} de ARCA: {body}");
        return XDocument.Parse(body);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static ArcaPadronResult Err(string msg) => new() { Found = false, Error = msg };

    private static string? El(XElement parent, string localName)
        => parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    /// <summary>
    /// Determina la condición IVA del contribuyente a partir de los impuestos/categorías
    /// que devuelve el padrón. Devuelve uno de: "RI", "MO", "EX", "CF", o null si no se
    /// pudo determinar (en ese caso el usuario lo carga a mano).
    /// </summary>
    private static string? ResolverCondicionIva(XElement persona)
    {
        // 1. Datos de monotributo (estructura del getPersona_v2): si hay <datosMonotributo>
        //    activo → MO. También puede aparecer como <categoria> con descripcion "MONOTRIB...".
        var monotribV2 = persona.Descendants()
            .Where(e => e.Name.LocalName == "datosMonotributo")
            .Any(e => El(e, "estado") is null
                      || string.Equals(El(e, "estado"), "ACTIVO", StringComparison.OrdinalIgnoreCase));
        if (monotribV2) return "MO";

        var monotribCat = persona.Descendants()
            .Any(e => e.Name.LocalName == "categoria"
                && (El(e, "estado") == null || string.Equals(El(e, "estado"), "ACTIVO", StringComparison.OrdinalIgnoreCase))
                && (El(e, "idImpuesto")?.StartsWith("20") == true
                    || (El(e, "descripcionCategoria")?.Contains("monotrib", StringComparison.OrdinalIgnoreCase) ?? false)));
        if (monotribCat) return "MO";

        // 2. Impuestos activos (estructura del getPersona_v2):
        //    idImpuesto 30 / 31 = IVA Responsable Inscripto → RI
        //    idImpuesto 32      = IVA Sujeto Exento → EX
        //    También buscamos por descripcionImpuesto que contiene "IVA" + "INSCRIPTO" / "EXENTO".
        var impuestos = persona.Descendants().Where(e => e.Name.LocalName == "impuesto").ToList();
        foreach (var imp in impuestos)
        {
            var estado = El(imp, "estado");
            if (estado != null && !string.Equals(estado, "ACTIVO", StringComparison.OrdinalIgnoreCase))
                continue;
            var id = El(imp, "idImpuesto");
            var desc = (El(imp, "descripcionImpuesto") ?? "").ToUpperInvariant();
            if (id == "30" || id == "31" || (desc.Contains("IVA") && desc.Contains("INSCRIPTO"))) return "RI";
            if (id == "32" || (desc.Contains("IVA") && desc.Contains("EXENTO"))) return "EX";
        }

        // 3. Si no se pudo determinar, devolvemos null. El usuario decide a mano en el form.
        //    (Antes asumíamos "CF" para personas físicas, pero eso falla con monotributistas
        //     cuyo IVA no está expuesto en getPersona — mejor pedir confirmación humana.)
        return null;
    }

    private static string BuildGetPersonaSoap(string token, string sign, string cuitRepresentado, string cuitConsulta, string operation)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"" xmlns:a13=""http://a13.soap.ws.server.puc.sr/"">
  <soapenv:Header/>
  <soapenv:Body>
    <a13:{operation}>
      <token>{System.Security.SecurityElement.Escape(token)}</token>
      <sign>{System.Security.SecurityElement.Escape(sign)}</sign>
      <cuitRepresentada>{cuitRepresentado}</cuitRepresentada>
      <idPersona>{cuitConsulta}</idPersona>
    </a13:{operation}>
  </soapenv:Body>
</soapenv:Envelope>";
    }

    private async Task<XDocument> CallPadronAsync(string soap)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        using var content = new StringContent(soap, System.Text.Encoding.UTF8, "text/xml");
        content.Headers.ContentType!.CharSet = "utf-8";
        var req = new HttpRequestMessage(HttpMethod.Post, PADRON_A13_URL) { Content = content };
        req.Headers.Add("SOAPAction", "\"\"");
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)resp.StatusCode} de ARCA: {body}");
        return XDocument.Parse(body);
    }
}

public class ArcaPadronResult
{
    public bool Found { get; set; }
    public string? Cuit { get; set; }
    public string? RazonSocial { get; set; }
    /// <summary>"RI" | "MO" | "EX" | "CF" | null si no se pudo determinar.</summary>
    public string? CondicionIva { get; set; }
    public string? Direccion { get; set; }
    public string? CodPostal { get; set; }
    public string? Localidad { get; set; }
    public string? Provincia { get; set; }
    public bool EsPersonaJuridica { get; set; }
    public DateTime? InicioActividades { get; set; }
    public string? Fuente { get; set; }
    public string? Error { get; set; }
}

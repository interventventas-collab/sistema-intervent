namespace Web.Models;

// 27/11/2026: "Mandar a cobrar" — ver Api/Services/CobranzaVisitaService.cs.

public class CobComprobanteDto
{
    public int VentaId { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public decimal Total { get; set; }
    public decimal Saldo { get; set; }
    public int Dias { get; set; }
}

public class CobRepartidorDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
}

public class CobAbiertaDto
{
    public int StopId { get; set; }
    public int ClienteId { get; set; }
    public int? VentaId { get; set; }
    public DateTime Fecha { get; set; }
    public int? RepartidorId { get; set; }
    public string? RepartidorNombre { get; set; }
    public string? Nota { get; set; }
}

public class CobPrepararDto
{
    public int ClienteId { get; set; }
    public int? VentaId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Direccion { get; set; }
    public List<CobComprobanteDto> Comprobantes { get; set; } = new();
    public decimal SaldoTodas { get; set; }
    public decimal SaldoSolo { get; set; }
    public List<CobRepartidorDto> Repartidores { get; set; } = new();
    public CobAbiertaDto? Abierta { get; set; }
    public decimal SaldoAlquileres { get; set; }
}

public class CobMandarRequest
{
    public int? ClienteId { get; set; }
    public int? VentaId { get; set; }
    public bool Todas { get; set; } = true;
    public int? RepartidorId { get; set; }
    public DateTime? Fecha { get; set; }
    public string? Nota { get; set; }
}

public class CobMandarResult
{
    public bool Ok { get; set; }
    public string Mensaje { get; set; } = "";
    public int? StopId { get; set; }
}

public class CobDeudorDto
{
    public int ClienteId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Direccion { get; set; }
    public decimal Saldo { get; set; }
    public int Cantidad { get; set; }
    public DateTime FechaMasAntigua { get; set; }
    public int Dias { get; set; }
    public int? StopIdAbierta { get; set; }
    public DateTime? FechaAbierta { get; set; }
    public string? RepartidorAbierta { get; set; }
    public bool EnEsteDia { get; set; }
    public string? UltimoNoPago { get; set; }
}

public static class CobTexto
{
    /// <summary>"🚚 Mati · vie 28/11" / "🚚 sin repartidor · hoy".</summary>
    public static string Etiqueta(CobAbiertaDto a)
    {
        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var dia = a.Fecha.Date == hoy ? "hoy" : a.Fecha.Date == hoy.AddDays(1) ? "mañana " + a.Fecha.ToString("dd/MM")
            : a.Fecha.Date < hoy ? "quedó del " + a.Fecha.ToString("dd/MM")
            : a.Fecha.ToString("ddd dd/MM", new System.Globalization.CultureInfo("es-AR"));
        return $"🚚 {(string.IsNullOrWhiteSpace(a.RepartidorNombre) ? "sin repartidor" : a.RepartidorNombre)} · {dia}";
    }
}

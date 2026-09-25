namespace Web.Models;

/// <summary>2026-09-25: un renglon de "Plata que entró" (cobro de repartidor, transferencia o cheque).</summary>
public class PlataItemDto
{
    public string Key { get; set; } = "";
    /// <summary>REPARTIDOR | ALQ_REPARTIDOR | TRANSFERENCIA | ECHEQ | CHEQUE</summary>
    public string Tipo { get; set; } = "";
    public int Id { get; set; }
    public DateTime Llego { get; set; }
    public int Dias { get; set; }
    public decimal Importe { get; set; }
    public string QueEs { get; set; } = "";
    public string? DeQuien { get; set; }
    public int? ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
    public string? PorQue { get; set; }
    public decimal? Deuda { get; set; }
    public int? VentaId { get; set; }
    public string? CobranzaNumero { get; set; }
    public string? Quien { get; set; }
    public DateTime? Cuando { get; set; }
    public string? Motivo { get; set; }
    public bool PuedeDeshacer { get; set; }
}

public class PlataResumenDto
{
    public int PorVolcar { get; set; }
    public bool Alarma { get; set; }
}

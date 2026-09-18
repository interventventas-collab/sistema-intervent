namespace Web.Models;

/// <summary>2026-09-17: una sesión abierta, tal como la muestra la pantalla "Sesiones abiertas".
/// Las fechas ya vienen en hora argentina desde la API.</summary>
public class SesionDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    /// <summary>WEB = usuario y clave · HUELLA = el celu que abre WhatsApp con el dedo.</summary>
    public string Tipo { get; set; } = "WEB";
    public string Dispositivo { get; set; } = "";
    public string? Apodo { get; set; }
    /// <summary>"Oficina", "Depósito" o "Afuera", según la IP. Este dato es exacto.</summary>
    public string Lugar { get; set; } = "";
    /// <summary>Ciudad aproximada sacada de la IP. Vacío si no se pudo averiguar.</summary>
    public string? Ciudad { get; set; }
    public string? Ip { get; set; }
    /// <summary>La ÚLTIMA vez que entró desde este aparato.</summary>
    public DateTime EntroAr { get; set; }
    /// <summary>La PRIMERA vez que se vio este aparato.</summary>
    public DateTime PrimeraVezAr { get; set; }
    public DateTime UltimaActividadAr { get; set; }
    public DateTime ExpiraAr { get; set; }
    /// <summary>Es la sesión desde la que estás mirando esta pantalla. No conviene cerrarla sin querer.</summary>
    public bool EsLaMia { get; set; }
    public bool AparatoNuevo { get; set; }
    public int? UserId { get; set; }
    public DateTime? CerradaAr { get; set; }
    public string? CerradaPor { get; set; }
    public string? CerradaMotivo { get; set; }
}

public class SesionesListadoDto
{
    public List<SesionDto> Abiertas { get; set; } = new();
    public List<SesionDto> Cerradas { get; set; } = new();
    /// <summary>2026-09-18: repartidores y la última vez que abrieron su link.</summary>
    public List<RepartidorUsoDto> Repartidores { get; set; } = new();
}

/// <summary>Un repartidor y la última vez que abrió su link (no tienen sesión, entran sin clave).</summary>
public class RepartidorUsoDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    /// <summary>Hora argentina. Null = no lo abrió desde que se empezó a anotar.</summary>
    public DateTime? UltimoUsoAr { get; set; }
    public string? Aparato { get; set; }
    public string? Lugar { get; set; }
    public string? Ciudad { get; set; }
    public string? Ip { get; set; }
}

/// <summary>Una entrada al sistema: "el 17/09 a las 16:40 entró desde acá".</summary>
public class EntradaSesionDto
{
    public DateTime CuandoAr { get; set; }
    public string? Ip { get; set; }
    public string Lugar { get; set; } = "";
    public string? Ciudad { get; set; }
}

/// <summary>"Las conexiones que empiezan con 190.2.3 son la Oficina".</summary>
public class RedConocidaDto
{
    public string Red { get; set; } = "";
    public string Nombre { get; set; } = "";
}

public class RedesConocidasDto
{
    public List<RedConocidaDto> Redes { get; set; } = new();
    /// <summary>Desde qué número estás entrando vos ahora, para no tener que adivinarlo.</summary>
    public string? MiIp { get; set; }
    /// <summary>¿Ya está la base de ciudades en el servidor? Tarda un rato en bajar la primera vez.</summary>
    public bool CiudadDisponible { get; set; }
    /// <summary>De cuándo es la base de ciudades que tenemos.</summary>
    public DateTime? CiudadFechaBase { get; set; }
}

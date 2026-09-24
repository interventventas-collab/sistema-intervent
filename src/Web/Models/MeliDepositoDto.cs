namespace Web.Models;

// 2026-09-21: página "Órdenes MeLi · Depósito" (/deposito/ordenes-meli). Sin plata a propósito.

public class MeliDepoListado
{
    public List<MeliDepoCuenta> Cuentas { get; set; } = new();
    public List<MeliDepoOrden> Ordenes { get; set; } = new();
}

public class MeliDepoCuenta
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
}

/// <summary>Una fila del listado = un ENVÍO (lo que va en un mismo paquete).</summary>
public class MeliDepoOrden
{
    public long Numero { get; set; }
    public long NumeroVenta { get; set; }
    /// <summary>ShippingId para pedir la etiqueta; null si es Full o no tiene envio.</summary>
    public long? NumeroEnvio { get; set; }
    public bool EtiquetaImpresa { get; set; }
    /// <summary>Primera impresion (UTC), si se sabe.</summary>
    public DateTime? EtiquetaImpresaAt { get; set; }
    public DateTime Fecha { get; set; }
    public string Cuenta { get; set; } = "";
    public string Comprador { get; set; } = "";
    public string Tipo { get; set; } = "";
    public string TipoClave { get; set; } = "";
    public string Estado { get; set; } = "";
    public string EstadoClave { get; set; } = "";
    public int Unidades { get; set; }
    public List<MeliDepoOrdenProducto> Productos { get; set; } = new();
}

public class MeliDepoOrdenProducto
{
    public string Titulo { get; set; } = "";
    public int Cantidad { get; set; }
    public string? Foto { get; set; }
}

public class MeliDepoFicha
{
    public bool Ok { get; set; }
    public string? Mensaje { get; set; }
    public long Numero { get; set; }
    public long? NumeroEnvio { get; set; }
    public long? NumeroVenta { get; set; }
    public DateTime? Fecha { get; set; }
    public string? Cuenta { get; set; }
    public string? Comprador { get; set; }
    public string? Tipo { get; set; }
    public string? Estado { get; set; }
    public int Unidades { get; set; }
    public List<MeliDepoFichaProducto> Productos { get; set; } = new();
    public bool MensajesOk { get; set; }
    public List<MeliDepoMensaje> Mensajes { get; set; } = new();
    public List<MeliDepoPregunta> Preguntas { get; set; } = new();
    public List<MeliDepoCompra> Compras { get; set; } = new();
}

public class MeliDepoFichaProducto
{
    public string Titulo { get; set; } = "";
    public int Cantidad { get; set; }
    public string? Sku { get; set; }
    public string? Foto { get; set; }
    public bool EsCombo { get; set; }
    public List<MeliDepoComponente> Componentes { get; set; } = new();
}

public class MeliDepoComponente
{
    public string Nombre { get; set; } = "";
    public string? Sku { get; set; }
    public decimal Cantidad { get; set; }
    public string? Formato { get; set; }
}

public class MeliDepoMensaje
{
    public string De { get; set; } = "";   // "comprador" | "vendedor"
    public string Texto { get; set; } = "";
    public DateTime? Fecha { get; set; }
}

public class MeliDepoPregunta
{
    public string Texto { get; set; } = "";
    public string? Respuesta { get; set; }
    public DateTime Fecha { get; set; }
    public string? Producto { get; set; }
    public bool DeEsteProducto { get; set; }
}

public class MeliDepoCompra
{
    public long Numero { get; set; }
    public DateTime Fecha { get; set; }
    public string Cuenta { get; set; } = "";
    public string Estado { get; set; } = "";
    public List<MeliDepoOrdenProducto> Productos { get; set; } = new();
}

namespace Web.Shared;

/// <summary>
/// 2026-11-06: menú nuevo ordenado según las 5 partes del negocio (pedido del dueño):
/// Ventas por fuera · Frikaf · MercadoLibre · Intereventos · Logística, y aparte lo que
/// comparten todas (Stock y compras, Productos y precios, Plata, Empleados).
/// Por ahora lo ve SOLO el usuario admin (MainLayout.MenuNuevo); los demás siguen con el menú
/// de siempre. Cada pantalla está en UN solo lugar: lo que está acá no se repite en el costado.
/// </summary>
public static class MenuNuevoCatalogo
{
    /// <summary>Un renglón del menú. Seccion != null = título chiquito antes del renglón.
    /// Permiso null = lo ve cualquiera que vea el menú. Externo = abre la página entera (mapa).</summary>
    public record Item(string Label, string Href, string? Permiso = null, string? Seccion = null, bool Externo = false);

    public record Grupo(string Titulo, string Subtitulo, bool DelNegocio, Item[] Items);

    /// <summary>Botones directos de todos los días (un clic, sin abrir nada).</summary>
    public static readonly Item[] Directos =
    {
        new("Ventas", "/cafe/ventas"),
        new("Cobranzas", "/cafe/tesoreria/cobranzas"),
        new("Preparación", "/cafe/preparacion"),
        new("Stock", "/cafe/stock-masivo"),
    };

    public static readonly Grupo[] Grupos =
    {
        new("Ventas por fuera", "WhatsApp, teléfono, en persona", true, new Item[]
        {
            new("Ventas", "/cafe/ventas"),
            new("Cobranzas", "/cafe/tesoreria/cobranzas"),
            new("¿Quién me debe?", "/cafe/clientes/panel-deudas"),
            new("Presupuestos", "/cafe/presupuestos"),
            new("Pedidos de Gaby", "/cafe/preventas"),
            new("Pedidos de WhatsApp", "/cafe/pedidos-whatsapp"),
            new("Visitas", "/visitas"),
            new("Clientes", "/cafe/clientes", Seccion: "Clientes"),
            new("Precios especiales", "/cafe/precios-especiales"),
        }),
        new("Frikaf", "solo café", true, new Item[]
        {
            new("Kilos de café vendidos", "/panorama?ver=kg"),
            new("Máquinas (comodato y financiación)", "/cafe/maquinas"),
            new("Fichas técnicas de café", "/cafe/fichas-tecnicas"),
            new("Post-venta café (molienda)", "/meli/postventa-cafe", "publicaciones"),
        }),
        new("MercadoLibre", "las 2 cuentas", true, new Item[]
        {
            new("Publicaciones", "/publicaciones-nueva", "publicaciones"),
            new("Órdenes", "/ordenes", "ordenes"),
            new("Preguntas", "/meli/preguntas", "publicaciones"),
            new("Respuestas automáticas", "/meli/respuestas-automaticas", "publicaciones"),
            new("Clonar publicación", "/meli/clonar", "publicaciones"),
            new("SKUs MeLi", "/cafe/skus-meli"),
            new("Reservados MeLi", "/cafe/reservados"),
            new("Cobros Mercado Pago", "/mercadopago/cobranzas", "cafe-tesoreria"),
            new("Teléfonos clientes MeLi", "/cafe/telefonos-clientes-meli", "cafe-telefonos-meli"),
        }),
        new("Intereventos", "alquileres", true, new Item[]
        {
            new("Inicio", "/alquileres", "alquileres"),
            new("Reservas", "/alquileres/reservas", "alquileres"),
            new("Calendario", "/alquileres/calendario", "alquileres"),
            new("Equipos", "/alquileres/equipos", "alquileres"),
            new("Fletes", "/alquileres/fletes", "alquileres"),
            new("Texto del presupuesto", "/alquileres/texto", "alquileres"),
        }),
        new("Logística", "armado y reparto", true, new Item[]
        {
            new("Preparación de pedidos", "/cafe/preparacion"),
            new("Mapa de reparto", "/mapeo?full=1", Externo: true),
            new("Clientes mapeados", "/cafe/clientes-mapeados"),
            new("Repartidores", "/cafe/repartidores"),
            new("Viajes", "/viajes"),
            new("Arqueo repartidor", "/cafe/arqueo-repartidor"),
            new("Entregas me1", "/meli/me1/entregas", "me1"),
            new("Códigos postales me1", "/meli/me1/codigos-postales", "me1"),
        }),
        new("Stock y compras", "", false, new Item[]
        {
            new("Stock", "/cafe/stock-masivo", Seccion: "Stock"),
            new("Vencimientos", "/cafe/vencimientos"),
            new("Historial stock", "/cafe/historial-stock"),
            new("Faltantes para pedir", "/stock/ideal"),
            new("Reposición / pedidos", "/stock/reposicion"),
            new("Ubicaciones", "/cafe/ubicaciones"),
            new("Tareas del depósito", "/deposito/tareas"),
            new("Proveedores", "/cafe/proveedores", Seccion: "Compras"),
            new("¿A quién le debo?", "/cafe/proveedores-ctacte"),
            new("Facturas de compra", "/contadora?tab=comp", "contadora"),
            new("Pagos a proveedores", "/cafe/tesoreria/pagos"),
            new("Compras", "/cafe/compras"),
        }),
        new("Productos y precios", "", false, new Item[]
        {
            new("Productos", "/cafe/productos"),
            new("Listas de precios", "/cafe/listas-precios"),
            new("Listas personalizadas", "/cafe/listas-precios-custom"),
            new("Precios de packs", "/cafe/packs-control"),
        }),
        new("Plata", "", false, new Item[]
        {
            new("Cajas", "/cafe/tesoreria/cajas", "cafe-tesoreria"),
            new("Cheques", "/cafe/tesoreria/cheques-todos", "cafe-tesoreria"),
            new("Plata que entró", "/plata-que-entro", "cafe-tesoreria"),
            new("Cobranzas a aprobar", "/cafe/cobranzas-pendientes", "cafe-tesoreria"),
            new("Pagos a confirmar", "/tesoreria/pagos-pendientes", "cafe-tesoreria"),
            new("Extracto banco", "/cafe/tesoreria/extracto-banco", "cafe-tesoreria"),
            new("Resumen tesorería", "/cafe/tesoreria/resumen", "cafe-tesoreria"),
            new("Bóveda de claves", "/vault"),
        }),
        new("Empleados", "", false, new Item[]
        {
            new("Horario", "/horas-extras"),
            new("Ficha empleados", "/nominas/empleados", "nominas"),
            new("Vacaciones", "/nominas/vacaciones", "nominas"),
            new("¿Cuánto debo pagar?", "/nominas/panel", "nominas"),
            new("Liquidaciones", "/nominas/liquidaciones", "nominas"),
        }),
    };

    /// <summary>Lo que casi no se usa: queda en el costado, adentro de "Más pantallas".</summary>
    public static readonly Item[] MasPantallas =
    {
        new("Inicio café", "/cafe"),
        new("Consultas", "/cafe/consultas"),
        new("Todos los códigos", "/cafe/catalogo"),
        new("Combos MeLi", "/cafe/combos"),
        new("Kits", "/cafe/kits"),
        new("Servicios", "/cafe/servicios"),
        new("OEMs", "/cafe/oems"),
        new("Marcas", "/cafe/marcas"),
        new("Fotos fondo verde/azul", "/cafe/fotos-chroma"),
        new("Ranking productos", "/stock/ranking"),
        new("Valor de stock", "/stock/valuacion"),
        new("Depósitos", "/cafe/depositos", "cafe-depositos"),
        new("Pagos rápidos (celu)", "/pagos-movil", "cafe-tesoreria"),
        new("Catálogo de bancos", "/cafe/configuracion/bancos", "cafe-tesoreria"),
        new("Cobrar con Mercado Pago", "/mercadopago/cobrar", "cafe-tesoreria"),
        new("Publicaciones (vieja)", "/publicaciones", "publicaciones"),
        new("Publicaciones ME1", "/meli/me1/publicaciones", "me1"),
        new("Re-vincular publicaciones", "/relink-meli-orphans", "import-productos"),
        new("Push de stock (lote seguro)", "/cafe/push-quietos", "publicaciones"),
        new("Inicio nóminas", "/nominas", "nominas"),
        new("Configuración del sistema", "/cafe/configuracion"),
        new("Errores ARCA", "/cafe/arca/errores"),
        new("Cotejo MeLi", "/cafe/cotejo"),
        new("Saldos migración", "/cafe/saldos"),
    };
}

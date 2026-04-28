namespace Api.Services;

public static class MenuDefinition
{
    public static readonly List<MenuGroup> MenuTree = new()
    {
        new MenuGroup("root", "Dashboard", new[]
        {
            new MenuItem("dashboard", "Dashboard", "/")
        }),
        new MenuGroup("mercadolibre", "MercadoLibre", new[]
        {
            new MenuItem("publicaciones", "Publicaciones", "/publicaciones"),
            new MenuItem("ordenes", "Ordenes", "/ordenes")
        }),
        new MenuGroup("inventario", "Productos y Servicios", new[]
        {
            new MenuItem("productos", "Productos", "/productos"),
            new MenuItem("combos", "Combos", "/combos"),
            new MenuItem("proveedores", "Proveedores", "/proveedores"),
            new MenuItem("marcas", "Marcas", "/marcas"),
            new MenuItem("clientes", "Clientes", "/clientes")
        }),
        new MenuGroup("ventas", "Ventas", new[]
        {
            new MenuItem("ventas", "Comprobantes", "/ventas")
        }),
        new MenuGroup("administracion", "Administracion", new[]
        {
            new MenuItem("usuarios", "Usuarios", "/usuarios"),
            new MenuItem("roles", "Roles", "/roles"),
            new MenuItem("integraciones", "Integraciones", "/integraciones"),
            new MenuItem("procesos", "Procesos", "/procesos"),
            new MenuItem("archivos", "Archivos", "/archivos"),
            new MenuItem("auditoria", "Auditoria", "/auditoria"),
            new MenuItem("backups", "Backups", "/backups")
        }),
        new MenuGroup("configuracion", "Configuracion", new[]
        {
            new MenuItem("config", "Configuracion", "/config")
        })
    };

    public static readonly List<string> AllMenuKeys =
        MenuTree.SelectMany(g => g.Items.Select(i => i.Key)).ToList();
}

public record MenuGroup(string GroupKey, string Label, MenuItem[] Items);
public record MenuItem(string Key, string Label, string Route);

using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<MeliAccount> MeliAccounts => Set<MeliAccount>();
    public DbSet<ArcaAccount> ArcaAccounts => Set<ArcaAccount>();
    public DbSet<ArcaWebserviceAccount> ArcaWebserviceAccounts => Set<ArcaWebserviceAccount>();
    public DbSet<ArcaCsrRequest> ArcaCsrRequests => Set<ArcaCsrRequest>();
    public DbSet<ArcaEmisor> ArcaEmisores => Set<ArcaEmisor>();
    public DbSet<MeliOrder> MeliOrders => Set<MeliOrder>();
    public DbSet<MeliItem> MeliItems => Set<MeliItem>();
    public DbSet<MeliQuestion> MeliQuestions => Set<MeliQuestion>();
    public DbSet<MeliShipment> MeliShipments => Set<MeliShipment>();
    public DbSet<MapeoDriver> MapeoDrivers => Set<MapeoDriver>();
    public DbSet<MapeoFavorito> MapeoFavoritos => Set<MapeoFavorito>();
    public DbSet<MapeoStop> MapeoStops => Set<MapeoStop>();
    public DbSet<MapeoRouteSnapshot> MapeoRouteSnapshots => Set<MapeoRouteSnapshot>();
    public DbSet<ContabProducto> ContabProductos => Set<ContabProducto>();
    public DbSet<ContabCombo> ContabCombos => Set<ContabCombo>();
    public DbSet<ContabComboItem> ContabComboItems => Set<ContabComboItem>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Combo> Combos => Set<Combo>();
    public DbSet<ComboItem> ComboItems => Set<ComboItem>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<CustomerTier> CustomerTiers => Set<CustomerTier>();
    public DbSet<ProductPriceOverride> ProductPriceOverrides => Set<ProductPriceOverride>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<ProductCompanyPrice> ProductCompanyPrices => Set<ProductCompanyPrice>();
    public DbSet<BrandCompanyMarkup> BrandCompanyMarkups => Set<BrandCompanyMarkup>();
    public DbSet<ProductStockBatch> ProductStockBatches => Set<ProductStockBatch>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<TreasuryAccount> TreasuryAccounts => Set<TreasuryAccount>();
    public DbSet<TreasuryMovement> TreasuryMovements => Set<TreasuryMovement>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Payroll> Payrolls => Set<Payroll>();
    public DbSet<PayrollPayment> PayrollPayments => Set<PayrollPayment>();
    public DbSet<ScheduledProcess> ScheduledProcesses => Set<ScheduledProcess>();
    public DbSet<ProcessExecutionLog> ProcessExecutionLogs => Set<ProcessExecutionLog>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<BackupFile> BackupFiles => Set<BackupFile>();
    // Modulo Alquileres (independiente)
    public DbSet<AlqEquipo> AlqEquipos => Set<AlqEquipo>();
    public DbSet<AlqCliente> AlqClientes => Set<AlqCliente>();
    public DbSet<AlqReserva> AlqReservas => Set<AlqReserva>();
    public DbSet<AlqReservaItem> AlqReservaItems => Set<AlqReservaItem>();
    // Modulo Nominas (independiente)
    public DbSet<NomEmpleado> NomEmpleados => Set<NomEmpleado>();
    public DbSet<NomLiquidacion> NomLiquidaciones => Set<NomLiquidacion>();
    public DbSet<NomPago> NomPagos => Set<NomPago>();
    // Boveda de contraseñas (independiente)
    public DbSet<VaultSetting> VaultSettings => Set<VaultSetting>();
    public DbSet<VaultEntry> VaultEntries => Set<VaultEntry>();
    // Postits del dashboard
    public DbSet<Postit> Postits => Set<Postit>();
    // Modulo Cafe (independiente)
    public DbSet<CafeCliente> CafeClientes => Set<CafeCliente>();
    public DbSet<CafeProducto> CafeProductos => Set<CafeProducto>();
    public DbSet<CafeSetting> CafeSettings => Set<CafeSetting>();
    public DbSet<CafeVenta> CafeVentas => Set<CafeVenta>();
    public DbSet<CafeVentaItem> CafeVentaItems => Set<CafeVentaItem>();
    public DbSet<CafeVentaPreparacionLog> CafeVentaPreparacionLogs => Set<CafeVentaPreparacionLog>();
    public DbSet<CafeChequeBanco> CafeChequesBanco => Set<CafeChequeBanco>();
    public DbSet<CafeCalendarioNota> CafeCalendarioNotas => Set<CafeCalendarioNota>();
    public DbSet<CafeExtractoMovimiento> CafeExtractoMovimientos => Set<CafeExtractoMovimiento>();
    public DbSet<CafeRepartidor> CafeRepartidores => Set<CafeRepartidor>();
    public DbSet<CafeCobranzaPendiente> CafeCobranzasPendientes => Set<CafeCobranzaPendiente>();
    public DbSet<CafeSaldoMigracion> CafeSaldosMigracion => Set<CafeSaldoMigracion>();
    public DbSet<CafeComodato> CafeComodatos => Set<CafeComodato>();
    public DbSet<CafeComodatoPago> CafeComodatoPagos => Set<CafeComodatoPago>();
    public DbSet<CafeCombo> CafeCombos => Set<CafeCombo>();
    public DbSet<CafeComboItem> CafeComboItems => Set<CafeComboItem>();
    public DbSet<CafeOem> CafeOems => Set<CafeOem>();
    public DbSet<CafeMarca> CafeMarcas => Set<CafeMarca>();
    public DbSet<CafeDescuentoCliente> CafeDescuentosCliente => Set<CafeDescuentoCliente>();
    public DbSet<CafeReglaPrecio> CafeReglasPrecios => Set<CafeReglaPrecio>();

    // Horas Extras de empleados (link publico tipo mapeo)
    public DbSet<HorasExtrasEmpleado> HorasExtrasEmpleados => Set<HorasExtrasEmpleado>();
    public DbSet<HorasExtrasRegistro> HorasExtrasRegistros => Set<HorasExtrasRegistro>();

    // Viajes de empleados (carga diaria + pagos + saldo, similar a horas extras)
    public DbSet<ViajesEmpleado> ViajesEmpleados => Set<ViajesEmpleado>();
    public DbSet<ViajesRegistro> ViajesRegistros => Set<ViajesRegistro>();
    public DbSet<ViajesPago> ViajesPagos => Set<ViajesPago>();

    // Stock — carga rapida via link publico (mobile-first, escaneo de codigos)
    public DbSet<StockOperador> StockOperadores => Set<StockOperador>();
    public DbSet<StockMovimiento> StockMovimientos => Set<StockMovimiento>();

    // Preventas / Pedidos de vendedor — link publico para que vendedor en la calle (Gaby)
    // cargue pedidos desde el celular sin lidiar con codigos/precios/stock.
    public DbSet<CafePreventaVendedor> CafePreventaVendedores => Set<CafePreventaVendedor>();
    public DbSet<CafePreventa> CafePreventas => Set<CafePreventa>();
    public DbSet<CafePreventaItem> CafePreventaItems => Set<CafePreventaItem>();
    public DbSet<CafeHistorialPrecio> CafeHistorialPrecios => Set<CafeHistorialPrecio>();
    public DbSet<CafeKit> CafeKits => Set<CafeKit>();
    public DbSet<CafeKitItem> CafeKitItems => Set<CafeKitItem>();
    public DbSet<CafeProveedor> CafeProveedores => Set<CafeProveedor>();
    public DbSet<CafeCompra> CafeCompras => Set<CafeCompra>();
    public DbSet<CafeCompraItem> CafeCompraItems => Set<CafeCompraItem>();
    // Tesoreria (Fase 1, 2026-05-12)
    public DbSet<CafeCaja> CafeCajas => Set<CafeCaja>();
    public DbSet<CafeCheque> CafeCheques => Set<CafeCheque>();
    public DbSet<CafeCobranza> CafeCobranzas => Set<CafeCobranza>();
    public DbSet<CafeCobranzaComprobante> CafeCobranzasComprobantes => Set<CafeCobranzaComprobante>();
    public DbSet<CafeCobranzaMedio> CafeCobranzasMedios => Set<CafeCobranzaMedio>();
    public DbSet<CafePagoProveedor> CafePagosProveedor => Set<CafePagoProveedor>();
    public DbSet<CafePagoProveedorComprobante> CafePagosProveedorComprobantes => Set<CafePagoProveedorComprobante>();
    public DbSet<CafePagoProveedorMedio> CafePagosProveedorMedios => Set<CafePagoProveedorMedio>();
    public DbSet<CafeDeposito> CafeDepositos => Set<CafeDeposito>();
    public DbSet<CafeStockPorDeposito> CafeStockPorDeposito => Set<CafeStockPorDeposito>();
    public DbSet<FileMetadata> FileMetadata => Set<FileMetadata>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasOne(u => u.RoleNav)
                  .WithMany(r => r.Users)
                  .HasForeignKey(u => u.RoleId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasIndex(rp => new { rp.RoleId, rp.MenuKey }).IsUnique();
            entity.HasOne(rp => rp.Role)
                  .WithMany(r => r.Permissions)
                  .HasForeignKey(rp => rp.RoleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppSetting>().HasKey(a => a.Key);

        modelBuilder.Entity<Integration>(entity =>
        {
            entity.HasIndex(i => i.Provider).IsUnique();
        });

        modelBuilder.Entity<MeliAccount>(entity =>
        {
            entity.HasIndex(a => a.MeliUserId).IsUnique();
        });

        modelBuilder.Entity<MeliOrder>(entity =>
        {
            entity.HasIndex(o => new { o.MeliOrderId, o.ItemId }).IsUnique();
            entity.HasIndex(o => o.MeliAccountId);
            entity.HasIndex(o => o.DateCreated);
            entity.HasIndex(o => o.PackId);
            entity.HasOne(o => o.MeliAccount)
                  .WithMany()
                  .HasForeignKey(o => o.MeliAccountId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MeliItem>(entity =>
        {
            // Index no-unique: una publicacion puede tener varias filas (una por variante).
            // La unicidad real (MeliItemId + VariationId) se maneja con indices filtrados en init.sql.
            entity.HasIndex(i => i.MeliItemId);
            entity.HasIndex(i => i.MeliAccountId);
            entity.HasIndex(i => i.Status);
            entity.HasIndex(i => i.UserProductId);
            entity.HasIndex(i => i.FamilyId);
            entity.HasOne(i => i.MeliAccount)
                  .WithMany()
                  .HasForeignKey(i => i.MeliAccountId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(i => i.ProductId);
            entity.HasOne(i => i.Product)
                  .WithMany(p => p.MeliItems)
                  .HasForeignKey(i => i.ProductId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(i => i.CafeProductoId);
            entity.HasOne(i => i.CafeProducto)
                  .WithMany()
                  .HasForeignKey(i => i.CafeProductoId)
                  .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(i => i.CafeComboId);
            entity.HasIndex(i => i.CafeKitId);
            entity.HasOne(i => i.CafeKit)
                  .WithMany()
                  .HasForeignKey(i => i.CafeKitId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ContabProducto>(entity =>
        {
            entity.ToTable("Contab_Productos");
            entity.HasIndex(p => p.Sku).IsUnique();
            entity.HasIndex(p => p.SkuPadre);
        });

        modelBuilder.Entity<ContabCombo>(entity =>
        {
            entity.ToTable("Contab_Combos");
            entity.HasIndex(c => c.SkuCombo).IsUnique();
        });

        modelBuilder.Entity<ContabComboItem>(entity =>
        {
            entity.ToTable("Contab_ComboItems");
            entity.HasIndex(i => i.SkuCombo);
            entity.HasIndex(i => i.SkuComponente);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasIndex(p => p.BaseProductId);
            entity.HasOne(p => p.BaseProduct)
                  .WithMany(p => p.DerivedProducts)
                  .HasForeignKey(p => p.BaseProductId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(p => p.BrandId);
            entity.HasOne(p => p.BrandNav)
                  .WithMany()
                  .HasForeignKey(p => p.BrandId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.HasIndex(s => s.Name);
            entity.HasIndex(s => s.Code).IsUnique();
        });

        modelBuilder.Entity<Brand>(entity =>
        {
            entity.HasIndex(b => b.Name).IsUnique();
            entity.HasIndex(b => b.Code).IsUnique();
        });

        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasIndex(c => c.Name);
            entity.HasIndex(c => c.Code).IsUnique();
        });

        modelBuilder.Entity<ProductStockBatch>(entity =>
        {
            entity.HasIndex(b => b.ProductId);
            entity.HasIndex(b => b.ExpiryDate);
            entity.HasOne(b => b.Product)
                  .WithMany()
                  .HasForeignKey(b => b.ProductId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Sale>(entity =>
        {
            entity.HasIndex(s => s.Number).IsUnique();
            entity.HasIndex(s => s.Date);
            entity.HasIndex(s => s.ClientId);
            entity.HasOne(s => s.Client)
                  .WithMany()
                  .HasForeignKey(s => s.ClientId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SaleItem>(entity =>
        {
            entity.HasIndex(si => si.SaleId);
            entity.HasOne(si => si.Sale)
                  .WithMany(s => s.Items)
                  .HasForeignKey(si => si.SaleId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(si => si.Product)
                  .WithMany()
                  .HasForeignKey(si => si.ProductId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TreasuryAccount>(entity =>
        {
            entity.HasIndex(a => a.Code).IsUnique();
            entity.HasIndex(a => a.Name);
        });

        modelBuilder.Entity<TreasuryMovement>(entity =>
        {
            entity.HasIndex(m => m.AccountId);
            entity.HasIndex(m => m.Date);
            entity.HasOne(m => m.Account)
                  .WithMany()
                  .HasForeignKey(m => m.AccountId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasIndex(e => e.Code).IsUnique();
            entity.HasIndex(e => new { e.LastName, e.FirstName });
        });

        modelBuilder.Entity<Payroll>(entity =>
        {
            entity.HasIndex(p => new { p.EmployeeId, p.Year, p.Month }).IsUnique();
            entity.HasOne(p => p.Employee)
                  .WithMany()
                  .HasForeignKey(p => p.EmployeeId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(p => p.PaidFromAccount)
                  .WithMany()
                  .HasForeignKey(p => p.PaidFromAccountId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PayrollPayment>(entity =>
        {
            entity.HasIndex(pp => pp.PayrollId);
            entity.HasOne(pp => pp.Payroll)
                  .WithMany(p => p.Payments)
                  .HasForeignKey(pp => pp.PayrollId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(pp => pp.Account)
                  .WithMany()
                  .HasForeignKey(pp => pp.AccountId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Combo>(entity =>
        {
            entity.HasIndex(c => c.Name);
        });

        modelBuilder.Entity<ComboItem>(entity =>
        {
            entity.HasIndex(ci => ci.ComboId);
            entity.HasIndex(ci => ci.ProductId);
            entity.HasOne(ci => ci.Combo)
                  .WithMany(c => c.Items)
                  .HasForeignKey(ci => ci.ComboId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(ci => ci.Product)
                  .WithMany()
                  .HasForeignKey(ci => ci.ProductId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasIndex(a => new { a.EntityType, a.EntityId });
            entity.HasIndex(a => a.CreatedAt);
        });

        modelBuilder.Entity<ScheduledProcess>(entity =>
        {
            entity.HasIndex(p => p.Code).IsUnique();
        });

        modelBuilder.Entity<BackupFile>(entity =>
        {
            entity.HasIndex(b => b.FileName).IsUnique();
            entity.HasIndex(b => b.CreatedAt);
        });

        modelBuilder.Entity<ProcessExecutionLog>(entity =>
        {
            entity.HasIndex(l => l.ProcessCode);
            entity.HasIndex(l => l.StartedAt);
            entity.HasOne(l => l.Process)
                  .WithMany()
                  .HasForeignKey(l => l.ProcessCode)
                  .HasPrincipalKey(p => p.Code)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // ===== Tesoreria Cafe (Fase 1, 2026-05-12) =====
        modelBuilder.Entity<CafeCaja>(entity =>
        {
            entity.HasIndex(c => c.Nombre).IsUnique();
        });

        modelBuilder.Entity<CafeCheque>(entity =>
        {
            entity.HasIndex(c => c.Estado);
            entity.HasIndex(c => c.FechaVencimiento);
            entity.HasOne(c => c.ClienteOrigen)
                  .WithMany()
                  .HasForeignKey(c => c.ClienteOrigenId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CafeCobranza>(entity =>
        {
            entity.HasIndex(c => c.Numero).IsUnique();
            entity.HasIndex(c => c.ClienteId);
            entity.HasIndex(c => c.Fecha);
            entity.HasOne(c => c.Cliente)
                  .WithMany()
                  .HasForeignKey(c => c.ClienteId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CafeCobranzaComprobante>(entity =>
        {
            entity.HasOne(c => c.Cobranza)
                  .WithMany(p => p.Comprobantes)
                  .HasForeignKey(c => c.CobranzaId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.Venta)
                  .WithMany()
                  .HasForeignKey(c => c.VentaId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CafeCobranzaMedio>(entity =>
        {
            entity.HasOne(c => c.Cobranza)
                  .WithMany(p => p.Medios)
                  .HasForeignKey(c => c.CobranzaId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.Caja)
                  .WithMany()
                  .HasForeignKey(c => c.CajaId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(c => c.Cheque)
                  .WithMany()
                  .HasForeignKey(c => c.ChequeId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CafePagoProveedor>(entity =>
        {
            entity.HasIndex(p => p.Numero).IsUnique();
            entity.HasIndex(p => p.ProveedorId);
            entity.HasOne(p => p.Proveedor)
                  .WithMany()
                  .HasForeignKey(p => p.ProveedorId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CafePagoProveedorComprobante>(entity =>
        {
            entity.HasOne(c => c.Pago)
                  .WithMany(p => p.Comprobantes)
                  .HasForeignKey(c => c.PagoId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.Compra)
                  .WithMany()
                  .HasForeignKey(c => c.CompraId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CafePagoProveedorMedio>(entity =>
        {
            entity.HasOne(c => c.Pago)
                  .WithMany(p => p.Medios)
                  .HasForeignKey(c => c.PagoId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.Caja)
                  .WithMany()
                  .HasForeignKey(c => c.CajaId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(c => c.Cheque)
                  .WithMany()
                  .HasForeignKey(c => c.ChequeId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

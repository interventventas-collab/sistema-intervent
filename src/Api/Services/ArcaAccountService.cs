using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Manejo de cuentas de ARCA (ex AFIP) para login automatizado.
/// Maneja CRUD + validación de duplicados por CUIT.
/// La contraseña se guarda en texto en la DB (decisión consciente: el scraper
/// la necesita para loguear; protegé el acceso a la DB).
/// </summary>
public class ArcaAccountService
{
    private readonly AppDbContext _db;

    public ArcaAccountService(AppDbContext db) { _db = db; }

    private static ArcaAccountDto Map(ArcaAccount a) => new(
        a.Id,
        a.Cuit,
        string.IsNullOrEmpty(a.CuitLogin) ? null : a.CuitLogin,
        string.IsNullOrEmpty(a.Alias) ? null : a.Alias,
        !string.IsNullOrEmpty(a.Password),
        a.IsActive,
        a.CreatedAt,
        a.UpdatedAt);

    /// <summary>Normaliza un CUIT/CUIL: deja solo dígitos. Devuelve null si no quedan 11.</summary>
    public static string? NormalizeCuit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length == 11 ? digits : null;
    }

    public async Task<List<ArcaAccountDto>> GetAllAsync()
    {
        var list = await _db.ArcaAccounts.OrderBy(a => a.Cuit).ToListAsync();
        return list.Select(Map).ToList();
    }

    public async Task<ArcaAccountDto?> GetByIdAsync(int id)
    {
        var a = await _db.ArcaAccounts.FindAsync(id);
        return a is null ? null : Map(a);
    }

    public async Task<(bool ok, string? error, ArcaAccountDto? dto)> CreateAsync(CreateArcaAccountRequest req)
    {
        var cuit = NormalizeCuit(req.Cuit);
        if (cuit is null) return (false, "El CUIT debe tener 11 dígitos", null);

        var cuitLogin = NormalizeCuit(req.CuitLogin);
        if (!string.IsNullOrWhiteSpace(req.CuitLogin) && cuitLogin is null)
            return (false, "El CUIT Login (si se completa) debe tener 11 dígitos", null);

        if (string.IsNullOrWhiteSpace(req.Password))
            return (false, "La contraseña es obligatoria", null);

        // Duplicado: misma combinación Cuit + CuitLogin (un mismo CUIT puede entrar
        // con varios CUIT Login distintos, ej: con su propio CUIT y con el del estudio).
        var existing = await _db.ArcaAccounts.FirstOrDefaultAsync(a =>
            a.Cuit == cuit && a.CuitLogin == cuitLogin);
        if (existing is not null)
            return (false, $"Ya existe una cuenta para el CUIT {cuit}" +
                (cuitLogin is null ? "" : $" con CUIT Login {cuitLogin}"), null);

        var entity = new ArcaAccount
        {
            Cuit = cuit,
            CuitLogin = cuitLogin,
            Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim(),
            Password = req.Password,
            IsActive = req.IsActive,
            CreatedAt = DateTime.UtcNow
        };
        _db.ArcaAccounts.Add(entity);
        await _db.SaveChangesAsync();
        return (true, null, Map(entity));
    }

    public async Task<(bool ok, string? error, ArcaAccountDto? dto)> UpdateAsync(int id, UpdateArcaAccountRequest req)
    {
        var entity = await _db.ArcaAccounts.FindAsync(id);
        if (entity is null) return (false, "Cuenta no encontrada", null);

        if (req.Cuit is not null)
        {
            var cuit = NormalizeCuit(req.Cuit);
            if (cuit is null) return (false, "El CUIT debe tener 11 dígitos", null);
            entity.Cuit = cuit;
        }

        if (req.CuitLogin is not null)
        {
            // Si viene vacío explícito, blanquearlo. Si viene con valor, validar.
            if (string.IsNullOrWhiteSpace(req.CuitLogin))
            {
                entity.CuitLogin = null;
            }
            else
            {
                var cuitLogin = NormalizeCuit(req.CuitLogin);
                if (cuitLogin is null) return (false, "El CUIT Login debe tener 11 dígitos", null);
                entity.CuitLogin = cuitLogin;
            }
        }

        if (req.Alias is not null)
            entity.Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim();

        // Solo cambiar password si vino con contenido. Si llega vacío/null, mantener la actual.
        if (!string.IsNullOrWhiteSpace(req.Password))
            entity.Password = req.Password;

        if (req.IsActive.HasValue)
            entity.IsActive = req.IsActive.Value;

        // Validar duplicado tras los cambios
        var dup = await _db.ArcaAccounts.FirstOrDefaultAsync(a =>
            a.Id != id && a.Cuit == entity.Cuit && a.CuitLogin == entity.CuitLogin);
        if (dup is not null)
            return (false, $"Ya existe otra cuenta para el CUIT {entity.Cuit}" +
                (entity.CuitLogin is null ? "" : $" con CUIT Login {entity.CuitLogin}"), null);

        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, null, Map(entity));
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var entity = await _db.ArcaAccounts.FindAsync(id);
        if (entity is null) return false;
        _db.ArcaAccounts.Remove(entity);
        await _db.SaveChangesAsync();
        return true;
    }
}

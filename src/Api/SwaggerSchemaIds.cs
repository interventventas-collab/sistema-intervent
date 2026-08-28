namespace Api;

/// <summary>2026-08-28: nombres unicos para los modelos que muestra Swagger.
/// Por default Swashbuckle usa el nombre corto de la clase, y en este proyecto hay varias clases
/// internas de controllers que se llaman igual (ej. AprobarRequest en CafeAltaClientes y en
/// AlqCobranzasPendientes). Al chocar, la generacion del documento tiraba 500 y /swagger quedaba
/// en blanco. Aca le anteponemos el controller/namespace para que no se pisen.</summary>
public static class SwaggerSchemaIds
{
    public static string For(Type t)
    {
        if (t.IsGenericType)
        {
            var raw = t.Name;
            var tick = raw.IndexOf('`');
            var baseName = tick >= 0 ? raw[..tick] : raw;
            return baseName + "De" + string.Join("Y", t.GetGenericArguments().Select(For));
        }

        var full = t.FullName ?? t.Name;
        // "Api.Controllers.CafeAltaClientesController+AprobarRequest" -> "CafeAltaClientes.AprobarRequest"
        full = full.Replace('+', '.');
        foreach (var prefijo in new[] { "Api.Controllers.", "Api.DTOs.", "Api.Models.", "Api.Services.", "Api." })
        {
            if (full.StartsWith(prefijo, StringComparison.Ordinal)) { full = full[prefijo.Length..]; break; }
        }
        return full.Replace("Controller.", ".");
    }
}

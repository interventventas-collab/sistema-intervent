namespace Web.Models;

// 2026-07-08: espacio en disco del servidor para el chip del dashboard.
// Lo llena el robot de las 2 AM (docker-cache-cleanup.sh) en AppSettings['system.disk.stats'].
public class DiskUsageDto
{
    public int TotalGb { get; set; }
    public int UsedGb { get; set; }
    public int FreeGb { get; set; }
    public int Pct { get; set; }
    public string? At { get; set; }
}

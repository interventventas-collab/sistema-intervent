using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SystemController : ControllerBase
{
    private readonly AppDbContext _db;

    public SystemController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("info")]
    public async Task<IActionResult> GetSystemInfo()
    {
        var process = Process.GetCurrentProcess();

        // Server info
        var serverInfo = new
        {
            MachineName = Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            DotnetVersion = RuntimeInformation.FrameworkDescription,
            ServerUptime = FormatUptime(DateTime.UtcNow - process.StartTime.ToUniversalTime()),
            WorkingMemoryMb = Math.Round(process.WorkingSet64 / 1024.0 / 1024.0, 1),
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"
        };

        // SQL Server info + Table counts - use raw ADO.NET with own connection
        object? sqlInfo = null;
        List<object>? tableCounts = null;
        var connStr = _db.Database.GetConnectionString();

        try
        {
            using var conn = new Microsoft.Data.SqlClient.SqlConnection(connStr);
            await conn.OpenAsync();

            // SQL Server info
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT
                        SERVERPROPERTY('ProductVersion') AS Version,
                        SERVERPROPERTY('Edition') AS Edition,
                        SERVERPROPERTY('ProductLevel') AS ProductLevel,
                        SERVERPROPERTY('ServerName') AS ServerName,
                        SERVERPROPERTY('Collation') AS Collation,
                        (SELECT SUM(CAST(size AS BIGINT)) * 8 / 1024 FROM sys.master_files WHERE database_id = DB_ID()) AS DatabaseSizeMb,
                        DB_NAME() AS DatabaseName,
                        (SELECT COUNT(*) FROM sys.tables) AS TableCount,
                        (SELECT create_date FROM sys.databases WHERE name = DB_NAME()) AS DatabaseCreated,
                        @@MAX_CONNECTIONS AS MaxConnections";

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        sqlInfo = new
                        {
                            Version = reader["Version"]?.ToString(),
                            Edition = reader["Edition"]?.ToString(),
                            ProductLevel = reader["ProductLevel"]?.ToString(),
                            ServerName = reader["ServerName"]?.ToString(),
                            Collation = reader["Collation"]?.ToString(),
                            DatabaseSizeMb = reader["DatabaseSizeMb"] != DBNull.Value ? Convert.ToInt64(reader["DatabaseSizeMb"]) : 0,
                            DatabaseName = reader["DatabaseName"]?.ToString(),
                            TableCount = reader["TableCount"] != DBNull.Value ? Convert.ToInt32(reader["TableCount"]) : 0,
                            DatabaseCreated = reader["DatabaseCreated"] != DBNull.Value ? Convert.ToDateTime(reader["DatabaseCreated"]) : (DateTime?)null,
                            MaxConnections = reader["MaxConnections"] != DBNull.Value ? Convert.ToInt32(reader["MaxConnections"]) : 0
                        };
                    }
                }
            }

            // Table row counts with size
            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = @"
                    SELECT
                        t.name AS TableName,
                        SUM(p.rows) AS [RowCount],
                        CAST(ROUND(SUM(a.total_pages) * 8.0, 2) AS FLOAT) AS SizeKB
                    FROM sys.tables t
                    INNER JOIN sys.indexes i ON t.object_id = i.object_id
                    INNER JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id
                    INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
                    WHERE p.index_id IN (0, 1)
                    GROUP BY t.name
                    ORDER BY SizeKB DESC";

                using (var reader2 = await cmd2.ExecuteReaderAsync())
                {
                    tableCounts = new List<object>();
                    while (await reader2.ReadAsync())
                    {
                        tableCounts.Add(new
                        {
                            Name = reader2["TableName"]?.ToString(),
                            Rows = Convert.ToInt64(reader2["RowCount"] != DBNull.Value ? reader2["RowCount"] : 0),
                            SizeKb = reader2["SizeKB"] != DBNull.Value ? Convert.ToDouble(reader2["SizeKB"]) : 0
                        });
                    }
                }
            }
        }
        catch
        {
            sqlInfo ??= new { Error = "No se pudo conectar a SQL Server" };
        }

        return Ok(new { Server = serverInfo, SqlServer = sqlInfo, Tables = tableCounts });
    }

    [HttpGet("host-info")]
    public async Task<IActionResult> GetHostInfo()
    {
        var host = new
        {
            Hostname = ReadHostFile("/host_proc/sys/kernel/hostname")?.Trim() ?? Environment.MachineName,
            TotalRamGb = 0.0,
            UsedRamGb = 0.0,
            RamUsagePercent = 0.0,
            CpuCount = 0,
            CpuModel = "",
            Uptime = "",
            DiskTotalGb = 0.0,
            DiskUsedGb = 0.0,
            DiskFreeGb = 0.0,
            DiskUsagePercent = 0.0,
            LoadAverage = ""
        };

        // Parse RAM from /host_proc/meminfo
        double totalRamKb = 0, availableRamKb = 0;
        try
        {
            var meminfo = ReadHostFile("/host_proc/meminfo") ?? "";
            foreach (var line in meminfo.Split('\n'))
            {
                if (line.StartsWith("MemTotal:"))
                    totalRamKb = ParseMemValue(line);
                else if (line.StartsWith("MemAvailable:"))
                    availableRamKb = ParseMemValue(line);
            }
        }
        catch { }

        double totalRamGb = Math.Round(totalRamKb / 1024.0 / 1024.0, 2);
        double usedRamGb = Math.Round((totalRamKb - availableRamKb) / 1024.0 / 1024.0, 2);
        double ramPercent = totalRamKb > 0 ? Math.Round((totalRamKb - availableRamKb) / totalRamKb * 100, 1) : 0;

        // Parse CPU from /host_proc/cpuinfo
        int cpuCount = 0;
        string cpuModel = "";
        try
        {
            var cpuinfo = ReadHostFile("/host_proc/cpuinfo") ?? "";
            foreach (var line in cpuinfo.Split('\n'))
            {
                if (line.StartsWith("processor"))
                    cpuCount++;
                else if (line.StartsWith("model name") && string.IsNullOrEmpty(cpuModel))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2) cpuModel = parts[1].Trim();
                }
            }
        }
        catch { }

        // Parse uptime from /host_proc/uptime
        string uptimeStr = "";
        try
        {
            var uptimeContent = ReadHostFile("/host_proc/uptime") ?? "";
            var uptimeSeconds = double.Parse(uptimeContent.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            uptimeStr = FormatUptime(TimeSpan.FromSeconds(uptimeSeconds));
        }
        catch { }

        // Parse load average from /host_proc/loadavg
        string loadAvg = "";
        try
        {
            var loadContent = ReadHostFile("/host_proc/loadavg") ?? "";
            var parts = loadContent.Trim().Split(' ');
            if (parts.Length >= 3)
                loadAvg = $"{parts[0]} {parts[1]} {parts[2]}";
        }
        catch { }

        // Disk info using DriveInfo for "/"
        double diskTotalGb = 0, diskUsedGb = 0, diskFreeGb = 0, diskPercent = 0;
        try
        {
            var drive = new DriveInfo("/");
            diskTotalGb = Math.Round(drive.TotalSize / 1024.0 / 1024.0 / 1024.0, 2);
            diskFreeGb = Math.Round(drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0, 2);
            diskUsedGb = Math.Round(diskTotalGb - diskFreeGb, 2);
            diskPercent = diskTotalGb > 0 ? Math.Round(diskUsedGb / diskTotalGb * 100, 1) : 0;
        }
        catch { }

        // Docker containers via unix socket
        var containers = new List<object>();
        try
        {
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var endpoint = new UnixDomainSocketEndPoint("/var/run/docker.sock");
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
            };

            using var dockerClient = new HttpClient(handler);
            dockerClient.BaseAddress = new Uri("http://localhost");

            var response = await dockerClient.GetAsync("/containers/json?all=true");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var dockerContainers = JsonSerializer.Deserialize<JsonElement>(json);

                if (dockerContainers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in dockerContainers.EnumerateArray())
                    {
                        // Get container name
                        var name = "";
                        if (c.TryGetProperty("Names", out var names) && names.ValueKind == JsonValueKind.Array)
                        {
                            var firstName = names.EnumerateArray().FirstOrDefault().GetString() ?? "";
                            name = firstName.TrimStart('/');
                        }

                        // Filter only project containers
                        if (!name.StartsWith("aiml-", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var image = c.TryGetProperty("Image", out var img) ? img.GetString() ?? "" : "";
                        var status = c.TryGetProperty("Status", out var st) ? st.GetString() ?? "" : "";
                        var state = c.TryGetProperty("State", out var stt) ? stt.GetString() ?? "" : "";

                        // Parse ports
                        var portsStr = "";
                        if (c.TryGetProperty("Ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
                        {
                            var portList = new List<string>();
                            foreach (var p in ports.EnumerateArray())
                            {
                                var privatePort = p.TryGetProperty("PrivatePort", out var pp) ? pp.GetInt32().ToString() : "";
                                var publicPort = p.TryGetProperty("PublicPort", out var pub) ? pub.GetInt32().ToString() : "";
                                var type = p.TryGetProperty("Type", out var tp) ? tp.GetString() ?? "" : "";

                                if (!string.IsNullOrEmpty(publicPort))
                                    portList.Add($"{publicPort}->{privatePort}/{type}");
                                else if (!string.IsNullOrEmpty(privatePort))
                                    portList.Add($"{privatePort}/{type}");
                            }
                            portsStr = string.Join(", ", portList.Distinct());
                        }

                        containers.Add(new
                        {
                            Name = name,
                            Image = image,
                            Status = status,
                            State = state,
                            Ports = portsStr
                        });
                    }
                }
            }
        }
        catch { }

        return Ok(new
        {
            Host = new
            {
                Hostname = ReadHostFile("/host_proc/sys/kernel/hostname")?.Trim() ?? Environment.MachineName,
                TotalRamGb = totalRamGb,
                UsedRamGb = usedRamGb,
                RamUsagePercent = ramPercent,
                CpuCount = cpuCount,
                CpuModel = cpuModel,
                Uptime = uptimeStr,
                DiskTotalGb = diskTotalGb,
                DiskUsedGb = diskUsedGb,
                DiskFreeGb = diskFreeGb,
                DiskUsagePercent = diskPercent,
                LoadAverage = loadAvg
            },
            Containers = containers
        });
    }

    private static string? ReadHostFile(string path)
    {
        try { return System.IO.File.ReadAllText(path); }
        catch { return null; }
    }

    private static double ParseMemValue(string line)
    {
        // Line format: "MemTotal:       16384000 kB"
        var parts = line.Split(':', 2);
        if (parts.Length < 2) return 0;
        var valuePart = parts[1].Trim().Split(' ')[0];
        return double.TryParse(valuePart, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0;
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        return $"{(int)uptime.TotalMinutes}m";
    }
}

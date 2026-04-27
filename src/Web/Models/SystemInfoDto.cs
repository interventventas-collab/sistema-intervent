namespace Web.Models;

public class SystemInfoDto
{
    public ServerInfoDto Server { get; set; } = new();
    public SqlServerInfoDto? SqlServer { get; set; }
    public List<TableCountDto> Tables { get; set; } = new();
}

public class ServerInfoDto
{
    public string MachineName { get; set; } = string.Empty;
    public string OsDescription { get; set; } = string.Empty;
    public string OsArchitecture { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public string DotnetVersion { get; set; } = string.Empty;
    public string ServerUptime { get; set; } = string.Empty;
    public double WorkingMemoryMb { get; set; }
    public string Environment { get; set; } = string.Empty;
}

public class SqlServerInfoDto
{
    public string? Version { get; set; }
    public string? Edition { get; set; }
    public string? ProductLevel { get; set; }
    public string? ServerName { get; set; }
    public string? Collation { get; set; }
    public long DatabaseSizeMb { get; set; }
    public string? DatabaseName { get; set; }
    public int TableCount { get; set; }
    public DateTime? DatabaseCreated { get; set; }
    public int MaxConnections { get; set; }
    public string? Error { get; set; }
}

public class TableCountDto
{
    public string Name { get; set; } = string.Empty;
    public long Rows { get; set; }
    public double SizeKb { get; set; }
}

public class HostInfoDto
{
    public HostServerDto Host { get; set; } = new();
    public List<DockerContainerDto> Containers { get; set; } = new();
}

public class HostServerDto
{
    public string Hostname { get; set; } = string.Empty;
    public double TotalRamGb { get; set; }
    public double UsedRamGb { get; set; }
    public double RamUsagePercent { get; set; }
    public int CpuCount { get; set; }
    public string CpuModel { get; set; } = string.Empty;
    public string Uptime { get; set; } = string.Empty;
    public double DiskTotalGb { get; set; }
    public double DiskUsedGb { get; set; }
    public double DiskFreeGb { get; set; }
    public double DiskUsagePercent { get; set; }
    public string LoadAverage { get; set; } = string.Empty;
}

public class DockerContainerDto
{
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Ports { get; set; } = string.Empty;
}

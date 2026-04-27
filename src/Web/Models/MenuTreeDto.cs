namespace Web.Models;

public class MenuTreeDto
{
    public string GroupKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<MenuItemDto> Items { get; set; } = new();
}

public class MenuItemDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
}

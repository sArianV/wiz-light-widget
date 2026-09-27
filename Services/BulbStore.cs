using System.IO;
using System.Text.Json;

namespace WizLightWidget.Services;

public class BulbPrefs
{
    public string Name { get; set; } = "";
    public bool IsFavorite { get; set; }
    public int? Order { get; set; }
}

public class BulbStore
{
    private readonly string _path;
    private Dictionary<string, BulbPrefs> _prefs = new();

    public BulbStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WizLightWidget");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "bulbs.json");
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;

        var json = File.ReadAllText(_path);
        try
        {
            _prefs = JsonSerializer.Deserialize<Dictionary<string, BulbPrefs>>(json) ?? new();
            return;
        }
        catch { /* fall through to legacy format */ }

        try
        {
            // Formato anterior: mac -> nombre (string simple), sin favoritos.
            var legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            _prefs = legacy?.ToDictionary(kv => kv.Key, kv => new BulbPrefs { Name = kv.Value }) ?? new();
        }
        catch
        {
            _prefs = new();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_prefs, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch { /* ignore persistence failures */ }
    }

    public string GetName(string mac, string fallback) =>
        _prefs.TryGetValue(mac, out var p) && !string.IsNullOrWhiteSpace(p.Name) ? p.Name : fallback;

    public bool GetFavorite(string mac) =>
        _prefs.TryGetValue(mac, out var p) && p.IsFavorite;

    public int GetOrder(string mac, int fallback) =>
        _prefs.TryGetValue(mac, out var p) && p.Order.HasValue ? p.Order.Value : fallback;

    public void SetName(string mac, string name)
    {
        GetOrCreate(mac).Name = name;
        Save();
    }

    public void SetFavorite(string mac, bool isFavorite)
    {
        GetOrCreate(mac).IsFavorite = isFavorite;
        Save();
    }

    /// <summary>Persiste el orden de todos los focos de una sola vez (un solo guardado a disco).</summary>
    public void SetOrders(IEnumerable<(string Mac, int Order)> orders)
    {
        foreach (var (mac, order) in orders)
            GetOrCreate(mac).Order = order;
        Save();
    }

    private BulbPrefs GetOrCreate(string mac)
    {
        if (!_prefs.TryGetValue(mac, out var p))
        {
            p = new BulbPrefs();
            _prefs[mac] = p;
        }
        return p;
    }
}

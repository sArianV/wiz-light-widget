using System.IO;
using System.Text.Json;

namespace WizLightWidget.Services;

public class SceneBulbState
{
    public string Mac { get; set; } = "";
    public bool IsOn { get; set; }
    public int Brightness { get; set; } = 100;
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
}

public class Scene
{
    public string Name { get; set; } = "";
    public List<SceneBulbState> Bulbs { get; set; } = new();
}

/// <summary>Persiste escenas (foto del estado de todos los focos) en escenas.json, junto a bulbs.json.</summary>
public class SceneStore
{
    private readonly string _path;
    private List<Scene> _scenes = new();

    public SceneStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WizLightWidget");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "scenes.json");
        Load();
    }

    public IReadOnlyList<Scene> Scenes => _scenes;

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            _scenes = JsonSerializer.Deserialize<List<Scene>>(json) ?? new();
        }
        catch { _scenes = new(); }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_scenes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch { /* ignore persistence failures */ }
    }

    /// <summary>Crea la escena o, si ya existe una con el mismo nombre, la reemplaza.</summary>
    public void AddOrUpdate(Scene scene)
    {
        int idx = _scenes.FindIndex(s => s.Name == scene.Name);
        if (idx >= 0) _scenes[idx] = scene;
        else _scenes.Add(scene);
        Save();
    }

    public void Remove(string name)
    {
        _scenes.RemoveAll(s => s.Name == name);
        Save();
    }
}

// PrismRGB - contrôle RGB unifié pour Windows
// Copyright (C) 2026 Torjant <https://github.com/Torjant/PrismRGB>
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrismRGB.Core.Config;

/// <summary>Lecture/écriture de la configuration dans %APPDATA%\PrismRGB\config.json (écriture atomique).</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly object _lock = new();

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "config.json");

    public ConfigStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrismRGB");
    }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), Json);
                if (cfg is not null)
                {
                    cfg.GetActiveProfile();
                    return cfg;
                }
            }
        }
        catch (Exception)
        {
            // Fichier corrompu : on le garde de côté et on repart sur une config neuve.
            try { File.Copy(FilePath, FilePath + ".corrompu", overwrite: true); } catch { }
        }
        var fresh = new AppConfig();
        fresh.GetActiveProfile();
        return fresh;
    }

    public void Save(AppConfig cfg)
    {
        lock (_lock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}

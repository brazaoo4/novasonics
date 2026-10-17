using System;
using System.IO;
using System.Text.Json;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public class ConfigService : IConfigService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly string _configDirectory;
        private readonly string _configFilePath;

        public NovaSonicsConfig Config { get; private set; } = new();

        public ConfigService()
        {
            _configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSonics");
            _configFilePath = Path.Combine(_configDirectory, "config.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string json = File.ReadAllText(_configFilePath);
                    var loaded = JsonSerializer.Deserialize<NovaSonicsConfig>(json, JsonOptions);
                    if (loaded != null)
                    {
                        Config = loaded;
                        return;
                    }
                }
            }
            catch { }

            Config = new NovaSonicsConfig();
            Save();
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(_configDirectory))
                {
                    Directory.CreateDirectory(_configDirectory);
                }

                string json = JsonSerializer.Serialize(Config, JsonOptions);
                File.WriteAllText(_configFilePath, json);
            }
            catch { }
        }

        public bool ExportConfig(string filePath)
        {
            try
            {
                string json = JsonSerializer.Serialize(Config, JsonOptions);
                File.WriteAllText(filePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool ImportConfig(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var imported = JsonSerializer.Deserialize<NovaSonicsConfig>(json, JsonOptions);
                    if (imported != null)
                    {
                        Config = imported;
                        Save();
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}

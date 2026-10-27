using System;
using System.IO;
using System.Text.Json;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public class ConfigService : IConfigService
    {
        private static readonly object _fileLock = new();
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly string _configDirectory;
        private readonly string _configFilePath;
        private readonly string _backupFilePath;

        public NovaSonicsConfig Config { get; private set; } = new();
        public event EventHandler? ConfigImported;

        public ConfigService()
        {
            _configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NovaSonics");
            _configFilePath = Path.Combine(_configDirectory, "config.json");
            _backupFilePath = Path.Combine(_configDirectory, "config.bak.json");
            Load();
        }

        public void Load()
        {
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(_configFilePath))
                    {
                        string json = File.ReadAllText(_configFilePath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var loaded = JsonSerializer.Deserialize<NovaSonicsConfig>(json, JsonOptions);
                            if (loaded != null)
                            {
                                loaded.ValidateAndSanitize();
                                Config = loaded;
                                return;
                            }
                        }
                    }

                    // Check if a valid .tmp file exists from an interrupted swap
                    string tmpFile = _configFilePath + ".tmp";
                    if (File.Exists(tmpFile))
                    {
                        string tmpJson = File.ReadAllText(tmpFile);
                        if (!string.IsNullOrWhiteSpace(tmpJson))
                        {
                            var loaded = JsonSerializer.Deserialize<NovaSonicsConfig>(tmpJson, JsonOptions);
                            if (loaded != null)
                            {
                                loaded.ValidateAndSanitize();
                                Config = loaded;
                                File.Move(tmpFile, _configFilePath, overwrite: true);
                                return;
                            }
                        }
                    }

                    // Check if a valid config.bak.json fallback exists
                    if (File.Exists(_backupFilePath))
                    {
                        string bakJson = File.ReadAllText(_backupFilePath);
                        if (!string.IsNullOrWhiteSpace(bakJson))
                        {
                            var loaded = JsonSerializer.Deserialize<NovaSonicsConfig>(bakJson, JsonOptions);
                            if (loaded != null)
                            {
                                loaded.ValidateAndSanitize();
                                Config = loaded;
                                Save();
                                return;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // If file is corrupted, preserve it as a backup rather than silently overwriting
                    try
                    {
                        if (File.Exists(_configFilePath))
                        {
                            string corruptBackup = Path.Combine(_configDirectory, $"config.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}.json");
                            File.Copy(_configFilePath, corruptBackup, overwrite: true);
                        }
                    }
                    catch { }

                    // Attempt recovery from config.bak.json
                    try
                    {
                        if (File.Exists(_backupFilePath))
                        {
                            string bakJson = File.ReadAllText(_backupFilePath);
                            var loaded = JsonSerializer.Deserialize<NovaSonicsConfig>(bakJson, JsonOptions);
                            if (loaded != null)
                            {
                                loaded.ValidateAndSanitize();
                                Config = loaded;
                                Save();
                                return;
                            }
                        }
                    }
                    catch { }
                }

                Config = new NovaSonicsConfig();
                Config.ValidateAndSanitize();
                Save();
            }
        }

        private System.Threading.Timer? _saveDebounceTimer;
        private readonly object _saveTimerLock = new();

        public void Save()
        {
            lock (_saveTimerLock)
            {
                _saveDebounceTimer?.Dispose();
                _saveDebounceTimer = new System.Threading.Timer(_ =>
                {
                    SaveImmediate();
                }, null, 150, System.Threading.Timeout.Infinite);
            }
        }

        public void SaveImmediate()
        {
            lock (_saveTimerLock)
            {
                _saveDebounceTimer?.Dispose();
                _saveDebounceTimer = null;
            }

            lock (_fileLock)
            {
                try
                {
                    if (!Directory.Exists(_configDirectory))
                    {
                        Directory.CreateDirectory(_configDirectory);
                    }

                    Config.ValidateAndSanitize();
                    string json = JsonSerializer.Serialize(Config, JsonOptions);
                    string tmpFile = _configFilePath + ".tmp";

                    using (var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var sw = new StreamWriter(fs))
                    {
                        sw.Write(json);
                        sw.Flush();
                        fs.Flush(true);
                    }

                    if (File.Exists(_configFilePath))
                    {
                        try { File.Copy(_configFilePath, _backupFilePath, overwrite: true); } catch { }
                    }

                    File.Move(tmpFile, _configFilePath, overwrite: true);
                }
                catch { }
            }
        }

        public bool ExportConfig(string filePath)
        {
            lock (_fileLock)
            {
                try
                {
                    Config.ValidateAndSanitize();
                    string json = JsonSerializer.Serialize(Config, JsonOptions);
                    File.WriteAllText(filePath, json);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool ImportConfig(string filePath)
        {
            bool success = false;
            lock (_fileLock)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        var info = new FileInfo(filePath);
                        if (info.Length <= 0 || info.Length > 2 * 1024 * 1024) return false;

                        string json = File.ReadAllText(filePath);
                        if (string.IsNullOrWhiteSpace(json)) return false;
                        var imported = JsonSerializer.Deserialize<NovaSonicsConfig>(json, JsonOptions);
                        if (imported != null)
                        {
                            imported.ValidateAndSanitize();
                            Config = imported;
                            Save();
                            success = true;
                        }
                    }
                }
                catch { }
            }

            if (success)
            {
                try { ConfigImported?.Invoke(this, EventArgs.Empty); } catch { }
            }
            return success;
        }
    }
}

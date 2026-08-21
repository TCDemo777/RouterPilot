using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Diagnostics;
using RouterPilot.Models;

namespace RouterPilot.Services
{
    public sealed class ClientProfileService
    {
        private readonly string _filePath;
        private readonly string _legacyFavoritesFilePath;
        private readonly AtomicJsonFileStore _jsonStore;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        public bool LastLoadSucceeded { get; private set; } = true;

        public ClientProfileService(
            ApplicationDataPathProvider? applicationDataPaths = null,
            AtomicJsonFileStore? jsonStore = null)
        {
            string folder = (applicationDataPaths ?? new ApplicationDataPathProvider()).CurrentPath;

            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "client-profiles.json");
            _legacyFavoritesFilePath = Path.Combine(folder, "client-favourites.json");
            _jsonStore = jsonStore ?? new AtomicJsonFileStore();
        }

        public Dictionary<string, ClientProfile> Load()
        {
            LastLoadSucceeded = true;
            try
            {
                if (!File.Exists(_filePath))
                {
                    var migrated = new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase);
                    if (File.Exists(_legacyFavoritesFilePath))
                    {
                        string legacyJson = File.ReadAllText(_legacyFavoritesFilePath);
                        string[] favoriteKeys = JsonSerializer.Deserialize<string[]>(legacyJson) ?? Array.Empty<string>();
                        foreach (string key in favoriteKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
                        {
                            migrated[key] = new ClientProfile
                            {
                                Key = key,
                                IsFavorite = true,
                                FirstSeenUtc = DateTime.UtcNow,
                                LastSeenUtc = DateTime.UtcNow
                            };
                        }
                    }

                    return migrated;
                }

                if (!_jsonStore.TryRead<List<ClientProfile>>(_filePath, _jsonOptions, out List<ClientProfile>? profiles))
                {
                    LastLoadSucceeded = false;
                    return new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase);
                }

                return (profiles ?? [])
                    .Where(profile => !string.IsNullOrWhiteSpace(profile.Key))
                    .GroupBy(profile => profile.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Last(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Debug.WriteLine($"Unable to load client profiles ({ex.GetType().Name}).");
                LastLoadSucceeded = false;
                return new Dictionary<string, ClientProfile>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public bool Save(IEnumerable<ClientProfile> profiles)
        {
            List<ClientProfile> ordered = profiles
                .Where(profile => !string.IsNullOrWhiteSpace(profile.Key))
                .OrderByDescending(profile => profile.IsFavorite)
                .ThenBy(profile => profile.Nickname)
                .ThenBy(profile => profile.Key)
                .ToList();

            try
            {
                _jsonStore.Write(_filePath, ordered, _jsonOptions);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Unable to save client profiles ({ex.GetType().Name}).");
                return false;
            }
        }
    }
}

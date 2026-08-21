using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RouterPilot.Services;

/// <summary>Provides small, per-file atomic JSON read/write primitives for RouterPilot-owned local state.</summary>
public sealed class AtomicJsonFileStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates =
        new(StringComparer.OrdinalIgnoreCase);

#if DEBUG
    // Test-only hook for simulating a failure after the complete temporary file
    // has been flushed but before the destination is replaced.
    internal static Action? BeforeReplaceForTesting { get; set; }
#endif

    public bool TryRead<T>(string path, JsonSerializerOptions? options, out T? value)
    {
        value = default;
        try
        {
            if (!File.Exists(path)) return false;
            string json = File.ReadAllText(path);
            value = JsonSerializer.Deserialize<T>(json, options);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to load local JSON state ({ex.GetType().Name}).");
            return false;
        }
    }

    public void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string targetPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("A local JSON file must have a parent directory.");
        string temporaryPath = targetPath + ".tmp";
        SemaphoreSlim gate = WriteGates.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));

        gate.Wait();
        try
        {
            Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(value, options);

            try
            {
                // Clear a left-over file from a previously interrupted attempt
                // before starting this serialized write.
                TryDeleteTemporaryFile(temporaryPath);

                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

#if DEBUG
                BeforeReplaceForTesting?.Invoke();
#endif

                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Unable to remove a local JSON temporary file ({ex.GetType().Name}).");
        }
    }
}

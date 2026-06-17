/*
 * Application Data Maintenance Service
 * Provides destructive user-requested reset operations for local Clash# data
 *
 * @author: WaterRun
 * @file: Service/AppDataMaintenanceService.cs
 * @date: 2026-06-17
 */

using System;
using System.IO;

namespace ClashSharp.Service;

/// <summary>Provides destructive user-requested reset operations for local Clash# data.</summary>
/// <remarks>
/// Invariants: Clear-all operations are scoped to the local application data directory.
/// Thread safety: Not thread-safe; intended for user-triggered maintenance on the UI thread.
/// Side effects: Stops runtime services, resets settings, and deletes local data files.
/// </remarks>
internal static class AppDataMaintenanceService
{
    /// <summary>Resets all persisted settings to their default values.</summary>
    public static void ResetAllSettings()
    {
        AppSettingsService.Instance.ResetAllSettings();
    }

    /// <summary>Clears all user data including settings, logs, profiles, and generated mihomo configuration.</summary>
    public static void ClearAllData()
    {
        RuntimeShutdownService.Shutdown();
        AppSettingsService.Instance.ResetAllSettings();
        TryClearLogStorage();
        ClearLocalDataDirectory();
        LogStorageService.Instance.ResetAfterDataDeletion();
        ProfileCatalogService.Instance.ResetAfterDataDeletion();
    }

    /// <summary>Clears log storage when the database can be opened before file deletion.</summary>
    private static void TryClearLogStorage()
    {
        try
        {
            LogStorageService.Instance.ClearAll();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            LogStorageService.Instance.AppendLog("Warning", "Maintenance", "Log storage could not be cleared before data deletion.", exception.Message);
        }
    }

    /// <summary>Deletes all files and child directories under the local application data directory.</summary>
    private static void ClearLocalDataDirectory()
    {
        string dataDirectory = Path.GetFullPath(AppDataPathService.ResolveLocalDataDirectory());
        Directory.CreateDirectory(dataDirectory);

        foreach (string filePath in Directory.EnumerateFiles(dataDirectory))
        {
            TryDeleteFile(filePath);
        }

        foreach (string directoryPath in Directory.EnumerateDirectories(dataDirectory))
        {
            TryDeleteDirectory(directoryPath);
        }
    }

    /// <summary>Deletes a file if possible.</summary>
    /// <param name="filePath">File path to delete. Must not be null.</param>
    private static void TryDeleteFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        try
        {
            File.Delete(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogStorageService.Instance.AppendLog("Warning", "Maintenance", "Local data file could not be deleted.", filePath);
        }
    }

    /// <summary>Deletes a directory tree if possible.</summary>
    /// <param name="directoryPath">Directory path to delete. Must not be null.</param>
    private static void TryDeleteDirectory(string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);

        try
        {
            Directory.Delete(directoryPath, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogStorageService.Instance.AppendLog("Warning", "Maintenance", "Local data directory could not be deleted.", directoryPath);
        }
    }
}

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FastScriptReload.Editor;
using System;
using System.Threading;

[InitializeOnLoad]
public class CustomFileWatcher : EditorWindow
{

    public class HashEntry
    {
        public Dictionary<string, string> hashes = new Dictionary<string, string>();

        // Some metadata for the update function to use
        // Note this data isn't exactly synced up or anything. It just reads it in when the filewatcher is initialized.
        public string searchPattern;
        public bool includeSubdirectories;

        public HashEntry(Dictionary<string, string> hashes, string searchPattern, bool includeSubdirectories)
        {
            this.hashes = hashes;
            this.searchPattern = searchPattern;
            this.includeSubdirectories = includeSubdirectories;
        }
    }

    public static Dictionary<string, HashEntry> fileHashes;
    private static object stateLock = new object();

    private static object listLock; // Shared lock object
    private static Thread livewatcherThread;

    public static bool initSignaled = false;

    static CustomFileWatcher()
    {
        // Setting default vals here to avoid some kinda weird race condition with the thread enabler
        fileHashes = new Dictionary<string, HashEntry>();
        listLock = new object();
        livewatcherThread = null;

        // Initialize the filewatcher on startup
        TryEnableLivewatching();
    }

    // Menu hook, actual functionality driven by various methods
    [MenuItem("Window/Fast Script Reload/Initialize Filewatcher")]
    public static void InitializeFileWatchers()
    {
        var fileWatcherSetupEntries = FastScriptReloadPreference.FileWatcherSetupEntries.GetElementsTyped();

        foreach (var fileWatcherSetupEntry in fileWatcherSetupEntries)
        {
            string directoryPath = PathFromSetupEntry(fileWatcherSetupEntry);

            InitializeSingularFilewatcher(directoryPath, fileWatcherSetupEntry.filter, fileWatcherSetupEntry.includeSubdirectories);
        }
    }

    [MenuItem("Window/Fast Script Reload/Update Filewatcher")]
    private static void UpdateFileWatcher()
    {
        if (fileHashes.Count > 0)
        {
            foreach (var kvp in fileHashes)
            {
                CheckForChanges(kvp.Key, kvp.Value.searchPattern, kvp.Value.includeSubdirectories);
            }
        }
        else
        {
            Debug.LogError("File watcher has not been initialized yet. Please initialize first.");
        }
    }

    [MenuItem("Window/Fast Script Reload/Begin Livewatching")]
    public static void TryEnableLivewatching()
    {
        if (livewatcherThread != null)
        {
            Debug.LogWarning("Livewatcher is already running.");
            return;
        }

        // Run on a separate thread every 1 second
        livewatcherThread = new Thread(() =>
        {
            Timer timer = new Timer((state) =>
            {
                // Go at it if we've initialized
                if (fileHashes.Count > 0)
                    UpdateFileWatcher();
            }, null, 0, 1000); // 1000 ms = 1 second
        });

        livewatcherThread.Start();
    }



    // Just handles watching one directory
    public static void InitializeSingularFilewatcher(string directoryPath, string searchPattern, bool includeSubdirectories)
    {
#if ImmersiveVrTools_DebugEnabled
        Debug.Log("Initializing hashes for directory: " + directoryPath);
#endif

        // Delegate all this to a thread too!
        Thread thread = new Thread(() =>
        {

            lock (stateLock)
            {
                var hashes = new Dictionary<string, string>();
                string[] files = Directory.GetFiles(directoryPath, searchPattern, includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

                foreach (string filePath in files)
                {
                    string hash = GetFileHash(filePath);
                    hashes[filePath] = hash;
                }

                fileHashes[directoryPath] = new HashEntry(hashes, searchPattern, includeSubdirectories);
            }
        });
        thread.Start();
    }

    private static void CheckForChanges(string directoryPath, string searchPattern, bool includeSubdirectories)
    {

        // Not really sure if this nuclear locking treatment is right but oh well
        lock (stateLock)
        {
            var hashes = fileHashes[directoryPath].hashes;

            // Time profiling: Start the stopwatch for Directory.GetFiles
#if ImmersiveVrTools_DebugEnabled

            System.Diagnostics.Stopwatch getFilesStopwatch = new System.Diagnostics.Stopwatch();
            getFilesStopwatch.Start();
#endif

            string[] files = Directory.GetFiles(directoryPath, searchPattern, includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

#if ImmersiveVrTools_DebugEnabled

            // Time profiling: Stop the stopwatch for Directory.GetFiles and log the elapsed time
            getFilesStopwatch.Stop();
            Debug.Log("Directory.GetFiles elapsed time: " + getFilesStopwatch.ElapsedMilliseconds + " ms");
#endif

            // Check if files were created or modified
            // Time profiling: Start the stopwatch for file creation/modification
            System.Diagnostics.Stopwatch fileChangeStopwatch = new System.Diagnostics.Stopwatch();
            fileChangeStopwatch.Start();

            foreach (string file in files)
            {
                if (!hashes.ContainsKey(file))
                {
                    // New file
#if ImmersiveVrTools_DebugEnabled
                    Debug.Log("New file: " + file);
#endif
                    continue;
                }

                else if (hashes[file] != GetFileHash(file))
                {
                    // File changed
#if ImmersiveVrTools_DebugEnabled
                    Debug.Log("File changed: " + file);
#endif
                    RecordChange(file);
                }
            }

#if ImmersiveVrTools_DebugEnabled
            // Time profiling: Stop the stopwatch for file creation/modification and log the elapsed time
            fileChangeStopwatch.Stop();
            Debug.Log("File creation/modification elapsed time: " + fileChangeStopwatch.ElapsedMilliseconds + " ms");
#endif

            // Check if any files were deleted
            // Time profiling: Start the stopwatch for file deletion
            System.Diagnostics.Stopwatch fileDeletionStopwatch = new System.Diagnostics.Stopwatch();
            fileDeletionStopwatch.Start();

            foreach (var kvp in hashes)
            {
                if (!File.Exists(kvp.Key))
                {
#if ImmersiveVrTools_DebugEnabled
                    Debug.Log("File deleted: " + kvp.Key);
#endif
                }
            }

            // Time profiling: Stop the stopwatch for file deletion and log the elapsed time
#if ImmersiveVrTools_DebugEnabled
            fileDeletionStopwatch.Stop();
            Debug.Log("File deletion elapsed time: " + fileDeletionStopwatch.ElapsedMilliseconds + " ms");
#endif

            // Update hashes
            hashes.Clear();
            foreach (string file in files)
            {
                string hash = GetFileHash(file);
                hashes[file] = hash;
            }
        }
    }


    private static string GetFileHash(string filePath)
    {
        using (var md5 = MD5.Create())
        {
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hashBytes = md5.ComputeHash(stream);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < hashBytes.Length; i++)
                {
                    sb.Append(hashBytes[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }

    // This baby is thread-safe
    private static void RecordChange(string path)
    {
        FastScriptReloadManager instance = FastScriptReloadManager.Instance;

        if (!instance._isEditorModeHotReloadEnabled && instance._lastPlayModeStateChange != PlayModeStateChange.EnteredPlayMode)
        {
#if ImmersiveVrTools_DebugEnabled
            Debug.Log("Application not playing, change to: " + path + " won't be compiled and hot reloaded");
#endif
            return;
        }

        lock (listLock) // Lock the list before modifying it
        {
            instance._dynamicFileHotReloadStateEntries.Add(new DynamicFileHotReloadState(path, DateTime.UtcNow));
        }
    }


    private static string PathFromSetupEntry(FileWatcherSetupEntry fileWatcherSetupEntry)
    {
        // Replace tokens for path
        string directoryPath = fileWatcherSetupEntry.path;
        foreach (var kv in FastScriptReloadManager.Instance.FileWatcherTokensToResolvePathFn)
        {
            directoryPath = directoryPath.Replace(kv.Key, kv.Value());
        }

        var directoryInfo = new DirectoryInfo(directoryPath);

        if (!directoryInfo.Exists)
        {
            Debug.Log($"FastScriptReload: Directory: '{directoryPath}' does not exist, make sure file-watcher setup is correct. You can access via: Window -> Fast Script Reload -> File Watcher (Advanced Setup)");
        }

        return directoryPath;
    }
}

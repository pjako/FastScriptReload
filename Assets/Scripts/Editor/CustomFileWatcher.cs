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
    public class FileSnapshot
    {
        public DateTime LastWriteTimeUtc { get; }
        public long Length { get; }
        public string Hash { get; }

        public FileSnapshot(DateTime lastWriteTimeUtc, long length, string hash)
        {
            LastWriteTimeUtc = lastWriteTimeUtc;
            Length = length;
            Hash = hash;
        }
    }

    public class HashEntry
    {
        private Dictionary<string, FileSnapshot> _snapshots = new Dictionary<string, FileSnapshot>();
        // Some metadata for the update function to use
        // WARN: Note this data isn't exactly synced up or anything. It just reads it in when the filewatcher is initialized.
        private string _searchPattern;
        private bool _includeSubdirectories;

        public Dictionary<string, FileSnapshot> Snapshots => _snapshots;
        public string SearchPattern => _searchPattern;
        public bool IncludeSubdirectories => _includeSubdirectories;

        public HashEntry(Dictionary<string, FileSnapshot> snapshots, string searchPattern, bool includeSubdirectories)
        {
            _snapshots = snapshots;
            _searchPattern = searchPattern;
            _includeSubdirectories = includeSubdirectories;
        }
    }

    private static Dictionary<string, HashEntry> FileHashes;
    private static object StateLock = new object();

    private static object ListLock; // Shared lock object
    // Kept in a field, an unreferenced Timer can be garbage collected, which silently stops it
    private static Timer LivewatcherTimer;

    public static bool InitSignaled = false;
    private static readonly int WatcherThreadRunEveryNSeconds = 500; //TODO: expose in settings

    static CustomFileWatcher()
    {
        FileHashes = new Dictionary<string, HashEntry>();
        ListLock = new object();
        LivewatcherTimer = null;
    }
    
    private static void UpdateFileWatcher()
    {
        // Watched directories are added from other threads, and a slow check can overlap with the next timer tick
        lock (StateLock)
        {
            if (FileHashes.Count > 0)
            {
                foreach (var kvp in FileHashes)
                {
                    CheckForChanges(kvp.Key, kvp.Value.SearchPattern, kvp.Value.IncludeSubdirectories);
                }
            }
            else
            {
                Debug.LogError("File watcher has not been initialized yet. Please initialize first.");
            }
        }
    }
    
    public static void TryEnableLivewatching()
    {
        if (LivewatcherTimer != null)
        {
            Debug.LogWarning("Livewatcher is already running.");
            return;
        }

        // Timer callbacks run on the thread pool, every WatcherThreadRunEveryNSeconds milliseconds
        LivewatcherTimer = new Timer((state) =>
        {
            // Go at it if we've initialized
            if (FileHashes.Count > 0)
            {
                UpdateFileWatcher();
            }
        }, null, 0, WatcherThreadRunEveryNSeconds);
    }
    
    public static void InitializeSingularFilewatcher(string directoryPath, string searchPattern, bool includeSubdirectories)
    {
#if ImmersiveVrTools_DebugEnabled
        Debug.Log("Initializing hashes for directory: " + directoryPath);
#endif

        var thread = new Thread(() =>
        {
            lock (StateLock)
            {
                var snapshots = new Dictionary<string, FileSnapshot>();
                var files = Directory.GetFiles(directoryPath, searchPattern, includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

                foreach (var filePath in files)
                {
                    if (TryCreateSnapshot(filePath, out var snapshot))
                    {
                        snapshots[filePath] = snapshot;
                    }
                }

                FileHashes[directoryPath] = new HashEntry(snapshots, searchPattern, includeSubdirectories);
            }
        });
        thread.Start();
    }

    private static void CheckForChanges(string directoryPath, string searchPattern, bool includeSubdirectories)
    {
        // Not really sure if this nuclear locking is needed
        lock (StateLock)
        {
            var snapshots = FileHashes[directoryPath].Snapshots;

#if ImmersiveVrTools_DebugEnabled
            var checkStopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif

            string[] files = Directory.GetFiles(directoryPath, searchPattern, includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);

            // Files missing from the new set were deleted and drop out of tracking
            var currentSnapshots = new Dictionary<string, FileSnapshot>(files.Length);
            foreach (var file in files)
            {
                snapshots.TryGetValue(file, out var snapshot);
                try
                {
                    var fileInfo = new FileInfo(file);
                    if (snapshot == null)
                    {
                        // New file, not hot reloaded but tracked from now on
#if ImmersiveVrTools_DebugEnabled
                        Debug.Log("New file: " + file);
#endif
                        snapshot = new FileSnapshot(fileInfo.LastWriteTimeUtc, fileInfo.Length, GetFileHash(file));
                    }
                    else if (snapshot.LastWriteTimeUtc != fileInfo.LastWriteTimeUtc || snapshot.Length != fileInfo.Length)
                    {
                        // Only hashed when timestamp or size changed, the hash filters out saves that didn't change contents
                        var updatedSnapshot = new FileSnapshot(fileInfo.LastWriteTimeUtc, fileInfo.Length, GetFileHash(file));
                        if (updatedSnapshot.Hash != snapshot.Hash)
                        {
#if ImmersiveVrTools_DebugEnabled
                            Debug.Log("File changed: " + file);
#endif
                            RecordChange(file);
                        }
                        snapshot = updatedSnapshot;
                    }
                }
                catch (IOException)
                {
                    // File is being written or was just deleted, the previous snapshot is kept so the change is picked up on a later check
                }

                if (snapshot != null)
                {
                    currentSnapshots[file] = snapshot;
                }
            }

            // Updated in place, FileHashes is being enumerated by the caller
            snapshots.Clear();
            foreach (var kvp in currentSnapshots)
            {
                snapshots[kvp.Key] = kvp.Value;
            }

#if ImmersiveVrTools_DebugEnabled
            Debug.Log("File watcher check elapsed time: " + checkStopwatch.ElapsedMilliseconds + " ms");
#endif
        }
    }

    private static bool TryCreateSnapshot(string filePath, out FileSnapshot snapshot)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            snapshot = new FileSnapshot(fileInfo.LastWriteTimeUtc, fileInfo.Length, GetFileHash(filePath));
            return true;
        }
        catch (IOException)
        {
            snapshot = null;
            return false;
        }
    }

    private static string GetFileHash(string filePath)
    {
        using (var md5 = MD5.Create())
        using (var stream = File.OpenRead(filePath))
        {
            var hashBytes = md5.ComputeHash(stream);
            var sb = new StringBuilder();
            for (var i = 0; i < hashBytes.Length; i++)
            {
                sb.Append(hashBytes[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }

    private static void RecordChange(string path)
    {
        if (FastScriptReloadManager.Instance.ShouldIgnoreFileChange()) return;

        lock (ListLock)
        {
            FastScriptReloadManager.Instance.AddFileChangeToProcess(path);
        }
    }
}

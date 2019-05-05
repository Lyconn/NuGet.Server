// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using NuGet.Server.Core.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NuGet.Server.Core.Infrastructure {
    /// <summary>
    /// ServerPackageRepository represents a folder of nupkgs on disk. All packages are cached during the first request
    /// in order to correctly determine attributes such as IsAbsoluteLatestVersion. Adding, removing, or making changes
    /// to packages on disk will clear the cache. This implementation is the core business logic for dealing with
    /// packages on the server side and deals with the the underlying concerns of storing packages both on disk
    /// and in memory (<see cref="IServerPackageStore"/> and <see cref="IServerPackageCache"/>, respectively).
    /// </summary>
    public class ServerPackageRepository
        : IServerPackageRepository, IDisposable {
        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1);

        private readonly IFileSystem _fileSystem;
        private readonly IServerPackageStore _serverPackageStore;
        private readonly Logging.ILogger _logger;
        private readonly ISettingsProvider _settingsProvider;

        private readonly IServerPackageCache _serverPackageCache;

        private readonly bool _runBackgroundTasks;
        private FileSystemWatcher _fileSystemWatcher;
        private string _watchDirectory;
        private bool _isFileSystemWatcherSuppressed;
        private bool _needsRebuild;

        private Timer _persistenceTimer;
        private Timer _rebuildTimer;

        public ServerPackageRepository(
            string path,
            IHashProvider hashProvider,
            ISettingsProvider settingsProvider = null,
            Logging.ILogger logger = null) {
            if (string.IsNullOrEmpty(path)) {
                throw new ArgumentNullException(nameof(path));
            }

            if (hashProvider == null) {
                throw new ArgumentNullException(nameof(hashProvider));
            }

            this._fileSystem = new PhysicalFileSystem(path);
            this._runBackgroundTasks = true;
            this._settingsProvider = settingsProvider ?? new DefaultSettingsProvider();
            this._logger = logger ?? new TraceLogger();
            this._serverPackageCache = this.InitializeServerPackageCache();
            this._serverPackageStore = new ServerPackageStore(
                this._fileSystem,
                new ExpandedPackageRepository(this._fileSystem, hashProvider),
                this._logger);
        }

        internal ServerPackageRepository(
            IFileSystem fileSystem,
            bool runBackgroundTasks,
            ExpandedPackageRepository innerRepository,
            ISettingsProvider settingsProvider = null,
            Logging.ILogger logger = null) {
            if (innerRepository == null) {
                throw new ArgumentNullException(nameof(innerRepository));
            }

            this._fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            this._runBackgroundTasks = runBackgroundTasks;
            this._settingsProvider = settingsProvider ?? new DefaultSettingsProvider();
            this._logger = logger ?? new TraceLogger();
            this._serverPackageCache = this.InitializeServerPackageCache();
            this._serverPackageStore = new ServerPackageStore(
                this._fileSystem,
                innerRepository,
                this._logger);
        }

        public string Source => this._fileSystem.Root;

        private bool AllowOverrideExistingPackageOnPush =>
            this._settingsProvider.GetBoolSetting("allowOverrideExistingPackageOnPush", false);

        private bool IgnoreSymbolsPackages =>
            this._settingsProvider.GetBoolSetting("ignoreSymbolsPackages", false);

        private bool EnableDelisting =>
            this._settingsProvider.GetBoolSetting("enableDelisting", false);

        private bool EnableFrameworkFiltering =>
            this._settingsProvider.GetBoolSetting("enableFrameworkFiltering", false);

        private bool EnableFileSystemMonitoring =>
            this._settingsProvider.GetBoolSetting("enableFileSystemMonitoring", true);

        private string CacheFileName => this._settingsProvider.GetStringSetting("cacheFileName", null);

        private TimeSpan InitialCacheRebuildAfter {
            get {
                int value = this.GetPositiveIntSetting("initialCacheRebuildAfterSeconds", 15);
                return TimeSpan.FromSeconds(value);
            }
        }

        private TimeSpan CacheRebuildFrequency {
            get {
                int value = this.GetPositiveIntSetting("cacheRebuildFrequencyInMinutes", 60);
                return TimeSpan.FromMinutes(value);
            }
        }

        private int GetPositiveIntSetting(string name, int defaultValue) {
            int value = this._settingsProvider.GetIntSetting(name, defaultValue);
            if (value <= 0) {
                value = defaultValue;
            }

            return value;
        }

        private ServerPackageCache InitializeServerPackageCache() => new ServerPackageCache(this._fileSystem, this.ResolveCacheFileName());

        private string ResolveCacheFileName() {
            string fileName = this.CacheFileName;
            const string suffix = ".cache.bin";

            if (string.IsNullOrWhiteSpace(fileName)) {
                // Default file name
                return Environment.MachineName.ToLowerInvariant() + suffix;
            }

            if (fileName.LastIndexOfAny(Path.GetInvalidFileNameChars()) > 0) {
                string message = string.Format(Strings.Error_InvalidCacheFileName, fileName);

                this._logger.Log(LogLevel.Error, message);

                throw new InvalidOperationException(message);
            }

            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                return fileName;
            }

            return fileName + suffix;
        }

        /// <summary>
        /// Package cache containing packages metadata. 
        /// This data is generated if it does not exist already.
        /// </summary>
        public async Task<IEnumerable<IServerPackage>> GetPackagesAsync(
            ClientCompatibility compatibility,
            CancellationToken token) {
            await this.RebuildIfNeededAsync(shouldLock: true, token: token);

            // First time we come here, attach the file system watcher.
            if (this._fileSystemWatcher == null &&
                this.EnableFileSystemMonitoring &&
                this._runBackgroundTasks) {
                this.RegisterFileSystemWatcher();
            }

            // First time we come here, setup background jobs.
            if (this._persistenceTimer == null &&
                this._runBackgroundTasks) {
                this.SetupBackgroundJobs();
            }

            IEnumerable<ServerPackage> cache = this._serverPackageCache.GetAll();

            if (!compatibility.AllowSemVer2) {
                cache = cache.Where(p => !p.IsSemVer2);
            }

            return cache;
        }

        private async Task RebuildIfNeededAsync(bool shouldLock, CancellationToken token) {
            /*
             * We rebuild the package storage under either of two conditions:
             *
             * 1. If the "needs rebuild" flag is set to true. This is initially the case when the repository is
             *    instantiated, if a non-package drop file system event occurred (e.g. a file deletion), or if the
             *    cache was manually cleared.
             *
             * 2. If the store has no packages at all. This is so we pick up initial packages as quickly as
             *    possible.
             */
            if (this._needsRebuild || this._serverPackageCache.IsEmpty()) {
                if (shouldLock) {
                    using (await this.LockAndSuppressFileSystemWatcherAsync(token)) {
                        // Check the flags again, just in case another thread already did this work.
                        if (this._needsRebuild || this._serverPackageCache.IsEmpty()) {
                            await this.RebuildPackageStoreWithoutLockingAsync(token);
                        }
                    }
                } else {
                    await this.RebuildPackageStoreWithoutLockingAsync(token);
                }
            }
        }

        public async Task<IEnumerable<IServerPackage>> SearchAsync(
            string searchTerm,
            IEnumerable<string> targetFrameworks,
            bool allowPrereleaseVersions,
            ClientCompatibility compatibility,
            CancellationToken token) => await this.SearchAsync(searchTerm, targetFrameworks, allowPrereleaseVersions, false, compatibility, token);

        public async Task<IEnumerable<IServerPackage>> SearchAsync(
            string searchTerm,
            IEnumerable<string> targetFrameworks,
            bool allowPrereleaseVersions,
            bool allowUnlistedVersions,
            ClientCompatibility compatibility,
            CancellationToken token) {
            IEnumerable<IServerPackage> cache = await this.GetPackagesAsync(compatibility, token);

            IEnumerable<IServerPackage> packages = cache
                .Find(searchTerm)
                .FilterByPrerelease(allowPrereleaseVersions);

            if (this.EnableDelisting && !allowUnlistedVersions) {
                packages = packages.Where(p => p.Listed);
            }

            if (this.EnableFrameworkFiltering && targetFrameworks.Any()) {
                // Get the list of framework names
                IEnumerable<System.Runtime.Versioning.FrameworkName> frameworkNames = targetFrameworks
                    .Select(frameworkName => VersionUtility.ParseFrameworkName(frameworkName));

                packages = packages
                    .Where(package => frameworkNames
                        .Any(frameworkName => VersionUtility
                            .IsCompatible(frameworkName, package.GetSupportedFrameworks())));
            }

            return packages;
        }

        internal async Task AddPackagesFromDropFolderAsync(CancellationToken token) {
            using (await this.LockAndSuppressFileSystemWatcherAsync(token)) {
                await this.RebuildIfNeededAsync(shouldLock: false, token: token);

                this.AddPackagesFromDropFolderWithoutLocking();
            }
        }

        /// <summary>
        /// This method requires <see cref="LockAndSuppressFileSystemWatcherAsync(CancellationToken)"/>.
        /// </summary>
        private void AddPackagesFromDropFolderWithoutLocking() {
            this._logger.Log(LogLevel.Info, "Start adding packages from drop folder.");

            try {
                HashSet<ServerPackage> serverPackages = new HashSet<ServerPackage>(IdAndVersionEqualityComparer.Instance);

                foreach (string packageFile in this._fileSystem.GetFiles(this._fileSystem.Root, "*.nupkg", recursive: false)) {
                    try {
                        // Create package
                        IPackage package = PackageFactory.Open(this._fileSystem.GetFullPath(packageFile));

                        if (!this.CanPackageBeAddedWithoutLocking(package, shouldThrow: false)) {
                            continue;
                        }

                        // Add the package to the file system store.
                        ServerPackage serverPackage = this._serverPackageStore.Add(
                            package,
                            this.EnableDelisting);

                        // Keep track of the the package for addition to metadata store.
                        serverPackages.Add(serverPackage);

                        // Remove file from drop folder
                        this._fileSystem.DeleteFile(packageFile);
                    } catch (UnauthorizedAccessException ex) {
                        // The file may be in use (still being copied) - ignore the error
                        this._logger.Log(LogLevel.Error, "Error adding package file {0} from drop folder: {1}", packageFile, ex.Message);
                    } catch (IOException ex) {
                        // The file may be in use (still being copied) - ignore the error
                        this._logger.Log(LogLevel.Error, "Error adding package file {0} from drop folder: {1}", packageFile, ex.Message);
                    }
                }

                // Add packages to metadata store in bulk
                this._serverPackageCache.AddRange(serverPackages, this.EnableDelisting);
                this._serverPackageCache.PersistIfDirty();

                this._logger.Log(LogLevel.Info, "Finished adding packages from drop folder.");
            } finally {
                OptimizedZipPackage.PurgeCache();
            }
        }

        /// <summary>
        /// Add a file to the repository.
        /// </summary>
        public async Task AddPackageAsync(IPackage package, CancellationToken token) {
            this._logger.Log(LogLevel.Info, "Start adding package {0} {1}.", package.Id, package.Version);

            using (await this.LockAndSuppressFileSystemWatcherAsync(token)) {
                await this.RebuildIfNeededAsync(shouldLock: false, token: token);

                this.CanPackageBeAddedWithoutLocking(package, shouldThrow: true);

                // Add the package to the file system store.
                ServerPackage serverPackage = this._serverPackageStore.Add(
                    package,
                    this.EnableDelisting);

                // Add the package to the metadata store.
                this._serverPackageCache.Add(serverPackage, this.EnableDelisting);

                this._logger.Log(LogLevel.Info, "Finished adding package {0} {1}.", package.Id, package.Version);
            }
        }

        private bool CanPackageBeAddedWithoutLocking(IPackage package, bool shouldThrow) {
            if (this.IgnoreSymbolsPackages && package.IsSymbolsPackage()) {
                string message = string.Format(Strings.Error_SymbolsPackagesIgnored, package);

                this._logger.Log(LogLevel.Error, message);

                if (shouldThrow) {
                    throw new InvalidOperationException(message);
                }

                return false;
            }

            // Does the package already exist?
            if (!this.AllowOverrideExistingPackageOnPush &&
                this._serverPackageCache.Exists(package.Id, package.Version)) {
                string message = string.Format(Strings.Error_PackageAlreadyExists, package);

                this._logger.Log(LogLevel.Error, message);

                if (shouldThrow) {
                    throw new InvalidOperationException(message);
                }

                return false;
            }

            return true;
        }

        /// <summary>
        /// Remove a package from the repository.
        /// </summary>
        public async Task RemovePackageAsync(string id, SemanticVersion version, CancellationToken token) {
            this._logger.Log(LogLevel.Info, "Start removing package {0} {1}.", id, version);

            IServerPackage package = await this.FindPackageAsync(id, version, token);

            if (package == null) {
                this._logger.Log(LogLevel.Info, "No-op when removing package {0} {1} because it doesn't exist.", id, version);
                return;
            }

            using (await this.LockAndSuppressFileSystemWatcherAsync(token)) {
                // Update the file system.
                this._serverPackageStore.Remove(package.Id, package.Version, this.EnableDelisting);

                // Update the metadata store.
                this._serverPackageCache.Remove(package.Id, package.Version, this.EnableDelisting);

                if (this.EnableDelisting) {
                    this._logger.Log(LogLevel.Info, "Unlisted package {0} {1}.", package.Id, package.Version);
                } else {

                    this._logger.Log(LogLevel.Info, "Finished removing package {0} {1}.", package.Id, package.Version);
                }
            }
        }

        public void Dispose() {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if (this._persistenceTimer != null) {
                this._persistenceTimer.Dispose();
            }

            if (this._rebuildTimer != null) {
                this._rebuildTimer.Dispose();
            }

            this.UnregisterFileSystemWatcher();
            this._serverPackageCache.PersistIfDirty();
        }

        /// <summary>
        /// This is an event handler for background work. Therefore, it should never throw exceptions.
        /// </summary>
        private async void RebuildPackageStoreAsync(CancellationToken token) {
            try {
                using (await this.LockAndSuppressFileSystemWatcherAsync(token)) {
                    await this.RebuildPackageStoreWithoutLockingAsync(token);
                }
            } catch (Exception exception) {
                this._logger.Log(LogLevel.Error, "An exception occurred while rebuilding the package store: {0}", exception);
            }
        }

        /// <summary>
        /// This method requires <see cref="LockAndSuppressFileSystemWatcherAsync(CancellationToken)"/>.
        /// </summary>
        private async Task RebuildPackageStoreWithoutLockingAsync(CancellationToken token) {
            this._logger.Log(LogLevel.Info, "Start rebuilding package store...");

            // Build cache
            HashSet<ServerPackage> packages = await this.ReadPackagesFromDiskWithoutLockingAsync(token);
            this._serverPackageCache.Clear();
            this._serverPackageCache.AddRange(packages, this.EnableDelisting);

            // Add packages from drop folder
            this.AddPackagesFromDropFolderWithoutLocking();

            // Persist
            this._serverPackageCache.PersistIfDirty();

            this._needsRebuild = false;

            this._logger.Log(LogLevel.Info, "Finished rebuilding package store.");
        }

        /// <summary>
        /// Loads all packages from disk and determines additional metadata such as the hash,
        /// IsAbsoluteLatestVersion, and IsLatestVersion.
        /// 
        /// This method requires <see cref="LockAndSuppressFileSystemWatcherAsync(CancellationToken)"/>.
        /// </summary>
        private async Task<HashSet<ServerPackage>> ReadPackagesFromDiskWithoutLockingAsync(CancellationToken token) {
            this._logger.Log(LogLevel.Info, "Start reading packages from disk...");

            try {
                HashSet<ServerPackage> packages = await this._serverPackageStore.GetAllAsync(this.EnableDelisting, token);

                this._logger.Log(LogLevel.Info, "Finished reading packages from disk.");

                return packages;
            } catch (Exception ex) {
                this._logger.Log(
                    LogLevel.Error,
                    "Error while reading packages from disk: {0} {1}",
                    ex.Message,
                    ex.StackTrace);

                throw;
            }
        }

        /// <summary>
        /// Sets the current cache to null so it will be regenerated next time.
        /// </summary>
        public async Task ClearCacheAsync(CancellationToken token) {
            using (await this.LockAndSuppressFileSystemWatcherAsync(token)) {
                OptimizedZipPackage.PurgeCache();

                this._serverPackageCache.Clear();
                this._serverPackageCache.Persist();
                this._needsRebuild = true;
                this._logger.Log(LogLevel.Info, "Cleared package cache.");
            }
        }

        private void SetupBackgroundJobs() {
            this._logger.Log(LogLevel.Info, "Registering background jobs...");

            // Persist to package store at given interval (when dirty)
            this._logger.Log(LogLevel.Info, "Persisting the cache file every 1 minute.");
            this._persistenceTimer = new Timer(
                callback: state => this._serverPackageCache.PersistIfDirty(),
                state: null,
                dueTime: TimeSpan.FromMinutes(1),
                period: TimeSpan.FromMinutes(1));

            // Rebuild the package store in the background
            this._logger.Log(LogLevel.Info, "Rebuilding the cache file for the first time after {0} second(s).", this.InitialCacheRebuildAfter.TotalSeconds);
            this._logger.Log(LogLevel.Info, "Rebuilding the cache file every {0} hour(s).", this.CacheRebuildFrequency.TotalHours);
            this._rebuildTimer = new Timer(
                callback: state => this.RebuildPackageStoreAsync(CancellationToken.None),
                state: null,
                dueTime: this.InitialCacheRebuildAfter,
                period: this.CacheRebuildFrequency);

            this._logger.Log(LogLevel.Info, "Finished registering background jobs.");
        }

        /// <summary>
        /// Registers the file system watcher to monitor changes on disk.
        /// </summary>
        private void RegisterFileSystemWatcher() {
            // When files are moved around, recreate the package cache
            if (this.EnableFileSystemMonitoring && this._runBackgroundTasks && this._fileSystemWatcher == null && !string.IsNullOrEmpty(this.Source) && Directory.Exists(this.Source)) {
                // ReSharper disable once UseObjectOrCollectionInitializer
                this._fileSystemWatcher = new FileSystemWatcher(this.Source) {
                    Filter = "*",
                    IncludeSubdirectories = true,
                };

                //Keep the normalized watch path.
                this._watchDirectory = Path.GetFullPath(this._fileSystemWatcher.Path);

                this._fileSystemWatcher.Changed += this.FileSystemChangedAsync;
                this._fileSystemWatcher.Created += this.FileSystemChangedAsync;
                this._fileSystemWatcher.Deleted += this.FileSystemChangedAsync;
                this._fileSystemWatcher.Renamed += this.FileSystemChangedAsync;

                this._fileSystemWatcher.EnableRaisingEvents = true;

                this._logger.Log(LogLevel.Verbose, "Created FileSystemWatcher - monitoring {0}.", this.Source);
            }
        }

        /// <summary>
        /// Unregisters and clears events of the file system watcher to monitor changes on disk.
        /// </summary>
        private void UnregisterFileSystemWatcher() {
            if (this._fileSystemWatcher != null) {
                this._fileSystemWatcher.EnableRaisingEvents = false;
                this._fileSystemWatcher.Changed -= this.FileSystemChangedAsync;
                this._fileSystemWatcher.Created -= this.FileSystemChangedAsync;
                this._fileSystemWatcher.Deleted -= this.FileSystemChangedAsync;
                this._fileSystemWatcher.Renamed -= this.FileSystemChangedAsync;
                this._fileSystemWatcher.Dispose();
                this._fileSystemWatcher = null;

                this._logger.Log(LogLevel.Verbose, "Destroyed FileSystemWatcher - no longer monitoring {0}.", this.Source);
            }

            this._watchDirectory = null;
        }

        /// <summary>
        /// This is an event handler for background work. Therefore, it should never throw exceptions.
        /// </summary>
        private async void FileSystemChangedAsync(object sender, FileSystemEventArgs e) {
            try {
                if (this._isFileSystemWatcherSuppressed) {
                    return;
                }

                if (this.ShouldIgnoreFileSystemEvent(e)) {
                    this._logger.Log(LogLevel.Verbose, "File system event ignored. File: {0} - Change: {1}", e.Name, e.ChangeType);
                    return;
                }

                this._logger.Log(LogLevel.Verbose, "File system changed. File: {0} - Change: {1}", e.Name, e.ChangeType);

                string changedDirectory = Path.GetDirectoryName(e.FullPath);
                if (changedDirectory == null || this._watchDirectory == null) {
                    return;
                }

                changedDirectory = Path.GetFullPath(changedDirectory);

                // 1) If a .nupkg is dropped in the root, add it as a package
                if (string.Equals(changedDirectory, this._watchDirectory, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Path.GetExtension(e.Name), ".nupkg", StringComparison.OrdinalIgnoreCase)) {
                    // When a package is dropped into the server packages root folder, add it to the repository.
                    await this.AddPackagesFromDropFolderAsync(CancellationToken.None);
                }

                // 2) If a file is updated in a subdirectory, *or* a folder is deleted, invalidate the cache
                if ((!string.Equals(changedDirectory, this._watchDirectory, StringComparison.OrdinalIgnoreCase) && File.Exists(e.FullPath))
                    || e.ChangeType == WatcherChangeTypes.Deleted) {
                    // TODO: invalidating *all* packages for every nupkg change under this folder seems more expensive than it should.
                    // Recommend using e.FullPath to figure out which nupkgs need to be (re)computed.

                    await this.ClearCacheAsync(CancellationToken.None);
                }
            } catch (Exception exception) {
                this._logger.Log(LogLevel.Error, "An exception occurred while handling a file system event: {0}", exception);
            }
        }

        private bool ShouldIgnoreFileSystemEvent(FileSystemEventArgs e) {
            // We can only ignore Created or Changed events. All other types are always processed. Eventually we could
            // try to ignore some Deleted events in the case of API package delete, but this is harder.
            if (e.ChangeType != WatcherChangeTypes.Created
                && e.ChangeType != WatcherChangeTypes.Changed) {
                this._logger.Log(LogLevel.Verbose, "The file system event change type is not ignorable.");
                return false;
            }

            /// We can only ignore events related to file paths changed by the
            /// <see cref="ExpandedPackageRepository"/>. If the file system event is representing a known file path
            /// extracted during package push, we can ignore the event. File system events are supressed during package
            /// push but this is still necessary since file system events can come some time after the suppression
            /// window has ended.
            if (!KnownPathUtility.TryParseFileName(e.Name, out string id, out SemanticVersion version)) {
                this._logger.Log(LogLevel.Verbose, "The file system event is not related to a known package path.");
                return false;
            }

            /// The file path could have been generated by <see cref="ExpandedPackageRepository"/>. Now
            /// determine if the package is in the cache.
            ServerPackage matchingPackage = this._serverPackageCache
                .GetAll()
                .Where(p => StringComparer.OrdinalIgnoreCase.Equals(p.Id, id))
                .Where(p => version.Equals(p.Version))
                .FirstOrDefault();

            if (matchingPackage == null) {
                this._logger.Log(LogLevel.Verbose, "The file system event is not related to a known package.");
                return false;
            }

            FileInfo fileInfo = new FileInfo(e.FullPath);
            if (!fileInfo.Exists) {
                this._logger.Log(LogLevel.Verbose, "The package file is missing.");
                return false;
            }

            DateTimeOffset minimumCreationTime = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (fileInfo.CreationTimeUtc < minimumCreationTime) {
                this._logger.Log(LogLevel.Verbose, "The package file was not created recently.");
                return false;
            }

            return true;
        }

        private async Task<Lock> LockAsync(CancellationToken token) {
            Lock handle = new Lock(this._syncLock);
            await handle.WaitAsync(token);
            return handle;
        }

        private async Task<SuppressedFileSystemWatcher> LockAndSuppressFileSystemWatcherAsync(CancellationToken token) {
            SuppressedFileSystemWatcher handle = new SuppressedFileSystemWatcher(this);
            await handle.WaitAsync(token);
            return handle;
        }

        /// <summary>
        /// A disposable type that wraps a semaphore so dispose releases the semaphore. This allows for more ergonomic
        /// used (such as in a <code>using</code> statement).
        /// </summary>
        private sealed class Lock : IDisposable {
            private readonly SemaphoreSlim _semaphore;
            private bool _lockTaken;

            public Lock(SemaphoreSlim semaphore) {
                this._semaphore = semaphore;
            }

            public bool LockTaken => this._lockTaken;

            public async Task WaitAsync(CancellationToken token) {
                await this._semaphore.WaitAsync(token);
                this._lockTaken = true;
            }

            public void Dispose() {
                if (this._lockTaken) {
                    this._semaphore.Release();
                    this._lockTaken = false;
                }
            }
        }

        private sealed class SuppressedFileSystemWatcher : IDisposable {
            private readonly ServerPackageRepository _repository;
            private Lock _lockHandle;

            public SuppressedFileSystemWatcher(ServerPackageRepository repository) {
                this._repository = repository ?? throw new ArgumentNullException(nameof(repository));
            }

            public bool LockTaken => this._lockHandle.LockTaken;

            public async Task WaitAsync(CancellationToken token) {
                this._lockHandle = await this._repository.LockAsync(token);
                this._repository._isFileSystemWatcherSuppressed = true;
            }

            public void Dispose() {
                if (this._lockHandle != null && this._lockHandle.LockTaken) {
                    this._repository._isFileSystemWatcherSuppressed = false;
                    this._lockHandle.Dispose();
                }
            }
        }
    }
}
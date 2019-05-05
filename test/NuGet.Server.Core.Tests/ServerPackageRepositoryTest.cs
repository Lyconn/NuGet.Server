﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Moq;
using NuGet.Server.Core.Infrastructure;
using NuGet.Server.Core.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NuGet.Server.Core.Tests {
    public class ServerPackageRepositoryTest {
        private static CancellationToken Token => CancellationToken.None;

        public static async Task<ServerPackageRepository> CreateServerPackageRepositoryAsync(
            string path,
            Action<ExpandedPackageRepository> setupRepository = null,
            Func<string, object, object> getSetting = null) {
            PhysicalFileSystem fileSystem = new PhysicalFileSystem(path);
            ExpandedPackageRepository expandedPackageRepository = new ExpandedPackageRepository(fileSystem);

            setupRepository?.Invoke(expandedPackageRepository);

            ServerPackageRepository serverRepository = new ServerPackageRepository(
                fileSystem,
                runBackgroundTasks: false,
                innerRepository: expandedPackageRepository,
                logger: new Infrastructure.NullLogger(),
                settingsProvider: getSetting != null ? new FuncSettingsProvider(getSetting) : null);

            await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token); // caches the files

            return serverRepository;
        }

        private async Task<ServerPackageRepository> CreateServerPackageRepositoryWithSemVer2Async(
            TemporaryDirectory temporaryDirectory) {
            return await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                repository.AddPackage(this.CreatePackage("test1", "1.0"));
                repository.AddPackage(this.CreatePackage("test2", "1.0-beta"));
                repository.AddPackage(this.CreatePackage("test3", "1.0-beta.1"));
                repository.AddPackage(this.CreatePackage("test4", "1.0-beta+foo"));
                repository.AddPackage(this.CreatePackage(
                    "test5",
                    "1.0-beta",
                    new PackageDependency(
                        "SomePackage",
                        VersionUtility.ParseVersionSpec("1.0.0-beta.1"))));
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ServerPackageRepositoryAddsPackagesFromDropFolderOnStart(bool allowOverride) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                Dictionary<string, IPackage> packagesToAddToDropFolder = new Dictionary<string, IPackage>
                {
                    {"test.1.11.nupkg", this.CreatePackage("test", "1.11")},
                    {"test.1.9.nupkg", this.CreatePackage("test", "1.9")},
                    {"test.2.0-alpha.nupkg", this.CreatePackage("test", "2.0-alpha")},
                    {"test.2.0.0.nupkg", this.CreatePackage("test", "2.0.0")},
                    {"test.2.0.0-0test.nupkg", this.CreatePackage("test", "2.0.0-0test")},
                    {"test.2.0.0-test+tag.nupkg", this.CreatePackage("test", "2.0.0-test+tag")}
                };
                foreach (KeyValuePair<string, IPackage> packageToAddToDropFolder in packagesToAddToDropFolder) {
                    using (FileStream stream = File.Create(
                        Path.Combine(temporaryDirectory.Path, packageToAddToDropFolder.Key))) {
                        packageToAddToDropFolder.Value.GetStream().CopyTo(stream);
                    }
                }

                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(
                    temporaryDirectory.Path,
                    getSetting: (key, defaultValue) => {
                        if (key == "allowOverrideExistingPackageOnPush") {
                            return allowOverride;
                        }

                        return defaultValue;
                    });

                // Act
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token);

                // Assert
                Assert.Equal(packagesToAddToDropFolder.Count, packages.Count());
                foreach (KeyValuePair<string, IPackage> packageToAddToDropFolder in packagesToAddToDropFolder) {
                    IServerPackage package = packages.FirstOrDefault(
                            p => p.Id == packageToAddToDropFolder.Value.Id
                                && p.Version == packageToAddToDropFolder.Value.Version);

                    // check the package from drop folder has been added
                    Assert.NotNull(package);

                    // check the package in the drop folder has been removed
                    Assert.False(File.Exists(Path.Combine(temporaryDirectory.Path, packageToAddToDropFolder.Key)));
                }
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryRemovePackage() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test", "1.11"));
                    repository.AddPackage(this.CreatePackage("test", "1.9"));
                    repository.AddPackage(this.CreatePackage("test", "2.0-alpha"));
                    repository.AddPackage(this.CreatePackage("test", "2.0.0"));
                    repository.AddPackage(this.CreatePackage("test", "2.0.0-0test"));
                    repository.AddPackage(this.CreatePackage("test", "2.0.0-test+tag"));
                    repository.AddPackage(this.CreatePackage("test", "2.0.1+taggedOnly"));
                });

                // Act
                await serverRepository.RemovePackageAsync("test", new SemanticVersion("1.11"), Token);
                await serverRepository.RemovePackageAsync("test", new SemanticVersion("2.0-alpha"), Token);
                await serverRepository.RemovePackageAsync("test", new SemanticVersion("2.0.1"), Token);
                await serverRepository.RemovePackageAsync("test", new SemanticVersion("2.0.0-0test"), Token);
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token);

                // Assert
                Assert.Equal(3, packages.Count());
                Assert.Equal(1, packages.Count(p => p.SemVer2IsLatest));
                Assert.Equal("2.0.0", packages.First(p => p.SemVer2IsLatest).Version.ToString());

                Assert.Equal(1, packages.Count(p => p.SemVer2IsAbsoluteLatest));
                Assert.Equal("2.0.0", packages.First(p => p.SemVer2IsAbsoluteLatest).Version.ToString());
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryNeedsRebuildIsHandledWhenAddingAfterClear() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path);

                // Act
                await serverRepository.ClearCacheAsync(Token);
                await serverRepository.AddPackageAsync(this.CreatePackage("test", "1.2"), Token);
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token);

                // Assert
                packages = packages.OrderBy(p => p.Version);

                Assert.Single(packages);
                Assert.Equal(new SemanticVersion("1.2.0"), packages.ElementAt(0).Version);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ServerPackageRepository_DuplicateAddAfterClearObservesOverrideOption(bool allowOverride) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(
                    temporaryDirectory.Path,
                    getSetting: (key, defaultValue) => {
                        if (key == "allowOverrideExistingPackageOnPush") {
                            return allowOverride;
                        }

                        return defaultValue;
                    });

                await serverRepository.AddPackageAsync(this.CreatePackage("test", "1.2"), Token);
                await serverRepository.ClearCacheAsync(Token);

                // Act & Assert
                if (allowOverride) {
                    await serverRepository.AddPackageAsync(this.CreatePackage("test", "1.2"), Token);
                } else {
                    await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                        await serverRepository.AddPackageAsync(this.CreatePackage("test", "1.2"), Token));
                }
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ServerPackageRepository_DuplicateInDropFolderAfterClearObservesOverrideOption(bool allowOverride) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(
                    temporaryDirectory.Path,
                    getSetting: (key, defaultValue) => {
                        if (key == "allowOverrideExistingPackageOnPush") {
                            return allowOverride;
                        }

                        return defaultValue;
                    });

                await serverRepository.AddPackageAsync(this.CreatePackage("test", "1.2"), Token);
                IServerPackage existingPackage = await serverRepository.FindPackageAsync(
                    "test",
                    new SemanticVersion("1.2"),
                    Token);
                string dropFolderPackagePath = Path.Combine(temporaryDirectory, "test.nupkg");
                await serverRepository.ClearCacheAsync(Token);

                // Act
                File.Copy(existingPackage.FullPath, dropFolderPackagePath);
                await serverRepository.AddPackagesFromDropFolderAsync(Token);

                // Assert
                Assert.NotEqual(allowOverride, File.Exists(dropFolderPackagePath));
            }
        }

        [Fact]
        public async Task ServerPackageRepositorySearch() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test", "1.0"));
                    repository.AddPackage(this.CreatePackage("test2", "1.0"));
                    repository.AddPackage(this.CreatePackage("test3", "1.0-alpha"));
                    repository.AddPackage(this.CreatePackage("test3", "2.0.0"));
                    repository.AddPackage(this.CreatePackage("test4", "2.0"));
                    repository.AddPackage(this.CreatePackage("test5", "1.0.0-0test"));
                    repository.AddPackage(this.CreatePackage("test6", "1.2.3+taggedOnly"));
                });

                // Act
                IEnumerable<IServerPackage> includePrerelease = await serverRepository.SearchAsync(
                    "test3",
                    allowPrereleaseVersions: true,
                    compatibility: ClientCompatibility.Max,
                    token: Token);
                IEnumerable<IServerPackage> excludePrerelease = await serverRepository.SearchAsync(
                    "test3",
                    allowPrereleaseVersions: false,
                    compatibility: ClientCompatibility.Max,
                    token: Token);
                IEnumerable<IServerPackage> ignoreTag = await serverRepository.SearchAsync(
                    "test6",
                    allowPrereleaseVersions: false,
                    compatibility: ClientCompatibility.Max,
                    token: Token);

                // Assert
                Assert.Equal("test3", includePrerelease.First().Id);
                Assert.Equal(2, includePrerelease.Count());
                Assert.Single(excludePrerelease);
                Assert.Equal("test6", ignoreTag.First().Id);
                Assert.Single(ignoreTag);
            }
        }

        [Fact]
        public async Task ServerPackageRepositorySearchSupportsFilteringOutSemVer2() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await this.CreateServerPackageRepositoryWithSemVer2Async(temporaryDirectory);

                // Act
                IEnumerable<IServerPackage> actual = await serverRepository.SearchAsync(
                    "test",
                    targetFrameworks: Enumerable.Empty<string>(),
                    allowPrereleaseVersions: true,
                    compatibility: ClientCompatibility.Default,
                    token: Token);

                // Assert
                List<IServerPackage> packages = actual.OrderBy(p => p.Id).ToList();
                Assert.Equal(2, packages.Count);
                Assert.Equal("test1", packages[0].Id);
                Assert.Equal("test2", packages[1].Id);
            }
        }

        [Fact]
        public async Task ServerPackageRepositorySearchUnlisted() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test1", "1.0"));
                }, EnableDelisting);

                // Assert base setup
                List<IServerPackage> packages = (await serverRepository.SearchAsync(
                    "test1",
                    allowPrereleaseVersions: true,
                    compatibility: ClientCompatibility.Max,
                    token: Token)).ToList();
                Assert.Single(packages);
                Assert.Equal("test1", packages[0].Id);
                Assert.Equal("1.0", packages[0].Version.ToString());

                // Delist the package
                await serverRepository.RemovePackageAsync("test1", new SemanticVersion("1.0"), Token);

                // Verify that the package is not returned by search
                packages = (await serverRepository.SearchAsync(
                    "test1",
                    allowPrereleaseVersions: true,
                    compatibility: ClientCompatibility.Max,
                    token: Token)).ToList();
                Assert.Empty(packages);

                // Act: search with includeDelisted=true
                packages = (await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token)).ToList();

                // Assert
                Assert.Single(packages);
                Assert.Equal("test1", packages[0].Id);
                Assert.Equal("1.0", packages[0].Version.ToString());
                Assert.False(packages[0].Listed);
            }
        }

        [Fact]
        public async Task ServerPackageRepositorySearchUnlistingDisabledAndExclude() {
            await this.ServerPackageRepositorySearchUnlistedWithOptions(
                enableUnlisting: false,
                allowUnlistedVersions: false,
                searchable: false,
                gettable: false);
        }

        [Fact]
        public async Task ServerPackageRepositorySearchUnlistingDisabledAndInclude() {
            await this.ServerPackageRepositorySearchUnlistedWithOptions(
                enableUnlisting: false,
                allowUnlistedVersions: true,
                searchable: false,
                gettable: false);
        }

        [Fact]
        public async Task ServerPackageRepositorySearchUnlistingEnabledAndExclude() {
            await this.ServerPackageRepositorySearchUnlistedWithOptions(
                enableUnlisting: true,
                allowUnlistedVersions: false,
                searchable: false,
                gettable: true);
        }

        [Fact]
        public async Task ServerPackageRepositorySearchUnlistingEnabledAndInclude() {
            await this.ServerPackageRepositorySearchUnlistedWithOptions(
                enableUnlisting: true,
                allowUnlistedVersions: true,
                searchable: true,
                gettable: true);
        }

        private async Task ServerPackageRepositorySearchUnlistedWithOptions(
            bool enableUnlisting, bool allowUnlistedVersions, bool searchable, bool gettable) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                Func<string, object, object> getSetting = enableUnlisting ? EnableDelisting : (Func<string, object, object>)null;
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test1", "1.0"));
                }, getSetting);

                // Remove the package
                await serverRepository.RemovePackageAsync("test1", new SemanticVersion("1.0"), Token);

                // Verify that the package is not returned by search
                List<IServerPackage> packages = (await serverRepository.SearchAsync(
                    "test1",
                    allowPrereleaseVersions: true,
                    allowUnlistedVersions: allowUnlistedVersions,
                    compatibility: ClientCompatibility.Max,
                    token: Token)).ToList();
                if (searchable) {
                    Assert.Single(packages);
                    Assert.Equal("test1", packages[0].Id);
                    Assert.Equal("1.0", packages[0].Version.ToString());
                    Assert.False(packages[0].Listed);
                } else {
                    Assert.Empty(packages);
                }

                // Act: search with includeDelisted=true
                packages = (await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token)).ToList();

                // Assert
                if (gettable) {
                    Assert.Single(packages);
                    Assert.Equal("test1", packages[0].Id);
                    Assert.Equal("1.0", packages[0].Version.ToString());
                    Assert.False(packages[0].Listed);
                } else {
                    Assert.Empty(packages);
                }
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryFindPackageById() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test", "1.0"));
                    repository.AddPackage(this.CreatePackage("test2", "1.0"));
                    repository.AddPackage(this.CreatePackage("test3", "1.0-alpha"));
                    repository.AddPackage(this.CreatePackage("test4", "2.0"));
                    repository.AddPackage(this.CreatePackage("test4", "3.0.0+tagged"));
                    repository.AddPackage(this.CreatePackage("Not5", "4.0"));
                });

                // Act
                IEnumerable<IServerPackage> valid = await serverRepository.FindPackagesByIdAsync("test", ClientCompatibility.Max, Token);
                IEnumerable<IServerPackage> invalid = await serverRepository.FindPackagesByIdAsync("bad", ClientCompatibility.Max, Token);

                // Assert
                Assert.Equal("test", valid.First().Id);
                Assert.Empty(invalid);
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryFindPackageByIdSupportsFilteringOutSemVer2() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await this.CreateServerPackageRepositoryWithSemVer2Async(temporaryDirectory);

                // Act
                IEnumerable<IServerPackage> actual = await serverRepository.FindPackagesByIdAsync("test3", ClientCompatibility.Default, Token);

                // Assert
                Assert.Empty(actual);
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryGetPackagesSupportsFilteringOutSemVer2() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await this.CreateServerPackageRepositoryWithSemVer2Async(temporaryDirectory);

                // Act
                IEnumerable<IServerPackage> actual = await serverRepository.GetPackagesAsync(ClientCompatibility.Default, Token);

                // Assert
                List<IServerPackage> packages = actual.OrderBy(p => p.Id).ToList();
                Assert.Equal(2, packages.Count);
                Assert.Equal("test1", packages[0].Id);
                Assert.Equal("test2", packages[1].Id);
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryGetPackagesSupportsIncludingSemVer2() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await this.CreateServerPackageRepositoryWithSemVer2Async(temporaryDirectory);

                // Act
                IEnumerable<IServerPackage> actual = await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token);

                // Assert
                List<IServerPackage> packages = actual.OrderBy(p => p.Id).ToList();
                Assert.Equal(5, packages.Count);
                Assert.Equal("test1", packages[0].Id);
                Assert.Equal("test2", packages[1].Id);
                Assert.Equal("test3", packages[2].Id);
                Assert.Equal("test4", packages[3].Id);
                Assert.Equal("test5", packages[4].Id);
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryFindPackage() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test", "1.0"));
                    repository.AddPackage(this.CreatePackage("test2", "1.0"));
                    repository.AddPackage(this.CreatePackage("test3", "1.0.0-alpha"));
                    repository.AddPackage(this.CreatePackage("test4", "2.0"));
                    repository.AddPackage(this.CreatePackage("test4", "3.0.0+tagged"));
                    repository.AddPackage(this.CreatePackage("Not5", "4.0.0"));
                });

                // Act
                IServerPackage valid = await serverRepository.FindPackageAsync("test4", new SemanticVersion("3.0.0"), Token);
                IServerPackage valid2 = await serverRepository.FindPackageAsync("Not5", new SemanticVersion("4.0"), Token);
                IServerPackage validPreRel = await serverRepository.FindPackageAsync(
                    "test3",
                    new SemanticVersion("1.0.0-alpha"),
                    Token);
                IServerPackage invalidPreRel = await serverRepository.FindPackageAsync(
                    "test3",
                    new SemanticVersion("1.0.0"),
                    Token);
                IServerPackage invalid = await serverRepository.FindPackageAsync("bad", new SemanticVersion("1.0"), Token);

                // Assert
                Assert.Equal("test4", valid.Id);
                Assert.Equal("Not5", valid2.Id);
                Assert.Equal("test3", validPreRel.Id);
                Assert.Null(invalidPreRel);
                Assert.Null(invalid);
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryMultipleIds() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test", "0.9"));
                    repository.AddPackage(this.CreatePackage("test", "1.0"));
                    repository.AddPackage(this.CreatePackage("test2", "1.0"));
                    repository.AddPackage(this.CreatePackage("test3", "1.0-alpha"));
                    repository.AddPackage(this.CreatePackage("test3", "2.0.0+taggedOnly"));
                    repository.AddPackage(this.CreatePackage("test4", "2.0"));
                    repository.AddPackage(this.CreatePackage("test4", "3.0.0"));
                    repository.AddPackage(this.CreatePackage("test5", "2.0.0-onlyPre+tagged"));
                });

                // Act
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token);

                // Assert
                Assert.Equal(5, packages.Count(p => p.SemVer2IsAbsoluteLatest));
                Assert.Equal(4, packages.Count(p => p.SemVer2IsLatest));
                Assert.Equal(3, packages.Count(p => !p.SemVer2IsAbsoluteLatest));
                Assert.Equal(4, packages.Count(p => !p.SemVer2IsLatest));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ServerPackageRepositorySemVer1IsAbsoluteLatest(bool enableDelisting) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                Func<string, object, object> getSetting = enableDelisting ? EnableDelisting : (Func<string, object, object>)null;
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test", "2.0-alpha"));
                    repository.AddPackage(this.CreatePackage("test", "2.1-alpha"));
                    repository.AddPackage(this.CreatePackage("test", "2.2-beta"));
                    repository.AddPackage(this.CreatePackage("test", "2.3"));
                    repository.AddPackage(this.CreatePackage("test", "2.4.0-prerel"));
                    repository.AddPackage(this.CreatePackage("test", "2.5.0-prerel"));
                    repository.AddPackage(this.CreatePackage("test", "3.2.0+taggedOnly"));
                }, getSetting);

                await serverRepository.RemovePackageAsync(
                    "test",
                    new SemanticVersion("2.5.0-prerel"),
                    CancellationToken.None);

                // Act
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Default, Token);

                // Assert
                Assert.Equal(1, packages.Count(p => p.SemVer1IsAbsoluteLatest));
                Assert.Equal("2.4.0-prerel", packages.First(p => p.SemVer1IsAbsoluteLatest).Version.ToString());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ServerPackageRepositorySemVer2IsAbsoluteLatest(bool enableDelisting) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                Func<string, object, object> getSetting = enableDelisting ? EnableDelisting : (Func<string, object, object>)null;
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test", "2.0-alpha"));
                    repository.AddPackage(this.CreatePackage("test", "2.1-alpha"));
                    repository.AddPackage(this.CreatePackage("test", "2.2-beta"));
                    repository.AddPackage(this.CreatePackage("test", "2.3"));
                    repository.AddPackage(this.CreatePackage("test", "2.4.0-prerel"));
                    repository.AddPackage(this.CreatePackage("test", "3.2.0+taggedOnly"));
                    repository.AddPackage(this.CreatePackage("test", "3.3.0+unlisted"));
                }, getSetting);

                await serverRepository.RemovePackageAsync(
                    "test",
                    new SemanticVersion("3.3.0+unlisted"),
                    CancellationToken.None);

                // Act
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token);

                // Assert
                Assert.Equal(1, packages.Count(p => p.SemVer2IsAbsoluteLatest));
                Assert.Equal("3.2.0", packages.First(p => p.SemVer2IsAbsoluteLatest).Version.ToString());
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryIsLatestOnlyPreRel() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test", "2.0-alpha"));
                    repository.AddPackage(this.CreatePackage("test", "2.1-alpha"));
                    repository.AddPackage(this.CreatePackage("test", "2.2-beta+tagged"));
                });

                // Act
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token);

                // Assert
                Assert.Equal(0, packages.Count(p => p.SemVer2IsLatest));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ServerPackageRepositorySemVer1IsLatest(bool enableDelisting) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                Func<string, object, object> getSetting = enableDelisting ? EnableDelisting : (Func<string, object, object>)null;
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test1", "1.0.0"));
                    repository.AddPackage(this.CreatePackage("test1", "1.1.0"));
                    repository.AddPackage(this.CreatePackage("test1", "1.2.0+taggedOnly"));
                    repository.AddPackage(this.CreatePackage("test1", "2.0.0-alpha"));
                }, getSetting);

                await serverRepository.RemovePackageAsync(
                    "test1",
                    new SemanticVersion("1.1.0"),
                    CancellationToken.None);

                // Act
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Default, Token);

                // Assert
                Assert.Equal(1, packages.Count(p => p.SemVer1IsLatest));
                Assert.Equal("1.0.0", packages.First(p => p.SemVer1IsLatest).Version.ToString());
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ServerPackageRepositorySemVer2IsLatest(bool enableDelisting) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                Func<string, object, object> getSetting = enableDelisting ? EnableDelisting : (Func<string, object, object>)null;
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(this.CreatePackage("test", "1.11"));
                    repository.AddPackage(this.CreatePackage("test", "2.0"));
                    repository.AddPackage(this.CreatePackage("test", "1.9"));
                    repository.AddPackage(this.CreatePackage("test", "2.0-alpha"));
                    repository.AddPackage(this.CreatePackage("test1", "1.0.0"));
                    repository.AddPackage(this.CreatePackage("test1", "1.2.0+taggedOnly"));
                    repository.AddPackage(this.CreatePackage("test1", "2.0.0-alpha"));
                }, getSetting);

                await serverRepository.RemovePackageAsync(
                    "test",
                    new SemanticVersion("2.0"),
                    CancellationToken.None);

                // Act
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token);
                List<string> latestVersions = packages.Where(p => p.SemVer2IsLatest).Select(p => p.Version.ToString()).ToList();

                // Assert
                Assert.Equal(2, packages.Count(p => p.SemVer2IsLatest));
                Assert.Equal("1.11", packages
                    .OrderBy(p => p.Id)
                    .First(p => p.SemVer2IsLatest)
                    .Version
                    .ToString());
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryReadsDerivedData() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                IPackage package = this.CreatePackage("test", "1.0");
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path, repository => {
                    repository.AddPackage(package);
                });

                // Act
                IEnumerable<IServerPackage> packages = await serverRepository.GetPackagesAsync(ClientCompatibility.Max, Token);

                // Assert
                IServerPackage singlePackage = packages.SingleOrDefault();
                Assert.NotNull(singlePackage);
                Assert.Equal(package.GetStream().Length, singlePackage.PackageSize);
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryEmptyRepo() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                this.CreatePackage("test", "1.0");
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path);

                // Act
                IServerPackage findPackage = await serverRepository.FindPackageAsync("test", new SemanticVersion("1.0"), Token);
                IEnumerable<IServerPackage> findPackagesById = await serverRepository.FindPackagesByIdAsync(
                    "test",
                    ClientCompatibility.Max,
                    Token);
                List<IServerPackage> getPackages = (await serverRepository.GetPackagesAsync(
                    ClientCompatibility.Max,
                    Token)).ToList();
                List<IServerPackage> getPackagesWithDerivedData = (await serverRepository.GetPackagesAsync(
                    ClientCompatibility.Max,
                    Token)).ToList();
                IEnumerable<IServerPackage> getUpdates = await serverRepository.GetUpdatesAsync(
                    Enumerable.Empty<IPackageName>(),
                    includePrerelease: true,
                    includeAllVersions: true,
                    targetFramework: Enumerable.Empty<FrameworkName>(),
                    versionConstraints: Enumerable.Empty<IVersionSpec>(),
                    compatibility: ClientCompatibility.Max,
                    token: Token);
                List<IServerPackage> search = (await serverRepository.SearchAsync(
                    "test",
                    allowPrereleaseVersions: true,
                    compatibility: ClientCompatibility.Max,
                    token: Token)).ToList();
                string source = serverRepository.Source;

                // Assert
                Assert.Null(findPackage);
                Assert.Empty(findPackagesById);
                Assert.Empty(getPackages);
                Assert.Empty(getPackagesWithDerivedData);
                Assert.Empty(getUpdates);
                Assert.Empty(search);
                Assert.NotEmpty(source);
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryAddPackage() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path);

                // Act
                await serverRepository.AddPackageAsync(this.CreatePackage("Foo", "1.0.0"), Token);

                // Assert
                Assert.True(await serverRepository.ExistsAsync("Foo", new SemanticVersion("1.0.0"), Token));
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryAddPackageSemVer2() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path);

                // Act
                await serverRepository.AddPackageAsync(this.CreatePackage("Foo", "1.0.0+foo"), Token);

                // Assert
                Assert.True(await serverRepository.ExistsAsync("Foo", new SemanticVersion("1.0.0"), Token));
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryRemovePackageSemVer2() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(temporaryDirectory.Path);
                await serverRepository.AddPackageAsync(this.CreatePackage("Foo", "1.0.0+foo"), Token);

                // Act
                await serverRepository.RemovePackageAsync("Foo", new SemanticVersion("1.0.0+bar"), Token);

                // Assert
                Assert.False(await serverRepository.ExistsAsync("Foo", new SemanticVersion("1.0.0"), Token));
            }
        }

        [Fact]
        public async Task ServerPackageRepositoryAddPackageRejectsDuplicatesWithSemVer2() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                // Arrange
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(
                    temporaryDirectory.Path,
                    getSetting: (key, defaultValue) => {
                        if (key == "allowOverrideExistingPackageOnPush") {
                            return false;
                        }

                        return defaultValue;
                    });
                await serverRepository.AddPackageAsync(this.CreatePackage("Foo", "1.0.0-beta.1+foo"), Token);

                // Act & Assert
                InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await serverRepository.AddPackageAsync(this.CreatePackage("Foo", "1.0.0-beta.1+bar"), Token));
                Assert.Equal(
                    "Package Foo 1.0.0-beta.1 already exists. The server is configured to not allow overwriting packages that already exist.",
                    actual.Message);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public async Task ServerPackageRepository_CustomCacheFileNameNotConfigured_UseMachineNameAsFileName(string fileNameFromConfig) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(
                    temporaryDirectory.Path,
                    getSetting: (key, defaultValue) => key == "cacheFileName" ? fileNameFromConfig : defaultValue);

                string expectedCacheFileName = Path.Combine(serverRepository.Source, Environment.MachineName.ToLowerInvariant() + ".cache.bin");

                Assert.True(File.Exists(expectedCacheFileName));
            }
        }

        [Fact]
        public async Task ServerPackageRepository_CustomCacheFileNameIsConfigured_CustomCacheFileIsCreated() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(
                    temporaryDirectory.Path,
                    getSetting: (key, defaultValue) => key == "cacheFileName" ? "CustomFileName.cache.bin" : defaultValue);

                string expectedCacheFileName = Path.Combine(serverRepository.Source, "CustomFileName.cache.bin");

                Assert.True(File.Exists(expectedCacheFileName));
            }
        }

        [Fact]
        public async Task ServerPackageRepository_CustomCacheFileNameWithoutExtensionIsConfigured_CustomCacheFileWithExtensionIsCreated() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                ServerPackageRepository serverRepository = await CreateServerPackageRepositoryAsync(
                    temporaryDirectory.Path,
                    getSetting: (key, defaultValue) => key == "cacheFileName" ? "CustomFileName" : defaultValue);

                string expectedCacheFileName = Path.Combine(serverRepository.Source, "CustomFileName.cache.bin");

                Assert.True(File.Exists(expectedCacheFileName));
            }
        }

        [Theory]
        [InlineData("c:\\file\\is\\a\\path\\to\\Awesome.cache.bin")]
        [InlineData("random:invalidFileName.cache.bin")]
        public async Task ServerPackageRepository_CustomCacheFileNameIsInvalid_ThrowUp(string invlaidCacheFileName) {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                Task Code() => CreateServerPackageRepositoryAsync(
                    temporaryDirectory.Path,
                    getSetting: (key, defaultValue) => key == "cacheFileName" ? invlaidCacheFileName : defaultValue);

                await Assert.ThrowsAsync<InvalidOperationException>(Code);
            }
        }

        [Fact]
        public async Task ServerPackageRepository_CustomCacheFileNameIsInvalid_ThrowUpWithCorrectErrorMessage() {
            using (TemporaryDirectory temporaryDirectory = new TemporaryDirectory()) {
                Task Code() => CreateServerPackageRepositoryAsync(
                    temporaryDirectory.Path,
                    getSetting: (key, defaultValue) => key == "cacheFileName" ? "foo:bar/baz" : defaultValue);

                string expectedMessage = "Configured cache file name 'foo:bar/baz' is invalid. Keep it simple; No paths allowed.";
                Assert.Equal(expectedMessage, (await Assert.ThrowsAsync<InvalidOperationException>(Code)).Message);
            }
        }

        private static IPackage CreateMockPackage(string id, string version) {
            Mock<IPackage> package = new Mock<IPackage>();
            package.Setup(p => p.Id).Returns(id);
            package.Setup(p => p.Version).Returns(new SemanticVersion(version));
            package.Setup(p => p.IsLatestVersion).Returns(true);
            package.Setup(p => p.Listed).Returns(true);

            return package.Object;
        }

        private IPackage CreatePackage(
            string id,
            string version,
            PackageDependency packageDependency = null) {
            SemanticVersion parsedVersion = new SemanticVersion(version);
            PackageBuilder packageBuilder = new PackageBuilder {
                Id = id,
                Version = parsedVersion,
                Description = "Description",
                Authors = { "Test Author" }
            };

            if (packageDependency != null) {
                packageBuilder.DependencySets.Add(new PackageDependencySet(
                    new FrameworkName(".NETFramework,Version=v4.5"),
                    new[]
                    {
                        packageDependency
                    }));
            }

            Mock<IPackageFile> mockFile = new Mock<IPackageFile>();
            mockFile.Setup(m => m.Path).Returns("foo");
            mockFile.Setup(m => m.GetStream()).Returns(new MemoryStream());
            packageBuilder.Files.Add(mockFile.Object);

            MemoryStream packageStream = new MemoryStream();
            packageBuilder.Save(packageStream);

            // NuGet.Core package builder strips SemVer2 metadata when saving the output package. Fix up the version
            // in the actual manifest.
            packageStream.Seek(0, SeekOrigin.Begin);
            using (ZipArchive zipArchive = new ZipArchive(packageStream, ZipArchiveMode.Update, leaveOpen: true)) {
                ZipArchiveEntry manifestFile = zipArchive
                    .Entries
                    .First(f => Path.GetExtension(f.FullName) == NuGet.Constants.ManifestExtension);

                using (Stream manifestStream = manifestFile.Open()) {
                    Manifest manifest = Manifest.ReadFrom(manifestStream, validateSchema: false);
                    manifest.Metadata.Version = version;

                    manifestStream.SetLength(0);
                    manifest.Save(manifestStream);
                }
            }

            packageStream.Seek(0, SeekOrigin.Begin);
            ZipPackage outputPackage = new ZipPackage(packageStream);

            return outputPackage;
        }

        private static object EnableDelisting(string key, object defaultValue) {
            if (key == "enableDelisting") {
                return true;
            }

            return defaultValue;
        }
    }
}

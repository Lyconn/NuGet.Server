// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information. 

using Moq;
using NuGet.Server.Core.Infrastructure;
using NuGet.Server.Core.Tests.Infrastructure;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace NuGet.Server.Core.Tests {
    public class ServerPackageStoreTest {
        private static CancellationToken Token => CancellationToken.None;
        private const string PackageId = "NuGet.Versioning";
        private const string PackageVersionString = "3.5.0";
        private static readonly SemanticVersion PackageVersion = new SemanticVersion(PackageVersionString);

        [Fact]
        public async Task Remove_SupportsEnabledUnlisting() {
            // Arrange
            using (TemporaryDirectory directory = new TemporaryDirectory()) {
                PhysicalFileSystem fileSystem = new PhysicalFileSystem(directory);
                ExpandedPackageRepository repository = new ExpandedPackageRepository(fileSystem);
                Infrastructure.NullLogger logger = new Infrastructure.NullLogger();

                repository.AddPackage(this.CreatePackage(PackageId, PackageVersion));

                ServerPackageStore target = new ServerPackageStore(fileSystem, repository, logger);

                // Act
                target.Remove(PackageId, PackageVersion, enableDelisting: true);

                // Assert
                ServerPackage package = (await target.GetAllAsync(enableDelisting: true, token: Token)).SingleOrDefault();
                Assert.NotNull(package);
                Assert.Equal(PackageId, package.Id);
                Assert.Equal(PackageVersion, package.Version);
                Assert.False(package.Listed);

                FileInfo fileInfo = new FileInfo(package.FullPath);
                Assert.True(fileInfo.Exists);
                Assert.Equal(FileAttributes.Hidden, fileInfo.Attributes & FileAttributes.Hidden);
            }
        }

        [Fact]
        public async Task Remove_SupportsDisabledUnlisting() {
            // Arrange
            using (TemporaryDirectory directory = new TemporaryDirectory()) {
                PhysicalFileSystem fileSystem = new PhysicalFileSystem(directory);
                ExpandedPackageRepository repository = new ExpandedPackageRepository(fileSystem);
                Infrastructure.NullLogger logger = new Infrastructure.NullLogger();

                repository.AddPackage(this.CreatePackage(PackageId, PackageVersion));

                ServerPackageStore target = new ServerPackageStore(fileSystem, repository, logger);

                // Act
                target.Remove(PackageId, PackageVersion, enableDelisting: false);

                // Assert
                Assert.Empty(await target.GetAllAsync(enableDelisting: false, token: Token));
                Assert.Empty(repository.GetPackages());
            }
        }

        [Fact]
        public async Task Remove_NoOpsWhenPackageDoesNotExist() {
            // Arrange
            using (TemporaryDirectory directory = new TemporaryDirectory()) {
                PhysicalFileSystem fileSystem = new PhysicalFileSystem(directory);
                ExpandedPackageRepository repository = new ExpandedPackageRepository(fileSystem);
                Infrastructure.NullLogger logger = new Infrastructure.NullLogger();

                repository.AddPackage(this.CreatePackage(PackageId, PackageVersion));

                ServerPackageStore target = new ServerPackageStore(fileSystem, repository, logger);

                // Act
                target.Remove("Foo", PackageVersion, enableDelisting: false);

                // Assert
                ServerPackage package = (await target.GetAllAsync(enableDelisting: false, token: Token)).FirstOrDefault();
                Assert.NotNull(package);
                Assert.Equal(PackageId, package.Id);
                Assert.Equal(PackageVersion, package.Version);
                Assert.True(package.Listed);
            }
        }

        private IPackage CreatePackage(string id, SemanticVersion version) {
            Mock<IPackageFile> file = new Mock<IPackageFile>();
            file.Setup(x => x.GetStream()).Returns(() => Stream.Null);
            file.Setup(x => x.Path).Returns($"lib/net40/test.dll");

            PackageBuilder builder = new PackageBuilder {
                Id = id,
                Version = version,
                Description = id,
                Authors = { id },
                Files = { file.Object }
            };

            MemoryStream memoryStream = new MemoryStream();
            builder.Save(memoryStream);
            memoryStream.Position = 0;

            return new ZipPackage(memoryStream);
        }
    }
}

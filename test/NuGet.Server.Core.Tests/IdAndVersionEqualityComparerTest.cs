// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using NuGet.Server.Core.Infrastructure;
using Xunit;

namespace NuGet.Server.Core.Tests {
    public class IdAndVersionEqualityComparerTest {
        private const string IdA = "NuGet.Versioning";
        private const string IdB = "NuGet.Frameworks";
        private static readonly SemanticVersion VersionA = new SemanticVersion("1.0.0-beta");
        private static readonly SemanticVersion VersionB = new SemanticVersion("2.0.0");

        [Fact]
        public void NullEqualsNull() {
            // Arrange
            IdAndVersionEqualityComparer target = new IdAndVersionEqualityComparer();

            // Act & Assert
            Assert.True(target.Equals(null, null));
        }

        [Fact]
        public void NullDoesNotEqualNonNull() {
            // Arrange
            IdAndVersionEqualityComparer target = new IdAndVersionEqualityComparer();

            // Act & Assert
            Assert.False(target.Equals(new ServerPackage(), null));
        }

        [Fact]
        public void NullIdEqualsNullId() {
            // Arrange
            IdAndVersionEqualityComparer target = new IdAndVersionEqualityComparer();

            // Act & Assert
            Assert.True(target.Equals(
                new ServerPackage { Id = null, Version = VersionA },
                new ServerPackage { Id = null, Version = VersionA }));
        }

        [Fact]
        public void NullVersionEqualsNullVersion() {
            // Arrange
            IdAndVersionEqualityComparer target = new IdAndVersionEqualityComparer();

            // Act & Assert
            Assert.True(target.Equals(
                new ServerPackage { Id = IdA, Version = null },
                new ServerPackage { Id = IdA, Version = null }));
        }

        [Fact]
        public void SameIdAndVersionAreEqual() {
            // Arrange
            IdAndVersionEqualityComparer target = new IdAndVersionEqualityComparer();

            // Act & Assert
            Assert.True(target.Equals(
                new ServerPackage { Id = IdA, Version = VersionA },
                new ServerPackage { Id = IdA, Version = VersionA }));
        }

        [Fact]
        public void IdComparisonIsCaseInsensitive() {
            // Arrange
            IdAndVersionEqualityComparer target = new IdAndVersionEqualityComparer();

            // Act & Assert
            Assert.True(target.Equals(
                new ServerPackage { Id = IdA.ToLower(), Version = VersionA },
                new ServerPackage { Id = IdA.ToUpper(), Version = VersionA }));
        }

        [Fact]
        public void DifferentIdsAreNotEqual() {
            // Arrange
            IdAndVersionEqualityComparer target = new IdAndVersionEqualityComparer();

            // Act & Assert
            Assert.False(target.Equals(
                new ServerPackage { Id = IdA, Version = VersionA },
                new ServerPackage { Id = IdB, Version = VersionA }));
        }

        [Fact]
        public void DifferentVersionsAreNotEqual() {
            // Arrange
            IdAndVersionEqualityComparer target = new IdAndVersionEqualityComparer();

            // Act & Assert
            Assert.False(target.Equals(
                new ServerPackage { Id = IdA, Version = VersionA },
                new ServerPackage { Id = IdA, Version = VersionB }));
        }
    }
}

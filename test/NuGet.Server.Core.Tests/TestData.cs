// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Moq;
using System.IO;
using System.Reflection;

namespace NuGet.Server.Core.Tests {
    public static class TestData {
        public const string PackageResource = "NuGet.Core.2.12.0.nupkg";
        public const string PackageId = "NuGet.Core";
        public const string PackageVersionString = "2.12.0";
        public static readonly SemanticVersion PackageVersion = new SemanticVersion(PackageVersionString);

        public static Stream GetResourceStream(string name) {
            Assembly assembly = Assembly.GetExecutingAssembly();
            return assembly.GetManifestResourceStream($"NuGet.Server.Core.Tests.TestData.{name}");
        }

        public static string GetResourceString(string name) {
            using (Stream stream = GetResourceStream(name))
            using (StreamReader reader = new StreamReader(stream)) {
                return reader.ReadToEnd();
            }
        }

        public static void CopyResourceToPath(string name, string path) {
            using (Stream resourceStream = GetResourceStream(name))
            using (FileStream outputStream = File.Create(path)) {
                resourceStream.CopyTo(outputStream);
            }
        }

        public static Stream GenerateSimplePackage(string id, SemanticVersion version) {
            Mock<IPackageFile> simpleFile = new Mock<IPackageFile>();
            simpleFile.Setup(x => x.Path).Returns("file.txt");
            simpleFile.Setup(x => x.GetStream()).Returns(() => new MemoryStream(new byte[0]));

            PackageBuilder packageBuilder = new PackageBuilder {
                Id = id,
                Version = version
            };
            packageBuilder.Authors.Add("Integration test");
            packageBuilder.Description = "Simple test package.";
            packageBuilder.Files.Add(simpleFile.Object);

            MemoryStream memoryStream = new MemoryStream();
            packageBuilder.Save(memoryStream);
            memoryStream.Position = 0;

            return memoryStream;
        }
    }
}

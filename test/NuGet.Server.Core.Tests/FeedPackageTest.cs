// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information. 
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace NuGet.Server.Core.Tests {
    public class FeedPackageTest {
        [Fact]
        public void FeedPackageHasSameMembersAsDataServicePackage() {
            // Arrange
            // This is not pretty but it's the most effective way.
            string[] excludedProperties = new[] { "Owners", "ReportAbuseUrl", "GalleryDetailsUrl", "DownloadUrl", "Rating", "RatingsCount", "Language",
                                             "AssemblyReferences", "FrameworkAssemblies", "DependencySets", "PackageAssemblyReferences", "LicenseNames",
                                             "LicenseNameCollection", "LicenseReportUrl"
            };

            HashSet<string> feedPackageProperties = new HashSet<string>(
                typeof(DataServices.ODataPackage).GetProperties().Select(p => p.Name), StringComparer.Ordinal);

            List<string> dataServiceProperties = typeof(DataServicePackage)
                .GetProperties()
                .Select(p => p.Name)
                .ToList();

            // Assert
            // Assert.Equal(feedPackageProperties.Count, dataServiceProperties.Count);
            foreach (string property in dataServiceProperties) {
                if (excludedProperties.Contains(property)) {
                    continue;
                }

                Assert.True(feedPackageProperties.Contains(property),
                    string.Format(CultureInfo.InvariantCulture,
                        "Property {0} could not be found in NuGet.Server package.", property));
            }
        }
    }
}

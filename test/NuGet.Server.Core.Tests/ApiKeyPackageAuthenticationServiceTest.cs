// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information. 
using NuGet.Server.Core.Infrastructure;
using System;
using Xunit;

namespace NuGet.Server.Core.Tests {
    public class ApiKeyPackageAuthenticationServiceTest {
        [Theory]
        [InlineData(null, null)]
        [InlineData(null, "test-key")]
        [InlineData("incorrect-key", null)]
        [InlineData("incorrect-key", "test-key")]
        [InlineData("test-key", "test-key")]
        public void AuthenticationServiceReturnsTrueIfRequireApiKeyValueIsSetToFalse(string key, string apiKey) {
            ApiKeyPackageAuthenticationService apiKeyAuthService = new ApiKeyPackageAuthenticationService(false, apiKey);

            // Act
            bool result = apiKeyAuthService.IsAuthenticatedInternal(key);

            // Assert
            Assert.True(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("incorrect-key")]
        public void AuthenticationServiceReturnsFalseIfKeyDoesNotMatchConfigurationKey(string key) {
            ApiKeyPackageAuthenticationService apiKeyAuthService = new ApiKeyPackageAuthenticationService(true, "test-key");

            // Act
            bool result = apiKeyAuthService.IsAuthenticatedInternal(key);

            // Assert
            Assert.False(result);
        }


        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ConstructorThrowsWhenApiKeyRequiredAndMissing(string apiKey) => Assert.Throws<ArgumentException>(() => new ApiKeyPackageAuthenticationService(true, apiKey));

        [Theory]
        [InlineData("test-key")]
        [InlineData("tEst-Key")]
        public void AuthenticationServiceReturnsTrueIfKeyMatchesConfigurationKey(string key) {
            ApiKeyPackageAuthenticationService apiKeyAuthService = new ApiKeyPackageAuthenticationService(true, "test-key");

            // Act
            bool result = apiKeyAuthService.IsAuthenticatedInternal(key);

            // Assert
            Assert.True(result);
        }


    }
}

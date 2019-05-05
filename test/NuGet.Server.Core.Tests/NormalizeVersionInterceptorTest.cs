// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information. 
using NuGet.Server.Core.DataServices;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Xunit;

namespace NuGet.Server.Core.Tests {
    public class NormalizeVersionInterceptorTest {
        private static readonly MemberInfo _versionMember = typeof(ODataPackage).GetProperty("Version");
        private static readonly MemberInfo _normalizedVersionMember = typeof(ODataPackage).GetProperty("NormalizedVersion");

        public static IEnumerable<object[]> TheoryData {
            get {
                return new[]
                {
                    new object[]
                    {
                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.Constant("1.0.0.0"),
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _versionMember)),

                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.Constant("1.0.0"),
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _normalizedVersionMember))
                    },

                    new object[]
                    {
                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _versionMember),
                            Expression.Constant("1.0.0.0")),

                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.Constant("1.0.0"),
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _normalizedVersionMember))
                    },

                    new object[]
                    {
                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _versionMember),
                            Expression.Constant("1.0.0-00test")),

                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.Constant("1.0.0-00test"),
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _normalizedVersionMember))
                    },

                    new object[]
                    {
                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _versionMember),
                            Expression.Constant("1.0.0-00test+tagged")),

                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.Constant("1.0.0-00test"),
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _normalizedVersionMember))
                    },

                    new object[]
                    {
                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _versionMember),
                            Expression.Constant("1.0.0+taggedOnly")),

                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.Constant("1.0.0"),
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _normalizedVersionMember))
                    },
                };
            }
        }

        [Theory]
        [MemberData(nameof(TheoryData))]
        public void RewritesVersionPropertyNameToNormalizedVersionPropertyName(Expression originalExpression, Expression expectedExpression) {
            // Arrange
            NormalizeVersionInterceptor interceptor = new NormalizeVersionInterceptor();

            // Act
            Expression rewrittenExpression = interceptor.Visit(originalExpression);

            // Assert
            Assert.Equal(rewrittenExpression.ToString(), expectedExpression.ToString());
        }

        [Fact]
        public void FindsPackagesUsingNormalizedVersion() {
            // Arrange
            List<ODataPackage> data = new List<ODataPackage> {
                new ODataPackage { Id = "foo", Version = "1.0.0.0.0.0", NormalizedVersion = "1.0.0" },
                new ODataPackage { Id = "foo1", Version = "2.0.0+tagged", NormalizedVersion = "2.0.0" }
            };

            IQueryable<ODataPackage> queryable = data.AsQueryable().InterceptWith(new NormalizeVersionInterceptor());

            // Act
            ODataPackage result1 = queryable.FirstOrDefault(p => p.Version == "1.0");
            ODataPackage result2 = queryable.FirstOrDefault(p => p.Version == "1.0.0");
            ODataPackage result3 = queryable.FirstOrDefault(p => p.Version == "1.0.0.0");
            ODataPackage result4 = queryable.FirstOrDefault(p => p.Version == "2.0");
            ODataPackage result5 = queryable.FirstOrDefault(p => p.Version == "2.0.0");
            ODataPackage result6 = queryable.FirstOrDefault(p => p.Version == "2.0.0+someOtherTag");

            // Assert
            Assert.Equal(result1, data[0]);
            Assert.Equal(result2, data[0]);
            Assert.Equal(result3, data[0]);
            Assert.Equal(result4, data[1]);
            Assert.Equal(result5, data[1]);
            Assert.Equal(result6, data[1]);
        }
    }
}
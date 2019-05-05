// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information. 
using NuGet.Server.Core.DataServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Xunit;

namespace NuGet.Server.Core.Tests {
    public class IgnoreCaseForPackageIdInterceptorTest {
        private static readonly MemberInfo _idMember = typeof(ODataPackage).GetProperty("Id");
        private static readonly Expression<Func<string, string, int>> _ordinalIgnoreCaseComparer = (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a, b);

        public static IEnumerable<object[]> TheoryData {
            get {
                return new[]
                {
                    new object[]
                    {
                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.Constant("NEWTONSOFT.JSON"),
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _idMember)),

                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.Invoke(_ordinalIgnoreCaseComparer,
                                Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _idMember),
                                Expression.Constant("NEWTONSOFT.JSON")
                            ),
                            Expression.Constant(0))
                    },

                    new object[]
                    {
                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _idMember),
                            Expression.Constant("NEWTONSOFT.JSON")),

                        Expression.MakeBinary(
                            ExpressionType.Equal,
                            Expression.Invoke(_ordinalIgnoreCaseComparer,
                                Expression.MakeMemberAccess(Expression.Parameter(typeof(ODataPackage)), _idMember),
                                Expression.Constant("NEWTONSOFT.JSON")
                            ),
                            Expression.Constant(0))
                    }
                };
            }
        }

        [Theory]
        [MemberData(nameof(TheoryData))]
        public void RewritesIdComparisonToIgnoreCaseComparison(Expression originalExpression, Expression expectedExpression) {
            // Arrange
            IgnoreCaseForPackageIdInterceptor interceptor = new IgnoreCaseForPackageIdInterceptor();

            // Act
            Expression rewrittenExpression = interceptor.Visit(originalExpression);

            // Assert
            Assert.Equal(rewrittenExpression.ToString(), expectedExpression.ToString());
        }

        [Fact]
        public void FindsPackagesIgnoringCase() {
            // Arrange
            List<ODataPackage> data = new List<ODataPackage> {
                new ODataPackage { Id = "foo" },
                new ODataPackage { Id = "BAR" },
                new ODataPackage { Id = "bAz" }
            };

            IQueryable<ODataPackage> queryable = data.AsQueryable().InterceptWith(new IgnoreCaseForPackageIdInterceptor());

            // Act
            ODataPackage result1 = queryable.FirstOrDefault(p => p.Id == "foo");
            ODataPackage result2 = queryable.FirstOrDefault(p => p.Id == "FOO");
            ODataPackage result3 = queryable.FirstOrDefault(p => p.Id == "Foo");

            ODataPackage result4 = queryable.FirstOrDefault(p => p.Id == "bar");
            ODataPackage result5 = queryable.FirstOrDefault(p => p.Id == "BAR");
            ODataPackage result6 = queryable.FirstOrDefault(p => p.Id == "baR");

            ODataPackage result7 = queryable.FirstOrDefault(p => p.Id == "baz");
            ODataPackage result8 = queryable.FirstOrDefault(p => p.Id == "BAZ");
            ODataPackage result9 = queryable.FirstOrDefault(p => p.Id == "bAz");

            // Assert
            Assert.Equal(result1.Id, data[0].Id);
            Assert.Equal(result2.Id, data[0].Id);
            Assert.Equal(result3.Id, data[0].Id);

            Assert.Equal(result4.Id, data[1].Id);
            Assert.Equal(result5.Id, data[1].Id);
            Assert.Equal(result6.Id, data[1].Id);

            Assert.Equal(result7.Id, data[2].Id);
            Assert.Equal(result8.Id, data[2].Id);
            Assert.Equal(result9.Id, data[2].Id);
        }
    }
}
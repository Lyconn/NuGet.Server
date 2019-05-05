// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using NuGet.Server.Core.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Xunit.Abstractions;

namespace NuGet.Server.Core.Tests {
    public class TestOutputLogger : Logging.ILogger {
        private readonly ITestOutputHelper _output;
        private ConcurrentQueue<string> _messages;

        public TestOutputLogger(ITestOutputHelper output) {
            this._output = output;
            this._messages = new ConcurrentQueue<string>();
        }

        public IEnumerable<string> Messages => this._messages;

        public void Clear() => this._messages = new ConcurrentQueue<string>();

        public void Log(LogLevel level, string message, params object[] args) {
            string formattedMessage = $"[{level.ToString().Substring(0, 4).ToUpperInvariant()}] {string.Format(message, args)}";
            this._messages.Enqueue(formattedMessage);
            this._output.WriteLine(formattedMessage);
        }
    }
}

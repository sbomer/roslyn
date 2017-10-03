﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CommandLine;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CompilerServer
{
    internal class DesktopBuildServerController : BuildServerController
    {
        internal const string KeepAliveSettingName = "keepalive";

        private readonly NameValueCollection _appSettings;

        internal DesktopBuildServerController(NameValueCollection appSettings)
        {
            _appSettings = appSettings;
        }

        protected override IClientConnectionHost CreateClientConnectionHost(string pipeName)
        {
            var compilerServerHost = CreateCompilerServerHost();
            return CreateClientConnectionHostForServerHost(compilerServerHost, pipeName);
        }

        internal static ICompilerServerHost CreateCompilerServerHost()
        {
            // VBCSCompiler is installed in the same directory as csc.exe and vbc.exe which is also the 
            // location of the response files.
            var clientDirectory = AppDomain.CurrentDomain.BaseDirectory;
#if NET46
            var sdkDirectory = RuntimeEnvironment.GetRuntimeDirectory();
#else
            var sdkDirectory = (string)null;
#endif
            return new DesktopCompilerServerHost(clientDirectory, sdkDirectory);
        }

        internal static IClientConnectionHost CreateClientConnectionHostForServerHost(
            ICompilerServerHost compilerServerHost,
            string pipeName)
        {

            if (PlatformInformation.IsWindows)
            {
                return new NamedPipeClientConnectionHost(compilerServerHost, pipeName);
            }
            else
            {
                return new DomainSocketClientConnectionHost(compilerServerHost, pipeName);
            }
        }

        protected internal override TimeSpan? GetKeepAliveTimeout()
        {
            try
            {
                int keepAliveValue;
                string keepAliveStr = _appSettings[KeepAliveSettingName];
                if (int.TryParse(keepAliveStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out keepAliveValue) &&
                    keepAliveValue >= 0)
                {
                    if (keepAliveValue == 0)
                    {
                        // This is a one time server entry.
                        return null;
                    }
                    else
                    {
                        return TimeSpan.FromSeconds(keepAliveValue);
                    }
                }
                else
                {
                    return ServerDispatcher.DefaultServerKeepAlive;
                }
            }
            catch (Exception e)
            {
                CompilerServerLogger.LogException(e, "Could not read AppSettings");
                return ServerDispatcher.DefaultServerKeepAlive;
            }
        }

        protected override Task<Stream> ConnectForShutdownAsync(string pipeName, int timeout)
        {
            if (PlatformInformation.IsWindows)
            {
                var client = new NamedPipeClientStream(pipeName);
                client.Connect(timeout);
                return Task.FromResult<Stream>(client);
            }
            else
            {
                return Task.FromResult<Stream>(UnixDomainSocket.CreateClient(pipeName));
            }
        }

        protected override string GetDefaultPipeName()
        {
            var clientDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return BuildServerConnection.GetPipeNameForPathOpt(clientDirectory);
        }

        protected override bool? WasServerRunning(string pipeName)
        {
            string mutexName = BuildServerConnection.GetServerMutexName(pipeName);
            return BuildServerConnection.WasServerMutexOpen(mutexName);
        }

        protected override int RunServerCore(string pipeName, IClientConnectionHost connectionHost, IDiagnosticListener listener, TimeSpan? keepAlive, CancellationToken cancellationToken)
        {
            // Grab the server mutex to prevent multiple servers from starting with the same
            // pipename and consuming excess resources. If someone else holds the mutex
            // exit immediately with a non-zero exit code
            var mutexName = BuildServerConnection.GetServerMutexName(pipeName);
            bool holdsMutex;
            using (var serverMutex = new Mutex(initiallyOwned: true,
                                               name: mutexName,
                                               createdNew: out holdsMutex))
            {
                if (!holdsMutex)
                {
                    return CommonCompiler.Failed;
                }

                try
                {
                    return base.RunServerCore(pipeName, connectionHost, listener, keepAlive, cancellationToken);
                }
                finally
                {
                    serverMutex.ReleaseMutex();
                }
            }
        }

        internal static new int RunServer(string pipeName, IClientConnectionHost clientConnectionHost = null, IDiagnosticListener listener = null, TimeSpan? keepAlive = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            BuildServerController controller = new DesktopBuildServerController(new NameValueCollection());
            return controller.RunServer(pipeName, clientConnectionHost, listener, keepAlive, cancellationToken);
        }
    }
}

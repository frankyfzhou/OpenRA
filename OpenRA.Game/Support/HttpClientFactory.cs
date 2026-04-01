#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Net.Http;

namespace OpenRA.Support
{
	public static class HttpClientFactory
	{
		const int MaxConnectionPerServer = 20;
		static readonly TimeSpan ConnectionLifeTime = TimeSpan.FromMinutes(1);

		static readonly Lazy<HttpMessageHandler> Handler = new(GetHandler);

		/// <summary>
		/// Base URL of the relay HTTP proxy (e.g. "http://localhost:9090").
		/// Set during WASM init to enable CORS-proxied requests to master.openra.net.
		/// </summary>
		public static string WebRelayHttpBase { get; set; }

		public static HttpClient Create()
		{
			// SocketsHttpHandler is not supported in browser WASM.
			// Plain HttpClient uses BrowserHttpHandler which delegates to browser fetch().
			if (OperatingSystem.IsBrowser())
				return new HttpClient();

			return new HttpClient(Handler.Value, false);
		}

		static HttpMessageHandler GetHandler()
		{
			return new SocketsHttpHandler
			{
				// https://github.com/dotnet/corefx/issues/26895
				// https://github.com/dotnet/corefx/issues/26331
				// https://github.com/dotnet/corefx/pull/26839
				PooledConnectionLifetime = ConnectionLifeTime,
				PooledConnectionIdleTimeout = ConnectionLifeTime,
				MaxConnectionsPerServer = MaxConnectionPerServer
			};
		}
	}
}

#nullable enable

/*
TShock, a server mod for Terraria
Copyright (C) 2011-2026 TShock Contributors

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TShockNG.Contracts.Hosting;

namespace Rests
{
    public sealed class EscapedParameter
    {
        public string Name { get; }
        public string Value { get; }

        public EscapedParameter(string name, string value)
        {
            Name = name;
            Value = value;
        }
    }

    /// <summary>
    /// Transport-neutral REST parameter collection.
    /// Values are already URL-decoded by the HTTP host.
    /// </summary>
    public sealed class EscapedParameterCollection : IEnumerable<EscapedParameter>
    {
        private readonly IReadOnlyDictionary<string, string> collection;

        public string? this[string key] => collection.TryGetValue(key, out var value) ? value : null;

        public EscapedParameterCollection(IReadOnlyDictionary<string, string> collection)
        {
            this.collection = collection;
        }

        public IEnumerator<EscapedParameter> GetEnumerator()
        {
            foreach (var parameter in collection)
                yield return new EscapedParameter(parameter.Key, parameter.Value);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class RestHttpRequest
    {
        public string Method { get; }
        public string Path { get; }

        internal RestHttpRequest(string method, string path)
        {
            Method = method;
            Path = path;
        }
    }

    public sealed class RestHttpContext
    {
        public IPEndPoint RemoteEndPoint { get; }

        internal RestHttpContext(string remoteAddress)
        {
            var address = IPAddress.TryParse(remoteAddress, out var parsed) ? parsed : IPAddress.None;
            RemoteEndPoint = new IPEndPoint(address, 0);
        }
    }

    public delegate object RestCommandD(RestRequestArgs args);

    public class RestRequestArgs
    {
        public RestVerbs Verbs { get; }
        public EscapedParameterCollection Parameters { get; }
        public RestHttpRequest Request { get; }
        public SecureRest.TokenData TokenData { get; }
        public RestHttpContext Context { get; }

        public RestRequestArgs(
            RestVerbs verbs,
            EscapedParameterCollection parameters,
            RestHttpRequest request,
            RestHttpContext context)
            : this(verbs, parameters, request, SecureRest.TokenData.None, context)
        {
        }

        public RestRequestArgs(
            RestVerbs verbs,
            EscapedParameterCollection parameters,
            RestHttpRequest request,
            SecureRest.TokenData tokenData,
            RestHttpContext context)
        {
            Verbs = verbs;
            Parameters = parameters;
            Request = request;
            TokenData = tokenData;
            Context = context;
        }
    }

    /// <summary>
    /// TShock REST routing adapter. HTTP hosting itself is owned by TShockNG.Core.
    /// </summary>
    public class Rest : IDisposable
    {
        private readonly List<RestCommand> commands = new();
        private readonly Dictionary<string, Tuple<string, string?>> redirects = new();
        private readonly string serverHeader;
        private IHttpServer? server;
        private Timer? tokenBucketTimer;

        public Dictionary<string, int> tokenBucket = new();
        public IPAddress Ip { get; set; }
        public int Port { get; set; }

        public Rest(IPAddress ip, int port)
        {
            Ip = ip;
            Port = port;
            var assembly = GetType().Assembly.GetName();
            serverHeader = $"{assembly.Name}/{assembly.Version}";
        }

        public virtual void Start()
        {
            try
            {
                server = TShockAPI.TShockRuntimeServices.RequireHttpServerFactory().Create(Ip, Port);
                server.StartAsync(HandleRequestAsync).AsTask().GetAwaiter().GetResult();
                tokenBucketTimer = new Timer(
                    _ => DegradeBucket(),
                    null,
                    TimeSpan.Zero,
                    TimeSpan.FromMinutes(Math.Max(TShockAPI.TShock.Config.Settings.RESTRequestBucketDecreaseIntervalMinutes, 1)));
            }
            catch (Exception ex)
            {
                TShockAPI.TShock.Log.Error(GetString("Fatal Startup Exception"));
                TShockAPI.TShock.Log.Error(ex.ToString());
                TShockAPI.TShock.Log.ConsoleError(GetString(
                    "Invalid REST configuration: \nYou may already have a REST service bound to port {0}. \nPlease adjust your configuration and restart the server.",
                    Port));
                throw;
            }
        }

        public void Start(IPAddress ip, int port)
        {
            Ip = ip;
            Port = port;
            Start();
        }

        public virtual void Stop()
        {
            server?.StopAsync().AsTask().GetAwaiter().GetResult();
        }

        public void Register(string path, RestCommandD callback) => AddCommand(new RestCommand(path, callback));
        public void Register(RestCommand command) => AddCommand(command);

        public void RegisterRedirect(string baseRoute, string targetRoute, string? upgradeRoute = null, bool parameterized = true)
        {
            redirects[baseRoute] = Tuple.Create(targetRoute, upgradeRoute);
        }

        protected void AddCommand(RestCommand command) => commands.Add(command);

        private void DegradeBucket()
        {
            lock (tokenBucket)
            {
                foreach (var key in tokenBucket.Keys.ToArray())
                {
                    var tokens = tokenBucket[key];
                    if (tokens > 1)
                        tokenBucket[key] = tokens - 1;
                    else
                        tokenBucket.Remove(key);
                }
            }
        }

        private ValueTask<HttpServerResponse> HandleRequestAsync(
            HttpServerRequest request,
            CancellationToken cancellationToken)
        {
            var result = ProcessRequest(request);
            if (result is null)
                throw new InvalidOperationException("REST endpoint returned a null response.");

            var json = JsonConvert.SerializeObject(result, Formatting.Indented);
            if (request.Query.TryGetValue("jsonp", out var jsonp) && !string.IsNullOrWhiteSpace(jsonp))
                json = $"{jsonp}({json});";

            var statusCode = HttpStatusCode.OK;
            if (result is RestObject restObject &&
                Enum.TryParse<HttpStatusCode>(restObject.Status, out var parsedStatus))
            {
                statusCode = parsedStatus;
            }

            IReadOnlyDictionary<string, string> headers = new Dictionary<string, string>
            {
                ["Server"] = serverHeader
            };

            return ValueTask.FromResult(new HttpServerResponse(
                (int)statusCode,
                "application/json; charset=utf-8",
                json,
                headers));
        }

        protected virtual object ProcessRequest(HttpServerRequest request)
        {
            try
            {
                var uri = request.Path.TrimEnd('/');
                if (uri.Length == 0)
                    uri = "/";

                string? upgrade = null;
                if (redirects.TryGetValue(uri, out var redirect))
                {
                    upgrade = redirect.Item2;
                    uri = redirect.Item1;
                }

                foreach (var command in commands)
                {
                    var verbs = new RestVerbs();
                    if (command.HasVerbs)
                    {
                        var match = Regex.Match(uri, command.UriVerbMatch);
                        if (!match.Success || match.Groups.Count - 1 != command.UriVerbs.Length)
                            continue;

                        for (var i = 0; i < command.UriVerbs.Length; i++)
                            verbs.Add(command.UriVerbs[i], match.Groups[i + 1].Value);
                    }
                    else if (!command.UriTemplate.Equals(uri, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var parameters = new EscapedParameterCollection(request.Query);
                    var restRequest = new RestHttpRequest(request.Method, request.Path);
                    var context = new RestHttpContext(request.RemoteAddress);
                    var result = ExecuteCommand(command, verbs, parameters, restRequest, context);
                    if (result is null)
                        continue;

                    if (!string.IsNullOrWhiteSpace(upgrade) &&
                        result is RestObject response &&
                        !response.ContainsKey("upgrade"))
                    {
                        response.Add("upgrade", upgrade);
                    }

                    return result;
                }
            }
            catch (Exception exception)
            {
                return new RestObject("500")
                {
                    { "error", GetString("Internal server error.") },
                    { "errormsg", exception.Message },
                    { "stacktrace", exception.StackTrace ?? string.Empty }
                };
            }

            return new RestObject("404")
            {
                { "error", GetString("Specified API endpoint doesn't exist. Refer to the documentation for a list of valid endpoints.") }
            };
        }

        protected virtual object ExecuteCommand(
            RestCommand command,
            RestVerbs verbs,
            EscapedParameterCollection parameters,
            RestHttpRequest request,
            RestHttpContext context)
        {
            var result = command.Execute(verbs, parameters, request, context);
            if (command.DoLog && TShockAPI.TShock.Config.Settings.LogRest)
            {
                var endpoint = BuildRequestUri(command, verbs, parameters, false);
                TShockAPI.TShock.Log.ConsoleInfo(GetString($"Anonymous requested REST endpoint: {endpoint}"));
            }

            return result;
        }

        protected virtual string BuildRequestUri(
            RestCommand command,
            RestVerbs verbs,
            EscapedParameterCollection parameters,
            bool includeToken = true)
        {
            var requestBuilder = new StringBuilder(command.UriTemplate);
            var separator = '?';
            foreach (var parameter in parameters)
            {
                if (!includeToken && parameter.Name.Equals("token", StringComparison.OrdinalIgnoreCase))
                    continue;

                requestBuilder.Append(separator);
                requestBuilder.Append(parameter.Name);
                requestBuilder.Append('=');
                requestBuilder.Append(parameter.Value);
                separator = '&';
            }

            return requestBuilder.ToString();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            tokenBucketTimer?.Dispose();
            tokenBucketTimer = null;
            server?.Dispose();
            server = null;
        }
    }
}

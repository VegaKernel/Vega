using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Vega.Contracts.Operations;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

public sealed record VegaDebugOptions(
    bool Enabled,
    string[] AllowedPlugins,
    bool AllowNonPublicMembers,
    bool AllowMethodInvoke,
    bool AllowStateMutation,
    bool AllowGarbageCollection,
    int MaxMembers,
    int MaxStringLength)
{
    public static VegaDebugOptions Default { get; } = new(
        false,
        ["VegaToolsMCP"],
        false,
        false,
        false,
        false,
        256,
        4096);

    public static VegaDebugOptions Normalize(VegaDebugOptions? candidate) => new(
        candidate?.Enabled ?? Default.Enabled,
        (candidate?.AllowedPlugins ?? Default.AllowedPlugins)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
        candidate?.AllowNonPublicMembers ?? Default.AllowNonPublicMembers,
        candidate?.AllowMethodInvoke ?? Default.AllowMethodInvoke,
        candidate?.AllowStateMutation ?? Default.AllowStateMutation,
        candidate?.AllowGarbageCollection ?? Default.AllowGarbageCollection,
        Math.Clamp(candidate?.MaxMembers ?? Default.MaxMembers, 16, 2048),
        Math.Clamp(candidate?.MaxStringLength ?? Default.MaxStringLength, 256, 65536));
}

public static class VegaDebugOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static VegaDebugOptions Load(string path, IOperationsLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return File.Exists(path)
                ? VegaDebugOptions.Normalize(
                    JsonSerializer.Deserialize<VegaDebugOptions>(File.ReadAllText(path), JsonOptions))
                : VegaDebugOptions.Default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "debug-api",
                $"DEBUG API configuration '{Path.GetFullPath(path)}' is invalid; DEBUG API remains disabled.",
                ex);
            return VegaDebugOptions.Default;
        }
    }
}

/// <summary>Named process-internal roots exposed only through trusted plugin DEBUG contexts.</summary>
public sealed class VegaDebugRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, Func<object?>> roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly VegaDebugOptions options;

    public VegaDebugRegistry(VegaDebugOptions options) =>
        this.options = VegaDebugOptions.Normalize(options);

    public VegaDebugOptions Options => options;

    public void Register(string name, object root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Register(name, () => root);
    }

    public void Register(string name, Func<object?> root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(root);
        lock (sync)
        {
            roots[name.Trim()] = root;
        }
    }

    public IVegaDebugContext CreateContext(string pluginName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginName);
        return options.Enabled && options.AllowedPlugins.Contains(pluginName, StringComparer.OrdinalIgnoreCase)
            ? new Context(this, options)
            : VegaDebug.Disabled;
    }

    private bool TryResolve(string name, out object? value)
    {
        Func<object?>? provider;
        lock (sync)
        {
            roots.TryGetValue(name, out provider);
        }

        value = provider?.Invoke();
        return provider is not null;
    }

    private IReadOnlyList<VegaDebugRootSnapshot> GetRoots()
    {
        KeyValuePair<string, Func<object?>>[] copy;
        lock (sync)
        {
            copy = roots.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        var result = new VegaDebugRootSnapshot[copy.Length];
        for (var i = 0; i < copy.Length; i++)
        {
            object? value = copy[i].Value();
            Type? type = value?.GetType();
            result[i] = new VegaDebugRootSnapshot(
                copy[i].Key,
                type?.FullName ?? "<null>",
                type?.Assembly.GetName().Name ?? "<unknown>");
        }

        return Array.AsReadOnly(result);
    }

    private sealed class Context(VegaDebugRegistry registry, VegaDebugOptions options) : IVegaDebugContext
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public VegaDebugCapabilities Capabilities =>
            VegaDebugCapabilities.Runtime |
            VegaDebugCapabilities.Assemblies |
            VegaDebugCapabilities.Threads |
            VegaDebugCapabilities.Roots |
            (options.AllowNonPublicMembers ? VegaDebugCapabilities.NonPublicMembers : VegaDebugCapabilities.None) |
            (options.AllowMethodInvoke ? VegaDebugCapabilities.MethodInvoke : VegaDebugCapabilities.None) |
            (options.AllowStateMutation ? VegaDebugCapabilities.StateMutation : VegaDebugCapabilities.None) |
            (options.AllowGarbageCollection ? VegaDebugCapabilities.GarbageCollection : VegaDebugCapabilities.None);

        private BindingFlags Flags =>
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.Public |
            (options.AllowNonPublicMembers ? BindingFlags.NonPublic : BindingFlags.Default);

        public VegaDebugRuntimeSnapshot CaptureRuntime()
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
            ThreadPool.GetAvailableThreads(out int availableWorker, out int availableIo);
            DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
            DateTimeOffset startedAt = new(process.StartTime.ToUniversalTime());

            return new VegaDebugRuntimeSnapshot(
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.RuntimeIdentifier,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessId,
                capturedAt - startedAt,
                process.WorkingSet64,
                process.PrivateMemorySize64,
                GC.GetTotalMemory(false),
                GC.GetTotalAllocatedBytes(false),
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2),
                process.Threads.Count,
                maxWorker,
                maxIo,
                availableWorker,
                availableIo,
                capturedAt);
        }

        public IReadOnlyList<VegaDebugAssemblySnapshot> SnapshotAssemblies()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var result = new VegaDebugAssemblySnapshot[assemblies.Length];
            Array.Sort(assemblies, static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                left.GetName().Name,
                right.GetName().Name));

            for (var i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                AssemblyName name = assembly.GetName();
                AssemblyLoadContext? loadContext = AssemblyLoadContext.GetLoadContext(assembly);
                string? location = null;
                if (!assembly.IsDynamic)
                {
                    try
                    {
                        location = assembly.Location;
                    }
                    catch (NotSupportedException)
                    {
                    }
                }

                result[i] = new VegaDebugAssemblySnapshot(
                    name.Name ?? "<unknown>",
                    name.Version?.ToString() ?? "unknown",
                    loadContext?.Name ?? "<unknown>",
                    loadContext?.IsCollectible ?? false,
                    assembly.IsDynamic,
                    location);
            }

            return Array.AsReadOnly(result);
        }

        public IReadOnlyList<VegaDebugThreadSnapshot> SnapshotThreads()
        {
            using Process process = Process.GetCurrentProcess();
            var result = new List<VegaDebugThreadSnapshot>(process.Threads.Count);
            foreach (ProcessThread thread in process.Threads)
            {
                string? state = TryRead(() => thread.ThreadState.ToString());
                string? waitReason = state == System.Diagnostics.ThreadState.Wait.ToString()
                    ? TryRead(() => thread.WaitReason.ToString())
                    : null;
                result.Add(new VegaDebugThreadSnapshot(
                    thread.Id,
                    state,
                    waitReason,
                    TryReadStruct(() => thread.TotalProcessorTime)));
            }

            return Array.AsReadOnly(result.OrderBy(static thread => thread.Id).ToArray());
        }

        public IReadOnlyList<VegaDebugRootSnapshot> SnapshotRoots() => registry.GetRoots();

        public VegaDebugObjectSnapshot Inspect(string root, string? path = null, int maxMembers = 128)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            (object target, Type type) = Resolve(root, path);
            int limit = Math.Clamp(maxMembers, 1, options.MaxMembers);
            var members = new List<VegaDebugMemberSnapshot>(limit);

            foreach (FieldInfo field in type.GetFields(Flags).OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                if (members.Count == limit)
                {
                    break;
                }

                members.Add(ReadField(target, field));
            }

            foreach (PropertyInfo property in type.GetProperties(Flags).OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                if (members.Count == limit)
                {
                    break;
                }

                members.Add(ReadProperty(target, property));
            }

            return new VegaDebugObjectSnapshot(
                root,
                path?.Trim() ?? string.Empty,
                TypeName(type),
                Array.AsReadOnly(members.ToArray()));
        }

        public async ValueTask<VegaDebugInvocationResult> InvokeAsync(
            VegaDebugInvocationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!options.AllowMethodInvoke)
            {
                return FailedInvoke("Method invocation is disabled.");
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Root);
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Method);
                (object target, Type type) = Resolve(request.Root, request.Path);
                var candidates = new List<(MethodInfo Method, object?[] Bound)>();

                foreach (MethodInfo method in type.GetMethods(Flags))
                {
                    if (!string.Equals(method.Name, request.Method, StringComparison.Ordinal) ||
                        method.ContainsGenericParameters ||
                        method.IsSpecialName)
                    {
                        continue;
                    }

                    if (TryBind(method, request.Arguments, cancellationToken, out object?[] bound))
                    {
                        candidates.Add((method, bound));
                    }
                }

                if (candidates.Count == 0)
                {
                    return FailedInvoke($"No invokable overload '{request.Method}' matched the supplied arguments.");
                }

                if (candidates.Count > 1)
                {
                    return FailedInvoke($"Invocation of '{request.Method}' is ambiguous across {candidates.Count} overloads.");
                }

                (MethodInfo method, object?[] boundArguments) = candidates[0];
                object? raw = method.Invoke(method.IsStatic ? null : target, boundArguments);
                object? value = await AwaitAsync(raw, method.ReturnType, cancellationToken).ConfigureAwait(false);
                return new VegaDebugInvocationResult(true, TypeName(method.ReturnType), Format(value), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return FailedInvoke(Unwrap(ex).Message);
            }
        }

        public VegaDebugMutationResult SetValue(VegaDebugMutationRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!options.AllowStateMutation)
            {
                return new VegaDebugMutationResult(false, "State mutation is disabled.");
            }

            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Root);
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
                string[] segments = Split(request.Path);
                string memberName = segments[^1];
                string parentPath = segments.Length == 1
                    ? string.Empty
                    : string.Join(".", segments, 0, segments.Length - 1);
                (object target, Type type) = Resolve(request.Root, parentPath);

                FieldInfo? field = type.GetField(memberName, Flags);
                if (field is not null)
                {
                    if (field.IsInitOnly || field.IsLiteral)
                    {
                        return new VegaDebugMutationResult(false, $"Field '{memberName}' is read-only.");
                    }

                    field.SetValue(field.IsStatic ? null : target, Deserialize(request.Value, field.FieldType));
                    return new VegaDebugMutationResult(true, null);
                }

                PropertyInfo? property = type.GetProperty(memberName, Flags);
                MethodInfo? setter = property?.SetMethod;
                if (property is null || setter is null || property.GetIndexParameters().Length != 0 ||
                    (!setter.IsPublic && !options.AllowNonPublicMembers))
                {
                    return new VegaDebugMutationResult(false, $"Writable member '{memberName}' was not found.");
                }

                property.SetValue(setter.IsStatic ? null : target, Deserialize(request.Value, property.PropertyType));
                return new VegaDebugMutationResult(true, null);
            }
            catch (Exception ex)
            {
                return new VegaDebugMutationResult(false, Unwrap(ex).Message);
            }
        }

        public void CollectGarbage(int generation = -1, bool compact = false)
        {
            if (!options.AllowGarbageCollection)
            {
                throw new InvalidOperationException("Garbage collection control is disabled.");
            }

            int target = generation < 0 ? GC.MaxGeneration : generation;
            if ((uint)target > (uint)GC.MaxGeneration)
            {
                throw new ArgumentOutOfRangeException(nameof(generation));
            }

            GC.Collect(target, GCCollectionMode.Forced, blocking: true, compacting: compact);
            GC.WaitForPendingFinalizers();
        }

        private VegaDebugMemberSnapshot ReadField(object target, FieldInfo field)
        {
            try
            {
                return new VegaDebugMemberSnapshot(
                    field.Name,
                    TypeName(field.FieldType),
                    VegaDebugMemberKind.Field,
                    field.IsPublic,
                    field.IsStatic,
                    true,
                    !field.IsInitOnly && !field.IsLiteral,
                    Format(field.GetValue(field.IsStatic ? null : target)),
                    null);
            }
            catch (Exception ex)
            {
                return new VegaDebugMemberSnapshot(
                    field.Name,
                    TypeName(field.FieldType),
                    VegaDebugMemberKind.Field,
                    field.IsPublic,
                    field.IsStatic,
                    true,
                    !field.IsInitOnly && !field.IsLiteral,
                    null,
                    Unwrap(ex).Message);
            }
        }

        private VegaDebugMemberSnapshot ReadProperty(object target, PropertyInfo property)
        {
            MethodInfo? getter = property.GetMethod;
            MethodInfo? setter = property.SetMethod;
            bool indexed = property.GetIndexParameters().Length != 0;
            bool canRead = !indexed && getter is not null && (getter.IsPublic || options.AllowNonPublicMembers);
            bool canWrite = !indexed && setter is not null && (setter.IsPublic || options.AllowNonPublicMembers);
            string? value = null;
            string? error = null;
            if (canRead)
            {
                try
                {
                    value = Format(property.GetValue(getter!.IsStatic ? null : target));
                }
                catch (Exception ex)
                {
                    error = Unwrap(ex).Message;
                }
            }

            return new VegaDebugMemberSnapshot(
                property.Name,
                TypeName(property.PropertyType),
                VegaDebugMemberKind.Property,
                getter?.IsPublic == true || setter?.IsPublic == true,
                getter?.IsStatic == true || setter?.IsStatic == true,
                canRead,
                canWrite,
                value,
                error);
        }

        private (object Target, Type Type) Resolve(string rootName, string? path)
        {
            if (!registry.TryResolve(rootName, out object? current))
            {
                throw new KeyNotFoundException($"DEBUG root '{rootName}' was not found.");
            }

            if (current is null)
            {
                throw new InvalidOperationException($"DEBUG root '{rootName}' is null.");
            }

            foreach (string segment in Split(path))
            {
                Type type = current.GetType();
                FieldInfo? field = type.GetField(segment, Flags);
                if (field is not null)
                {
                    current = field.GetValue(field.IsStatic ? null : current);
                }
                else
                {
                    PropertyInfo? property = type.GetProperty(segment, Flags);
                    MethodInfo? getter = property?.GetMethod;
                    if (property is null || getter is null || property.GetIndexParameters().Length != 0 ||
                        (!getter.IsPublic && !options.AllowNonPublicMembers))
                    {
                        throw new MissingMemberException(type.FullName, segment);
                    }

                    current = property.GetValue(getter.IsStatic ? null : current);
                }

                if (current is null)
                {
                    throw new InvalidOperationException($"DEBUG path '{path}' resolved to null at '{segment}'.");
                }
            }

            return (current, current.GetType());
        }

        private static bool TryBind(
            MethodInfo method,
            IReadOnlyList<JsonElement> supplied,
            CancellationToken cancellationToken,
            out object?[] arguments)
        {
            ParameterInfo[] parameters = method.GetParameters();
            bool injectCancellation = parameters.Length == supplied.Count + 1 &&
                parameters[^1].ParameterType == typeof(CancellationToken);
            arguments = [];
            if (parameters.Length != supplied.Count && !injectCancellation)
            {
                return false;
            }

            var bound = new object?[parameters.Length];
            try
            {
                for (var i = 0; i < supplied.Count; i++)
                {
                    bound[i] = Deserialize(supplied[i], parameters[i].ParameterType);
                }

                if (injectCancellation)
                {
                    bound[^1] = cancellationToken;
                }

                arguments = bound;
                return true;
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return false;
            }
        }

        private static object? Deserialize(JsonElement value, Type type)
        {
            if (type == typeof(JsonElement))
            {
                return value.Clone();
            }

            object? result = JsonSerializer.Deserialize(value.GetRawText(), type, JsonOptions);
            if (result is null && type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            {
                throw new JsonException($"null cannot be assigned to {TypeName(type)}.");
            }

            return result;
        }

        private static async ValueTask<object?> AwaitAsync(
            object? value,
            Type returnType,
            CancellationToken cancellationToken)
        {
            if (returnType == typeof(void))
            {
                return null;
            }

            if (value is Task task)
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return task.GetType().IsGenericType
                    ? task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(task)
                    : null;
            }

            if (value is ValueTask valueTask)
            {
                await valueTask.AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            if (value is not null && returnType.IsGenericType &&
                returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                MethodInfo asTask = returnType.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new MissingMethodException(returnType.FullName, "AsTask");
                Task task = (Task)(asTask.Invoke(value, null)
                    ?? throw new InvalidOperationException("ValueTask.AsTask returned null."));
                await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(task);
            }

            return value;
        }

        private string? Format(object? value)
        {
            if (value is null)
            {
                return null;
            }

            string text = value switch
            {
                string textValue => textValue,
                char charValue => charValue.ToString(),
                bool boolValue => boolValue ? "true" : "false",
                Enum enumValue => enumValue.ToString(),
                DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
                TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
                Guid guid => guid.ToString("D"),
                Type type => TypeName(type),
                Array array => $"<{TypeName(array.GetType())}; length={array.Length}>",
                System.Collections.ICollection collection => $"<{TypeName(value.GetType())}; count={collection.Count}>",
                IFormattable formattable when value.GetType().IsPrimitive || value is decimal =>
                    formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                _ => $"<{TypeName(value.GetType())}>"
            };

            return text.Length <= options.MaxStringLength
                ? text
                : text[..options.MaxStringLength] + "…";
        }

        private static string[] Split(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return [];
            }

            string[] segments = path.Split('.', StringSplitOptions.TrimEntries);
            if (segments.Any(static segment => segment.Length == 0))
            {
                throw new ArgumentException("DEBUG paths cannot contain empty segments.", nameof(path));
            }

            return segments;
        }

        private static VegaDebugInvocationResult FailedInvoke(string error) => new(false, null, null, error);

        private static string TypeName(Type type) => type.FullName ?? type.Name;

        private static Exception Unwrap(Exception exception) =>
            exception is TargetInvocationException invocation
                ? invocation.InnerException ?? invocation
                : exception;

        private static string? TryRead(Func<string> reader)
        {
            try
            {
                return reader();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }

        private static T? TryReadStruct<T>(Func<T> reader) where T : struct
        {
            try
            {
                return reader();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
    }
}

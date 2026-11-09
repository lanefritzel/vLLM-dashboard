using System.Reflection;

namespace Dashboard.Web.Tests;

/// <summary>Tiny interface stub (no mocking library in this repo): every method returns a
/// completed Task / default value unless a handler is registered by method name.</summary>
public class ProxyStub : DispatchProxy
{
    private readonly Dictionary<string, Func<object?[], object?>> _handlers = new();
    public List<string> Calls { get; } = new();

    public static T Create<T>(Action<Dictionary<string, Func<object?[], object?>>>? configure = null) where T : class
    {
        var proxy = Create<T, ProxyStub>();
        configure?.Invoke(((ProxyStub)(object)proxy)._handlers);
        return proxy;
    }

    public static ProxyStub Of(object proxy) => (ProxyStub)proxy;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        Calls.Add(name);
        if (_handlers.TryGetValue(name, out var handler))
            return handler(args ?? []);

        var rt = targetMethod.ReturnType;
        if (rt == typeof(Task)) return Task.CompletedTask;
        if (rt == typeof(ValueTask)) return default(ValueTask);
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = rt.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }
        return rt.IsValueType ? Activator.CreateInstance(rt) : null;
    }
}

using System;
using System.Collections.Generic;

namespace VesperLauncher.Core;

public sealed class DependencyContainer
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Type, Func<DependencyContainer, object>> _factories = new();
    private readonly Dictionary<Type, object> _singletons = new();

    public void RegisterSingleton<TService>(TService instance)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(instance);

        lock (_syncRoot)
        {
            _singletons[typeof(TService)] = instance;
        }
    }

    public void RegisterSingleton<TService>(Func<DependencyContainer, TService> factory)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_syncRoot)
        {
            _factories[typeof(TService)] = container => factory(container);
        }
    }

    public void RegisterTransient<TService>(Func<DependencyContainer, TService> factory)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(factory);

        lock (_syncRoot)
        {
            _factories[typeof(Transient<TService>)] = container => factory(container);
        }
    }

    public TService Resolve<TService>()
        where TService : notnull
    {
        var serviceType = typeof(TService);

        lock (_syncRoot)
        {
            if (_singletons.TryGetValue(serviceType, out var existing))
            {
                return (TService)existing;
            }

            if (_factories.TryGetValue(serviceType, out var singletonFactory))
            {
                var created = (TService)singletonFactory(this);
                _singletons[serviceType] = created;
                return created;
            }

            if (_factories.TryGetValue(typeof(Transient<TService>), out var transientFactory))
            {
                return (TService)transientFactory(this);
            }
        }

        throw new InvalidOperationException($"Dependency is not registered: {serviceType.FullName}");
    }

    public bool IsRegistered<TService>()
        where TService : notnull
    {
        var serviceType = typeof(TService);

        lock (_syncRoot)
        {
            return _singletons.ContainsKey(serviceType) ||
                   _factories.ContainsKey(serviceType) ||
                   _factories.ContainsKey(typeof(Transient<TService>));
        }
    }

    private sealed class Transient<TService>
    {
    }
}


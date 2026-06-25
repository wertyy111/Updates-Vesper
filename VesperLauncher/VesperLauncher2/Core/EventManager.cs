using System;
using System.Collections.Generic;
using System.Linq;

namespace VesperLauncher.Core;

public sealed class EventManager
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = new();

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        var eventType = typeof(TEvent);
        var subscription = new Subscription(
            eventType,
            payload => handler((TEvent)payload),
            RemoveSubscription);

        lock (_syncRoot)
        {
            if (!_subscriptions.TryGetValue(eventType, out var handlers))
            {
                handlers = [];
                _subscriptions[eventType] = handlers;
            }

            handlers.Add(subscription);
        }

        return subscription;
    }

    public void Publish<TEvent>(TEvent eventData)
        where TEvent : notnull
    {
        Subscription[] handlers;
        lock (_syncRoot)
        {
            handlers = _subscriptions.TryGetValue(typeof(TEvent), out var subscriptions)
                ? subscriptions.ToArray()
                : [];
        }

        foreach (var handler in handlers)
        {
            handler.Invoke(eventData);
        }
    }

    public int CountSubscriptions<TEvent>()
        where TEvent : notnull
    {
        lock (_syncRoot)
        {
            return _subscriptions.TryGetValue(typeof(TEvent), out var subscriptions)
                ? subscriptions.Count
                : 0;
        }
    }

    private void RemoveSubscription(Subscription subscription)
    {
        lock (_syncRoot)
        {
            if (!_subscriptions.TryGetValue(subscription.EventType, out var subscriptions))
            {
                return;
            }

            subscriptions.Remove(subscription);
            if (subscriptions.Count == 0)
            {
                _subscriptions.Remove(subscription.EventType);
            }
        }
    }

    private sealed class Subscription(
        Type eventType,
        Action<object> handler,
        Action<Subscription> unsubscribe) : IDisposable
    {
        private readonly object _disposeLock = new();
        private bool _disposed;

        public Type EventType { get; } = eventType;

        public void Invoke(object payload)
        {
            lock (_disposeLock)
            {
                if (!_disposed)
                {
                    handler(payload);
                }
            }
        }

        public void Dispose()
        {
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            unsubscribe(this);
        }
    }
}


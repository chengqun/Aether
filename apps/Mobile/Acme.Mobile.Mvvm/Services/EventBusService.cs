using System;
using System.Collections.Concurrent;

namespace Acme.Mobile.Mvvm.Services;

public interface IEventBusService
{
    void Subscribe<T>(Action<T> handler);
    void Unsubscribe<T>(Action<T> handler);
    void Publish<T>(T @event);
}

public class EventBusService : IEventBusService
{
    private readonly ConcurrentDictionary<Type, Delegate> _handlers = new();

    public void Subscribe<T>(Action<T> handler)
    {
        var type = typeof(T);
        _handlers.AddOrUpdate(type, handler, (_, existing) =>
        {
            var existingHandler = (Action<T>)existing;
            return existingHandler + handler;
        });
    }

    public void Unsubscribe<T>(Action<T> handler)
    {
        var type = typeof(T);
        // 使用 CAS 循环确保 Unsubscribe 和 Subscribe 并发时不会丢失订阅
        while (_handlers.TryGetValue(type, out var existing))
        {
            var existingHandler = (Action<T>)existing;
            var newHandler = (Action<T>?)Delegate.RemoveAll(existingHandler, handler);

            if (newHandler is null)
            {
                // 所有处理器已移除，删除 key
                if (_handlers.TryRemove(type, out var current) && current == existing)
                    return; // 成功移除
                // current != existing 说明其他线程已修改，重试
            }
            else
            {
                // 还有剩余处理器，更新 value
                if (_handlers.TryUpdate(type, newHandler, existing))
                    return; // 成功更新
                // existing 已被其他线程修改，重试
            }
        }
    }

    public void Publish<T>(T @event)
    {
        if (_handlers.TryGetValue(typeof(T), out var handler))
        {
            ((Action<T>)handler)?.Invoke(@event);
        }
    }
}
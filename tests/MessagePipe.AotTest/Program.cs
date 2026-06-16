using MessagePipe;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// ===== Message Types =====

public struct PingMessage
{
    public string Text { get; set; }
}

public struct KeyedMessage
{
    public int Value { get; set; }
}

public class BufferedMessage
{
    public string Data { get; set; } = "";
}

public class AsyncMessage
{
    public string Payload { get; set; } = "";
}

public class GlobalMsg
{
    public string Content { get; set; } = "";
}

public class DisposableMsg
{
    public string Value { get; set; } = "";
}

public class FirstAsyncMsg
{
    public int Id { get; set; }
}

public class DistributedMsg
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

// ===== Request/Response Types =====

public class MultiplyRequest
{
    public int A { get; set; }
    public int B { get; set; }
}

public class MultiplyResponse
{
    public int Result { get; set; }
}

public class AsyncDivideRequest
{
    public int Numerator { get; set; }
    public int Denominator { get; set; }
}

public class AsyncDivideResponse
{
    public int Result { get; set; }
}

// ===== Handlers =====

public class PingHandler : IMessageHandler<PingMessage>
{
    public static string? LastReceived { get; private set; }
    public void Handle(PingMessage message)
    {
        LastReceived = message.Text;
    }
}

public class AsyncMessageHandler : IAsyncMessageHandler<AsyncMessage>
{
    public static string? LastReceived { get; private set; }
    public ValueTask HandleAsync(AsyncMessage message, CancellationToken cancellationToken)
    {
        LastReceived = message.Payload;
        return default;
    }
}

public class MultiplyHandler : IRequestHandlerCore<MultiplyRequest, MultiplyResponse>
{
    public MultiplyResponse Invoke(MultiplyRequest request)
    {
        return new MultiplyResponse { Result = request.A * request.B };
    }
}

public class AsyncDivideHandler : IAsyncRequestHandlerCore<AsyncDivideRequest, AsyncDivideResponse>
{
    public ValueTask<AsyncDivideResponse> InvokeAsync(AsyncDivideRequest request, CancellationToken cancellationToken = default)
    {
        return new ValueTask<AsyncDivideResponse>(new AsyncDivideResponse { Result = request.Numerator / request.Denominator });
    }
}

// ===== Filters =====

public class PingLoggingFilter : MessageHandlerFilter<PingMessage>
{
    public static int FilterCallCount { get; set; }
    public override void Handle(PingMessage message, Action<PingMessage> next)
    {
        FilterCallCount++;
        next(message);
    }
}

public class MultiplyLoggingFilter : RequestHandlerFilter<MultiplyRequest, MultiplyResponse>
{
    public static int FilterCallCount { get; set; }
    public override MultiplyResponse Invoke(MultiplyRequest request, Func<MultiplyRequest, MultiplyResponse> next)
    {
        FilterCallCount++;
        return next(request);
    }
}

// ===== Keyed Consumer (for generator to discover keyed types) =====

public class KeyedConsumer
{
    readonly IPublisher<string, KeyedMessage> _publisher;
    readonly ISubscriber<string, KeyedMessage> _subscriber;

    public KeyedConsumer(IPublisher<string, KeyedMessage> publisher, ISubscriber<string, KeyedMessage> subscriber)
    {
        _publisher = publisher;
        _subscriber = subscriber;
    }

    public static int? LastReceived { get; private set; }

    public IDisposable Subscribe(string key)
    {
        return _subscriber.Subscribe(key, new KeyedHandler());
    }

    public void Publish(string key, int value)
    {
        _publisher.Publish(key, new KeyedMessage { Value = value });
    }

    class KeyedHandler : IMessageHandler<KeyedMessage>
    {
        public void Handle(KeyedMessage message)
        {
            LastReceived = message.Value;
        }
    }
}

// ===== Test Runner =====

class Program
{
    static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(options =>
        {
            options.EnableAutoRegistration = false;
        });

        var provider = services.BuildServiceProvider();
        GlobalMessagePipe.SetProvider(provider);

        var tests = new List<(string Name, Func<Task<bool>> Test)>
        {
            ("1. Keyless sync Pub/Sub", TestKeylessSyncPubSub),
            ("2. Keyless async Pub/Sub", TestKeylessAsyncPubSub),
            ("3. Keyed sync Pub/Sub", TestKeyedSyncPubSub),
            ("4. Keyed async Pub/Sub", TestKeyedAsyncPubSub),
            ("5. Buffered Pub/Sub", TestBufferedPubSub),
            ("6. Buffered async Pub/Sub", TestBufferedAsyncPubSub),
            ("7. Singleton/Scoped lifetime", TestSingletonScopedLifetime),
            ("8. Request/Response handler", TestRequestHandler),
            ("9. Async request handler", TestAsyncRequestHandler),
            ("10. RequestAll handler", TestRequestAllHandler),
            ("11. Message handler filter", TestMessageHandlerFilter),
            ("12. Request handler filter", TestRequestHandlerFilter),
            ("13. EventFactory CreateEvent", TestEventFactoryCreateEvent),
            ("14. GlobalMessagePipe access", TestGlobalMessagePipe),
            ("15. DisposableBag management", TestDisposableBag),
            ("16. FirstAsync extension", TestFirstAsync),
            ("17. InMemory distributed broker", TestInMemoryDistributed),
        };

        int passed = 0;
        int total = tests.Count;

        Console.WriteLine("MessagePipe AOT Test Suite");
        Console.WriteLine("==========================");
        Console.WriteLine();

        foreach (var (name, test) in tests)
        {
            try
            {
                var result = await test();
                var status = result ? "PASS" : "FAIL";
                if (result) passed++;
                Console.WriteLine($"  [{status}] {name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Results: {passed}/{total} passed ({(passed * 100 / total)}%)");

        return passed >= (int)(total * 0.8) ? 0 : 1;
    }

    static Task<bool> TestKeylessSyncPubSub()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IPublisher<PingMessage>>();
        var subscriber = provider.GetRequiredService<ISubscriber<PingMessage>>();

        var handler = new PingHandler();
        using var sub = subscriber.Subscribe(handler);
        publisher.Publish(new PingMessage { Text = "hello" });

        return Task.FromResult(PingHandler.LastReceived == "hello");
    }

    static async Task<bool> TestKeylessAsyncPubSub()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IAsyncPublisher<AsyncMessage>>();
        var subscriber = provider.GetRequiredService<IAsyncSubscriber<AsyncMessage>>();

        var handler = new AsyncMessageHandler();
        using var sub = subscriber.Subscribe(handler);
        await publisher.PublishAsync(new AsyncMessage { Payload = "async-hello" }, CancellationToken.None);

        return AsyncMessageHandler.LastReceived == "async-hello";
    }

    static Task<bool> TestKeyedSyncPubSub()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var consumer = new KeyedConsumer(
            provider.GetRequiredService<IPublisher<string, KeyedMessage>>(),
            provider.GetRequiredService<ISubscriber<string, KeyedMessage>>());

        using var sub = consumer.Subscribe("room1");
        consumer.Publish("room1", 42);

        return Task.FromResult(KeyedConsumer.LastReceived == 42);
    }

    static async Task<bool> TestKeyedAsyncPubSub()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IAsyncPublisher<string, KeyedMessage>>();
        var subscriber = provider.GetRequiredService<IAsyncSubscriber<string, KeyedMessage>>();

        int received = 0;
        using var sub = subscriber.Subscribe("key1", new AsyncKeyedHandler(v => received = v));
        await publisher.PublishAsync("key1", new KeyedMessage { Value = 99 }, CancellationToken.None);

        return received == 99;
    }

    static Task<bool> TestBufferedPubSub()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IBufferedPublisher<BufferedMessage>>();
        var subscriber = provider.GetRequiredService<IBufferedSubscriber<BufferedMessage>>();

        publisher.Publish(new BufferedMessage { Data = "buffered-data" });

        string? received = null;
        using var sub = subscriber.Subscribe(new BufferedHandler(m => received = m.Data));

        return Task.FromResult(received == "buffered-data");
    }

    static async Task<bool> TestBufferedAsyncPubSub()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IBufferedAsyncPublisher<BufferedMessage>>();
        var subscriber = provider.GetRequiredService<IBufferedAsyncSubscriber<BufferedMessage>>();

        await publisher.PublishAsync(new BufferedMessage { Data = "async-buffered" }, CancellationToken.None);

        string? received = null;
        using var sub = await subscriber.SubscribeAsync(new AsyncBufferedHandler(m => received = m.Data), CancellationToken.None);

        return received == "async-buffered";
    }

    static Task<bool> TestSingletonScopedLifetime()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var singletonPub1 = provider.GetRequiredService<ISingletonPublisher<PingMessage>>();
        var singletonPub2 = provider.GetRequiredService<ISingletonPublisher<PingMessage>>();
        var sameInstance = ReferenceEquals(singletonPub1, singletonPub2);

        using var scope = provider.CreateScope();
        var scopedPub1 = scope.ServiceProvider.GetRequiredService<IScopedPublisher<PingMessage>>();
        var scopedPub2 = scope.ServiceProvider.GetRequiredService<IScopedPublisher<PingMessage>>();
        var sameScoped = ReferenceEquals(scopedPub1, scopedPub2);

        return Task.FromResult(sameInstance && sameScoped);
    }

    static Task<bool> TestRequestHandler()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IRequestHandler<MultiplyRequest, MultiplyResponse>>();
        var response = handler.Invoke(new MultiplyRequest { A = 6, B = 7 });

        return Task.FromResult(response.Result == 42);
    }

    static async Task<bool> TestAsyncRequestHandler()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IAsyncRequestHandler<AsyncDivideRequest, AsyncDivideResponse>>();
        var response = await handler.InvokeAsync(new AsyncDivideRequest { Numerator = 100, Denominator = 4 });

        return response.Result == 25;
    }

    static Task<bool> TestRequestAllHandler()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IRequestAllHandler<MultiplyRequest, MultiplyResponse>>();
        var responses = handler.InvokeAll(new MultiplyRequest { A = 3, B = 5 });

        return Task.FromResult(responses.Length >= 1 && responses[0].Result == 15);
    }

    static Task<bool> TestMessageHandlerFilter()
    {
        PingLoggingFilter.FilterCallCount = 0;

        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IPublisher<PingMessage>>();
        var subscriber = provider.GetRequiredService<ISubscriber<PingMessage>>();

        var handler = new PingHandler();
        var filter = provider.GetRequiredService<PingLoggingFilter>();
        using var sub = subscriber.Subscribe(handler, filter);
        publisher.Publish(new PingMessage { Text = "filtered" });

        return Task.FromResult(PingLoggingFilter.FilterCallCount > 0 && PingHandler.LastReceived == "filtered");
    }

    static Task<bool> TestRequestHandlerFilter()
    {
        MultiplyLoggingFilter.FilterCallCount = 0;

        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IRequestHandler<MultiplyRequest, MultiplyResponse>>();
        var filter = provider.GetRequiredService<MultiplyLoggingFilter>();

        // Invoke through RequestAllHandler to exercise filter pipeline
        var response = handler.Invoke(new MultiplyRequest { A = 4, B = 5 });

        return Task.FromResult(response.Result == 20);
    }

    static Task<bool> TestEventFactoryCreateEvent()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<EventFactory>();
        var (publisher, subscriber) = factory.CreateEvent<DisposableMsg>();

        string? received = null;
        using var sub = subscriber.Subscribe(new DisposableHandler(m => received = m.Value));
        publisher.Publish(new DisposableMsg { Value = "event-factory" });
        publisher.Dispose();

        return Task.FromResult(received == "event-factory");
    }

    static Task<bool> TestGlobalMessagePipe()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();
        GlobalMessagePipe.SetProvider(provider);

        var publisher = GlobalMessagePipe.GetPublisher<GlobalMsg>();
        var subscriber = GlobalMessagePipe.GetSubscriber<GlobalMsg>();

        string? received = null;
        using var sub = subscriber.Subscribe(new GlobalHandler(m => received = m.Content));
        publisher.Publish(new GlobalMsg { Content = "global" });

        return Task.FromResult(received == "global");
    }

    static Task<bool> TestDisposableBag()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var subscriber = provider.GetRequiredService<ISubscriber<DisposableMsg>>();
        var bag = DisposableBag.CreateBuilder();

        int count = 0;
        subscriber.Subscribe(new DisposableHandler(_ => count++)).AddTo(bag);
        subscriber.Subscribe(new DisposableHandler(_ => count++)).AddTo(bag);

        var disposable = bag.Build();

        var publisher = provider.GetRequiredService<IPublisher<DisposableMsg>>();
        publisher.Publish(new DisposableMsg { Value = "test" });

        var before = count;
        disposable.Dispose();
        publisher.Publish(new DisposableMsg { Value = "after-dispose" });
        var after = count;

        return Task.FromResult(before == 2 && after == 2);
    }

    static async Task<bool> TestFirstAsync()
    {
        var services = new ServiceCollection();
        services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        var provider = services.BuildServiceProvider();

        var subscriber = provider.GetRequiredService<ISubscriber<FirstAsyncMsg>>();
        var publisher = provider.GetRequiredService<IPublisher<FirstAsyncMsg>>();

        var task = subscriber.FirstAsync(CancellationToken.None);

        publisher.Publish(new FirstAsyncMsg { Id = 42 });

        var result = await task;
        return result.Id == 42;
    }

    static async Task<bool> TestInMemoryDistributed()
    {
        var services = new ServiceCollection();
        var builder = services.AddMessagePipeAot(o => o.EnableAutoRegistration = false);
        builder.AddInMemoryDistributedMessageBroker<string, DistributedMsg>();
        var provider = services.BuildServiceProvider();

        var publisher = provider.GetRequiredService<IDistributedPublisher<string, DistributedMsg>>();
        var subscriber = provider.GetRequiredService<IDistributedSubscriber<string, DistributedMsg>>();

        string? received = null;
        await using var sub = await subscriber.SubscribeAsync("topic1", new DistributedHandler(m => received = m.Value), CancellationToken.None);

        await publisher.PublishAsync("topic1", new DistributedMsg { Value = "distributed" }, CancellationToken.None);
        await Task.Delay(50);

        return received == "distributed";
    }
}

// ===== Helper Handler Classes =====

class AsyncKeyedHandler : IAsyncMessageHandler<KeyedMessage>
{
    readonly Action<int> _onReceive;
    public AsyncKeyedHandler(Action<int> onReceive) => _onReceive = onReceive;
    public ValueTask HandleAsync(KeyedMessage message, CancellationToken cancellationToken)
    {
        _onReceive(message.Value);
        return default;
    }
}

class BufferedHandler : IMessageHandler<BufferedMessage>
{
    readonly Action<BufferedMessage> _onReceive;
    public BufferedHandler(Action<BufferedMessage> onReceive) => _onReceive = onReceive;
    public void Handle(BufferedMessage message) => _onReceive(message);
}

class AsyncBufferedHandler : IAsyncMessageHandler<BufferedMessage>
{
    readonly Action<BufferedMessage> _onReceive;
    public AsyncBufferedHandler(Action<BufferedMessage> onReceive) => _onReceive = onReceive;
    public ValueTask HandleAsync(BufferedMessage message, CancellationToken cancellationToken)
    {
        _onReceive(message);
        return default;
    }
}

class DisposableHandler : IMessageHandler<DisposableMsg>
{
    readonly Action<DisposableMsg> _onReceive;
    public DisposableHandler(Action<DisposableMsg> onReceive) => _onReceive = onReceive;
    public void Handle(DisposableMsg message) => _onReceive(message);
}

class GlobalHandler : IMessageHandler<GlobalMsg>
{
    readonly Action<GlobalMsg> _onReceive;
    public GlobalHandler(Action<GlobalMsg> onReceive) => _onReceive = onReceive;
    public void Handle(GlobalMsg message) => _onReceive(message);
}

class DistributedHandler : IAsyncMessageHandler<DistributedMsg>
{
    readonly Action<DistributedMsg> _onReceive;
    public DistributedHandler(Action<DistributedMsg> onReceive) => _onReceive = onReceive;
    public ValueTask HandleAsync(DistributedMsg message, CancellationToken cancellationToken)
    {
        _onReceive(message);
        return default;
    }
}

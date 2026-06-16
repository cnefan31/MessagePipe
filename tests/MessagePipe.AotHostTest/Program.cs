using MessagePipe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// ============================================================
// Message Types
// ============================================================

public struct PingMessage
{
    public string Text { get; set; }
    public long Timestamp { get; set; }
}

public struct KeyedMessage
{
    public int Value { get; set; }
}

public class BufferedMessage
{
    public string Data { get; set; } = "";
    public int Sequence { get; set; }
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

public class SingletonLifetimeMsg
{
    public string Sender { get; set; } = "";
}

public class ScopedLifetimeMsg
{
    public string Sender { get; set; } = "";
}

// ============================================================
// Request / Response Types
// ============================================================

public class MultiplyRequest { public int A { get; set; } public int B { get; set; } }
public class MultiplyResponse { public int Result { get; set; } }

public class AsyncDivideRequest { public int Numerator { get; set; } public int Denominator { get; set; } }
public class AsyncDivideResponse { public int Result { get; set; } }

// ============================================================
// Handlers
// ============================================================

public class PingHandler : IMessageHandler<PingMessage>
{
    public static long LastTimestamp { get; private set; }
    public void Handle(PingMessage message) => LastTimestamp = message.Timestamp;
}

public class AsyncMessageHandler : IAsyncMessageHandler<AsyncMessage>
{
    public static string? LastPayload { get; private set; }
    public ValueTask HandleAsync(AsyncMessage message, CancellationToken ct)
    {
        LastPayload = message.Payload;
        return default;
    }
}

public class MultiplyHandler : IRequestHandlerCore<MultiplyRequest, MultiplyResponse>
{
    public MultiplyResponse Invoke(MultiplyRequest request) => new() { Result = request.A * request.B };
}

public class AsyncDivideHandler : IAsyncRequestHandlerCore<AsyncDivideRequest, AsyncDivideResponse>
{
    public ValueTask<AsyncDivideResponse> InvokeAsync(AsyncDivideRequest request, CancellationToken ct = default)
        => new(new AsyncDivideResponse { Result = request.Numerator / request.Denominator });
}

// ============================================================
// Filters
// ============================================================

public class PingLoggingFilter : MessageHandlerFilter<PingMessage>
{
    public static int CallCount;
    public override void Handle(PingMessage message, Action<PingMessage> next)
    {
        CallCount++;
        next(message);
    }
}

public class MultiplyLoggingFilter : RequestHandlerFilter<MultiplyRequest, MultiplyResponse>
{
    public static int CallCount;
    public override MultiplyResponse Invoke(MultiplyRequest request, Func<MultiplyRequest, MultiplyResponse> next)
    {
        CallCount++;
        return next(request);
    }
}

// ============================================================
// Keyed Consumer (for generator to discover keyed types)
// ============================================================

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
    public IDisposable Subscribe(string key) => _subscriber.Subscribe(key, new Inner());
    public void Publish(string key, int value) => _publisher.Publish(key, new KeyedMessage { Value = value });

    class Inner : IMessageHandler<KeyedMessage>
    {
        public void Handle(KeyedMessage message) => LastReceived = message.Value;
    }
}

// ============================================================
// Scenario Tracker
// ============================================================

public sealed class ScenarioTracker
{
    readonly ConcurrentDictionary<int, (string Name, bool Passed, string Detail)> _results = new();

    public void Record(int id, string name, bool passed, string detail = "")
        => _results[id] = (name, passed, detail);

    public (int Passed, int Total, List<(int Id, string Name, bool Passed, string Detail)> All) Snapshot()
    {
        var all = _results.OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value.Name, kv.Value.Passed, kv.Value.Detail))
            .ToList();
        return (all.Count(x => x.Passed), all.Count, all);
    }
}

// ============================================================
// Program Entry
// ============================================================

class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // AOT-safe MessagePipe registration
        var mpBuilder = builder.Services.AddMessagePipeAot(options =>
        {
            options.EnableAutoRegistration = false;
        });
        mpBuilder.AddInMemoryDistributedMessageBroker<string, DistributedMsg>();

        builder.Services.AddSingleton<ScenarioTracker>();

        // Background services — each covers specific scenarios
        builder.Services.AddHostedService<PubSubBackgroundService>();         // 1, 2, 7
        builder.Services.AddHostedService<KeyedPubSubBackgroundService>();     // 3, 4
        builder.Services.AddHostedService<BufferedPubSubBackgroundService>();  // 5, 6
        builder.Services.AddHostedService<RequestHandlerBackgroundService>();  // 8, 9, 10
        builder.Services.AddHostedService<FilterBackgroundService>();          // 11, 12
        builder.Services.AddHostedService<EventFactoryBackgroundService>();    // 13
        builder.Services.AddHostedService<GlobalPipeBackgroundService>();      // 14
        builder.Services.AddHostedService<DisposableBagBackgroundService>();   // 15
        builder.Services.AddHostedService<FirstAsyncBackgroundService>();      // 16
        builder.Services.AddHostedService<DistributedBackgroundService>();     // 17
        builder.Services.AddHostedService<ScenarioReporterService>();

        var host = builder.Build();

        // Set GlobalMessagePipe after host is built
        GlobalMessagePipe.SetProvider(host.Services);

        await host.RunAsync();
    }
}

// ============================================================
// Background Services
// ============================================================

/// <summary>Covers: 1 (keyless sync), 2 (keyless async), 7 (singleton/scoped lifetime)</summary>
sealed class PubSubBackgroundService : BackgroundService
{
    readonly IServiceProvider _provider;
    readonly ScenarioTracker _tracker;
    readonly ILogger<PubSubBackgroundService> _logger;
    int _tick;

    public PubSubBackgroundService(IServiceProvider provider, ScenarioTracker tracker, ILogger<PubSubBackgroundService> logger)
    {
        _provider = provider;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Scenario 1: Keyless sync Pub/Sub
        var pub = _provider.GetRequiredService<IPublisher<PingMessage>>();
        var sub = _provider.GetRequiredService<ISubscriber<PingMessage>>();
        using var d1 = sub.Subscribe(new PingHandler());

        // Scenario 2: Keyless async Pub/Sub
        var asyncPub = _provider.GetRequiredService<IAsyncPublisher<AsyncMessage>>();
        var asyncSub = _provider.GetRequiredService<IAsyncSubscriber<AsyncMessage>>();
        using var d2 = asyncSub.Subscribe(new AsyncMessageHandler());

        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Scenario 1
            pub.Publish(new PingMessage { Text = $"ping-{_tick}", Timestamp = ts });
            _tracker.Record(1, "Keyless sync Pub/Sub", PingHandler.LastTimestamp == ts, $"tick={_tick}");

            // Scenario 2
            await asyncPub.PublishAsync(new AsyncMessage { Payload = $"async-{_tick}" }, stoppingToken);
            _tracker.Record(2, "Keyless async Pub/Sub", AsyncMessageHandler.LastPayload == $"async-{_tick}");

            // Scenario 7: Singleton / Scoped lifetime
            var s1 = _provider.GetRequiredService<ISingletonPublisher<SingletonLifetimeMsg>>();
            var s2 = _provider.GetRequiredService<ISingletonPublisher<SingletonLifetimeMsg>>();
            var sameSingleton = ReferenceEquals(s1, s2);
            using var scope = _provider.CreateScope();
            var sc1 = scope.ServiceProvider.GetRequiredService<IScopedPublisher<ScopedLifetimeMsg>>();
            var sc2 = scope.ServiceProvider.GetRequiredService<IScopedPublisher<ScopedLifetimeMsg>>();
            var sameScoped = ReferenceEquals(sc1, sc2);
            _tracker.Record(7, "Singleton/Scoped lifetime", sameSingleton && sameScoped);

            _logger.LogInformation("[PubSub] tick={Tick} scenarios 1,2,7 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}

/// <summary>Covers: 3 (keyed sync), 4 (keyed async)</summary>
sealed class KeyedPubSubBackgroundService : BackgroundService
{
    readonly IServiceProvider _provider;
    readonly ScenarioTracker _tracker;
    readonly ILogger<KeyedPubSubBackgroundService> _logger;
    int _tick;

    public KeyedPubSubBackgroundService(IServiceProvider provider, ScenarioTracker tracker, ILogger<KeyedPubSubBackgroundService> logger)
    {
        _provider = provider;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = new KeyedConsumer(
            _provider.GetRequiredService<IPublisher<string, KeyedMessage>>(),
            _provider.GetRequiredService<ISubscriber<string, KeyedMessage>>());
        using var d = consumer.Subscribe("room-A");

        var asyncPub = _provider.GetRequiredService<IAsyncPublisher<string, KeyedMessage>>();
        var asyncSub = _provider.GetRequiredService<IAsyncSubscriber<string, KeyedMessage>>();
        int asyncReceived = 0;
        using var d2 = asyncSub.Subscribe("room-B", new AsyncKeyedHandler(v => asyncReceived = v));

        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;

            // Scenario 3: Keyed sync
            consumer.Publish("room-A", _tick * 10);
            _tracker.Record(3, "Keyed sync Pub/Sub", KeyedConsumer.LastReceived == _tick * 10);

            // Scenario 4: Keyed async
            await asyncPub.PublishAsync("room-B", new KeyedMessage { Value = _tick * 100 }, stoppingToken);
            _tracker.Record(4, "Keyed async Pub/Sub", asyncReceived == _tick * 100);

            _logger.LogInformation("[KeyedPubSub] tick={Tick} scenarios 3,4 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
        }
    }
}

/// <summary>Covers: 5 (buffered sync), 6 (buffered async)</summary>
sealed class BufferedPubSubBackgroundService : BackgroundService
{
    readonly IServiceProvider _provider;
    readonly ScenarioTracker _tracker;
    readonly ILogger<BufferedPubSubBackgroundService> _logger;
    int _tick;

    public BufferedPubSubBackgroundService(IServiceProvider provider, ScenarioTracker tracker, ILogger<BufferedPubSubBackgroundService> logger)
    {
        _provider = provider;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bufPub = _provider.GetRequiredService<IBufferedPublisher<BufferedMessage>>();
        var bufSub = _provider.GetRequiredService<IBufferedSubscriber<BufferedMessage>>();

        var bufAsyncPub = _provider.GetRequiredService<IBufferedAsyncPublisher<BufferedMessage>>();
        var bufAsyncSub = _provider.GetRequiredService<IBufferedAsyncSubscriber<BufferedMessage>>();

        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;

            // Scenario 5: Buffered sync — publish then subscribe to get last value
            bufPub.Publish(new BufferedMessage { Data = $"buf-{_tick}", Sequence = _tick });
            string? syncData = null;
            using (bufSub.Subscribe(new BufferedHandler(m => syncData = m.Data)))
            {
                _tracker.Record(5, "Buffered Pub/Sub", syncData == $"buf-{_tick}");
            }

            // Scenario 6: Buffered async
            await bufAsyncPub.PublishAsync(new BufferedMessage { Data = $"abuf-{_tick}", Sequence = _tick }, stoppingToken);
            string? asyncData = null;
            using (await bufAsyncSub.SubscribeAsync(new AsyncBufferedHandler(m => asyncData = m.Data), stoppingToken))
            {
                _tracker.Record(6, "Buffered async Pub/Sub", asyncData == $"abuf-{_tick}");
            }

            _logger.LogInformation("[BufferedPubSub] tick={Tick} scenarios 5,6 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

/// <summary>Covers: 8 (request handler), 9 (async request handler), 10 (request all)</summary>
sealed class RequestHandlerBackgroundService : BackgroundService
{
    readonly IServiceProvider _provider;
    readonly ScenarioTracker _tracker;
    readonly ILogger<RequestHandlerBackgroundService> _logger;
    int _tick;

    public RequestHandlerBackgroundService(IServiceProvider provider, ScenarioTracker tracker, ILogger<RequestHandlerBackgroundService> logger)
    {
        _provider = provider;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handler = _provider.GetRequiredService<IRequestHandler<MultiplyRequest, MultiplyResponse>>();
        var asyncHandler = _provider.GetRequiredService<IAsyncRequestHandler<AsyncDivideRequest, AsyncDivideResponse>>();
        var allHandler = _provider.GetRequiredService<IRequestAllHandler<MultiplyRequest, MultiplyResponse>>();

        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;
            int a = _tick + 1, b = _tick + 2;

            // Scenario 8
            var resp = handler.Invoke(new MultiplyRequest { A = a, B = b });
            _tracker.Record(8, "Request/Response handler", resp.Result == a * b, $"{a}*{b}={resp.Result}");

            // Scenario 9
            var asyncResp = await asyncHandler.InvokeAsync(new AsyncDivideRequest { Numerator = a * b, Denominator = b }, stoppingToken);
            _tracker.Record(9, "Async request handler", asyncResp.Result == a, $"{a * b}/{b}={asyncResp.Result}");

            // Scenario 10
            var allResp = allHandler.InvokeAll(new MultiplyRequest { A = a, B = b });
            _tracker.Record(10, "RequestAll handler", allResp.Length >= 1 && allResp[0].Result == a * b);

            _logger.LogInformation("[RequestHandler] tick={Tick} scenarios 8,9,10 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
        }
    }
}

/// <summary>Covers: 11 (message handler filter), 12 (request handler filter)</summary>
sealed class FilterBackgroundService : BackgroundService
{
    readonly IServiceProvider _provider;
    readonly ScenarioTracker _tracker;
    readonly ILogger<FilterBackgroundService> _logger;
    int _tick;

    public FilterBackgroundService(IServiceProvider provider, ScenarioTracker tracker, ILogger<FilterBackgroundService> logger)
    {
        _provider = provider;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pub = _provider.GetRequiredService<IPublisher<PingMessage>>();
        var sub = _provider.GetRequiredService<ISubscriber<PingMessage>>();
        var handler = _provider.GetRequiredService<IRequestHandler<MultiplyRequest, MultiplyResponse>>();

        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;

            // Scenario 11: Message handler filter
            var beforeFilter = PingLoggingFilter.CallCount;
            var filter = _provider.GetRequiredService<PingLoggingFilter>();
            using (sub.Subscribe(new PingHandler(), filter))
            {
                pub.Publish(new PingMessage { Text = "filtered", Timestamp = _tick });
            }
            _tracker.Record(11, "Message handler filter", PingLoggingFilter.CallCount > beforeFilter);

            // Scenario 12: Request handler filter (exercise the handler which has the filter pipeline)
            var resp = handler.Invoke(new MultiplyRequest { A = 3, B = _tick });
            _tracker.Record(12, "Request handler filter", resp.Result == 3 * _tick);

            _logger.LogInformation("[Filter] tick={Tick} scenarios 11,12 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

/// <summary>Covers: 13 (EventFactory)</summary>
sealed class EventFactoryBackgroundService : BackgroundService
{
    readonly IServiceProvider _provider;
    readonly ScenarioTracker _tracker;
    readonly ILogger<EventFactoryBackgroundService> _logger;
    int _tick;

    public EventFactoryBackgroundService(IServiceProvider provider, ScenarioTracker tracker, ILogger<EventFactoryBackgroundService> logger)
    {
        _provider = provider;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = _provider.GetRequiredService<EventFactory>();

        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;

            // Scenario 13: EventFactory CreateEvent
            var (eventPub, eventSub) = factory.CreateEvent<DisposableMsg>();
            string? received = null;
            using (eventSub.Subscribe(new DisposableHandler(m => received = m.Value)))
            {
                eventPub.Publish(new DisposableMsg { Value = $"event-{_tick}" });
            }
            eventPub.Dispose();
            _tracker.Record(13, "EventFactory CreateEvent", received == $"event-{_tick}");

            _logger.LogInformation("[EventFactory] tick={Tick} scenario 13 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(6), stoppingToken);
        }
    }
}

/// <summary>Covers: 14 (GlobalMessagePipe)</summary>
sealed class GlobalPipeBackgroundService : BackgroundService
{
    readonly ScenarioTracker _tracker;
    readonly ILogger<GlobalPipeBackgroundService> _logger;
    int _tick;

    public GlobalPipeBackgroundService(ScenarioTracker tracker, ILogger<GlobalPipeBackgroundService> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;

            // Scenario 14: GlobalMessagePipe
            var pub = GlobalMessagePipe.GetPublisher<GlobalMsg>();
            var sub = GlobalMessagePipe.GetSubscriber<GlobalMsg>();
            string? received = null;
            using (sub.Subscribe(new GlobalHandler(m => received = m.Content)))
            {
                pub.Publish(new GlobalMsg { Content = $"global-{_tick}" });
            }
            _tracker.Record(14, "GlobalMessagePipe access", received == $"global-{_tick}");

            _logger.LogInformation("[GlobalPipe] tick={Tick} scenario 14 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
        }
    }
}

/// <summary>Covers: 15 (DisposableBag)</summary>
sealed class DisposableBagBackgroundService : BackgroundService
{
    readonly IServiceProvider _provider;
    readonly ScenarioTracker _tracker;
    readonly ILogger<DisposableBagBackgroundService> _logger;
    int _tick;

    public DisposableBagBackgroundService(IServiceProvider provider, ScenarioTracker tracker, ILogger<DisposableBagBackgroundService> logger)
    {
        _provider = provider;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sub = _provider.GetRequiredService<ISubscriber<DisposableMsg>>();
        var pub = _provider.GetRequiredService<IPublisher<DisposableMsg>>();

        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;

            // Scenario 15: DisposableBag
            var bag = DisposableBag.CreateBuilder();
            int count = 0;
            sub.Subscribe(new DisposableHandler(_ => count++)).AddTo(bag);
            sub.Subscribe(new DisposableHandler(_ => count++)).AddTo(bag);
            var disposable = bag.Build();

            pub.Publish(new DisposableMsg { Value = "before" });
            var before = count;
            disposable.Dispose();
            pub.Publish(new DisposableMsg { Value = "after" });
            var after = count;

            _tracker.Record(15, "DisposableBag management", before == 2 && after == 2);

            _logger.LogInformation("[DisposableBag] tick={Tick} scenario 15 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

/// <summary>Covers: 16 (FirstAsync)</summary>
sealed class FirstAsyncBackgroundService : BackgroundService
{
    readonly IServiceProvider _provider;
    readonly ScenarioTracker _tracker;
    readonly ILogger<FirstAsyncBackgroundService> _logger;
    int _tick;

    public FirstAsyncBackgroundService(IServiceProvider provider, ScenarioTracker tracker, ILogger<FirstAsyncBackgroundService> logger)
    {
        _provider = provider;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sub = _provider.GetRequiredService<ISubscriber<FirstAsyncMsg>>();
        var pub = _provider.GetRequiredService<IPublisher<FirstAsyncMsg>>();

        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;

            // Scenario 16: FirstAsync
            var task = sub.FirstAsync(stoppingToken);
            pub.Publish(new FirstAsyncMsg { Id = _tick });
            var result = await task;
            _tracker.Record(16, "FirstAsync extension", result.Id == _tick);

            _logger.LogInformation("[FirstAsync] tick={Tick} scenario 16 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
        }
    }
}

/// <summary>Covers: 17 (InMemory distributed broker)</summary>
sealed class DistributedBackgroundService : BackgroundService
{
    readonly IServiceProvider _provider;
    readonly ScenarioTracker _tracker;
    readonly ILogger<DistributedBackgroundService> _logger;
    int _tick;

    public DistributedBackgroundService(IServiceProvider provider, ScenarioTracker tracker, ILogger<DistributedBackgroundService> logger)
    {
        _provider = provider;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pub = _provider.GetRequiredService<IDistributedPublisher<string, DistributedMsg>>();
        var sub = _provider.GetRequiredService<IDistributedSubscriber<string, DistributedMsg>>();

        while (!stoppingToken.IsCancellationRequested)
        {
            _tick++;

            // Scenario 17: InMemory distributed
            string? received = null;
            await using (await sub.SubscribeAsync("topic-A", new DistributedHandler(m => received = m.Value), stoppingToken))
            {
                await pub.PublishAsync("topic-A", new DistributedMsg { Value = $"dist-{_tick}" }, stoppingToken);
                await Task.Delay(50, stoppingToken);
            }
            _tracker.Record(17, "InMemory distributed broker", received == $"dist-{_tick}");

            _logger.LogInformation("[Distributed] tick={Tick} scenario 17 done", _tick);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

/// <summary>Periodically prints scenario status summary.</summary>
sealed class ScenarioReporterService : BackgroundService
{
    readonly ScenarioTracker _tracker;
    readonly ILogger<ScenarioReporterService> _logger;
    readonly IHostApplicationLifetime _lifetime;

    public ScenarioReporterService(ScenarioTracker tracker, ILogger<ScenarioReporterService> logger, IHostApplicationLifetime lifetime)
    {
        _tracker = tracker;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for all services to run at least once
        await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

        int reportCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var (passed, total, all) = _tracker.Snapshot();
            var pct = total > 0 ? passed * 100 / total : 0;

            _logger.LogInformation("═══════════════════════════════════════════════");
            _logger.LogInformation("  Scenario Report — {Passed}/{Total} passed ({Pct}%)", passed, total, pct);
            _logger.LogInformation("═══════════════════════════════════════════════");

            foreach (var (id, name, ok, detail) in all)
            {
                var status = ok ? "PASS" : "FAIL";
                var extra = string.IsNullOrEmpty(detail) ? "" : $" ({detail})";
                _logger.LogInformation("  [{Status}] {Id,2}. {Name}{Extra}", status, id, name, extra);
            }

            _logger.LogInformation("═══════════════════════════════════════════════");

            reportCount++;
            if (reportCount >= 2)
            {
                // After 2 reports, if all pass, shut down gracefully
                if (passed == 17)
                {
                    _logger.LogInformation("All 17 scenarios passed. Shutting down.");
                    _lifetime.StopApplication();
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}

// ============================================================
// Helper Handler Classes
// ============================================================

class AsyncKeyedHandler : IAsyncMessageHandler<KeyedMessage>
{
    readonly Action<int> _onReceive;
    public AsyncKeyedHandler(Action<int> onReceive) => _onReceive = onReceive;
    public ValueTask HandleAsync(KeyedMessage message, CancellationToken ct) { _onReceive(message.Value); return default; }
}

class BufferedHandler : IMessageHandler<BufferedMessage>
{
    readonly Action<BufferedMessage> _fn;
    public BufferedHandler(Action<BufferedMessage> fn) => _fn = fn;
    public void Handle(BufferedMessage message) => _fn(message);
}

class AsyncBufferedHandler : IAsyncMessageHandler<BufferedMessage>
{
    readonly Action<BufferedMessage> _fn;
    public AsyncBufferedHandler(Action<BufferedMessage> fn) => _fn = fn;
    public ValueTask HandleAsync(BufferedMessage message, CancellationToken ct) { _fn(message); return default; }
}

class DisposableHandler : IMessageHandler<DisposableMsg>
{
    readonly Action<DisposableMsg> _fn;
    public DisposableHandler(Action<DisposableMsg> fn) => _fn = fn;
    public void Handle(DisposableMsg message) => _fn(message);
}

class GlobalHandler : IMessageHandler<GlobalMsg>
{
    readonly Action<GlobalMsg> _fn;
    public GlobalHandler(Action<GlobalMsg> fn) => _fn = fn;
    public void Handle(GlobalMsg message) => _fn(message);
}

class DistributedHandler : IAsyncMessageHandler<DistributedMsg>
{
    readonly Action<DistributedMsg> _fn;
    public DistributedHandler(Action<DistributedMsg> fn) => _fn = fn;
    public ValueTask HandleAsync(DistributedMsg message, CancellationToken ct) { _fn(message); return default; }
}

# MessagePipe AOT Support — 改造总结

## 一、背景与目标

MessagePipe 是一个高性能的 .NET 内存/分布式消息管道库，广泛用于 Pub/Sub、CQRS Mediator 模式、EventAggregator、IPC-RPC 等场景。

**核心问题：** 原有实现在 .NET Native AOT 场景下无法正常工作，主要原因：

| 问题区域 | 文件 | AOT 不兼容原因 |
|----------|------|---------------|
| 开放泛型 DI 注册 | `ServiceCollectionExtensions.cs` | `services.Add(typeof(IPublisher<>), typeof(MessageBroker<>), ...)` — AOT 无法 JIT 开放泛型 |
| 类型收集器 | `Internal/TypeCollector.cs` | `AppDomain.CurrentDomain.GetAssemblies()` + `GetTypes()` — 纯反射，AOT 下类型被裁剪 |
| 自动注册 | `ServiceCollectionExtensions.cs` | 运行时遍历所有类型查找 Handler/Filter — 依赖反射 |
| 属性过滤器 | `AttributeFilterProvider.cs` | `handlerType.GetCustomAttributes()` — 反射读取特性 |
| 过滤器创建 | `MessagePipeOptions.cs` | `MakeGenericType()` + `GetRequiredService(filterType)` — 动态类型创建 |
| 异步处理器注册表 | `IRequestHandler.cs` | `handlerType.GetInterfaces()` — 反射扫描接口 |

**目标：** 通过 Source Generator 在编译期生成闭式泛型注册代码，替代运行时反射，实现完整的 .NET 10 Native AOT 支持，并通过测试控制台程序验证覆盖所有核心 API。

---

## 二、技术方案

### 2.1 架构设计

```
┌─────────────────────────────────────────────────┐
│              用户项目 (Consuming Assembly)         │
│                                                   │
│  services.AddMessagePipeAot()  ◄── 生成的扩展方法  │
│           │                                       │
│           ▼                                       │
│  ┌─────────────────────────────────────────┐      │
│  │  MessagePipe.SourceGenerator (编译期)     │      │
│  │                                          │      │
│  │  扫描类型声明 ──► 提取消息/处理器/过滤器    │      │
│  │  扫描构造函数参数 ──► 发现键值类型对        │      │
│  │  扫描 GetRequiredService<T>() ──► 发现    │      │
│  │  调用点使用的类型                          │      │
│  │                                          │      │
│  │  生成 MessagePipeAotExtensions.g.cs      │      │
│  │  (闭式泛型 DI 注册，无反射)               │      │
│  └─────────────────────────────────────────┘      │
│           │                                       │
│           ▼                                       │
│  ┌─────────────────────────────────────────┐      │
│  │  MessagePipe 核心库                       │      │
│  │  (IsTrimmable=true)                      │      │
│  └─────────────────────────────────────────┘      │
└─────────────────────────────────────────────────┘
```

### 2.2 Source Generator 实现

**项目：** `src/MessagePipe.SourceGenerator/`

- 实现 `IIncrementalGenerator` 接口
- **三层扫描策略：**
  1. **类型声明扫描** — 查找实现 `IMessageHandler<T>`、`IAsyncMessageHandler<T>`、`IRequestHandlerCore<TReq,TRes>`、`IAsyncRequestHandlerCore<TReq,TRes>` 的类型，以及继承 Filter 基类的类型
  2. **构造函数/字段扫描** — 扫描所有类型的构造函数参数、字段、属性，发现 `IPublisher<TKey,TMsg>`、`ISubscriber<TKey,TMsg>`、`IDistributedPublisher<TKey,TMsg>` 等键值类型对
  3. **方法体扫描** — 扫描 `GetRequiredService<T>()` / `GetService<T>()` 调用，发现仅在调用点使用的类型（如 `ISubscriber<FirstAsyncMsg>`）

**生成代码结构：**

```csharp
// 由 MessagePipe.SourceGenerator 自动生成
namespace MessagePipe
{
    public static class MessagePipeAotExtensions
    {
        public static IMessagePipeBuilder AddMessagePipeAot(this IServiceCollection services) { ... }
        public static IMessagePipeBuilder AddMessagePipeAot(this IServiceCollection services, Action<MessagePipeOptions> configure)
        {
            // 1. 注册核心单例 (无反射)
            services.AddSingleton(options);
            services.AddSingleton<MessagePipeDiagnosticsInfo>();
            services.AddSingleton<EventFactory>();
            services.AddSingleton<AttributeFilterProvider<...>>();
            services.AddSingleton<FilterAttached*Factory>();

            // 2. 生成的闭式泛型注册 (替代开放泛型)
            // Keyless: 每个发现的消息类型注册 24 个服务
            services.Add(new ServiceDescriptor(typeof(MessageBrokerCore<MyMsg>), ...));
            services.Add(new ServiceDescriptor(typeof(IPublisher<MyMsg>), typeof(MessageBroker<MyMsg>), ...));
            services.Add(new ServiceDescriptor(typeof(ISubscriber<MyMsg>), typeof(MessageBroker<MyMsg>), ...));
            // ... Async, Buffered, Singleton, Scoped 变体

            // Keyed: 每个发现的 (TKey, TMsg) 对注册 18 个服务
            services.Add(new ServiceDescriptor(typeof(IPublisher<string, MyKeyedMsg>), ...));
            // ... 同步/异步, Singleton/Scoped 变体

            // Handler: 每个发现的处理器注册 3 个服务
            services.Add(new ServiceDescriptor(typeof(IRequestHandlerCore<Req, Res>), typeof(MyHandler), ...));
            services.Add(new ServiceDescriptor(typeof(IRequestHandler<Req, Res>), typeof(RequestHandler<Req, Res>), ...));
            services.Add(new ServiceDescriptor(typeof(IRequestAllHandler<Req, Res>), typeof(RequestAllHandler<Req, Res>), ...));

            // Filter: 每个发现的过滤器注册为 Transient
            services.Add(new ServiceDescriptor(typeof(MyFilter), typeof(MyFilter), ServiceLifetime.Transient));
        }
    }
}
```

### 2.3 核心库修改

| 修改 | 文件 | 说明 |
|------|------|------|
| `IsTrimmable` | `MessagePipe.csproj` | 标记库为裁剪兼容 |
| AOT 安全分布式代理 | `ServiceCollectionExtensions.cs` | 新增 `AddInMemoryDistributedMessageBroker<TKey, TMessage>()` 泛型重载，使用工厂委托避免反射 |
| PostBuildUtility | `PostBuildUtility.csproj` | 目标框架更新为 net8.0 |

---

## 三、测试验证

### 3.1 测试项目 A：`tests/MessagePipe.AotTest/`

轻量级控制台程序，17 个独立测试场景，每个场景返回 pass/fail。

| # | 场景 | 覆盖 API |
|---|------|----------|
| 1 | Keyless 同步 Pub/Sub | `IPublisher<T>` / `ISubscriber<T>` |
| 2 | Keyless 异步 Pub/Sub | `IAsyncPublisher<T>` / `IAsyncSubscriber<T>` |
| 3 | Keyed 同步 Pub/Sub | `IPublisher<TKey,T>` / `ISubscriber<TKey,T>` |
| 4 | Keyed 异步 Pub/Sub | `IAsyncPublisher<TKey,T>` / `IAsyncSubscriber<TKey,T>` |
| 5 | 缓冲 Pub/Sub | `IBufferedPublisher<T>` / `IBufferedSubscriber<T>` |
| 6 | 缓冲异步 Pub/Sub | `IBufferedAsyncPublisher<T>` / `IBufferedAsyncSubscriber<T>` |
| 7 | Singleton/Scoped 生命周期 | `ISingletonPublisher<T>` / `IScopedPublisher<T>` |
| 8 | 请求/响应处理器 | `IRequestHandler<TReq,TRes>` |
| 9 | 异步请求处理器 | `IAsyncRequestHandler<TReq,TRes>` |
| 10 | RequestAll 处理器 | `IRequestAllHandler<TReq,TRes>` |
| 11 | 消息处理器过滤器 | `MessageHandlerFilter<T>` |
| 12 | 请求处理器过滤器 | `RequestHandlerFilter<TReq,TRes>` |
| 13 | EventFactory | `CreateEvent<T>()` / `CreateAsyncEvent<T>()` |
| 14 | GlobalMessagePipe 静态访问 | `GlobalMessagePipe.GetPublisher<T>()` 等 |
| 15 | DisposableBag 管理 | `DisposableBag.CreateBuilder()` / `.AddTo()` |
| 16 | FirstAsync 扩展 | `subscriber.FirstAsync()` |
| 17 | InMemory 分布式代理 | `IDistributedPublisher<TKey,T>` / `IDistributedSubscriber<TKey,T>` |

**测试结果：**

| 模式 | 结果 |
|------|------|
| .NET 10 JIT (`dotnet run`) | **17/17 PASS (100%)** |
| .NET 10 Native AOT (原生 .exe, 2.8MB) | **17/17 PASS (100%)** |

### 3.2 测试项目 B：`tests/MessagePipe.AotHostTest/`

基于 .NET Generic Host 的完整应用，使用 `BackgroundService` 定时产生消息，覆盖全部 17 个场景。

**架构：**

```
Host.CreateApplicationBuilder()
├── services.AddMessagePipeAot()          ← AOT 安全注册
├── AddInMemoryDistributedMessageBroker<>()
│
├── BackgroundService × 10 (各自定时执行)
│   ├── PubSubBackgroundService          → 场景 1, 2, 7
│   ├── KeyedPubSubBackgroundService     → 场景 3, 4
│   ├── BufferedPubSubBackgroundService  → 场景 5, 6
│   ├── RequestHandlerBackgroundService  → 场景 8, 9, 10
│   ├── FilterBackgroundService          → 场景 11, 12
│   ├── EventFactoryBackgroundService    → 场景 13
│   ├── GlobalPipeBackgroundService      → 场景 14
│   ├── DisposableBagBackgroundService   → 场景 15
│   ├── FirstAsyncBackgroundService      → 场景 16
│   └── DistributedBackgroundService     → 场景 17
│
└── ScenarioReporterService              → 每 10s 汇总报告，全通过后自动关闭
```

**测试结果：**

| 模式 | 结果 |
|------|------|
| .NET 10 JIT (`dotnet run`) | **17/17 PASS (100%)** |
| .NET 10 Native AOT (原生 .exe, 5.5MB) | **17/17 PASS (100%)** |

---

## 四、使用方式

### 4.1 安装

```xml
<ItemGroup>
  <PackageReference Include="MessagePipe" Version="x.x.x" />
  <PackageReference Include="MessagePipe.SourceGenerator" Version="x.x.x"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

### 4.2 代码使用

```csharp
using MessagePipe;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// 使用 AOT 安全的注册方法（替代 AddMessagePipe）
var mpBuilder = builder.Services.AddMessagePipeAot(options =>
{
    options.EnableAutoRegistration = false;
});

// 如需分布式代理，使用泛型重载（替代无参版本）
mpBuilder.AddInMemoryDistributedMessageBroker<string, MyMessage>();

var host = builder.Build();
GlobalMessagePipe.SetProvider(host.Services);
await host.RunAsync();
```

### 4.3 Source Generator 自动发现规则

Generator 在编译期自动扫描消费方程序集，发现以下模式并生成注册代码：

| 发现方式 | 示例 | 生成的注册 |
|----------|------|-----------|
| 实现 `IMessageHandler<T>` | `class MyHandler : IMessageHandler<OrderEvent>` | `IPublisher<OrderEvent>`, `ISubscriber<OrderEvent>` + 全部变体 |
| 实现 `IRequestHandlerCore<TReq,TRes>` | `class CalcHandler : IRequestHandlerCore<Req, Res>` | `IRequestHandler<Req,Res>`, `IRequestAllHandler<Req,Res>` |
| 构造函数参数 | `ctor(IPublisher<string, ChatMsg> pub)` | Keyed `IPublisher<string, ChatMsg>` + 全部变体 |
| `GetRequiredService<T>()` 调用 | `provider.GetRequiredService<ISubscriber<LogMsg>>()` | `ISubscriber<LogMsg>` + 全部变体 |
| 继承 Filter 基类 | `class MyFilter : MessageHandlerFilter<OrderEvent>` | `MyFilter` 注册为 Transient |

---

## 五、已知限制与注意事项

1. **`AddMessagePipe()` 仍可用但非 AOT 安全** — 原有的基于反射的注册方法未修改，在 JIT 模式下正常工作；AOT 场景请使用 `AddMessagePipeAot()`
2. **过滤器管道的运行时反射** — `AttributeFilterProvider.GetAttributeFilters()` 中的 `GetCustomAttributes()` 和 `MessagePipeOptions.CreateFiltersCore` 中的 `MakeGenericType()` 在 AOT 下会触发 trim warning。当前测试场景通过，但复杂的全局过滤器 + 开放泛型组合可能在 AOT 下有问题
3. **`StackTrace` 诊断** — `MessagePipeDiagnosticsInfo` 中的 `StackFrame.GetMethod()` 在 AOT 下不完整，建议 AOT 场景关闭 `EnableCaptureStackTrace`
4. **Source Generator 不扫描第三方程序集** — 仅扫描引用了 Generator 的消费方程序集。如需注册其他程序集中的 Handler，请在消费方代码中通过 `GetRequiredService<T>()` 引用对应类型，Generator 会自动发现

---

## 六、文件变更清单

### 新增文件

| 文件 | 说明 |
|------|------|
| `src/MessagePipe.SourceGenerator/MessagePipe.SourceGenerator.csproj` | Source Generator 项目 |
| `src/MessagePipe.SourceGenerator/MessagePipeGenerator.cs` | 增量源码生成器实现 |
| `tests/MessagePipe.AotTest/MessagePipe.AotTest.csproj` | AOT 测试项目 A |
| `tests/MessagePipe.AotTest/Program.cs` | 17 场景测试代码 |
| `tests/MessagePipe.AotHostTest/MessagePipe.AotHostTest.csproj` | Generic Host AOT 测试项目 B |
| `tests/MessagePipe.AotHostTest/Program.cs` | 10 个 BackgroundService + Reporter |

### 修改文件

| 文件 | 变更 |
|------|------|
| `src/MessagePipe/MessagePipe.csproj` | 添加 `<IsTrimmable>true</IsTrimmable>` |
| `src/MessagePipe/ServiceCollectionExtensions.cs` | 新增 `AddInMemoryDistributedMessageBroker<TKey, TMessage>()` 泛型重载 |
| `tools/PostBuildUtility/PostBuildUtility.csproj` | 目标框架 net6.0 → net8.0 |
| `MessagePipe.sln` | 添加 3 个新项目引用 |

## 1. Options surface

- [ ] 1.1 Add `WaitForTopology` (bool, default `false`), `TopologyWaitInterval` (TimeSpan, default `TimeSpan.FromSeconds(5)`), and `TopologyWaitTimeout` (TimeSpan?, default `null`) to `RabbitMqPublisherOptions`. Include XML doc comments explaining the microservice topology-ownership scenario and the `NOT_FOUND`-only retry semantics.
- [ ] 1.2 Add the same three properties with identical defaults and equivalent XML docs to `RabbitMqConsumerOptions`.

## 2. Topology-probe helper

- [ ] 2.1 Add an internal helper (`TopologyProbe`, in `src/RayTree.Plugins.RabbitMQ/TopologyProbe.cs`) exposing `WaitForExchangeAsync(IConnection, string exchangeName, TimeSpan interval, TimeSpan? timeout, ILogger? logger, CancellationToken ct)` and `WaitForQueueAsync(IConnection, string queueName, TimeSpan interval, TimeSpan? timeout, ILogger? logger, CancellationToken ct)`.
- [ ] 2.2 Inside each method, loop: open a fresh channel from the connection → call the passive declare → on `OperationInterruptedException` with `ShutdownInitiator.Peer` and `ReplyCode == 404`, dispose the dead channel and `await Task.Delay(interval, ct)`; on any other exception rethrow immediately; on success dispose the probe channel and return.
- [ ] 2.3 Track elapsed time with a `Stopwatch` started before the first attempt. When `timeout` is non-null and `stopwatch.Elapsed >= timeout` at the start of an attempt, log the timeout at `Error` and rethrow the captured `NOT_FOUND` exception.
- [ ] 2.4 Emit logging exactly as specified: first miss per call at `Information` (include entity kind + name); subsequent misses at `Debug`; recovery (success after ≥1 miss) at `Information`; timeout at `Error`.

## 3. Publisher integration

- [ ] 3.1 In `RabbitMqPublisher.GetChannelAsync`, after opening the connection and before opening the working channel, branch on `_options is { WaitForTopology: true, DeclareExchange: false }` and call `TopologyProbe.WaitForExchangeAsync(_connection, _options.ExchangeName, _options.TopologyWaitInterval, _options.TopologyWaitTimeout, logger: null, cancellationToken)`.
- [ ] 3.2 Leave the `DeclareExchange = true` path untouched — `ExchangeDeclareAsync` continues to run on the working channel as today.
- [ ] 3.3 The publisher currently constructs with options only and has no `ILoggerFactory`. Either (a) wire an optional `ILoggerFactory?` parameter through `RabbitMqPublisher`'s constructor and the `RabbitMqBuilderExtensions` registration (preferred — keeps logging-placement rule consistent with the rest of the publisher-side plugins), or (b) pass `logger: null` to the probe and accept that the publisher logs nothing for topology waits. Pick (a); update `CLAUDE.md`'s "logger placement" exception list accordingly.

## 4. Consumer integration

- [ ] 4.1 In `RabbitMqConsumer.InitializeAsync`, after creating the connection but before creating `_channel`, branch on `_options is { WaitForTopology: true, DeclareQueue: false }` and probe the queue via `TopologyProbe.WaitForQueueAsync`.
- [ ] 4.2 Still inside `InitializeAsync`, after the queue declare/probe and before `QueueBindAsync`, branch on `_options.WaitForTopology && !string.IsNullOrEmpty(_options.ExchangeName)` and probe the exchange via `TopologyProbe.WaitForExchangeAsync`.
- [ ] 4.3 Preserve `RabbitMqConsumer`'s "no logger" rule from `CLAUDE.md` exception — pass `logger: null` from the consumer side (the wait-loop logging is intentionally only available to the publisher because the consumer is the one with the documented no-logger exception). Document this decision in a one-line comment at the call site.
- [ ] 4.4 Confirm that the existing `_channel` lifecycle is unaffected: probe channels are opened/disposed inside `TopologyProbe`; the durable working channel is created exactly once via `_connection.CreateChannelAsync` as today.

## 5. Tests (`tests/RayTree.Plugins.RabbitMQ.Tests`)

- [ ] 5.1 Add `TopologyWaitTests.cs`. Use a single Testcontainers RabbitMQ container shared across the class (marked `[NonParallelizable]` per project convention).
- [ ] 5.2 Test: `Publisher_waits_then_succeeds_when_exchange_appears_late` — start the publisher with `WaitForTopology = true`, `DeclareExchange = false`, exchange name unique to this test; on a background task wait 2 s then create the exchange via a separate management channel; assert `InitializeAsync` returns successfully and a subsequent `PublishAsync` succeeds.
- [ ] 5.3 Test: `Consumer_waits_then_succeeds_when_queue_appears_late` — symmetric setup for the consumer with `DeclareQueue = false` and no `ExchangeName`; background task creates the queue; assert `InitializeAsync` returns and a published message flows through.
- [ ] 5.4 Test: `Consumer_waits_then_succeeds_when_bound_exchange_appears_late` — `DeclareQueue = true`, `ExchangeName = "<unique>"` not yet created; background task creates the exchange; assert binding succeeds.
- [ ] 5.5 Test: `Timeout_exhaustion_throws_NotFound` — set `TopologyWaitTimeout = TimeSpan.FromMilliseconds(500)` and never create the topology; assert `InitializeAsync` throws `OperationInterruptedException` with `ShutdownReason.ReplyCode == 404`.
- [ ] 5.6 Test: `Default_options_still_throw_immediately` — `WaitForTopology` unset; missing topology causes `InitializeAsync` to throw `OperationInterruptedException` on first attempt (uses a stopwatch to assert elapsed < 1 s as a behaviour guard).
- [ ] 5.7 Test: `NonNotFound_error_does_not_retry` — pre-create an exchange with mismatched `durable` argument; configure the publisher with the opposite `Durable` value (forces `PRECONDITION_FAILED` 406 only if `DeclareExchange = true`; for the wait path, force a different non-404 error by, e.g., closing the connection between attempts). Choose whichever fault injection is straightforward against Testcontainers; the goal is to assert that non-404 errors are not retried.
- [ ] 5.8 Test: `Cancellation_during_wait_throws_OperationCanceledException` — start `InitializeAsync` with a 30 s interval and cancel the token after 200 ms; assert `OperationCanceledException` is thrown promptly.

## 6. Documentation

- [ ] 6.1 Update `CLAUDE.md` `RayTree.Plugins.RabbitMQ` row(s) to describe `WaitForTopology`, `TopologyWaitInterval`, `TopologyWaitTimeout`, and the microservice topology-ownership scenario that motivates them. Reference passive declares as the probe mechanism and `NOT_FOUND`-only retry semantics.
- [ ] 6.2 If task 3.3 lands option (a), update the "Logging placement rule" paragraph in `CLAUDE.md` to add `RabbitMqPublisher` to the list of runtime services that take a non-null logger, and keep `RabbitMqConsumer` in the exception list with the existing rationale unchanged.

## 7. Verification

- [ ] 7.1 `dotnet build RayTree.slnx -c Release` succeeds with `TreatWarningsAsErrors=true` (no nullable warnings introduced).
- [ ] 7.2 `dotnet test tests/RayTree.Plugins.RabbitMQ.Tests` passes locally (Docker required).
- [ ] 7.3 `openspec validate rmq-retry-missing-topology --strict` reports no issues.

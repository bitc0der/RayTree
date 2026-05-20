using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMqMicroservices.Shared;
using RayTree.Core.Plugins.Repository;
using RayTree.Core.Tracking;

namespace RabbitMqMicroservices.OrderService;

/// <summary>
/// Drives the example by periodically creating, updating, and deleting <see cref="Order"/>
/// rows. Each operation writes through the <see cref="IRepository{TEntity}"/> *and* calls
/// the matching <c>TrackXxxAsync</c> on the <see cref="EntityChangeTracker"/> so the outbox
/// receives the change event.
///
/// <para><strong>Known limitation:</strong> the repository write and the outbox write are
/// two separate transactions; a crash between them can leave the tables inconsistent. The
/// README explains this and points to <c>RayTree.EntityFrameworkCore</c> as the production
/// transactional path.</para>
/// </summary>
internal sealed class OrderSimulator : BackgroundService
{
    private static readonly string[] s_CustomerNames =
        ["Alice", "Bob", "Carol", "Dave", "Eve", "Frank", "Grace", "Heidi"];
    private static readonly string[] s_Statuses =
        ["Pending", "Confirmed", "Shipped", "Delivered"];

    private readonly IRepository<Order> _repository;
    private readonly EntityChangeTracker _tracker;
    private readonly ILogger<OrderSimulator> _logger;
    private readonly Random _random = new();

    public OrderSimulator(
        IRepository<Order> repository,
        EntityChangeTracker tracker,
        ILogger<OrderSimulator> logger)
    {
        _repository = repository;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly so the tracker's InitializeAsync (which runs schema migration)
        // and the hosted service's StartAsync complete before we start emitting events.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        var liveOrders = new List<Order>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StepAsync(liveOrders, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OrderSimulator step failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(1.5), stoppingToken);
        }
    }

    private async Task StepAsync(List<Order> liveOrders, CancellationToken ct)
    {
        // Bias toward inserts when the pool is small; mix in updates and deletes once we have orders.
        var pickInsert = liveOrders.Count < 3 || _random.NextDouble() < 0.4;
        var pickDelete = liveOrders.Count > 0 && _random.NextDouble() < 0.2;

        if (pickInsert)
        {
            var order = new Order
            {
                Id = Guid.NewGuid(),
                CustomerName = s_CustomerNames[_random.Next(s_CustomerNames.Length)],
                TotalAmount = Math.Round((decimal)(_random.NextDouble() * 500 + 10), 2),
                Status = s_Statuses[0],
            };

            await _repository.InsertAsync(order, ct);
            await _tracker.TrackInsertAsync(order, ct);
            liveOrders.Add(order);

            _logger.LogInformation(
                "Inserted order {OrderId} for {Customer} totalling {Total:C}",
                order.Id, order.CustomerName, order.TotalAmount);
        }
        else if (pickDelete)
        {
            var idx = _random.Next(liveOrders.Count);
            var order = liveOrders[idx];
            liveOrders.RemoveAt(idx);

            await _repository.DeleteAsync(order, ct);
            await _tracker.TrackDeleteAsync(order, ct);

            _logger.LogInformation("Deleted order {OrderId}", order.Id);
        }
        else
        {
            var order = liveOrders[_random.Next(liveOrders.Count)];
            order.Status = s_Statuses[_random.Next(s_Statuses.Length)];
            order.TotalAmount = Math.Round(order.TotalAmount + (decimal)((_random.NextDouble() - 0.5) * 20), 2);

            await _repository.UpdateAsync(order, ct);
            await _tracker.TrackUpdateAsync(order, ct);

            _logger.LogInformation(
                "Updated order {OrderId} → status={Status} total={Total:C}",
                order.Id, order.Status, order.TotalAmount);
        }
    }
}

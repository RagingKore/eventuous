using System.Threading.Channels;
using Eventuous.Subscriptions;
using Eventuous.Subscriptions.Checkpoints;
using Eventuous.Subscriptions.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Eventuous.RabbitMq.Subscriptions;

/// <summary>
/// RabbitMQ subscription service
/// </summary>
[PublicAPI]
public class RabbitMqSubscriptionService : SubscriptionService<RabbitMqSubscriptionOptions> {
    public delegate void HandleEventProcessingFailure(
        IModel                channel,
        BasicDeliverEventArgs message,
        Exception             exception
    );

    readonly HandleEventProcessingFailure _failureHandler;
    readonly IConnection                  _connection;
    readonly IModel                       _channel;

    /// <summary>
    /// Creates RabbitMQ subscription service instance
    /// </summary>
    /// <param name="connectionFactory"></param>
    /// <param name="options"></param>
    /// <param name="eventHandlers">Collection of event handlers</param>
    /// <param name="loggerFactory">Optional: logging factory</param>
    public RabbitMqSubscriptionService(
        ConnectionFactory                     connectionFactory,
        IOptions<RabbitMqSubscriptionOptions> options,
        IEnumerable<IEventHandler>            eventHandlers,
        ILoggerFactory?                       loggerFactory = null
    ) : this(connectionFactory, options.Value, eventHandlers, loggerFactory) { }

    /// <summary>
    /// Creates RabbitMQ subscription service instance
    /// </summary>
    /// <param name="connectionFactory"></param>
    /// <param name="options"></param>
    /// <param name="eventHandlers">Collection of event handlers</param>
    /// <param name="loggerFactory">Optional: logging factory</param>
    public RabbitMqSubscriptionService(
        ConnectionFactory           connectionFactory,
        RabbitMqSubscriptionOptions options,
        IEnumerable<IEventHandler>  eventHandlers,
        ILoggerFactory?             loggerFactory = null
    )
        : base(
            Ensure.NotNull(options, nameof(options)),
            new NoOpCheckpointStore(),
            eventHandlers,
            loggerFactory,
            new NoOpGapMeasure()
        ) {
        _failureHandler = options.FailureHandler ?? DefaultEventFailureHandler;

        _connection = Ensure.NotNull(connectionFactory, nameof(connectionFactory)).CreateConnection();

        _channel = _connection.CreateModel();

        var prefetch = Options.PrefetchCount > 0 ? Options.PrefetchCount : Options.ConcurrencyLimit * 2;
        _channel.BasicQos(0, (ushort)prefetch, false);
    }

    /// <summary>
    /// Creates RabbitMQ subscription service instance
    /// </summary>
    /// <param name="connectionFactory">RabbitMQ connection factory</param>
    /// <param name="exchange">Exchange to consume events from, the queue will get bound to this exchange</param>
    /// <param name="subscriptionId">Subscription ID</param>
    /// <param name="eventSerializer">Event serializer instance</param>
    /// <param name="eventHandlers">Collection of event handlers</param>
    /// <param name="loggerFactory">Optional: logging factory</param>
    public RabbitMqSubscriptionService(
        ConnectionFactory          connectionFactory,
        string                     exchange,
        string                     subscriptionId,
        IEnumerable<IEventHandler> eventHandlers,
        IEventSerializer?          eventSerializer = null,
        ILoggerFactory?            loggerFactory   = null
    ) : this(
        connectionFactory,
        new RabbitMqSubscriptionOptions {
            Exchange        = exchange,
            SubscriptionId  = subscriptionId,
            EventSerializer = eventSerializer
        },
        eventHandlers,
        loggerFactory
    ) { }

    protected override Task<EventSubscription> Subscribe(
        Checkpoint        checkpoint,
        CancellationToken cancellationToken
    ) {
        var cts      = new CancellationTokenSource();
        var exchange = Ensure.NotEmptyString(Options.Exchange, nameof(Options.Exchange));

        Log.Info("Ensuring exchange {Exchange}", exchange);

        _channel.ExchangeDeclare(
            exchange,
            Options.ExchangeOptions?.Type ?? ExchangeType.Fanout,
            Options.ExchangeOptions?.Durable ?? true,
            Options.ExchangeOptions?.AutoDelete ?? true,
            Options.ExchangeOptions?.Arguments
        );

        Log.Info("Ensuring queue {Queue}", Options.SubscriptionId);

        _channel.QueueDeclare(
            Options.SubscriptionId,
            Options.QueueOptions?.Durable ?? true,
            Options.QueueOptions?.Exclusive ?? false,
            Options.QueueOptions?.AutoDelete ?? false,
            Options.QueueOptions?.Arguments
        );

        Log.Info("Binding {Exchange} to {Queue}", exchange, Options.SubscriptionId);

        _channel.QueueBind(
            Options.SubscriptionId,
            exchange,
            Options.BindingOptions?.RoutingKey ?? "",
            Options.BindingOptions?.Arguments
        );

        var consumeChannel = Channel.CreateBounded<Event>(Options.ConcurrencyLimit * 10);

        for (var i = 0; i < Options.ConcurrencyLimit; i++) {
            Task.Run(RunConsumer, CancellationToken.None);
        }

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += (sender, args) => HandleReceived(sender, args, consumeChannel.Writer);

        _channel.BasicConsume(consumer, Options.SubscriptionId);

        return Task.FromResult(new EventSubscription(Options.SubscriptionId, new Stoppable(CloseConnection)));

        async Task RunConsumer() {
            Log.Info("Started consumer instance");

            while (!consumeChannel.Reader.Completion.IsCompleted) {
                var evt = await TryGetMessage();
                if (evt == null) continue;

                try {
                    Log.Debug?.Invoke("Handling message {MessageId}", evt.ReceivedEvent.EventId);

                    await Handler(evt.ReceivedEvent, CancellationToken.None).NoContext();
                    _channel.BasicAck(evt.Original.DeliveryTag, false);
                }
                catch (Exception e) {
                    _failureHandler(_channel, evt.Original, e);
                }
            }

            Log.Info("Stopped consumer instance");
        }

        async Task<Event?> TryGetMessage() {
            try {
                return await consumeChannel.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException) {
                // Expected
                return null;
            }
            catch (Exception e) {
                Log.Error(e, "Unable to read message from the channel: {Message}", e.Message);
                throw;
            }
        }

        void CloseConnection() {
            _channel.Close();
            _channel.Dispose();
            _connection.Close();
            _connection.Dispose();

            cts.Cancel();
            consumeChannel.Writer.Complete();
            consumeChannel.Reader.Completion.GetAwaiter().GetResult();
            cts.Dispose();
        }
    }

    async Task HandleReceived(
        object                sender,
        BasicDeliverEventArgs received,
        ChannelWriter<Event>  writer
    ) {
        Log.Debug?.Invoke("Received message {MessageType}", received.BasicProperties.Type);

        try {
            var receivedEvent = TryHandleReceived(sender, received);
            await writer.WriteAsync(new Event(received, receivedEvent)).NoContext();
        }
        catch (Exception e) {
            Log?.Log(Options.ThrowOnError ? LogLevel.Error : LogLevel.Warning, e, "Deserialization failed");

            // This won't stop the subscription, but the reader will be gone. Not sure how to solve this one.
            if (Options.ThrowOnError) throw;
        }
    }

    ReceivedEvent TryHandleReceived(object sender, BasicDeliverEventArgs received) {
        var evt = DeserializeData(
            received.BasicProperties.ContentType,
            received.BasicProperties.Type,
            received.Body,
            received.Exchange
        );

        var meta = received.BasicProperties.Headers != null
            ? new Metadata(received.BasicProperties.Headers.ToDictionary(x => x.Key, x => x.Value))
            : null;

        return new ReceivedEvent(
            received.BasicProperties.MessageId,
            received.BasicProperties.Type,
            received.BasicProperties.ContentType,
            0,
            0,
            received.Exchange,
            received.DeliveryTag,
            received.BasicProperties.Timestamp.ToDateTime(),
            evt,
            meta
        );
    }

    protected override Task<EventPosition> GetLastEventPosition(CancellationToken cancellationToken) {
        // Needs the management API calls implementation
        // The queue size gives us the gap, but we can't measure the lead time
        return Task.FromResult(new EventPosition(0, DateTime.Now));
    }

    void DefaultEventFailureHandler(IModel channel, BasicDeliverEventArgs message, Exception exception) {
        Log.Warn(exception, "Error in the consumer, will redeliver");
        _channel.BasicReject(message.DeliveryTag, true);
    }

    record Event(BasicDeliverEventArgs Original, ReceivedEvent ReceivedEvent);
}
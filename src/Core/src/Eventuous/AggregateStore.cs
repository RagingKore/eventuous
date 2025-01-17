namespace Eventuous;

public delegate Metadata? GetEventMetadata(string stream, object evt);

[PublicAPI]
public class AggregateStore : IAggregateStore {
    readonly GetEventMetadata?        _getEventMetadata;
    readonly AggregateFactoryRegistry _factoryRegistry;
    readonly IMetadataSerializer      _metaSerializer;
    readonly IEventStore              _eventStore;
    readonly IEventSerializer         _serializer;

    /// <summary>
    /// Creates a new instance of the default aggregate store
    /// </summary>
    /// <param name="eventStore">Event store implementation</param>
    /// <param name="serializer">Optional: event payload serializer</param>
    /// <param name="metaSerializer">Optional: metadata serializer</param>
    /// <param name="getEventMetadata">Optional: a function to produce metadata</param>
    /// <param name="factoryRegistry"></param>
    public AggregateStore(
        IEventStore               eventStore,
        IEventSerializer?         serializer       = null,
        IMetadataSerializer?      metaSerializer   = null,
        GetEventMetadata?         getEventMetadata = null,
        AggregateFactoryRegistry? factoryRegistry  = null
    ) {
        _getEventMetadata = getEventMetadata;
        _factoryRegistry  = factoryRegistry ?? AggregateFactoryRegistry.Instance;
        _eventStore       = Ensure.NotNull(eventStore, nameof(eventStore));
        _serializer       = serializer ?? DefaultEventSerializer.Instance;
        _metaSerializer   = metaSerializer ?? DefaultMetadataSerializer.Instance;
    }

    public async Task<AppendEventsResult> Store<T>(
        T                 aggregate,
        CancellationToken cancellationToken
    )
        where T : Aggregate {
        Ensure.NotNull(aggregate, nameof(aggregate));

        if (aggregate.Changes.Count == 0) return AppendEventsResult.NoOp;

        var stream          = StreamName.For<T>(aggregate.GetId());
        var expectedVersion = new ExpectedStreamVersion(aggregate.OriginalVersion);

        var result = await _eventStore.AppendEvents(
                stream,
                expectedVersion,
                aggregate.Changes.Select(ToStreamEvent).ToArray(),
                cancellationToken
            )
            .NoContext();

        return result;

        StreamEvent ToStreamEvent(object evt) {
            var (eventType, payload) = _serializer.SerializeEvent(evt);
            var meta      = _getEventMetadata?.Invoke(stream, evt);
            var metaBytes = meta == null ? null : _metaSerializer.Serialize(meta);
            return new(eventType, payload, metaBytes, _serializer.ContentType, -1);
        }
    }

    public async Task<T> Load<T>(string id, CancellationToken cancellationToken)
        where T : Aggregate {
        Ensure.NotEmptyString(id, nameof(id));

        const int pageSize = 500;

        var stream    = StreamName.For<T>(id);
        var aggregate = _factoryRegistry.CreateInstance<T>();

        try {
            var position = StreamReadPosition.Start;

            while (true) {
                var readCount = await _eventStore.ReadStream(
                        stream,
                        position,
                        pageSize,
                        Fold,
                        cancellationToken
                    )
                    .NoContext();

                if (readCount == 0) break;

                position = new StreamReadPosition(position.Value + readCount);
            }
        }
        catch (StreamNotFound e) {
            throw new Exceptions.AggregateNotFound<T>(id, e);
        }

        return aggregate;

        void Fold(StreamEvent streamEvent) {
            var evt = Deserialize(streamEvent);
            if (evt == null) return;

            aggregate.Fold(evt);
        }

        object? Deserialize(StreamEvent streamEvent)
            => _serializer.DeserializeEvent(streamEvent.Data.AsSpan(), streamEvent.EventType);
    }

    public Task<bool> Exists<T>(string id, CancellationToken cancellationToken) where T : Aggregate {
        var stream    = StreamName.For<T>(id);
        return _eventStore.StreamExists(stream, cancellationToken);
    }
}
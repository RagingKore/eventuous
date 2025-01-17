using System.Text.Json;
using EventStore.Client;
using Eventuous.EventStore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime.Serialization.SystemTextJson;

namespace Eventuous.Tests.EventStore.Fixtures;

public class IntegrationFixture {
    public IEventStore      EventStore     { get; }
    public IAggregateStore  AggregateStore { get; }
    public EventStoreClient Client         { get; }
    public Fixture          Auto           { get; } = new();

    IEventSerializer Serializer { get; } = new DefaultEventSerializer(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
            .ConfigureForNodaTime(DateTimeZoneProviders.Tzdb)
    );

    public static IntegrationFixture Instance { get; } = new();

    public IntegrationFixture() {
        DefaultEventSerializer.SetDefaultSerializer(Serializer);
        var settings = EventStoreClientSettings.Create("esdb://localhost:2113?tls=false");
        Client         = new EventStoreClient(settings);
        EventStore     = new EsdbEventStore(Client, null);
        AggregateStore = new AggregateStore(EventStore);

    }
}
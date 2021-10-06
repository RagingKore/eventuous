﻿using Eventuous.AspNetCore.Tests.Sut;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Eventuous.AspNetCore.Tests;

public class AggregateFactoryRegistrationTests {
    readonly AggregateFactoryRegistry _registry;

    public AggregateFactoryRegistrationTests() {
        var host = new TestServer(BuildHost());
        _registry = host.Host.Services.GetService<AggregateFactoryRegistry>();
    }

    [Fact]
    public void ShouldCreateNewAggregateWithExplicitFunction() {
        var instance = _registry.CreateInstance<TestAggregate>();
        instance.Should().BeOfType<TestAggregate>();
        instance.Dependency.Should().NotBeNull();
        instance.State.Should().NotBeNull();
    }

    [Fact]
    public void ShouldCreateNewAggregateByResolve() {
        var instance = _registry.CreateInstance<AnotherTestAggregate>();
        instance.Should().BeOfType<AnotherTestAggregate>();
        instance.Dependency.Should().NotBeNull();
        instance.State.Should().NotBeNull();
    }

    [Fact]
    public void ShouldCreateTwoSeparateInstances() {
        var instance1 = _registry.CreateInstance<AnotherTestAggregate>();
        var instance2 = _registry.CreateInstance<AnotherTestAggregate>();
        instance1.Should().NotBeSameAs(instance2);
    }

    static IWebHostBuilder BuildHost() => new WebHostBuilder().UseStartup<Startup>();

    public class Startup {
        public void ConfigureServices(IServiceCollection services) {
            services.AddAggregateStore<FakeStore>();
            services.AddSingleton<TestDependency>();

            services.AddAggregate(
                sp => new TestAggregate(sp.GetRequiredService<TestDependency>())
            );

            services.AddAggregate<AnotherTestAggregate>();
        }

        public void Configure(IApplicationBuilder app) => app.UseAggregateFactory();
    }
}
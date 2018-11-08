﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading.Tasks;
using Autofac;
using Microsoft.Azure.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Services.AzureSearch;
using NuGet.Services.AzureSearch.Db2AzureSearch;
using NuGet.Services.AzureSearch.Wrappers;

namespace NuGet.Jobs
{
    public class Job : JsonConfigurationJob
    {
        private const string Db2AzureSearchSectionName = "Db2AzureSearch";
        private const string SearchIndexKey = "SearchIndex";
        private const string HijackIndexKey = "HijackIndex";

        public override async Task Run()
        {
            var db2AzureSearch = _serviceProvider.GetRequiredService<IDb2AzureSearchCommand>();
            await db2AzureSearch.ExecuteAsync();
        }

        protected override void ConfigureAutofacServices(ContainerBuilder containerBuilder)
        {
            containerBuilder
                .Register(c =>
                {
                    var serviceClient = c.Resolve<ISearchServiceClientWrapper>();
                    var options = c.Resolve<IOptionsSnapshot<Db2AzureSearchConfiguration>>();
                    return serviceClient.Indexes.GetClient(options.Value.SearchIndexName);
                })
                .SingleInstance()
                .Keyed<ISearchIndexClientWrapper>(SearchIndexKey);

            containerBuilder
                .Register(c =>
                {
                    var serviceClient = c.Resolve<ISearchServiceClientWrapper>();
                    var options = c.Resolve<IOptionsSnapshot<Db2AzureSearchConfiguration>>();
                    return serviceClient.Indexes.GetClient(options.Value.HijackIndexName);
                })
                .SingleInstance()
                .Keyed<ISearchIndexClientWrapper>(HijackIndexKey);

            containerBuilder
                .Register<IDb2AzureSearchCommand>(c => new Db2AzureSearchCommand(
                    c.Resolve<INewPackageRegistrationProducer>(),
                    c.Resolve<IIndexActionBuilder>(),
                    c.Resolve<ISearchServiceClientWrapper>(),
                    c.ResolveKeyed<ISearchIndexClientWrapper>(SearchIndexKey),
                    c.ResolveKeyed<ISearchIndexClientWrapper>(HijackIndexKey),
                    c.Resolve<IOptionsSnapshot<Db2AzureSearchConfiguration>>(),
                    c.Resolve<ILogger<Db2AzureSearchCommand>>()));
        }

        protected override void ConfigureJobServices(IServiceCollection services, IConfigurationRoot configurationRoot)
        {
            services.Configure<Db2AzureSearchConfiguration>(configurationRoot.GetSection(Db2AzureSearchSectionName));

            services.AddSingleton<ISearchServiceClient>(p =>
            {
                var options = p.GetRequiredService<IOptionsSnapshot<Db2AzureSearchConfiguration>>();
                return new SearchServiceClient(
                    options.Value.SearchServiceName,
                    new SearchCredentials(options.Value.SearchServiceApiKey));
            });

            services.AddSingleton<ISearchServiceClientWrapper, SearchServiceClientWrapper>();
            services.AddTransient<IEntitiesContextFactory, EntitiesContextFactory>();
            services.AddTransient<INewPackageRegistrationProducer, NewPackageRegistrationProducer>();
            services.AddTransient<IIndexActionBuilder, IndexActionBuilder>();
        }
    }
}

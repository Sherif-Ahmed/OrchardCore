using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Fluid.Values;
using Microsoft.Extensions.Logging;
using Nest;
using OrchardCore.Liquid;
using OrchardCore.Search.Abstractions;
using OrchardCore.Search.Elasticsearch.Core.Models;
using OrchardCore.Search.Elasticsearch.Core.Services;
using OrchardCore.Settings;

namespace OrchardCore.Search.Elasticsearch.Services;

public class ElasticsearchService : ISearchService
{
    private readonly ISiteService _siteService;
    private readonly ElasticIndexManager _elasticIndexManager;
    private readonly ElasticIndexSettingsService _elasticIndexSettingsService;
    private readonly IElasticSearchQueryService _elasticsearchQueryService;
    private readonly IElasticClient _elasticClient;
    private readonly JavaScriptEncoder _javaScriptEncoder;
    private readonly ILiquidTemplateManager _liquidTemplateManager;
    private readonly ILogger _logger;

    public ElasticsearchService(
        ISiteService siteService,
        ElasticIndexManager elasticIndexManager,
        ElasticIndexSettingsService elasticIndexSettingsService,
        IElasticSearchQueryService elasticsearchQueryService,
        IElasticClient elasticClient,
        JavaScriptEncoder javaScriptEncoder,
        ILiquidTemplateManager liquidTemplateManager,
        ILogger<ElasticsearchService> logger
        )
    {
        _siteService = siteService;
        _elasticIndexManager = elasticIndexManager;
        _elasticIndexSettingsService = elasticIndexSettingsService;
        _elasticsearchQueryService = elasticsearchQueryService;
        _elasticClient = elasticClient;
        _javaScriptEncoder = javaScriptEncoder;
        _liquidTemplateManager = liquidTemplateManager;
        _logger = logger;
    }

    public string Name => "Elasticsearch";

    public async Task<SearchResult> SearchAsync(string indexName, string term, int start, int pageSize)
    {
        var index = !string.IsNullOrWhiteSpace(indexName) ? indexName.Trim() : await DefaultIndexAsync();

        var result = new SearchResult();

        if (index == null || !await _elasticIndexManager.Exists(index))
        {
            _logger.LogWarning("Elasticsearch: Couldn't execute search. The search index doesn't exist.");

            return result;
        }

        var elasticIndexSettings = await _elasticIndexSettingsService.GetSettingsAsync(index);
        result.Latest = elasticIndexSettings.IndexLatest;

        var siteSettings = await _siteService.GetSiteSettingsAsync();
        var searchSettings = siteSettings.As<ElasticSettings>();

        if (searchSettings.DefaultSearchFields == null || searchSettings.DefaultSearchFields.Length == 0)
        {
            _logger.LogWarning("Elasticsearch: Couldn't execute search. No serach provider settings was defined.");

            return result;
        }

        try
        {
            var searchType = searchSettings.GetSearchType();
            QueryContainer query = null;

            if (searchType == ElasticSettings.CustomSearchType && !string.IsNullOrWhiteSpace(searchSettings.DefaultQuery))
            {
                var tokenizedContent = await _liquidTemplateManager.RenderStringAsync(searchSettings.DefaultQuery, _javaScriptEncoder,
                    new Dictionary<string, FluidValue>()
                    {
                        ["term"] = new StringValue(term)
                    });

                try
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(tokenizedContent));

                    var searchRequest = await _elasticClient.RequestResponseSerializer.DeserializeAsync<SearchRequest>(stream);

                    query = searchRequest.Query;
                }
                catch { }
            }
            else if (searchType == ElasticSettings.QueryStringSearchType)
            {
                query = new QueryStringQuery
                {
                    Fields = searchSettings.DefaultSearchFields,
                    Analyzer = await _elasticIndexSettingsService.GetQueryAnalyzerAsync(index),
                    Query = term
                };
            }

            query ??= new MultiMatchQuery
            {
                Fields = searchSettings.DefaultSearchFields,
                Analyzer = await _elasticIndexSettingsService.GetQueryAnalyzerAsync(index),
                Query = term
            };

            result.ContentItemIds = await _elasticsearchQueryService.ExecuteQueryAsync(index, query, null, start, pageSize);
            result.Success = true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Incorrect Elasticsearch search query syntax provided in search.");
        }

        return result;
    }

    private async Task<string> DefaultIndexAsync()
    {
        var siteSettings = await _siteService.GetSiteSettingsAsync();

        return siteSettings.As<ElasticSettings>().SearchIndex;
    }
}
